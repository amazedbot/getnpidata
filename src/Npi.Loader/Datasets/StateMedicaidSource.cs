using System.Globalization;
using System.Text.Json;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// State Medicaid enrolled-provider lists (docs/PRODUCT_PLAN.md Phase 3; owner's OK 2026-10-13), a sign of current practice for
/// the record-quality label and a line on the profile: New York's Medicaid Enrolled Provider Listing (health.data.ny.gov
/// <c>keti-qx5t</c>, ~1.1M rows, one per enrollment and service address; exported grouped by NPI, ~236k rows) and California's
/// Enrolled Medi-Cal Fee-for-Service Providers (data.chhs.ca.gov, a ~120 MB CSV, ~380k rows), and since step 3.7 (owner,
/// 2026-10-17) Florida AHCA's Provider Master List (<see cref="FloridaMedicaidList"/>, a ~17 MB zip). All three list current
/// enrollments only and are updated about weekly. Into <c>state_medicaid</c>, one row per NPI and state, replaced together
/// (staging + RENAME); the version is the three lists' update times (Florida's: the zip's Last-Modified).
/// </summary>
public sealed class StateMedicaidSource : DatasetSource
{
    public const string NewYorkDomain = "health.data.ny.gov";
    public const string NewYorkId = "keti-qx5t";

    /// <summary>The California resource's name in its CKAN package.</summary>
    public const string CaliforniaResourceName = "Enrolled Medi-Cal Fee-for-Service (FFS) Providers";

    public override string Name => "state_medicaid";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    // The grouped export: per NPI the earliest enrollment and one profession (an NPI can be enrolled as several).
    public static Uri NewYorkExportUrl => new($"https://{NewYorkDomain}/resource/{NewYorkId}.csv?$select="
        + Uri.EscapeDataString("npi,min(enrollment_begin_date) AS since,max(profession_or_service) AS profession")
        + "&$where=" + Uri.EscapeDataString("npi IS NOT NULL") + "&$group=npi&$limit=50000000");

    private static Uri NewYorkMetadataUrl => new($"https://{NewYorkDomain}/api/views/{NewYorkId}.json");

    private static readonly CsvColumn[] NewYorkColumns =
    [
        new("npi", "npi", CsvValue.Npi), new("since", "since", CsvValue.FlexibleDate), new("profession", "kind"),
    ];

    private static readonly CsvColumn[] CaliforniaColumns =
    [
        new("NPI", "npi", CsvValue.Npi), new("Enroll_Status_Eff_DT", "since", CsvValue.FlexibleDate),
        new("FI_Provider_Specialty", "kind"), new("FI_Provider_Type", "kind2"),
    ];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var (newYork, california) = await FindBothAsync(context, ct);
        var florida = await FloridaVersionAsync(context, ct);
        return new DatasetRelease($"NY {newYork} | CA {california.Version} | FL {florida}", california.Url);
    }

    // The Provider Master List's Last-Modified (AHCA replaces the file in place), else its ETag or size.
    private static async Task<string> FloridaVersionAsync(DatasetContext context, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, context.Options.FloridaMedicaidListUrl);
        using var response = await context.Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var content = response.Content.Headers;
        return content.LastModified?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
               ?? response.Headers.ETag?.Tag ?? $"{content.ContentLength} bytes";
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var (newYork, california) = await FindBothAsync(context, ct);
        var newYorkPath = await context.DownloadAsync(new DatasetRelease(newYork, NewYorkExportUrl), "state_medicaid_ny.csv", ct);
        string? californiaPath = null, floridaPath = null;
        var loaded = false;
        const string raw = "state_medicaid_raw_staging";
        try
        {
            californiaPath = await context.DownloadAsync(california, "state_medicaid_ca.csv", ct);
            floridaPath = await context.DownloadAsync(new DatasetRelease("FL", new Uri(context.Options.FloridaMedicaidListUrl)), "state_medicaid_fl.zip", ct);
            await using var connection = await context.Database.OpenAsync(ct);
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection,
                    $"""
                    CREATE TABLE `{raw}` (`npi` CHAR(10) NULL, `since` DATE NULL, `kind` VARCHAR(255) NULL, `kind2` VARCHAR(255) NULL)
                    DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
                    """, ct);
                var counts = await TableSwap.ReplaceAsync(connection, ["state_medicaid"], context.Options.MinRowRatio, async () =>
                {
                    await CsvTableLoader.LoadAsync(connection, newYorkPath, raw, NewYorkColumns, ct);
                    await InsertAsync(connection, raw, "NY", ct);
                    await Database.ExecuteAsync(connection, $"TRUNCATE TABLE `{raw}`", ct);
                    await CsvTableLoader.LoadAsync(connection, californiaPath, raw, CaliforniaColumns, ct);
                    await InsertAsync(connection, raw, "CA", ct);
                    await Database.ExecuteAsync(connection, $"TRUNCATE TABLE `{raw}`", ct);
                    await LoadFloridaAsync(context, connection, floridaPath, raw, ct);
                    await InsertAsync(connection, raw, "FL", ct);
                }, ct, log: context.Log);
                loaded = true;
                return counts["state_medicaid"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }
        finally
        {
            // Kept after a failed load: the next attempt downloads them again only if they changed.
            if (loaded)
            {
                FileDownloader.Delete(newYorkPath);
                FileDownloader.Delete(californiaPath!);
                FileDownloader.Delete(floridaPath!);
            }
        }
    }

    // Florida's current enrollments into the raw table (no kind of provider: the list gives only AHCA's numeric type codes).
    private static async Task LoadFloridaAsync(DatasetContext context, MySqlConnector.MySqlConnection connection, string path, string raw, CancellationToken ct)
    {
        await using var rows = new TsvRows(Path.Combine(context.Options.ResolvedWorkFolder, "datasets"), "state_medicaid_fl");
        await using (var file = File.OpenRead(path))
        {
            foreach (var e in FloridaMedicaidList.Read(file, DateOnly.FromDateTime(DateTime.UtcNow)))
            {
                rows.Add(e.Npi, e.Since, null, null);
            }
        }

        if (rows.Rows == 0)
        {
            throw new InvalidDataException("The Florida Provider Master List has no current enrollments.");
        }

        await rows.LoadAsync(connection, raw, ["npi", "since", "kind", "kind2"], ct);
        context.Log.Information("Florida Provider Master List: {Rows:N0} current enrollments", rows.Rows);
    }

    // One row per NPI: the earliest enrollment and the kind of provider (California's specialty, else its provider type).
    private static Task InsertAsync(MySqlConnector.MySqlConnection connection, string raw, string state, CancellationToken ct) =>
        Database.ExecuteAsync(connection,
            $"""
            INSERT INTO `state_medicaid_staging` (`npi`, `state`, `enrolled_since`, `provider_type`)
            SELECT `npi`, '{state}', MIN(`since`), LEFT(COALESCE(MAX({Named("kind")}), MAX({Named("kind2")})), 100)
            FROM `{raw}` WHERE `npi` IS NOT NULL GROUP BY `npi`
            """, ct);

    // A kind of provider that says something ("N/A" and "NOT APPLICABLE" don't).
    private static string Named(string column) => $"CASE WHEN `{column}` NOT IN ('', 'N/A', 'NOT APPLICABLE') THEN `{column}` END";

    private static async Task<(string NewYork, DatasetRelease California)> FindBothAsync(DatasetContext context, CancellationToken ct)
    {
        string newYork;
        await using (var stream = await context.Http.GetStreamAsync(NewYorkMetadataUrl, ct))
        {
            newYork = StateLicenseSource.ParseRowsUpdated(stream);
        }

        await using var package = await context.Http.GetStreamAsync(new Uri(context.Options.MediCalPackageUrl), ct);
        return (newYork, ParseCaliforniaPackage(package));
    }

    /// <summary>The California list's resource in the CKAN <c>package_show</c> answer: its download URL and last change.</summary>
    public static DatasetRelease ParseCaliforniaPackage(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var resource in doc.RootElement.GetProperty("result").GetProperty("resources").EnumerateArray())
        {
            if (resource.TryGetProperty("name", out var name) && name.GetString()?.Trim() == CaliforniaResourceName
                && resource.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } link)
            {
                var modified = resource.TryGetProperty("last_modified", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                return new DatasetRelease(modified ?? link, new Uri(link));
            }
        }

        throw new InvalidDataException($"data.chhs.ca.gov lists no \"{CaliforniaResourceName}\" resource.");
    }
}
