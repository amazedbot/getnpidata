using System.Globalization;
using System.IO.Compression;
using System.Text;
using MySqlConnector;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// HHS's "Medicaid Provider Enrollment Segments" (T-MSIS, opendata.hhs.gov; docs/PRODUCT_PLAN.md step 3.7, owner's OK 2026-10-15):
/// every state's Medicaid and CHIP provider enrollment periods, January 2018 to December 2024, one ~300 MB zip of an 11.5 GB CSV.
/// Read as it unzips and summarized per NPI and state into enrollment periods (<see cref="MedicaidEnrollmentSegments"/>) →
/// <c>medicaid_enrollment_period</c> (staging + RENAME; the NPIs whose periods changed are queued for the record quality) and the
/// months covered → <c>medicaid_data_period</c>. Not a current list: the profile shows the periods as history, and the record
/// quality counts a period still open at the end of the data, or ended within two years of the data's as-of date, as a sign of
/// practice. Version = the release folder's date and the start of its checksum.
/// </summary>
public sealed class MedicaidEnrollmentSource : DatasetSource
{
    public const string Slug = "medicaid-provider-enrollment-segments";

    public const string Table = "medicaid_enrollment_period";

    public override string Name => "medicaid_enrollment_segments";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    /// <summary>An 11.5 GB CSV read through and ~0.6 GB of segments in memory.</summary>
    public override bool Heavy => true;

    public override Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
        HhsOpenData.FindLatestAsync(context.Http, context.Options.HhsOpenDataUrl, Slug, ct);

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "medicaid_enrollment_segments.csv.zip", async (connection, path) =>
        {
            var published = DateOnly.TryParseExact(release.PeriodEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var p)
                ? p : DateOnly.FromDateTime(DateTime.UtcNow);
            MedicaidEnrollmentSummary? summary = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var counts = await TableSwap.ReplaceAsync(connection, [Table], context.Options.MinRowRatio, async () =>
            {
                await using var rows = new TsvRows(Path.Combine(context.Options.ResolvedWorkFolder, "datasets"), Table);
                await using (var file = File.OpenRead(path))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
                using (var reader = new StreamReader(HhsOpenData.OpenLargestCsv(zip, "The enrollment segments zip"), Encoding.UTF8, false, 1 << 20))
                {
                    summary = MedicaidEnrollmentSegments.Summarize(reader, published,
                        r => rows.Add(r.Npi, r.State, r.Period, r.From, r.Until, r.EndedStatus));
                }

                context.Log.Information("Medicaid enrollment segments: {Lines:N0} lines read in {Seconds:N0}s, {Skipped:N0} without a valid NPI; " +
                                        "{Periods:N0} enrollment periods, {First:yyyy-MM} to {Last:yyyy-MM}", summary.Lines, watch.Elapsed.TotalSeconds,
                    summary.SkippedNpis, summary.Periods, summary.FirstMonth, summary.LastMonth);
                context.Log.Information("Medicaid enrollment segments, NPIs per state: {States}",
                    string.Join(", ", summary.NpisByState.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key} {s.Value:N0}")));
                await rows.LoadAsync(connection, Table + "_staging", ["npi", "state", "period", "enrolled_from", "enrolled_until", "ended_status"], ct);
            }, ct, log: context.Log);
            await MedicaidPeriod.SaveAsync(connection, Name, summary!.FirstMonth, summary.LastMonth, ct);
            return counts[Table];
        }, ct);
}

/// <summary>
/// HHS's "Medicaid Provider Spending by HCPCS" (T-MSIS, opendata.hhs.gov; docs/PRODUCT_PLAN.md step 3.7): Medicaid claims by billing
/// and servicing NPI, procedure and month, 2018–2024, one ~3.8 GB zip of an 11.6 GB CSV. Read as it unzips and summarized in memory
/// (<see cref="MedicaidSpending"/>) → <c>medicaid_billing_year</c> (per NPI and year) and <c>medicaid_top_procedure</c> (the newest
/// year's top five per NPI, described as Medicare's files describe the code), both staging + RENAME, the NPIs whose years changed
/// queued for the record quality; the months covered → <c>medicaid_data_period</c>. Version = the release folder's date and the
/// start of its checksum.
/// </summary>
public sealed class MedicaidSpendingSource : DatasetSource
{
    public const string Slug = "medicaid-provider-spending";

    public const string YearTable = "medicaid_billing_year";

    public const string ProcedureTable = "medicaid_top_procedure";

    public override string Name => "medicaid_spending";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    /// <summary>A 3.8 GB download, an 11.6 GB CSV read through and the per-NPI sums in memory.</summary>
    public override bool Heavy => true;

    /// <summary>The procedures' descriptions come from Medicare's by-service file when it is loaded in the same run.</summary>
    public override IReadOnlyList<string> After => ["cms_physician_by_service"];

    public override Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
        HhsOpenData.FindLatestAsync(context.Http, context.Options.HhsOpenDataUrl, Slug, ct);

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "medicaid_spending.csv.zip", async (connection, path) =>
        {
            MedicaidSpendingSummary? summary = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var counts = await TableSwap.ReplaceAsync(connection, [YearTable, ProcedureTable], context.Options.MinRowRatio, async () =>
            {
                var folder = Path.Combine(context.Options.ResolvedWorkFolder, "datasets");
                await using var years = new TsvRows(folder, YearTable);
                await using var procedures = new TsvRows(folder, ProcedureTable);
                await using (var file = File.OpenRead(path))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
                using (var reader = new StreamReader(HhsOpenData.OpenLargestCsv(zip, "The Medicaid spending zip"), Encoding.UTF8, false, 1 << 20))
                {
                    summary = MedicaidSpending.Summarize(reader,
                        y => years.Add(y.Npi, y.Year, y.ClaimLines, y.Paid, y.BillingLines, y.BillingPaid, y.ServicingLines, y.ServicingPaid),
                        p => procedures.Add(p.Npi, p.Rank, p.Year, p.Hcpcs, p.ClaimLines, p.Paid), capacity: 1 << 23);
                }

                context.Log.Information("Medicaid spending: {Lines:N0} lines read in {Seconds:N0}s, {Skipped:N0} without a valid NPI, {Amounts:N0} with an " +
                                        "impossible amount; {Npis:N0} NPIs in {NpiYears:N0} NPI-years, {First:yyyy-MM} to {Last:yyyy-MM}", summary.Lines,
                    watch.Elapsed.TotalSeconds, summary.SkippedNpis, summary.SkippedAmounts, summary.Npis, summary.NpiYears, summary.FirstMonth, summary.LastMonth);
                context.Log.Information("Medicaid spending by year: {Years}", string.Join(", ", summary.ByYear.OrderBy(y => y.Key)
                    .Select(y => string.Create(CultureInfo.InvariantCulture, $"{y.Key} {y.Value.Npis:N0} NPIs ${y.Value.Paid / 1e9m:N1}B"))));
                await years.LoadAsync(connection, YearTable + "_staging",
                    ["npi", "data_year", "claim_lines", "paid", "billing_lines", "billing_paid", "servicing_lines", "servicing_paid"], ct);
                await procedures.LoadAsync(connection, ProcedureTable + "_staging", ["npi", "procedure_rank", "data_year", "hcpcs", "claim_lines", "paid"], ct);
                // Medicare's wording of the codes it also pays for (state-specific codes stay without one).
                await Database.ExecuteAsync(connection,
                    $"""
                    UPDATE `{ProcedureTable}_staging` t
                    JOIN (SELECT `hcpcs`, MAX(`description`) AS `description` FROM `medicare_top_service`
                          WHERE `hcpcs` IN (SELECT DISTINCT `hcpcs` FROM `{ProcedureTable}_staging`) GROUP BY `hcpcs`) m ON m.`hcpcs` = t.`hcpcs`
                    SET t.`description` = m.`description`
                    """, ct);
            }, ct, log: context.Log);
            await MedicaidPeriod.SaveAsync(connection, Name, summary!.FirstMonth, summary.LastMonth, ct);
            return counts[YearTable];
        }, ct);
}

/// <summary>The months a T-MSIS file covers (<c>medicaid_data_period</c>).</summary>
public static class MedicaidPeriod
{
    public const string Table = "medicaid_data_period";

    public static Task SaveAsync(MySqlConnection connection, string source, DateOnly first, DateOnly last, CancellationToken ct) =>
        Database.ExecuteAsync(connection,
            $"""
            INSERT INTO `{Table}` (`source`, `first_month`, `last_month`) VALUES (@source, @first, @last) AS new
            ON DUPLICATE KEY UPDATE `first_month` = new.`first_month`, `last_month` = new.`last_month`
            """, ct, param: new { source, first = first.ToDateTime(TimeOnly.MinValue), last = last.ToDateTime(TimeOnly.MinValue) });
}
