using System.Text.Json;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// The newest file of one titled Open Payments dataset (CLAUDE.md §7 Stage 5.5 item 13): CMS's own summaries over every
/// published program year, which carry the recipient NPI, so the ~9 GB detail file of each year needn't be read. The
/// file name carries the publication date (…_P06302026_…), so a new publication is a new version.
/// </summary>
public abstract class OpenPaymentsSummarySource : DatasetSource
{
    /// <summary>The catalog title of the dataset, matched exactly.</summary>
    protected abstract string Title { get; }

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        await using var stream = await context.Http.GetStreamAsync(new Uri(context.Options.OpenPaymentsCatalogUrl), ct);
        return ParseCatalog(stream, Title);
    }

    /// <summary>The CSV of the catalog entry titled <paramref name="title"/>.</summary>
    public static DatasetRelease ParseCatalog(Stream json, string title)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var dataset in doc.RootElement.EnumerateArray())
        {
            if (!string.Equals((dataset.TryGetProperty("title", out var t) ? t.GetString() : null)?.Trim(), title, StringComparison.Ordinal)
                || !dataset.TryGetProperty("distribution", out var distributions) || distributions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var distribution in distributions.EnumerateArray())
            {
                var data = distribution.TryGetProperty("data", out var d) ? d : distribution;
                if (data.TryGetProperty("downloadURL", out var download) && download.GetString() is { } u && u.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    var url = new Uri(u);
                    return new DatasetRelease(Path.GetFileName(url.AbsolutePath), url);
                }
            }
        }

        throw new InvalidDataException($"The Open Payments catalog lists no CSV titled \"{title}\".");
    }

    /// <summary>Loads the file into a staging copy of <paramref name="rawTemplate"/>, runs <paramref name="summarize"/> on it, and drops it.</summary>
    protected static Task<long> LoadRawAsync(DatasetContext context, DatasetRelease release, string fileName, string rawTemplate,
        IReadOnlyList<CsvColumn> columns, Func<MySqlConnector.MySqlConnection, string, Task<long>> summarize, CancellationToken ct) =>
        WithDownloadAsync(context, release, fileName, async (connection, path) =>
        {
            var raw = rawTemplate + "_staging";
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` LIKE `{rawTemplate}`", ct);
                var rows = await CsvTableLoader.LoadAsync(connection, path, raw, columns, ct);
                context.Log.Information("Loaded {Rows:N0} rows of {File}", rows, release.Version);
                return await summarize(connection, raw);
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }, ct);
}

/// <summary>
/// Open Payments per NPI and program year (2019 on): general payments, research payments, research funding as a
/// principal investigator, and ownership or investment interests. A recipient with several CMS profile IDs is summed;
/// CMS's "All" rows (every year together) are left out and computed when shown.
/// </summary>
public sealed class OpenPaymentsYearSource : OpenPaymentsSummarySource
{
    public override string Name => "open_payments_years";

    protected override string Title => "Payments grouped by physician (distinct) for all years";

    private static readonly CsvColumn[] Columns =
    [
        new("Covered_Recipient_NPI", "npi", CsvValue.Npi),
        new("Program_Year", "program_year"),
        new("General_Total_Payment", "general_amount", CsvValue.Number),
        new("General_Total_Transactions", "general_records", CsvValue.OptionalWholeNumber),
        new("Research_Total_Payment", "research_amount", CsvValue.Number),
        new("Research_Total_Transactions", "research_records", CsvValue.OptionalWholeNumber),
        new("Total_Associated_Research_Payments", "associated_research_amount", CsvValue.Number),
        new("Total_Associated_Research_Transactions", "associated_research_records", CsvValue.OptionalWholeNumber),
        new("Invested_Total_Amount", "invested_amount", CsvValue.Number),
        new("Interest_Total_Amount", "interest_value", CsvValue.Number),
        new("Invested_Total_Transactions", "ownership_records", CsvValue.OptionalWholeNumber),
    ];

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        LoadRawAsync(context, release, "open_payments_years.csv", "open_payments_year_raw", Columns, async (connection, raw) =>
        {
            var counts = await TableSwap.ReplaceAsync(connection, ["open_payments_year"], context.Options.MinRowRatio, () =>
                Database.ExecuteAsync(connection,
                    $"""
                    INSERT INTO `open_payments_year_staging` (`npi`, `program_year`, `general_amount`, `general_records`, `research_amount`,
                      `research_records`, `associated_research_amount`, `associated_research_records`, `invested_amount`, `interest_value`,
                      `ownership_records`)
                    SELECT `npi`, CAST(`program_year` AS UNSIGNED),
                      ROUND(SUM(COALESCE(`general_amount`, 0)), 2), SUM(COALESCE(`general_records`, 0)),
                      ROUND(SUM(COALESCE(`research_amount`, 0)), 2), SUM(COALESCE(`research_records`, 0)),
                      ROUND(SUM(COALESCE(`associated_research_amount`, 0)), 2), SUM(COALESCE(`associated_research_records`, 0)),
                      ROUND(SUM(COALESCE(`invested_amount`, 0)), 2), ROUND(SUM(COALESCE(`interest_value`, 0)), 2), SUM(COALESCE(`ownership_records`, 0))
                    FROM `{raw}`
                    WHERE `npi` IS NOT NULL AND `program_year` REGEXP '^[0-9]{4}$'
                    GROUP BY `npi`, CAST(`program_year` AS UNSIGNED)
                    """, ct), ct);
            return counts["open_payments_year"];
        }, ct);
}

/// <summary>
/// The <see cref="TopCompanies"/> companies that paid each NPI the most over all published program years, with the
/// amounts split by payment type (general, research, research as principal investigator, ownership/investment).
/// </summary>
public sealed class OpenPaymentsCompanySource : OpenPaymentsSummarySource
{
    public const int TopCompanies = 5;

    public override string Name => "open_payments_companies";

    protected override string Title => "Payments grouped by covered recipient and reporting entities for all years";

    private static readonly CsvColumn[] Columns =
    [
        new("Covered_Recipient_NPI", "npi", CsvValue.Npi),
        new("Payment_Type", "payment_type"),
        new("AMGPO_Name", "company"),
        new("Number_of_Transaction", "records", CsvValue.OptionalWholeNumber),
        new("Total_Amount", "amount", CsvValue.Number),
    ];

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        LoadRawAsync(context, release, "open_payments_companies.csv", "open_payments_company_raw", Columns, async (connection, raw) =>
        {
            var counts = await TableSwap.ReplaceAsync(connection, ["open_payments_company"], context.Options.MinRowRatio, () =>
                Database.ExecuteAsync(connection,
                    $"""
                    INSERT INTO `open_payments_company_staging` (`npi`, `company_rank`, `company`, `total_amount`, `general_amount`, `research_amount`,
                      `associated_research_amount`, `ownership_amount`, `records`)
                    SELECT `npi`, `rnk`, `company`, `total`, `general`, `research`, `associated`, `ownership`, `records`
                    FROM (
                      SELECT x.*, ROW_NUMBER() OVER (PARTITION BY `npi` ORDER BY `total` DESC, `company`) AS `rnk`
                      FROM (
                        SELECT `npi`, `company`,
                          ROUND(SUM(COALESCE(`amount`, 0)), 2) AS `total`,
                          ROUND(SUM(IF(`payment_type` = 'General', COALESCE(`amount`, 0), 0)), 2) AS `general`,
                          ROUND(SUM(IF(`payment_type` = 'Research', COALESCE(`amount`, 0), 0)), 2) AS `research`,
                          ROUND(SUM(IF(`payment_type` = 'Associated Research', COALESCE(`amount`, 0), 0)), 2) AS `associated`,
                          ROUND(SUM(IF(`payment_type` LIKE 'Ownership%', COALESCE(`amount`, 0), 0)), 2) AS `ownership`,
                          SUM(COALESCE(`records`, 0)) AS `records`
                        FROM `{raw}` WHERE `npi` IS NOT NULL AND `company` IS NOT NULL
                        GROUP BY `npi`, `company`
                      ) x
                    ) ranked
                    WHERE `rnk` <= {TopCompanies}
                    """, ct), ct);
            return counts["open_payments_company"];
        }, ct);
}
