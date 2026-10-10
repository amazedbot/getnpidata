using Dapper;
using MySqlConnector;
using Npi.Loader.Db;
using Serilog;

namespace Npi.Loader.Datasets;

/// <summary>Shared services for <see cref="DatasetSource"/>s during one refresh.</summary>
public sealed class DatasetContext(LoaderOptions options, Database database, HttpClient http, ILogger log)
{
    private CmsCatalog? _cmsCatalog;

    public LoaderOptions Options { get; } = options;

    public Database Database { get; } = database;

    public HttpClient Http { get; } = http;

    public ILogger Log { get; } = log;

    /// <summary>The data.cms.gov catalog, fetched once per refresh (~18 MB) and shared by every CMS source.</summary>
    public async Task<CmsCatalog> CmsCatalogAsync(CancellationToken ct) =>
        _cmsCatalog ??= await CmsCatalog.LoadAsync(Http, new Uri(Options.CmsCatalogUrl), ct);

    public Task<DatasetRelease> ProviderDataAsync(string datasetId, CancellationToken ct) =>
        ProviderDataCatalog.FindLatestAsync(Http, new Uri(Options.ProviderDataMetastoreUrl), datasetId, ct);

    /// <summary>Downloads the release file into the work folder's <c>datasets</c> subfolder.</summary>
    public Task<string> DownloadAsync(DatasetRelease release, string fileName, CancellationToken ct) =>
        new FileDownloader(Http, Log, Options.DownloadAttempts)
            .DownloadAsync(release.Url, fileName, Path.Combine(Options.ResolvedWorkFolder, "datasets"), ct);
}

/// <summary>One external dataset joined to NPIs (CLAUDE.md §7 Stage 5.5). Its version is tracked in <c>reference_data</c>.</summary>
public abstract class DatasetSource
{
    /// <summary>The <c>reference_data.source</c> key and the name accepted by <c>Npi.Loader datasets &lt;name&gt;</c>.</summary>
    public abstract string Name { get; }

    /// <summary>How long a successful check stays valid before <c>run</c> looks for a new release again.</summary>
    public virtual TimeSpan CheckInterval => TimeSpan.FromHours(20);

    public abstract Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct);

    /// <summary>Downloads and loads <paramref name="release"/>, replacing the source's tables.</summary>
    /// <returns>The number of rows loaded.</returns>
    public abstract Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct);

    /// <summary>Downloads the file, runs <paramref name="load"/> on it, and deletes it afterwards.</summary>
    protected static async Task<long> WithDownloadAsync(DatasetContext context, DatasetRelease release, string fileName,
        Func<MySqlConnection, string, Task<long>> load, CancellationToken ct)
    {
        var path = await context.DownloadAsync(release, fileName, ct);
        try
        {
            await using var connection = await context.Database.OpenAsync(ct);
            return await load(connection, path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// Refreshes the Stage 5.5 datasets: each source is checked at most every <see cref="DatasetSource.CheckInterval"/>
/// and reloaded only when it published a new version. A failing source doesn't stop the others.
/// </summary>
public sealed class DatasetLoader(LoaderOptions options, Database database, HttpClient http, ILogger log,
    IReadOnlyList<DatasetSource>? sources = null, TimeProvider? clock = null)
{
    /// <summary>Every source, in refresh order.</summary>
    public static IReadOnlyList<DatasetSource> AllSources { get; } =
    [
        new LeieSource(),
        new OptOutSource(),
        new OrderReferringSource(),
        new CareCompareClinicianSource(),
        new FacilityAffiliationSource(),
        new HospitalSource(),
        new NursingHomeSource(),
        new FacilityEnrollmentSource(),
        new HcahpsSource(),
        new HomeHealthSource(),
        new HospiceSource(),
        new MipsSource(),
        new MedicareUtilizationSource(),
        new MedicareServicesSource(),
        new PartDPrescriberSource(),
        new ShortageAreaSource(),
        new CountyPopulationSource(),
        new OpenPaymentsSource(),
        new OpenPaymentsYearSource(),
        new OpenPaymentsCompanySource(),
    ];

    private readonly IReadOnlyList<DatasetSource> _sources = sources ?? AllSources;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private sealed record StoredVersion(string Version, DateTime CheckedAt);

    /// <param name="force">Reload even when the version is unchanged (the <c>datasets</c> command).</param>
    /// <param name="only">Refresh only this source (by <see cref="DatasetSource.Name"/>); null for all.</param>
    /// <returns>True when every refreshed source is current.</returns>
    public async Task<bool> RefreshAsync(bool force, string? only, CancellationToken ct)
    {
        var selected = only is null ? _sources : _sources.Where(s => string.Equals(s.Name, only, StringComparison.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
        {
            log.Error("Unknown dataset '{Name}'. Known: {Names}", only, string.Join(", ", _sources.Select(s => s.Name)));
            return false;
        }

        var context = new DatasetContext(options, database, http, log);
        var ok = true;
        foreach (var source in selected)
        {
            try
            {
                await RefreshAsync(context, source, force, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.Error(ex, "Dataset {Source} failed", source.Name);
                ok = false;
            }
        }

        return ok;
    }

    private async Task RefreshAsync(DatasetContext context, DatasetSource source, bool force, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var connection = await database.OpenAsync(ct);
        var stored = await connection.QuerySingleOrDefaultAsync<StoredVersion>(new CommandDefinition(
            "SELECT `version` AS Version, `checked_at` AS CheckedAt FROM `reference_data` WHERE `source` = @source",
            new { source = source.Name }, cancellationToken: ct));
        if (!force && stored is not null && now - stored.CheckedAt < source.CheckInterval)
        {
            log.Information("Dataset {Source} {Version} checked {Hours:N0} h ago", source.Name, stored.Version, (now - stored.CheckedAt).TotalHours);
            return;
        }

        var latest = await source.FindLatestAsync(context, ct);
        if (!force && stored?.Version == latest.Version)
        {
            await Database.ExecuteAsync(connection, "UPDATE `reference_data` SET `checked_at` = @now WHERE `source` = @source", ct,
                param: new { now, source = source.Name });
            log.Information("Dataset {Source} {Version} is current", source.Name, latest.Version);
            return;
        }

        var started = DateTime.UtcNow;
        var rows = await source.LoadAsync(context, latest, ct);
        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
            VALUES (@source, @version, @url, @rows, @now, @now) AS new
            ON DUPLICATE KEY UPDATE `version` = new.`version`, `source_url` = new.`source_url`, `rows_loaded` = new.`rows_loaded`,
              `loaded_at` = new.`loaded_at`, `checked_at` = new.`checked_at`
            """, ct, param: new { source = source.Name, version = latest.Version, url = latest.Url.ToString(), rows, now = _clock.GetUtcNow().UtcDateTime });
        log.Information("Dataset {Source} {Version}: {Rows:N0} rows loaded in {Seconds:N0}s", source.Name, latest.Version, rows,
            (DateTime.UtcNow - started).TotalSeconds);
    }
}
