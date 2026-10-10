using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// CMS Open Payments General Payment Data for the newest program year (CLAUDE.md §7 Stage 5.5 item 6). The
/// file is large (~9 GB, ~16M rows), so it is loaded raw and summarized per NPI: totals, amounts by nature of
/// payment, and the top three payers. Per company (item 17), over every recipient: amounts by nature of payment and
/// its <see cref="TopProducts"/> products. Per product (item 19, part 1; every product a payment names, up to five, each
/// counted with the payment's full amount): totals, companies, kinds of payment, specialties, the providers paid most,
/// and per provider its top products. CMS republishes each year's file (e.g. the January refresh); a new file name is a
/// new version.
/// </summary>
public sealed partial class OpenPaymentsSource : DatasetSource
{
    public const int TopPayers = 3;

    public const int TopProducts = 25;

    public const int TopProductSpecialties = 10;

    public const int TopProductRecipients = 100;

    public const int TopProviderProducts = 5;

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
        new("Associated_Drug_or_Biological_NDC_1", "ndc"),
        new("Associated_Device_or_Medical_Supply_PDI_1", "device_id"),
        .. Enumerable.Range(2, 4).SelectMany(n => new CsvColumn[]
        {
            new($"Name_of_Drug_or_Biological_or_Device_or_Medical_Supply_{n}", $"product{n}"),
            new($"Indicate_Drug_or_Biological_or_Device_or_Medical_Supply_{n}", $"product_kind{n}"),
            new($"Product_Category_or_Therapeutic_Area_{n}", $"product_category{n}"),
            new($"Associated_Drug_or_Biological_NDC_{n}", $"ndc{n}"),
            new($"Associated_Device_or_Medical_Supply_PDI_{n}", $"device_id{n}"),
        }),
    ];

    [GeneratedRegex(@"^(?<year>\d{4}) General Payment Data$")]
    private static partial Regex GeneralPaymentTitle();

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        await using var stream = await context.Http.GetStreamAsync(new Uri(context.Options.OpenPaymentsCatalogUrl), ct);
        return ParseCatalog(stream);
    }

    /// <summary>The newest "&lt;year&gt; General Payment Data" entry of the Open Payments metastore.</summary>
    public static DatasetRelease ParseCatalog(Stream json) => ParseCatalog(json, GeneralPaymentTitle(), "General Payment Data");

    /// <summary>The newest entry whose title matches <paramref name="title"/> (a regex with a "year" group).</summary>
    public static DatasetRelease ParseCatalog(Stream json, Regex title, string label)
    {
        using var doc = JsonDocument.Parse(json);
        DatasetRelease? best = null;
        var bestYear = 0;
        foreach (var dataset in doc.RootElement.EnumerateArray())
        {
            var name = dataset.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var match = title.Match(name.Trim());
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

        return best ?? throw new InvalidDataException($"The Open Payments catalog lists no \"<year> {label}\" CSV.");
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

                // Every product a payment names (slots 1-5), one row each, keyed by the product slug; then per product and NPI.
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{Products}`, `{ProductNpis}`", ct);
                var slots = string.Join("\n UNION ALL\n", Enumerable.Range(1, 5).Select(n =>
                {
                    var x = n == 1 ? "" : n.ToString(CultureInfo.InvariantCulture);
                    return $"SELECT {OpenPaymentsProducts.Slug($"`product{x}`")} AS `slug`, TRIM(`product{x}`) AS `name`, `product_kind{x}` AS `kind`, " +
                           $"`product_category{x}` AS `category`, `ndc{x}` AS `ndc`, `device_id{x}` AS `device_id`, `company_id`, `npi`, `amount`, `nature` " +
                           $"FROM `{raw}` WHERE TRIM(COALESCE(`product{x}`, '')) <> ''";
                }));
                await Database.ExecuteAsync(connection,
                    $"""
                    CREATE TABLE `{Products}` (KEY (`slug`), KEY (`company_id`, `slug`)) ENGINE=InnoDB
                    SELECT * FROM ({slots}) x WHERE `slug` <> ''
                    """, ct);
                await Database.ExecuteAsync(connection,
                    $"""
                    CREATE TABLE `{ProductNpis}` (KEY (`slug`), KEY (`npi`)) ENGINE=InnoDB
                    SELECT `slug`, `npi`, ROUND(SUM(COALESCE(`amount`, 0)), 2) AS `amount`, COUNT(*) AS `records`
                    FROM `{Products}` WHERE `npi` IS NOT NULL GROUP BY `slug`, `npi`
                    """, ct);

                var counts = await TableSwap.ReplaceAsync(connection,
                    ["open_payments_summary", "open_payments_nature", "open_payments_payer", "op_company_nature", "op_company_product", "op_product",
                     "op_product_company", "op_product_nature", "op_product_specialty", "op_product_recipient", "op_provider_product"],
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
                            INSERT INTO `op_company_product_staging` (`company_id`, `product_rank`, `program_year`, `product`, `kind`, `category`, `amount`, `records`, `slug`)
                            SELECT `company_id`, `rnk`, {year}, `name`, `kind`, `category`, `amount`, `records`, `slug`
                            FROM (
                              SELECT x.*, ROW_NUMBER() OVER (PARTITION BY `company_id` ORDER BY `amount` DESC, `slug`) AS `rnk`
                              FROM (
                                SELECT `company_id`, `slug`, UPPER(MAX(`name`)) AS `name`, MAX(`kind`) AS `kind`, MAX(`category`) AS `category`,
                                  ROUND(SUM(COALESCE(`amount`, 0)), 2) AS `amount`, COUNT(*) AS `records`
                                FROM `{Products}` WHERE `company_id` IS NOT NULL
                                GROUP BY `company_id`, `slug`
                              ) x
                            ) ranked
                            WHERE `rnk` <= {TopProducts}
                            """, ct);
                        // A product's name, type, category, NDC and device identifier: its most used value (by amount, then count).
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `op_product_staging` (`slug`, `name`, `kind`, `category`, `ndc`, `device_id`, `program_year`, `amount`, `records`, `companies`, `providers`)
                            SELECT t.`slug`, COALESCE(n.`v`, t.`slug`), k.`v`, c.`v`, d.`v`, i.`v`, {year}, t.`amount`, t.`records`, t.`companies`, t.`providers`
                            FROM (
                              SELECT `slug`, ROUND(SUM(COALESCE(`amount`, 0)), 2) AS `amount`, COUNT(*) AS `records`, COUNT(DISTINCT `company_id`) AS `companies`,
                                COUNT(DISTINCT `npi`) AS `providers`
                              FROM `{Products}` GROUP BY `slug`
                            ) t
                            {MostUsed("name", "n")}
                            {MostUsed("kind", "k")}
                            {MostUsed("category", "c")}
                            {MostUsed("ndc", "d")}
                            {MostUsed("device_id", "i")}
                            """, ct);
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `op_product_company_staging` (`slug`, `company_id`, `amount`, `records`)
                            SELECT `slug`, `company_id`, ROUND(SUM(COALESCE(`amount`, 0)), 2), COUNT(*)
                            FROM `{Products}` WHERE `company_id` IS NOT NULL GROUP BY `slug`, `company_id`
                            """, ct);
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `op_product_nature_staging` (`slug`, `nature`, `amount`, `records`)
                            SELECT `slug`, COALESCE(`nature`, 'Not specified'), ROUND(SUM(COALESCE(`amount`, 0)), 2), COUNT(*)
                            FROM `{Products}` GROUP BY `slug`, COALESCE(`nature`, 'Not specified')
                            """, ct);
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `op_product_specialty_staging` (`slug`, `specialty_rank`, `specialty`, `providers`, `amount`)
                            SELECT `slug`, `rnk`, `specialty`, `providers`, `amount`
                            FROM (
                              SELECT x.*, ROW_NUMBER() OVER (PARTITION BY `slug` ORDER BY `amount` DESC, `specialty`) AS `rnk`
                              FROM (
                                SELECT s.`slug`, t.`Classification` AS `specialty`, COUNT(*) AS `providers`, ROUND(SUM(s.`amount`), 2) AS `amount`
                                FROM `{ProductNpis}` s
                                JOIN `provider` p ON p.`npi` = s.`npi`
                                JOIN `taxonomy_codes` t ON t.`Taxonomy_Code` = p.`primary_taxonomy_code`
                                WHERE t.`Classification` IS NOT NULL
                                GROUP BY s.`slug`, t.`Classification`
                              ) x
                            ) ranked
                            WHERE `rnk` <= {TopProductSpecialties}
                            """, ct);
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `op_product_recipient_staging` (`slug`, `recipient_rank`, `npi`, `amount`, `records`)
                            SELECT `slug`, `rnk`, `npi`, `amount`, `records`
                            FROM (
                              SELECT s.*, ROW_NUMBER() OVER (PARTITION BY s.`slug` ORDER BY s.`amount` DESC, s.`npi`) AS `rnk`
                              FROM `{ProductNpis}` s JOIN `provider` p ON p.`npi` = s.`npi`
                            ) ranked
                            WHERE `rnk` <= {TopProductRecipients}
                            """, ct);
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `op_provider_product_staging` (`npi`, `product_rank`, `slug`, `amount`, `records`)
                            SELECT `npi`, `rnk`, `slug`, `amount`, `records`
                            FROM (SELECT s.*, ROW_NUMBER() OVER (PARTITION BY s.`npi` ORDER BY s.`amount` DESC, s.`slug`) AS `rnk` FROM `{ProductNpis}` s) ranked
                            WHERE `rnk` <= {TopProviderProducts}
                            """, ct);
                    }, ct);
                return counts["open_payments_summary"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`, `{Products}`, `{ProductNpis}`", CancellationToken.None);
            }
        }, ct);

    private const string Products = "op_product_scratch";

    private const string ProductNpis = "op_product_npi_scratch";

    // LEFT JOIN of a product's most used value of one column (by amount, then count, then the value), spellings compared
    // exactly ("Eliquis" and "ELIQUIS" are different spellings of one product).
    private static string MostUsed(string column, string alias) =>
        $"""
        LEFT JOIN (
          SELECT `slug`, `v` FROM (
            SELECT `slug`, MIN(`{column}`) AS `v`,
              ROW_NUMBER() OVER (PARTITION BY `slug` ORDER BY SUM(COALESCE(`amount`, 0)) DESC, COUNT(*) DESC, MIN(`{column}`) COLLATE utf8mb4_bin) AS `rn`
            FROM `{Products}` WHERE TRIM(COALESCE(`{column}`, '')) <> '' GROUP BY `slug`, `{column}` COLLATE utf8mb4_bin
          ) r WHERE `rn` = 1
        ) {alias} ON {alias}.`slug` = t.`slug`
        """;
}
