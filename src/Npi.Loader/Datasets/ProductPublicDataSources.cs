using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Npi.Loader.Csv;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// CMS spending by drug (CLAUDE.md §7 Stage 5.5 item 19, part 3): Medicare Part D, Medicare Part B and Medicaid, per
/// brand name and year (the five years each release covers), from the data.cms.gov catalog. Part D and Medicaid use
/// the "Overall" (all manufacturers) rows; Part B is summed over the brand's HCPCS codes (CMS marks some brand names
/// with a trailing "*", dropped here).
/// </summary>
public sealed partial class DrugSpendingSource : DatasetSource
{
    public static readonly (string Program, string Title)[] Files =
    [
        ("Part D", "Medicare Part D Spending by Drug"),
        ("Part B", "Medicare Part B Spending by Drug"),
        ("Medicaid", "Medicaid Spending by Drug"),
    ];

    public override string Name => "drug_spending";

    [GeneratedRegex(@"^Tot_Spndng_(\d{4})$")]
    private static partial Regex SpendingYear();

    private static async Task<List<(string Program, DatasetRelease Release)>> FindAsync(DatasetContext context, CancellationToken ct)
    {
        var catalog = await context.CmsCatalogAsync(ct);
        return Files.Select(f => (f.Program, catalog.FindLatest(f.Title) ?? throw new InvalidDataException($"The CMS catalog has no \"{f.Title}\" CSV."))).ToList();
    }

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var files = await FindAsync(context, ct);
        return new DatasetRelease(string.Join(" | ", files.Select(f => $"{f.Program} {f.Release.PeriodEnd}")), files[0].Release.Url);
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var files = await FindAsync(context, ct);
        await using var connection = await context.Database.OpenAsync(ct);
        const string raw = "drug_spending_raw_staging";
        try
        {
            var counts = await TableSwap.ReplaceAsync(connection, ["drug_spending"], context.Options.MinRowRatio, async () =>
            {
                foreach (var (program, file) in files)
                {
                    var path = await context.DownloadAsync(file, $"cms_spending_{program.Replace(' ', '_')}.csv", ct);
                    try
                    {
                        IReadOnlyList<string> header;
                        await using (var stream = File.OpenRead(path))
                        {
                            header = CsvHeader.Read(stream).Columns.Select(c => c.Trim().TrimStart('﻿')).ToList();
                        }

                        // The five newest years, each in a slot; a year without a column (e.g. Medicaid has no beneficiaries) stays NULL.
                        var years = header.Select(h => SpendingYear().Match(h)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                            .Distinct().Order().TakeLast(5).ToList();
                        var columns = new List<CsvColumn>
                        {
                            new("Brnd_Name", "brand_name"), new("Gnrc_Name", "generic_name"),
                        };
                        if (header.Contains("Mftr_Name"))
                        {
                            columns.Add(new("Mftr_Name", "manufacturer"));
                        }

                        if (header.Contains("HCPCS_Cd"))
                        {
                            columns.Add(new("HCPCS_Cd", "hcpcs"));
                        }

                        for (var i = 0; i < years.Count; i++)
                        {
                            var slot = i + 1;
                            foreach (var (prefix, column) in new[] { ("Tot_Spndng_", "spending"), ("Tot_Dsg_Unts_", "units"), ("Tot_Clms_", "claims"), ("Tot_Benes_", "benes") })
                            {
                                if (header.Contains(prefix + years[i]))
                                {
                                    columns.Add(new(prefix + years[i], column + slot, CsvValue.OptionalNumber));
                                }
                            }
                        }

                        await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                        await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` LIKE `drug_spending_raw`", ct);
                        var rows = await CsvTableLoader.LoadAsync(connection, path, raw, columns, ct);
                        var overall = header.Contains("Mftr_Name") ? "AND `manufacturer` = 'Overall'" : "";
                        var selects = years.Select((year, i) =>
                            $"""
                            SELECT LEFT(TRIM(TRAILING '*' FROM TRIM(`brand_name`)), 255) AS `brand`, LEFT(TRIM(TRAILING '*' FROM TRIM(COALESCE(`generic_name`, ''))), 255) AS `generic`,
                              {year} AS `year`, `spending{i + 1}` AS `spending`, `units{i + 1}` AS `units`, `claims{i + 1}` AS `claims`, `benes{i + 1}` AS `benes`
                            FROM `{raw}` WHERE `brand_name` IS NOT NULL AND `spending{i + 1}` IS NOT NULL {overall}
                            """);
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `drug_spending_staging` (`program`, `brand_name`, `generic_name`, `year`, `spending`, `units`, `claims`, `beneficiaries`)
                            SELECT @program, `brand`, `generic`, `year`, ROUND(SUM(`spending`), 2), SUM(`units`), SUM(`claims`), SUM(`benes`)
                            FROM ({string.Join("\nUNION ALL\n", selects)}) x
                            GROUP BY `brand`, `generic`, `year`
                            """, ct, param: new { program });
                        context.Log.Information("{Program} spending by drug: {Rows:N0} rows, years {First}–{Last}", program, rows, years.FirstOrDefault(), years.LastOrDefault());
                    }
                    finally
                    {
                        File.Delete(path);
                    }
                }
            }, ct);
            return counts["drug_spending"];
        }
        finally
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
        }
    }
}

/// <summary>
/// NADAC, the National Average Drug Acquisition Cost (Medicaid's weekly survey of what pharmacies pay), from the newest
/// "NADAC (National Average Drug Acquisition Cost) &lt;year&gt;" file in data.medicaid.gov's catalog: the newest price of
/// each package NDC.
/// </summary>
public sealed partial class NadacSource : DatasetSource
{
    public override string Name => "nadac";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    [GeneratedRegex(@"^NADAC \(National Average Drug Acquisition Cost\) (?<year>\d{4})$")]
    private static partial Regex Title();

    private static readonly CsvColumn[] Columns =
    [
        new("NDC Description", "description"), new("NDC", "ndc"), new("NADAC Per Unit", "per_unit", CsvValue.OptionalNumber),
        new("Effective Date", "effective_date", CsvValue.DateMdy), new("Pricing Unit", "pricing_unit"), new("OTC", "otc"),
        new("Classification for Rate Setting", "classification"), new("As of Date", "as_of", CsvValue.DateMdy),
    ];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        await using var stream = await context.Http.GetStreamAsync(new Uri(context.Options.MedicaidCatalogUrl), ct);
        return OpenPaymentsSource.ParseCatalog(stream, Title(), "NADAC (National Average Drug Acquisition Cost)");
    }

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "nadac.csv", async (connection, path) =>
        {
            const string raw = "nadac_raw_staging";
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` LIKE `nadac_raw`", ct);
                var rows = await CsvTableLoader.LoadAsync(connection, path, raw, Columns, ct);
                context.Log.Information("NADAC: {Rows:N0} weekly prices", rows);
                var counts = await TableSwap.ReplaceAsync(connection, ["nadac"], context.Options.MinRowRatio, () =>
                    Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `nadac_staging` (`ndc`, `description`, `per_unit`, `pricing_unit`, `effective_date`, `classification`, `otc`, `as_of`)
                        SELECT `ndc`, `description`, `per_unit`, `pricing_unit`, `effective_date`, `classification`, `otc`, `as_of`
                        FROM (
                          SELECT r.*, ROW_NUMBER() OVER (PARTITION BY `ndc` ORDER BY `as_of` DESC, `effective_date` DESC) AS `rn`
                          FROM `{raw}` r WHERE CHAR_LENGTH(`ndc`) = 11 AND `ndc` REGEXP '^[0-9]+$' AND `per_unit` IS NOT NULL
                        ) x WHERE `rn` = 1
                        """, ct), ct);
                return counts["nadac"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }, ct);
}

/// <summary>FDA's drug shortage list (openFDA drug/shortages): every listed shortage with its products' NDCs.</summary>
public sealed class DrugShortageSource : DatasetSource
{
    public override string Name => "fda_shortages";

    private static async Task<(Uri Url, string ExportDate)> FindAsync(DatasetContext context, CancellationToken ct)
    {
        await using var stream = await context.Http.GetStreamAsync(new Uri(context.Options.FdaDownloadIndexUrl), ct);
        using var doc = JsonDocument.Parse(stream);
        var e = doc.RootElement.GetProperty("results").GetProperty("drug").GetProperty("shortages");
        return (new Uri(e.GetProperty("partitions")[0].GetProperty("file").GetString()!), e.GetProperty("export_date").GetString() ?? "");
    }

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var (url, exported) = await FindAsync(context, ct);
        return new DatasetRelease($"shortages {exported}", url);
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var (url, _) = await FindAsync(context, ct);
        var zipPath = await context.DownloadAsync(new DatasetRelease(release.Version, url), "fda_shortages.zip", ct);
        var shortages = new List<object>();
        var keys = new HashSet<(string Key, int Id)>();
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            await using var json = zip.Entries.Single(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Open();
            var id = 0;
            await foreach (var r in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(new JsonArrayStream(json, "results"), cancellationToken: ct))
            {
                id++;
                var openfda = FdaProductSource.Obj(r, "openfda");
                shortages.Add(new
                {
                    id, generic = CompanyRecords.Trim(FdaProductSource.Str(r, "generic_name"), 500),
                    substance = CompanyRecords.Trim(FdaProductSource.Str(openfda, "substance_name") ?? FdaProductSource.Str(openfda, "generic_name"), 500),
                    company = CompanyRecords.Trim(FdaProductSource.Str(r, "company_name"), 255), presentation = CompanyRecords.Trim(FdaProductSource.Str(r, "presentation"), 1000),
                    status = CompanyRecords.Trim(FdaProductSource.Str(r, "status"), 40), availability = CompanyRecords.Trim(FdaProductSource.Str(r, "availability"), 100),
                    reason = CompanyRecords.Trim(FdaProductSource.Str(r, "shortage_reason"), 500), related = CompanyRecords.Trim(FdaProductSource.Str(r, "related_info"), 2000),
                    initial = Mdy(FdaProductSource.Str(r, "initial_posting_date")), updated = Mdy(FdaProductSource.Str(r, "update_date")),
                });
                foreach (var key in FdaProductSource.Strings(openfda, "product_ndc").Concat(FdaProductSource.Strings(openfda, "package_ndc"))
                             .Append(FdaProductSource.Str(r, "package_ndc") ?? "").Select(FdaProductSource.NdcKey).OfType<string>())
                {
                    keys.Add((key, id));
                }
            }
        }
        finally
        {
            File.Delete(zipPath);
        }

        await using var connection = await context.Database.OpenAsync(ct);
        var counts = await TableSwap.ReplaceAsync(connection, ["drug_shortage", "drug_shortage_ndc"], context.Options.MinRowRatio, async () =>
        {
            await CompanyRecords.InsertAsync(connection,
                """
                INSERT INTO `drug_shortage_staging` (`id`, `generic_name`, `substance`, `company`, `presentation`, `status`, `availability`, `reason`, `related_info`,
                  `initial_date`, `update_date`)
                VALUES (@id, @generic, @substance, @company, @presentation, @status, @availability, @reason, @related, @initial, @updated)
                """, shortages, ct);
            await CompanyRecords.InsertAsync(connection, "INSERT INTO `drug_shortage_ndc_staging` (`ndc_key`, `shortage_id`) VALUES (@Key, @Id)",
                keys.Select(k => new { k.Key, k.Id }), ct);
        }, ct);
        return counts["drug_shortage"];
    }

    private static DateTime? Mdy(string? value) =>
        DateTime.TryParseExact(value, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>
/// A cache filled from a public API a slice at a time (part 3): each run asks about the products never asked or asked
/// longest ago (older than <see cref="MaxAge"/>), up to a budget, and stops early at a rate limit; the rows are upserted,
/// never swapped. The version is the day, so <c>run</c> continues it daily.
/// </summary>
public abstract class ProductApiCacheSource : DatasetSource
{
    protected abstract TimeSpan MaxAge { get; }

    public override TimeSpan CheckInterval => TimeSpan.FromHours(20);

    public override Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
        Task.FromResult(new DatasetRelease(DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), new Uri("https://open.fda.gov/")));

    /// <summary>The products to ask about (slug, kind, query name), stalest first, at most <paramref name="limit"/>.</summary>
    protected static async Task<List<(string Slug, string Kind, string Query)>> CandidatesAsync(MySqlConnection connection, string candidatesSql, string cache,
        TimeSpan maxAge, int limit, CancellationToken ct) =>
        (await connection.QueryAsync<(string Slug, string Kind, string Query)>(new CommandDefinition(
            $"""
            SELECT c.`slug`, c.`kind`, c.`query_name` FROM ({candidatesSql}) c
            LEFT JOIN `{cache}` x ON x.`slug` = c.`slug`
            WHERE c.`query_name` IS NOT NULL AND CHAR_LENGTH(c.`query_name`) >= 3
              AND (x.`slug` IS NULL OR x.`fetched_at` < @before OR x.`query_name` <> c.`query_name`)
            ORDER BY x.`fetched_at` IS NOT NULL, x.`fetched_at`, c.`amount` DESC
            LIMIT @limit
            """, new { before = DateTime.UtcNow - maxAge, limit }, cancellationToken: ct))).ToList();

    /// <summary>
    /// GET a JSON answer; null when the API says there's nothing (404); throws <see cref="RateLimitedException"/> on 429 and
    /// <see cref="RejectedException"/> when the API refuses the question (400: a name it can't search).
    /// </summary>
    protected static async Task<JsonDocument?> GetJsonAsync(DatasetContext context, string url, CancellationToken ct)
    {
        using var response = await context.Http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new RejectedException();
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new RateLimitedException();
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    protected sealed class RateLimitedException : Exception;

    protected sealed class RejectedException : Exception;

    /// <summary>A name the APIs can search: letters, digits, spaces and - . / + only, spaces collapsed.</summary>
    public static string Clean(string name) =>
        string.Join(' ', new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '/' or '+' ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A name as a quoted search phrase: double quotes and backslashes removed.</summary>
    protected static string Phrase(string name) => Uri.EscapeDataString("\"" + Clean(name) + "\"");

    // Drugs and biologicals by their FDA brand name (else the product name); devices by their GUDID brand name.
    protected const string Products =
        """
        SELECT p.`slug`, IF(p.`kind` IN ('Drug', 'Biological'), 'drug', 'device') AS `kind`, p.`amount`,
          CASE WHEN p.`kind` IN ('Drug', 'Biological') THEN COALESCE((SELECT MIN(n.`brand_name`) FROM `fda_ndc_product` n WHERE n.`ndc_key` = p.`ndc_key`), p.`name`)
               ELSE (SELECT f.`brand_name` FROM `fda_device` f WHERE f.`device_id` = TRIM(p.`device_id`)) END AS `query_name`
        FROM `op_product` p
        """;
}

/// <summary>
/// Adverse event report counts from openFDA (part 3): drugs and biologicals in FAERS by the product name as reported
/// (<c>patient.drug.medicinalproduct</c>; FDA's brand annotation finds nothing), total and serious; devices in MAUDE by
/// brand name, deaths, injuries and malfunctions. Without <see cref="LoaderOptions.OpenFdaApiKey"/> openFDA allows 1,000
/// requests a day, so a run makes at most <see cref="LoaderOptions.OpenFdaRequestsPerRun"/>.
/// </summary>
public sealed class ProductAdverseEventSource : ProductApiCacheSource
{
    public override string Name => "product_adverse_events";

    protected override TimeSpan MaxAge => TimeSpan.FromDays(7);

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        await using var connection = await context.Database.OpenAsync(ct);
        var budget = string.IsNullOrWhiteSpace(context.Options.OpenFdaApiKey) ? context.Options.OpenFdaRequestsPerRun : context.Options.OpenFdaRequestsPerRunWithKey;
        var key = string.IsNullOrWhiteSpace(context.Options.OpenFdaApiKey) ? "" : "&api_key=" + Uri.EscapeDataString(context.Options.OpenFdaApiKey);
        var done = 0L;
        foreach (var (slug, kind, query) in await CandidatesAsync(connection, Products, "product_adverse_events", MaxAge, budget, ct))
        {
            int? reports = null, serious = null, deaths = null, injuries = null, malfunctions = null;
            try
            {
                if (kind == "drug")
                {
                    using var doc = await GetJsonAsync(context, $"{context.Options.OpenFdaApiUrl}drug/event.json?search=patient.drug.medicinalproduct:{Phrase(query)}&count=serious{key}", ct);
                    var terms = Terms(doc);
                    reports = terms.Values.Sum();
                    serious = terms.GetValueOrDefault("1");
                }
                else
                {
                    using var doc = await GetJsonAsync(context, $"{context.Options.OpenFdaApiUrl}device/event.json?search=device.brand_name:{Phrase(query)}&count=event_type.exact{key}", ct);
                    var terms = Terms(doc);
                    reports = terms.Values.Sum();
                    deaths = terms.GetValueOrDefault("Death");
                    injuries = terms.GetValueOrDefault("Injury");
                    malfunctions = terms.GetValueOrDefault("Malfunction");
                }
            }
            catch (RateLimitedException)
            {
                context.Log.Warning("openFDA rate limit reached after {Done:N0} products; the rest continue on the next run", done);
                break;
            }
            catch (RejectedException)
            {
                context.Log.Debug("openFDA refused the search for {Query}; stored as unknown", query);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO `product_adverse_events` (`slug`, `kind`, `query_name`, `reports`, `serious`, `deaths`, `injuries`, `malfunctions`, `fetched_at`)
                VALUES (@slug, @kind, @query, @reports, @serious, @deaths, @injuries, @malfunctions, UTC_TIMESTAMP())
                ON DUPLICATE KEY UPDATE `kind` = VALUES(`kind`), `query_name` = VALUES(`query_name`), `reports` = VALUES(`reports`), `serious` = VALUES(`serious`),
                  `deaths` = VALUES(`deaths`), `injuries` = VALUES(`injuries`), `malfunctions` = VALUES(`malfunctions`), `fetched_at` = VALUES(`fetched_at`)
                """, new { slug, kind, query = CompanyRecords.Trim(query, 500), reports, serious, deaths, injuries, malfunctions }, cancellationToken: ct));
            done++;
            await Task.Delay(context.Options.ApiRequestDelay, ct);
        }

        context.Log.Information("Adverse event counts: {Done:N0} products asked", done);
        return done;
    }

    // count= answers: {"results": [{"term": "1", "count": 123}, …]}; none (404) → empty.
    private static Dictionary<string, int> Terms(JsonDocument? doc) =>
        doc is null || !doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array
            ? []
            : results.EnumerateArray().GroupBy(r => r.GetProperty("term").ToString()).ToDictionary(g => g.Key, g => g.Sum(r => r.GetProperty("count").GetInt32()));
}

/// <summary>
/// ClinicalTrials.gov study counts (part 3) for drugs and biologicals, by intervention name: their generic name from
/// FDA's NDC directory, else the product name; all studies and those recruiting now.
/// </summary>
public sealed class ProductTrialSource : ProductApiCacheSource
{
    public override string Name => "product_trials";

    protected override TimeSpan MaxAge => TimeSpan.FromDays(30);

    private const string Drugs =
        """
        SELECT p.`slug`, 'drug' AS `kind`, p.`amount`,
          COALESCE((SELECT MIN(n.`generic_name`) FROM `fda_ndc_product` n WHERE n.`ndc_key` = p.`ndc_key`), p.`name`) AS `query_name`
        FROM `op_product` p WHERE p.`kind` IN ('Drug', 'Biological')
        """;

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        await using var connection = await context.Database.OpenAsync(ct);
        var done = 0L;
        foreach (var (slug, _, query) in await CandidatesAsync(connection, Drugs, "product_trials", MaxAge, context.Options.ClinicalTrialsPerRun, ct))
        {
            int? studies = null, recruiting = null;
            try
            {
                var term = Uri.EscapeDataString(Clean(query));
                studies = Total(await GetJsonAsync(context, $"{context.Options.ClinicalTrialsApiUrl}?query.intr={term}&countTotal=true&pageSize=1&fields=NCTId", ct));
                await Task.Delay(context.Options.ApiRequestDelay, ct);
                recruiting = Total(await GetJsonAsync(context, $"{context.Options.ClinicalTrialsApiUrl}?query.intr={term}&filter.overallStatus=RECRUITING&countTotal=true&pageSize=1&fields=NCTId", ct));
            }
            catch (RateLimitedException)
            {
                context.Log.Warning("ClinicalTrials.gov rate limit reached after {Done:N0} products; the rest continue on the next run", done);
                break;
            }
            catch (RejectedException)
            {
                context.Log.Debug("ClinicalTrials.gov refused the search for {Query}; stored as unknown", query);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO `product_trials` (`slug`, `query_name`, `studies`, `recruiting`, `fetched_at`) VALUES (@slug, @query, @studies, @recruiting, UTC_TIMESTAMP())
                ON DUPLICATE KEY UPDATE `query_name` = VALUES(`query_name`), `studies` = VALUES(`studies`), `recruiting` = VALUES(`recruiting`), `fetched_at` = VALUES(`fetched_at`)
                """, new { slug, query = CompanyRecords.Trim(query, 500), studies, recruiting }, cancellationToken: ct));
            done++;
            await Task.Delay(context.Options.ApiRequestDelay, ct);
        }

        context.Log.Information("ClinicalTrials.gov counts: {Done:N0} products asked", done);
        return done;
    }

    private static int? Total(JsonDocument? doc)
    {
        using (doc)
        {
            return doc is not null && doc.RootElement.TryGetProperty("totalCount", out var t) && t.TryGetInt32(out var n) ? n : doc is null ? 0 : null;
        }
    }
}
