using System.IO.Compression;
using System.Net.Http.Headers;
using Dapper;
using MySqlConnector;
using Npi.Loader.Db;
using Serilog;

namespace Npi.Loader.Reference;

/// <summary>
/// Stage 2 (CLAUDE.md §7): keeps taxonomy_codes (NUCC), zip_county (HUD), county and zip_centroid
/// (Census) current. Each source is checked independently; a failure in one doesn't stop the others.
/// Versions live in <c>reference_data</c>.
/// </summary>
public sealed class ReferenceLoader(LoaderOptions options, Database database, HttpClient http, ILogger log, TimeProvider? clock = null)
{
    public const string Nucc = "nucc";
    public const string Hud = "hud_zip_county";
    public const string Gazetteer = "census_gazetteer";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private sealed record StoredVersion(string Version, DateTime CheckedAt);

    /// <param name="force">Reload every source even when its version is unchanged (the <c>reference</c> command).</param>
    /// <returns>True when every source is current.</returns>
    public async Task<bool> RefreshAllAsync(bool force, CancellationToken ct)
    {
        var ok = true;
        foreach (var (name, refresh) in new (string, Func<bool, CancellationToken, Task>)[]
                 {
                     (Nucc, RefreshNuccAsync),
                     (Hud, RefreshHudAsync),
                     (Gazetteer, RefreshCensusAsync),
                 })
        {
            try
            {
                await refresh(force, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.Error(ex, "Reference data {Source} failed", name);
                ok = false;
            }
        }

        return ok;
    }

    public async Task RefreshNuccAsync(bool force, CancellationToken ct)
    {
        var pageUrl = new Uri(options.NuccPageUrl);
        var latest = NuccTaxonomy.FindLatest(await http.GetStringAsync(pageUrl, ct), pageUrl)
            ?? throw new InvalidDataException($"No nucc_taxonomy_<version>.csv link found on {pageUrl}; the page layout may have changed.");
        if (await IsCurrentAsync(Nucc, latest.Version, force, ct))
        {
            return;
        }

        var codes = NuccTaxonomy.Parse(new MemoryStream(await http.GetByteArrayAsync(latest.Url, ct)));
        if (codes.Count < options.MinTaxonomyCodes)
        {
            throw new InvalidDataException($"NUCC {latest.Version} lists only {codes.Count} codes, fewer than {options.MinTaxonomyCodes} (26.1 has 883).");
        }

        var table = ReferenceTables.ToDataTable(codes,
            ("Taxonomy_Code", typeof(string), c => c.Code),
            ("Grouping", typeof(string), c => c.Grouping),
            ("Classification", typeof(string), c => c.Classification),
            ("Specialization", typeof(string), c => c.Specialization),
            ("Definition", typeof(string), c => c.Definition),
            ("Notes", typeof(string), c => c.Notes),
            ("Display_Name", typeof(string), c => c.DisplayName),
            ("Section", typeof(string), c => c.Section),
            ("Nucc_Version", typeof(string), _ => latest.Version));

        await using var connection = await database.OpenAsync(ct);
        try
        {
            // Upsert, not replace: codes NUCC retires stay (NPIs may still carry them) with their older Nucc_Version.
            await ReferenceTables.CopyToStagingAsync(connection, "taxonomy_codes", table, ct);
            await Database.ExecuteAsync(connection,
                """
                INSERT INTO `taxonomy_codes` (`Taxonomy_Code`, `Grouping`, `Classification`, `Specialization`, `Definition`, `Notes`, `Display_Name`, `Section`, `Nucc_Version`)
                SELECT `Taxonomy_Code`, `Grouping`, `Classification`, `Specialization`, `Definition`, `Notes`, `Display_Name`, `Section`, `Nucc_Version`
                FROM `taxonomy_codes_staging` s
                ON DUPLICATE KEY UPDATE `Grouping` = s.`Grouping`, `Classification` = s.`Classification`, `Specialization` = s.`Specialization`,
                  `Definition` = s.`Definition`, `Notes` = s.`Notes`, `Display_Name` = s.`Display_Name`, `Section` = s.`Section`, `Nucc_Version` = s.`Nucc_Version`
                """, ct);
        }
        finally
        {
            await Database.ExecuteAsync(connection, "DROP TABLE IF EXISTS `taxonomy_codes_staging`", CancellationToken.None);
        }

        await SaveVersionAsync(connection, Nucc, latest.Version, latest.Url, codes.Count, ct);
        log.Information("NUCC taxonomy {Version}: {Rows:N0} codes loaded from {Url}", latest.Version, codes.Count, latest.Url);
    }

    public async Task RefreshHudAsync(bool force, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.HudApiToken))
        {
            throw new InvalidOperationException(
                "HudApiToken is not set. Set it with: dotnet user-secrets --project src/Npi.Loader set \"HudApiToken\" \"<token>\"");
        }

        // HUD publishes quarterly and the only way to see the version is to download everything
        // (~8 MB), so check at most every HudRefreshDays.
        var stored = await GetVersionAsync(Hud, ct);
        if (!force && stored is not null && UtcNow - stored.CheckedAt < TimeSpan.FromDays(options.HudRefreshDays))
        {
            log.Information("HUD ZIP-county {Version} checked {Days:N0} day(s) ago; next check after {Interval} days", stored.Version,
                (UtcNow - stored.CheckedAt).TotalDays, options.HudRefreshDays);
            return;
        }

        var url = new Uri(options.HudApiUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.HudApiToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HUD API returned {(int)response.StatusCode} {response.ReasonPhrase}. Check HudApiToken (it may have expired).");
        }

        var crosswalk = HudZipCounty.Parse(await response.Content.ReadAsStreamAsync(ct));
        if (await IsCurrentAsync(Hud, crosswalk.Version, force, ct))
        {
            return;
        }

        var table = ReferenceTables.ToDataTable(crosswalk.Rows,
            ("zip5", typeof(string), r => r.Zip5),
            ("county_fips", typeof(string), r => r.CountyFips),
            ("res_ratio", typeof(decimal), r => r.ResRatio),
            ("bus_ratio", typeof(decimal), r => r.BusRatio),
            ("oth_ratio", typeof(decimal), r => r.OthRatio),
            ("tot_ratio", typeof(decimal), r => r.TotRatio),
            ("usps_city", typeof(string), r => r.City),
            ("usps_state", typeof(string), r => r.State),
            ("year", typeof(short), _ => (short)crosswalk.Year),
            ("quarter", typeof(sbyte), _ => (sbyte)crosswalk.Quarter));

        await using var connection = await database.OpenAsync(ct);
        var rows = await ReferenceTables.ReplaceAsync(connection, "zip_county", table, options.MinRowRatio, ct);
        await SaveVersionAsync(connection, Hud, crosswalk.Version, url, rows, ct);
        log.Information("HUD ZIP-county {Version}: {Rows:N0} ZIP/county pairs loaded ({Skipped} rows without a county FIPS skipped)",
            crosswalk.Version, rows, crosswalk.Skipped);
    }

    public async Task RefreshCensusAsync(bool force, CancellationToken ct)
    {
        var baseUrl = new Uri(options.GazetteerBaseUrl.EndsWith('/') ? options.GazetteerBaseUrl : options.GazetteerBaseUrl + "/");
        var year = CensusGeography.FindLatestGazetteerYear(await http.GetStringAsync(baseUrl, ct))
            ?? throw new InvalidDataException($"No <year>_Gazetteer/ folder found at {baseUrl}; the layout may have changed.");
        var version = year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (await IsCurrentAsync(Gazetteer, version, force, ct))
        {
            return;
        }

        var gazetteerCounties = CensusGeography.ParseGazetteerCounties(await OpenZippedTextAsync(CensusGeography.CountiesUrl(baseUrl, year), ct));
        var codes2020 = CensusGeography.ParseCountyCodes2020(new MemoryStream(await http.GetByteArrayAsync(new Uri(options.CountyCodes2020Url), ct)));
        var counties = CensusGeography.Merge(gazetteerCounties, codes2020);
        var centroids = CensusGeography.ParseGazetteerZcta(await OpenZippedTextAsync(CensusGeography.ZctaUrl(baseUrl, year), ct));

        var countyTable = ReferenceTables.ToDataTable(counties,
            ("county_fips", typeof(string), c => c.CountyFips),
            ("state", typeof(string), c => c.State),
            ("county_name", typeof(string), c => c.Name),
            ("source", typeof(string), c => c.Source));
        var centroidTable = ReferenceTables.ToDataTable(centroids,
            ("zip5", typeof(string), c => c.Zip5),
            ("lat", typeof(decimal), c => c.Lat),
            ("lon", typeof(decimal), c => c.Lon));

        await using var connection = await database.OpenAsync(ct);
        var countyRows = await ReferenceTables.ReplaceAsync(connection, "county", countyTable, options.MinRowRatio, ct);
        var centroidRows = await ReferenceTables.ReplaceAsync(connection, "zip_centroid", centroidTable, options.MinRowRatio, ct);
        await SaveVersionAsync(connection, Gazetteer, version, baseUrl, countyRows + centroidRows, ct);
        log.Information("Census {Year} Gazetteer: {Counties:N0} counties ({Fallback} from the 2020 codes file), {Centroids:N0} ZIP centroids",
            year, countyRows, counties.Count - gazetteerCounties.Count, centroidRows);
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    private async Task<Stream> OpenZippedTextAsync(Uri url, CancellationToken ct)
    {
        var zip = new ZipArchive(new MemoryStream(await http.GetByteArrayAsync(url, ct)), ZipArchiveMode.Read);
        var entry = zip.Entries.SingleOrDefault(e => e.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{url} should contain exactly one .txt file.");
        var buffer = new MemoryStream();
        await using (var s = entry.Open())
        {
            await s.CopyToAsync(buffer, ct);
        }

        buffer.Position = 0;
        return buffer;
    }

    /// <summary>True (after recording the check) when <paramref name="version"/> is already loaded and no reload is forced.</summary>
    private async Task<bool> IsCurrentAsync(string source, string version, bool force, CancellationToken ct)
    {
        var stored = await GetVersionAsync(source, ct);
        if (force || stored is null || stored.Version != version)
        {
            return false;
        }

        await using var connection = await database.OpenAsync(ct);
        await Database.ExecuteAsync(connection, "UPDATE `reference_data` SET `checked_at` = @now WHERE `source` = @source", ct,
            param: new { now = UtcNow, source });
        log.Information("Reference data {Source} {Version} is current", source, version);
        return true;
    }

    private async Task<StoredVersion?> GetVersionAsync(string source, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<StoredVersion>(new CommandDefinition(
            "SELECT `version` AS Version, `checked_at` AS CheckedAt FROM `reference_data` WHERE `source` = @source", new { source }, cancellationToken: ct));
    }

    private Task SaveVersionAsync(MySqlConnection connection, string source, string version, Uri url, int rows, CancellationToken ct) =>
        Database.ExecuteAsync(connection,
            """
            INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
            VALUES (@source, @version, @url, @rows, @now, @now) AS new
            ON DUPLICATE KEY UPDATE `version` = new.`version`, `source_url` = new.`source_url`, `rows_loaded` = new.`rows_loaded`,
              `loaded_at` = new.`loaded_at`, `checked_at` = new.`checked_at`
            """, ct, param: new { source, version, url = url.ToString(), rows, now = UtcNow });
}
