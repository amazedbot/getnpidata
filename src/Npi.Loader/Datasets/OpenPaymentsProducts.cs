using System.Globalization;
using System.Text.RegularExpressions;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>Products named in Open Payments (CLAUDE.md §7 Stage 5.5 item 19).</summary>
public static class OpenPaymentsProducts
{
    /// <summary>
    /// SQL for a product's slug: its name lower-case, every run of characters other than a–z and 0–9 replaced by "-",
    /// without leading or trailing "-", at most 200 characters ("ELIQUIS", " Eliquis " → eliquis; "Eliquis 5mg" → eliquis-5mg).
    /// The key of the product pages; '' when nothing is left.
    /// </summary>
    public static string Slug(string column) =>
        $"LEFT(TRIM(BOTH '-' FROM REGEXP_REPLACE(LOWER(TRIM(COALESCE({column}, ''))), '[^a-z0-9]+', '-')), 200)";
}

/// <summary>
/// CMS Open Payments Research Payment Data for the newest program year (item 19, part 1): research payments naming a
/// product (up to five, each counted with the full amount), with the study and its ClinicalTrials.gov ID, summarized
/// per product: totals and the <see cref="TopStudies"/> largest studies.
/// </summary>
public sealed partial class OpenPaymentsResearchSource : DatasetSource
{
    public const int TopStudies = 10;

    public override string Name => "open_payments_research";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    [GeneratedRegex(@"^(?<year>\d{4}) Research Payment Data$")]
    private static partial Regex ResearchPaymentTitle();

    private static readonly CsvColumn[] Columns =
    [
        new("Applicable_Manufacturer_or_Applicable_GPO_Making_Payment_ID", "company_id"),
        new("Total_Amount_of_Payment_USDollars", "amount", CsvValue.Number),
        new("Name_of_Study", "study"),
        new("ClinicalTrials_Gov_Identifier", "nct_id"),
        new("Name_of_Drug_or_Biological_or_Device_or_Medical_Supply_1", "product"),
        .. Enumerable.Range(2, 4).Select(n => new CsvColumn($"Name_of_Drug_or_Biological_or_Device_or_Medical_Supply_{n}", $"product{n}")),
    ];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        await using var stream = await context.Http.GetStreamAsync(new Uri(context.Options.OpenPaymentsCatalogUrl), ct);
        return OpenPaymentsSource.ParseCatalog(stream, ResearchPaymentTitle(), "Research Payment Data");
    }

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "open_payments_research.csv", async (connection, path) =>
        {
            const string raw = "op_research_raw_staging";
            const string scratch = "op_research_product_scratch";
            var year = (release.DataYear ?? throw new InvalidDataException("The research release has no program year.")).ToString(CultureInfo.InvariantCulture);
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`, `{scratch}`", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` LIKE `op_research_raw`", ct);
                var rows = await CsvTableLoader.LoadAsync(connection, path, raw, Columns, ct);
                context.Log.Information("Loaded {Rows:N0} Open Payments research records", rows);
                var slots = string.Join("\n UNION ALL\n", Enumerable.Range(1, 5).Select(n =>
                {
                    var x = n == 1 ? "" : n.ToString(CultureInfo.InvariantCulture);
                    return $"SELECT {OpenPaymentsProducts.Slug($"`product{x}`")} AS `slug`, `company_id`, `amount`, `study`, `nct_id` " +
                           $"FROM `{raw}` WHERE TRIM(COALESCE(`product{x}`, '')) <> ''";
                }));
                await Database.ExecuteAsync(connection,
                    $"CREATE TABLE `{scratch}` (KEY (`slug`)) ENGINE=InnoDB SELECT * FROM ({slots}) x WHERE `slug` <> ''", ct);

                var counts = await TableSwap.ReplaceAsync(connection, ["op_product_research", "op_product_study"], context.Options.MinRowRatio, async () =>
                {
                    await Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `op_product_research_staging` (`slug`, `program_year`, `amount`, `records`, `studies`)
                        SELECT `slug`, {year}, ROUND(SUM(COALESCE(`amount`, 0)), 2), COUNT(*), COUNT(DISTINCT COALESCE(`nct_id`, LEFT(`study`, 200)))
                        FROM `{scratch}` GROUP BY `slug`
                        """, ct);
                    await Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `op_product_study_staging` (`slug`, `study_rank`, `study`, `nct_id`, `company_id`, `amount`, `records`)
                        SELECT `slug`, `rnk`, `study`, `nct_id`, `company_id`, `amount`, `records`
                        FROM (
                          SELECT x.*, ROW_NUMBER() OVER (PARTITION BY `slug` ORDER BY `amount` DESC, `study_key`) AS `rnk`
                          FROM (
                            SELECT `slug`, COALESCE(`nct_id`, LEFT(`study`, 200)) AS `study_key`, MAX(`study`) AS `study`, MAX(`nct_id`) AS `nct_id`,
                              MAX(`company_id`) AS `company_id`, ROUND(SUM(COALESCE(`amount`, 0)), 2) AS `amount`, COUNT(*) AS `records`
                            FROM `{scratch}` WHERE `study` IS NOT NULL OR `nct_id` IS NOT NULL
                            GROUP BY `slug`, COALESCE(`nct_id`, LEFT(`study`, 200))
                          ) x
                        ) ranked
                        WHERE `rnk` <= {TopStudies}
                        """, ct);
                }, ct);
                return counts["op_product_research"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`, `{scratch}`", CancellationToken.None);
            }
        }, ct);
}
