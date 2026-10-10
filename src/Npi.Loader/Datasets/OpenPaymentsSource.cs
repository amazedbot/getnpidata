using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// CMS Open Payments General Payment Data for the newest program year (CLAUDE.md §7 Stage 5.5 item 6). The
/// file is large (~9 GB, ~16M rows), so it is loaded raw and summarized per NPI: totals, amounts by nature of
/// payment, and the top three payers. Per company (item 17), over every recipient: amounts by nature of payment and
/// the <see cref="TopProducts"/> products named first on the payments. CMS republishes each year's file (e.g. the
/// January refresh); a new file name is a new version.
/// </summary>
public sealed partial class OpenPaymentsSource : DatasetSource
{
    public const int TopPayers = 3;

    public const int TopProducts = 25;

    public override string Name => "open_payments";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    private static readonly CsvColumn[] Columns =
    [
        new("Covered_Recipient_NPI", "npi", CsvValue.Npi),
        new("Applicable_Manufacturer_or_Applicable_GPO_Making_Payment_Name", "payer"),
        new("Total_Amount_of_Payment_USDollars", "amount", CsvValue.Number),
        new("Number_of_Payments_Included_in_Total_Amount", "payments", CsvValue.OptionalWholeNumber),
        new("Nature_of_Payment_or_Transfer_of_Value", "nature"),
        new("Applicable_Manufacturer_or_Applicable_GPO_Making_Payment_ID", "company_id"),
        new("Name_of_Drug_or_Biological_or_Device_or_Medical_Supply_1", "product"),
        new("Indicate_Drug_or_Biological_or_Device_or_Medical_Supply_1", "product_kind"),
        new("Product_Category_or_Therapeutic_Area_1", "product_category"),
    ];

    [GeneratedRegex(@"^(?<year>\d{4}) General Payment Data$")]
    private static partial Regex GeneralPaymentTitle();

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        await using var stream = await context.Http.GetStreamAsync(new Uri(context.Options.OpenPaymentsCatalogUrl), ct);
        return ParseCatalog(stream);
    }

    /// <summary>The newest "&lt;year&gt; General Payment Data" entry of the Open Payments metastore.</summary>
    public static DatasetRelease ParseCatalog(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        DatasetRelease? best = null;
        var bestYear = 0;
        foreach (var dataset in doc.RootElement.EnumerateArray())
        {
            var title = dataset.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var match = GeneralPaymentTitle().Match(title.Trim());
            if (!match.Success || !dataset.TryGetProperty("distribution", out var distributions) || distributions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
            foreach (var distribution in distributions.EnumerateArray())
            {
                var data = distribution.TryGetProperty("data", out var d) ? d : distribution;
                if (year > bestYear && data.TryGetProperty("downloadURL", out var download) && download.GetString() is { } u
                    && u.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    var url = new Uri(u);
                    best = new DatasetRelease($"{year} {Path.GetFileName(url.AbsolutePath)}", url, $"{year}-12-31");
                    bestYear = year;
                }
            }
        }

        return best ?? throw new InvalidDataException("The Open Payments catalog lists no \"<year> General Payment Data\" CSV.");
    }

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "open_payments_general.csv", async (connection, path) =>
        {
            const string raw = "open_payments_raw_staging";
            var year = (release.DataYear ?? throw new InvalidDataException("The Open Payments release has no program year."))
                .ToString(CultureInfo.InvariantCulture);
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` LIKE `open_payments_raw`", ct);
                var rows = await CsvTableLoader.LoadAsync(connection, path, raw, Columns, ct);
                context.Log.Information("Loaded {Rows:N0} Open Payments records", rows);

                var counts = await TableSwap.ReplaceAsync(connection,
                    ["open_payments_summary", "open_payments_nature", "open_payments_payer", "op_company_nature", "op_company_product"],
                    context.Options.MinRowRatio, async () =>
                    {
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `open_payments_summary_staging` (`npi`, `program_year`, `total_amount`, `records`, `payers`)
                            SELECT `npi`, {year}, ROUND(SUM(COALESCE(`amount`, 0)), 2), COUNT(*), COUNT(DISTINCT `payer`)
                            FROM `{raw}` WHERE `npi` IS NOT NULL GROUP BY `npi`
                            """, ct);
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `open_payments_nature_staging` (`npi`, `nature`, `amount`, `records`)
                            SELECT `npi`, COALESCE(`nature`, 'Not specified'), ROUND(SUM(COALESCE(`amount`, 0)), 2), COUNT(*)
                            FROM `{raw}` WHERE `npi` IS NOT NULL GROUP BY `npi`, COALESCE(`nature`, 'Not specified')
                            """, ct);
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `open_payments_payer_staging` (`npi`, `payer_rank`, `payer`, `company_id`, `amount`, `records`)
                            SELECT `npi`, `rnk`, `payer`, `company_id`, `amount`, `records`
                            FROM (
                              SELECT `npi`, `payer`, `company_id`, `amount`, `records`,
                                ROW_NUMBER() OVER (PARTITION BY `npi` ORDER BY `amount` DESC, `payer`) AS `rnk`
                              FROM (
                                SELECT `npi`, MAX(`payer`) AS `payer`, `company_id`, ROUND(SUM(COALESCE(`amount`, 0)), 2) AS `amount`, COUNT(*) AS `records`
                                FROM `{raw}` WHERE `npi` IS NOT NULL AND `payer` IS NOT NULL GROUP BY `npi`, `company_id`
                              ) per_payer
                            ) ranked
                            WHERE `rnk` <= {TopPayers}
                            """, ct);
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `op_company_nature_staging` (`company_id`, `program_year`, `nature`, `amount`, `records`)
                            SELECT `company_id`, {year}, COALESCE(`nature`, 'Not specified'), ROUND(SUM(COALESCE(`amount`, 0)), 2), COUNT(*)
                            FROM `{raw}` WHERE `company_id` IS NOT NULL GROUP BY `company_id`, COALESCE(`nature`, 'Not specified')
                            """, ct);
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `op_company_product_staging` (`company_id`, `product_rank`, `program_year`, `product`, `kind`, `category`, `amount`, `records`)
                            SELECT `company_id`, `rnk`, {year}, `product`, `kind`, `category`, `amount`, `records`
                            FROM (
                              SELECT x.*, ROW_NUMBER() OVER (PARTITION BY `company_id` ORDER BY `amount` DESC, `product`) AS `rnk`
                              FROM (
                                SELECT `company_id`, UPPER(TRIM(`product`)) AS `product`, MAX(`product_kind`) AS `kind`, MAX(`product_category`) AS `category`,
                                  ROUND(SUM(COALESCE(`amount`, 0)), 2) AS `amount`, COUNT(*) AS `records`
                                FROM `{raw}` WHERE `company_id` IS NOT NULL AND TRIM(`product`) <> ''
                                GROUP BY `company_id`, UPPER(TRIM(`product`))
                              ) x
                            ) ranked
                            WHERE `rnk` <= {TopProducts}
                            """, ct);
                    }, ct);
                return counts["open_payments_summary"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }, ct);
}
