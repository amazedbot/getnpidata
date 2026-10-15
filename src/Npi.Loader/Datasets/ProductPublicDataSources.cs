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
    protected static Task<List<(string Slug, string Kind, string Query)>> CandidatesAsync(MySqlConnection connection, string candidatesSql, string cache,
        TimeSpan maxAge, int limit, CancellationToken ct) =>
        CandidatesAsync(connection, candidatesSql, cache, DateTime.UtcNow - maxAge, DateTime.UtcNow - maxAge, limit, ct);

    /// <summary>
    /// The products to ask about: never asked, asked under another name, or asked before <paramref name="drugsBefore"/> (drugs
    /// and biologicals) or <paramref name="devicesBefore"/> (devices); stalest first, at most <paramref name="limit"/>.
    /// </summary>
    protected static async Task<List<(string Slug, string Kind, string Query)>> CandidatesAsync(MySqlConnection connection, string candidatesSql, string cache,
        DateTime drugsBefore, DateTime devicesBefore, int limit, CancellationToken ct) =>
        (await connection.QueryAsync<(string Slug, string Kind, string Query)>(new CommandDefinition(
            $"""
            SELECT c.`slug`, c.`kind`, c.`query_name` FROM ({candidatesSql}) c
            LEFT JOIN `{cache}` x ON x.`slug` = c.`slug`
            WHERE c.`query_name` IS NOT NULL AND CHAR_LENGTH(c.`query_name`) >= 3
              AND (x.`slug` IS NULL OR x.`fetched_at` < IF(c.`kind` = 'drug', @drugsBefore, @devicesBefore) OR x.`query_name` <> c.`query_name`)
            ORDER BY x.`fetched_at` IS NOT NULL, x.`fetched_at`, c.`amount` DESC
            LIMIT @limit
            """, new { drugsBefore, devicesBefore, limit }, cancellationToken: ct))).ToList();

    /// <summary>
    /// GET a JSON answer; null when the API says there's nothing (404); throws <see cref="RateLimitedException"/> on 429 and
    /// <see cref="RejectedException"/> when the API refuses the question (400: a name it can't search).
    /// </summary>
    protected static async Task<JsonDocument?> GetJsonAsync(DatasetContext context, string url, CancellationToken ct)
    {
        // A server error (openFDA answers 500 now and then) or a dropped connection is retried twice, then the product is
        // skipped until the next run.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await GetOnceAsync(context, url, ct);
            }
            catch (UnavailableException) when (attempt < 3)
            {
                await Task.Delay(context.Options.ApiRetryDelay * attempt, ct);
            }
        }
    }

    private static async Task<JsonDocument?> GetOnceAsync(DatasetContext context, string url, CancellationToken ct)
    {
        HttpResponseMessage answer;
        try
        {
            answer = await context.Http.GetAsync(url, ct);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new UnavailableException(); // no connection, or HttpClient's timeout
        }

        using var response = answer;
        if ((int)response.StatusCode >= 500)
        {
            throw new UnavailableException();
        }

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

    protected sealed class UnavailableException : Exception;

    /// <summary>How a run of an API cache ended.</summary>
    internal enum Stop
    {
        /// <summary>Every product was asked.</summary>
        Finished,

        /// <summary>The API answered 429: the rest wait for the next run.</summary>
        RateLimited,

        /// <summary><see cref="LoaderOptions.ApiFailuresInARow"/> products in a row kept failing: the rest wait for the next run.</summary>
        KeptFailing,
    }

    /// <summary>
    /// Asks about every item, up to <paramref name="parallel"/> at once (the pace is set inside <paramref name="ask"/>), and
    /// saves each answer as soon as it comes, one save at a time, so a stopped run keeps what it saved. <paramref name="ask"/>
    /// returns null when the API kept failing for that item (asked again on the next run). A rate limit (429), or
    /// <paramref name="failuresInARow"/> items in a row that kept failing, stops the run early without failing it: no new
    /// item starts, the requests in flight are cancelled and their items wait for the next run, like the rest.
    /// </summary>
    internal static async Task<(long Saved, Stop Stop)> AskAllAsync<TItem, TAnswer>(IReadOnlyList<TItem> items, int parallel, int failuresInARow,
        Func<TItem, CancellationToken, Task<TAnswer?>> ask, Func<TItem, TAnswer, CancellationToken, Task> save, CancellationToken ct)
        where TAnswer : class
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var write = new SemaphoreSlim(1, 1);
        var saved = 0L;
        var failing = 0;
        var stop = (int)Stop.Finished;

        void StopWith(Stop reason)
        {
            Interlocked.CompareExchange(ref stop, (int)reason, (int)Stop.Finished);
            stopping.Cancel();
        }

        try
        {
            await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = stopping.Token },
                async (item, token) =>
                {
                    TAnswer? answer;
                    try
                    {
                        answer = await ask(item, token);
                    }
                    catch (RateLimitedException)
                    {
                        StopWith(Stop.RateLimited);
                        return;
                    }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        return; // stopped while in flight: asked again on the next run
                    }

                    if (answer is null)
                    {
                        if (Interlocked.Increment(ref failing) >= Math.Max(1, failuresInARow))
                        {
                            StopWith(Stop.KeptFailing);
                        }

                        return;
                    }

                    Interlocked.Exchange(ref failing, 0);
                    await write.WaitAsync(ct); // the run's own token: a save under way finishes even when the run stops
                    try
                    {
                        await save(item, answer, ct);
                        saved++;
                    }
                    finally
                    {
                        write.Release();
                    }
                });
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Stopped early: Parallel.ForEachAsync started no new item after that.
        }

        return (saved, (Stop)stop);
    }

    /// <summary>Says why a run stopped early (a warning, not a failure: the rest are asked on the next run).</summary>
    private protected static void LogStop(DatasetContext context, string api, Stop stop, long done)
    {
        if (stop == Stop.RateLimited)
        {
            context.Log.Warning("{Api} rate limit reached after {Done:N0} products; the rest continue on the next run", api, done);
        }
        else if (stop == Stop.KeptFailing)
        {
            context.Log.Warning("{Api} kept failing for {Failures} products in a row after {Done:N0} were saved; stopped early, the rest continue on the next run",
                api, context.Options.ApiFailuresInARow, done);
        }
    }

    /// <summary>
    /// Spaces requests, however many run at once: each <see cref="StartAsync"/> waits until <c>interval</c> after the
    /// previous request started, or, with <c>fromEnd</c> (one at a time), after it ended (dispose the result when it ends).
    /// </summary>
    internal sealed class Pacer(TimeSpan interval, bool fromEnd = false) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Lock _lock = new();
        private readonly TimeSpan _interval = interval;
        private DateTime _next = DateTime.MinValue;

        public async Task<IDisposable> StartAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                TimeSpan wait;
                lock (_lock)
                {
                    wait = _next - DateTime.UtcNow;
                }

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                }

                if (!fromEnd)
                {
                    lock (_lock)
                    {
                        _next = DateTime.UtcNow + _interval;
                    }
                }
            }
            finally
            {
                _gate.Release();
            }

            return fromEnd ? new Ended(this) : NoEnd.Instance;
        }

        public void Dispose() => _gate.Dispose();

        private void End()
        {
            lock (_lock)
            {
                _next = DateTime.UtcNow + _interval;
            }
        }

        private sealed class Ended(Pacer pacer) : IDisposable
        {
            public void Dispose() => pacer.End();
        }

        private sealed class NoEnd : IDisposable
        {
            public static readonly NoEnd Instance = new();

            public void Dispose()
            {
            }
        }
    }

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
/// brand name, deaths, injuries and malfunctions. A product is asked again only after openFDA exports new reports (FAERS
/// and MAUDE each have an export date in download.json), so the version is those dates, plus the day while products are
/// still waiting (never asked, renamed, or a run ended at its budget). Without <see cref="LoaderOptions.OpenFdaApiKey"/>
/// openFDA allows 1,000 requests a day, so a run makes at most <see cref="LoaderOptions.OpenFdaRequestsPerRun"/>, one at a
/// time; with the key, up to <see cref="LoaderOptions.OpenFdaParallelRequests"/> at once, one started every
/// <see cref="LoaderOptions.OpenFdaKeyedRequestInterval"/> (openFDA allows 240 a minute per key).
/// </summary>
public sealed class ProductAdverseEventSource : ProductApiCacheSource
{
    public override string Name => "product_adverse_events";

    // The products asked about come from op_product, their names from the FDA tables.
    public override IReadOnlyList<string> After => ["open_payments", "fda_products"];

    protected override TimeSpan MaxAge => TimeSpan.FromDays(7); // not used: openFDA's export dates decide

    /// <summary>openFDA's export dates of the drug (FAERS) and device (MAUDE) adverse event reports.</summary>
    private static async Task<(DateTime Drugs, DateTime Devices)> ExportDatesAsync(DatasetContext context, CancellationToken ct)
    {
        await using var stream = await context.Http.GetStreamAsync(new Uri(context.Options.FdaDownloadIndexUrl), ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var results = doc.RootElement.GetProperty("results");
        DateTime Date(string category) =>
            DateTime.ParseExact(results.GetProperty(category).GetProperty("event").GetProperty("export_date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return (Date("drug"), Date("device"));
    }

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var (drugs, devices) = await ExportDatesAsync(context, ct);
        await using var connection = await context.Database.OpenAsync(ct);
        var waiting = (await CandidatesAsync(connection, Products, "product_adverse_events", drugs, devices, 1, ct)).Count > 0;
        var version = FormattableString.Invariant($"faers {drugs:yyyy-MM-dd} | maude {devices:yyyy-MM-dd}");
        return new DatasetRelease(waiting ? FormattableString.Invariant($"{version} | {DateTime.UtcNow:yyyy-MM-dd}") : version, new Uri("https://open.fda.gov/"));
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        await using var connection = await context.Database.OpenAsync(ct);
        var keyed = !string.IsNullOrWhiteSpace(context.Options.OpenFdaApiKey);
        var budget = keyed ? context.Options.OpenFdaRequestsPerRunWithKey : context.Options.OpenFdaRequestsPerRun;
        var key = keyed ? "&api_key=" + Uri.EscapeDataString(context.Options.OpenFdaApiKey) : "";
        // The export dates FindLatestAsync put in the version ("faers yyyy-MM-dd | maude yyyy-MM-dd[ | day]"), so download.json isn't read again.
        var parts = release.Version.Split(" | ");
        var (drugsExported, devicesExported) = (Exported(parts[0], "faers "), Exported(parts[1], "maude "));
        var candidates = await CandidatesAsync(connection, Products, "product_adverse_events", drugsExported, devicesExported, budget, ct);
        var parallel = keyed ? Math.Max(1, context.Options.OpenFdaParallelRequests) : 1;
        context.Log.Information("Adverse event counts: {Count:N0} products to ask (FAERS {Drugs:yyyy-MM-dd}, MAUDE {Devices:yyyy-MM-dd}), {Parallel} at a time",
            candidates.Count, drugsExported, devicesExported, parallel);

        using var pacer = new Pacer(keyed ? context.Options.OpenFdaKeyedRequestInterval : context.Options.ApiRequestDelay);
        var (done, stop) = await AskAllAsync(candidates, parallel, context.Options.ApiFailuresInARow,
            async (candidate, token) =>
            {
                using (await pacer.StartAsync(token))
                {
                    return await AskAsync(context, candidate.Kind, candidate.Query, key, token);
                }
            },
            (candidate, counts, token) => connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO `product_adverse_events` (`slug`, `kind`, `query_name`, `reports`, `serious`, `deaths`, `injuries`, `malfunctions`, `fetched_at`)
                VALUES (@slug, @kind, @query, @reports, @serious, @deaths, @injuries, @malfunctions, UTC_TIMESTAMP())
                ON DUPLICATE KEY UPDATE `kind` = VALUES(`kind`), `query_name` = VALUES(`query_name`), `reports` = VALUES(`reports`), `serious` = VALUES(`serious`),
                  `deaths` = VALUES(`deaths`), `injuries` = VALUES(`injuries`), `malfunctions` = VALUES(`malfunctions`), `fetched_at` = VALUES(`fetched_at`)
                """,
                new
                {
                    slug = candidate.Slug, kind = candidate.Kind, query = CompanyRecords.Trim(candidate.Query, 500), reports = counts.Reports, serious = counts.Serious,
                    deaths = counts.Deaths, injuries = counts.Injuries, malfunctions = counts.Malfunctions,
                }, cancellationToken: token)),
            ct);
        LogStop(context, "openFDA", stop, done);
        context.Log.Information("Adverse event counts: {Done:N0} products asked", done);
        return done;
    }

    /// <summary>One product's counts; all null when openFDA refused the name (stored as unknown).</summary>
    private sealed record Counts(int? Reports, int? Serious, int? Deaths, int? Injuries, int? Malfunctions);

    private static DateTime Exported(string part, string prefix) =>
        DateTime.ParseExact(part[prefix.Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// One product's counts: all null when openFDA refuses the name (stored as unknown), null when it kept failing; a rate
    /// limit (429) is thrown and stops the run.
    /// </summary>
    private static async Task<Counts?> AskAsync(DatasetContext context, string kind, string query, string key, CancellationToken ct)
    {
        int? reports = null, serious = null, deaths = null, injuries = null, malfunctions = null;
        {
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
            catch (RejectedException)
            {
                context.Log.Debug("openFDA refused the search for {Query}; stored as unknown", query);
            }
            catch (UnavailableException)
            {
                context.Log.Warning("openFDA kept failing for {Query}; asked again on the next run", query);
                return null;
            }
        }

        return new Counts(reports, serious, deaths, injuries, malfunctions);
    }

    // count= answers: {"results": [{"term": "1", "count": 123}, …]}; none (404) → empty.
    private static Dictionary<string, int> Terms(JsonDocument? doc) =>
        doc is null || !doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array
            ? []
            : results.EnumerateArray().GroupBy(r => r.GetProperty("term").ToString()).ToDictionary(g => g.Key, g => g.Sum(r => r.GetProperty("count").GetInt32()));
}

/// <summary>
/// ClinicalTrials.gov study counts (part 3) for drugs and biologicals, by intervention name: their generic name from
/// FDA's NDC directory, else the product name; all studies and those recruiting now. Up to
/// <see cref="LoaderOptions.ClinicalTrialsParallelRequests"/> products at once, one request started every
/// <see cref="LoaderOptions.ClinicalTrialsRequestInterval"/> (200 ms, at most 5 a second; speed item 11). ClinicalTrials.gov
/// publishes no rate limit for API v2: neither https://clinicaltrials.gov/data-api/about-api nor the OpenAPI document at
/// https://clinicaltrials.gov/api/oas/v2 names one (checked 2026-10-15), and its answers carry no rate-limit headers.
/// Reports by other clients put a practical ceiling near 50 a minute per address, yet the one-at-a-time loop ran at
/// ~2.5 a second for months without a 429, so 5 a second is about twice that pace, well below what a busy public API
/// serves, and a 429 still ends the run early.
/// </summary>
public sealed class ProductTrialSource : ProductApiCacheSource
{
    public override string Name => "product_trials";

    // The products asked about come from op_product, their names from the FDA tables.
    public override IReadOnlyList<string> After => ["open_payments", "fda_products"];

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
        var candidates = await CandidatesAsync(connection, Drugs, "product_trials", MaxAge, context.Options.ClinicalTrialsPerRun, ct);
        context.Log.Information("ClinicalTrials.gov counts: {Count:N0} products to ask, {Parallel} at a time", candidates.Count, ParallelRequests(context.Options));
        var done = await AskProductsAsync(context, candidates.Select(c => (c.Slug, c.Query)).ToList(),
            (slug, query, counts, token) => connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO `product_trials` (`slug`, `query_name`, `studies`, `recruiting`, `fetched_at`) VALUES (@slug, @query, @studies, @recruiting, UTC_TIMESTAMP())
                ON DUPLICATE KEY UPDATE `query_name` = VALUES(`query_name`), `studies` = VALUES(`studies`), `recruiting` = VALUES(`recruiting`), `fetched_at` = VALUES(`fetched_at`)
                """, new { slug, query = CompanyRecords.Trim(query, 500), studies = counts.Studies, recruiting = counts.Recruiting }, cancellationToken: token)),
            ct);
        context.Log.Information("ClinicalTrials.gov counts: {Done:N0} products asked", done);
        return done;
    }

    /// <summary>A product's study counts: all and recruiting now; both null when ClinicalTrials.gov refused the name (stored as unknown).</summary>
    internal sealed record Counts(int? Studies, int? Recruiting);

    private static int ParallelRequests(LoaderOptions options) => Math.Max(1, options.ClinicalTrialsParallelRequests);

    /// <summary>
    /// Asks ClinicalTrials.gov about each product (slug, intervention name), two requests each, and hands every answer to
    /// <paramref name="save"/> as it comes (one save at a time, in the order they finish). Up to
    /// <see cref="LoaderOptions.ClinicalTrialsParallelRequests"/> products at once, one request started every
    /// <see cref="LoaderOptions.ClinicalTrialsRequestInterval"/>; with 1, one request at a time with
    /// <see cref="LoaderOptions.ApiRequestDelay"/> after each, as before. A 429, or <see cref="LoaderOptions.ApiFailuresInARow"/>
    /// products in a row that kept failing, ends the run early: the rest are asked on the next one. Returns the products saved.
    /// </summary>
    internal static async Task<long> AskProductsAsync(DatasetContext context, IReadOnlyList<(string Slug, string Query)> products,
        Func<string, string, Counts, CancellationToken, Task> save, CancellationToken ct)
    {
        var options = context.Options;
        var parallel = ParallelRequests(options);
        using var pacer = parallel == 1 ? new Pacer(options.ApiRequestDelay, fromEnd: true) : new Pacer(options.ClinicalTrialsRequestInterval);

        async Task<int?> TotalAsync(string url, CancellationToken token)
        {
            using (await pacer.StartAsync(token))
            {
                return Total(await GetJsonAsync(context, url, token));
            }
        }

        var (done, stop) = await AskAllAsync(products, parallel, options.ApiFailuresInARow,
            async (product, token) =>
            {
                try
                {
                    var term = Uri.EscapeDataString(Clean(product.Query));
                    var studies = await TotalAsync($"{options.ClinicalTrialsApiUrl}?query.intr={term}&countTotal=true&pageSize=1&fields=NCTId", token);
                    var recruiting = await TotalAsync($"{options.ClinicalTrialsApiUrl}?query.intr={term}&filter.overallStatus=RECRUITING&countTotal=true&pageSize=1&fields=NCTId", token);
                    return new Counts(studies, recruiting);
                }
                catch (RejectedException)
                {
                    context.Log.Debug("ClinicalTrials.gov refused the search for {Query}; stored as unknown", product.Query);
                    return new Counts(null, null);
                }
                catch (UnavailableException)
                {
                    context.Log.Warning("ClinicalTrials.gov kept failing for {Query}; asked again on the next run", product.Query);
                    return null;
                }
            },
            (product, counts, token) => save(product.Slug, product.Query, counts, token),
            ct);
        LogStop(context, "ClinicalTrials.gov", stop, done);
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
