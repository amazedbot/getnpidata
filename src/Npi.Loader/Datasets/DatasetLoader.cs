using Dapper;
using MySqlConnector;
using Npi.Loader.Db;
using Serilog;

namespace Npi.Loader.Datasets;

/// <summary>Shared services for <see cref="DatasetSource"/>s during one refresh.</summary>
public sealed class DatasetContext
{
    private readonly CatalogCache _catalogs;

    public DatasetContext(LoaderOptions options, Database database, HttpClient http, ILogger log)
        : this(options, database, http, log, new CatalogCache())
    {
    }

    private DatasetContext(LoaderOptions options, Database database, HttpClient http, ILogger log, CatalogCache catalogs)
    {
        Options = options;
        Database = database;
        Http = http;
        Log = log;
        _catalogs = catalogs;
    }

    public LoaderOptions Options { get; }

    public Database Database { get; }

    public HttpClient Http { get; }

    public ILogger Log { get; }

    /// <summary>The same services, logging with "[<paramref name="step"/>] " before each line (sources and states run side by side).</summary>
    public DatasetContext ForStep(string step) => new(Options, Database, Http, LoaderLog.ForStep(Log, step), _catalogs);

    /// <summary>The data.cms.gov catalog, fetched once per refresh (~18 MB) and shared by every CMS source, also when they run at once.</summary>
    public Task<CmsCatalog> CmsCatalogAsync(CancellationToken ct)
    {
        Task<CmsCatalog> fetch;
        lock (_catalogs.Lock)
        {
            // A failed fetch is tried again by the next source that needs it.
            if (_catalogs.Cms is not { IsFaulted: false, IsCanceled: false })
            {
                _catalogs.Cms = CmsCatalog.LoadAsync(Http, new Uri(Options.CmsCatalogUrl), ct);
            }

            fetch = _catalogs.Cms;
        }

        return fetch.WaitAsync(ct);
    }

    public Task<DatasetRelease> ProviderDataAsync(string datasetId, CancellationToken ct) =>
        ProviderDataCatalog.FindLatestAsync(Http, new Uri(Options.ProviderDataMetastoreUrl), datasetId, ct);

    /// <summary>Downloads the release file into the work folder's <c>datasets</c> subfolder.</summary>
    public Task<string> DownloadAsync(DatasetRelease release, string fileName, CancellationToken ct) =>
        Downloader().DownloadAsync(release.Url, fileName, Path.Combine(Options.ResolvedWorkFolder, "datasets"), ct);

    /// <summary>A downloader with the configured attempts and parallel parts.</summary>
    public FileDownloader Downloader() =>
        new(Http, Log, Options.DownloadAttempts, parts: Options.DownloadParts, partMinBytes: Options.DownloadPartMinBytes);

    // Shared by a context and those made from it with ForStep.
    private sealed class CatalogCache
    {
        public Lock Lock { get; } = new();

        public Task<CmsCatalog>? Cms { get; set; }
    }
}

/// <summary>One external dataset joined to NPIs (CLAUDE.md §7 Stage 5.5). Its version is tracked in <c>reference_data</c>.</summary>
public abstract class DatasetSource
{
    /// <summary>The <c>reference_data.source</c> key and the name accepted by <c>Npi.Loader datasets &lt;name&gt;</c>.</summary>
    public abstract string Name { get; }

    /// <summary>How long a successful check stays valid before <c>run</c> looks for a new release again.</summary>
    public virtual TimeSpan CheckInterval => TimeSpan.FromHours(20);

    /// <summary>
    /// The sources (by <see cref="Name"/>) that must finish before this one starts when they refresh in the same run: those whose
    /// tables it reads while loading, or that rebuild the same tables after their load. They come earlier in
    /// <see cref="DatasetLoader.AllSources"/>.
    /// </summary>
    public virtual IReadOnlyList<string> After => [];

    /// <summary>A load of several GB (download, MySQL work or memory); at most one heavy source runs at a time.</summary>
    public virtual bool Heavy => false;

    public abstract Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct);

    /// <summary>Downloads and loads <paramref name="release"/>, replacing the source's tables.</summary>
    /// <returns>The number of rows loaded.</returns>
    public abstract Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct);

    /// <summary>
    /// Downloads the file, runs <paramref name="load"/> on it, and deletes it once loaded. A failed load keeps it, so the next
    /// attempt downloads it again only if the publisher changed it (9 GB for Open Payments, 4 GB for Part D by drug).
    /// </summary>
    protected static async Task<long> WithDownloadAsync(DatasetContext context, DatasetRelease release, string fileName,
        Func<MySqlConnection, string, Task<long>> load, CancellationToken ct)
    {
        var path = await context.DownloadAsync(release, fileName, ct);
        long rows;
        await using (var connection = await context.Database.OpenAsync(ct))
        {
            rows = await load(connection, path);
        }

        FileDownloader.Delete(path);
        return rows;
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
        new FacilityOwnerSource(),
        new OwnershipChangeSource(),
        new NursingHomeInspectionSource(),
        new OpioidTreatmentSource(),
        new RevalidationSource(),
        new DmeSupplierSource(),
        new ProviderOfServicesSource(),
        new MedicareEnrollmentSource(),
        new HcahpsSource(),
        new HomeHealthSource(),
        new HospiceSource(),
        new MipsSource(),
        new StateLicenseSource(),
        new StateMedicaidSource(),
        new MedicareUtilizationSource(),
        new MedicareServicesSource(),
        new PartDPrescriberSource(),
        new MedicareHistorySource(),
        new ShortageAreaSource(),
        new CountyPopulationSource(),
        new OpenPaymentsSource(),
        new OpenPaymentsYearSource(),
        new OpenPaymentsCompanySource(),
        new OpenPaymentsEntitySource(),
        new OpenPaymentsResearchSource(),
        new FdaProductSource(),
        new DrugSpendingSource(),
        new NadacSource(),
        new DrugShortageSource(),
        new ProductAdverseEventSource(),
        new ProductTrialSource(),
        new PartDDrugPrescriberSource(),
        new FdaEnforcementSource(),
        new SecCompanySource(),
        new OigCiaSource(),
        new FemaStructureSource(),
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

        // Side by side, up to ParallelSources (speed item 7); each source checks and loads on its own connections, and one that
        // fails doesn't stop the others. Its lines start with its name.
        var context = new DatasetContext(options, database, http, log);
        var loaded = 0;
        var jobs = selected.Select(source => new ScheduledJob(source.Name, source.After, source.Heavy, async jobCt =>
        {
            if (await RefreshAsync(context.ForStep(source.Name), source, force, jobCt))
            {
                Interlocked.Increment(ref loaded);
            }
        })).ToList();
        var failed = await ParallelSchedule.RunAsync(jobs, options.ParallelSources, ct, (job, ex) => log.Error(ex, "Dataset {Source} failed", job.Name));
        if (loaded > 0)
        {
            DropOrphanSidecars(Path.Combine(options.ResolvedWorkFolder, "datasets"));
        }

        if (failed.Count > 0 && selected.Count > 1)
        {
            log.Error("Datasets failed: {Sources}", string.Join(", ", selected.Select(s => s.Name).Where(failed.Contains)));
        }

        return failed.Count == 0;
    }

    /// <returns>True when the source was loaded, false when it was current.</returns>
    private async Task<bool> RefreshAsync(DatasetContext context, DatasetSource source, bool force, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var connection = await database.OpenAsync(ct);
        var stored = await connection.QuerySingleOrDefaultAsync<StoredVersion>(new CommandDefinition(
            "SELECT `version` AS Version, `checked_at` AS CheckedAt FROM `reference_data` WHERE `source` = @source",
            new { source = source.Name }, cancellationToken: ct));
        if (!force && stored is not null && now - stored.CheckedAt < source.CheckInterval)
        {
            log.Information("Dataset {Source} {Version} checked {Hours:N0} h ago", source.Name, stored.Version, (now - stored.CheckedAt).TotalHours);
            return false;
        }

        var latest = await source.FindLatestAsync(context, ct);
        if (!force && stored?.Version == latest.Version)
        {
            await Database.ExecuteAsync(connection, "UPDATE `reference_data` SET `checked_at` = @now WHERE `source` = @source", ct,
                param: new { now, source = source.Name });
            log.Information("Dataset {Source} {Version} is current", source.Name, latest.Version);
            return false;
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
        return true;
    }

    // The validators of downloads a source deleted itself after loading them (FileDownloader.MetaPath).
    private static void DropOrphanSidecars(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var meta in Directory.EnumerateFiles(folder, "*.download.json", SearchOption.AllDirectories))
        {
            if (!File.Exists(meta[..^".download.json".Length]))
            {
                File.Delete(meta);
            }
        }
    }
}
