using System.Globalization;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// CMS "Medicare Part D Prescribers - by Provider and Drug" for the newest data year (CLAUDE.md §7 Stage 5.5 item 19,
/// part 4): ~26M rows (~4 GB), one per prescriber and drug. Only brand-name rows are kept (a generic's rows carry its
/// generic name as the brand), per brand and NPI → <c>part_d_brand_prescriber</c>, and summed per brand →
/// <c>part_d_brand</c>. The product pages match a product to brands by name when they are read (like the spending data)
/// and join the providers paid for it (<c>op_product_npi</c>). CMS suppresses beneficiary counts under 11 (NULL).
/// </summary>
public sealed class PartDDrugPrescriberSource : DatasetSource
{
    public const string CatalogTitle = "Medicare Part D Prescribers - by Provider and Drug";

    public override string Name => "part_d_prescribers";

    private static readonly CsvColumn[] Columns =
    [
        new("Prscrbr_NPI", "npi", CsvValue.Npi), new("Brnd_Name", "brand"), new("Gnrc_Name", "generic"),
        new("Tot_Clms", "claims", CsvValue.OptionalWholeNumber), new("Tot_30day_Fills", "fills", CsvValue.OptionalNumber),
        new("Tot_Drug_Cst", "drug_cost", CsvValue.OptionalNumber), new("Tot_Benes", "beneficiaries", CsvValue.OptionalWholeNumber),
    ];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
        (await context.CmsCatalogAsync(ct)).FindLatest(CatalogTitle) ?? throw new InvalidDataException($"The CMS catalog has no \"{CatalogTitle}\" CSV.");

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "cms_part_d_by_provider_and_drug.csv", async (connection, path) =>
        {
            const string raw = "part_d_drug_raw_staging";
            var year = (release.DataYear ?? throw new InvalidDataException("The Part D release has no data year.")).ToString(CultureInfo.InvariantCulture);
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` LIKE `part_d_drug_raw`", ct);
                var rows = await CsvTableLoader.LoadAsync(connection, path, raw, Columns, ct);
                context.Log.Information("Part D by provider and drug: {Rows:N0} rows", rows);
                var counts = await TableSwap.ReplaceAsync(connection, ["part_d_brand_prescriber", "part_d_brand"], context.Options.MinRowRatio, async () =>
                {
                    await Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `part_d_brand_prescriber_staging` (`brand`, `npi`, `claims`, `fills`, `drug_cost`, `beneficiaries`)
                        SELECT LEFT(TRIM(`brand`), 255), `npi`, SUM(COALESCE(`claims`, 0)), SUM(`fills`), ROUND(SUM(`drug_cost`), 2), SUM(`beneficiaries`)
                        FROM `{raw}`
                        WHERE `npi` IS NOT NULL AND TRIM(COALESCE(`brand`, '')) <> '' AND UPPER(TRIM(`brand`)) <> UPPER(TRIM(COALESCE(`generic`, '')))
                        GROUP BY LEFT(TRIM(`brand`), 255), `npi`
                        """, ct);
                    await Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `part_d_brand_staging` (`brand`, `generic`, `data_year`, `prescribers`, `claims`, `drug_cost`, `beneficiaries`)
                        SELECT b.`brand`, g.`generic`, {year}, COUNT(*), SUM(b.`claims`), ROUND(SUM(b.`drug_cost`), 2), SUM(b.`beneficiaries`)
                        FROM `part_d_brand_prescriber_staging` b
                        LEFT JOIN (SELECT LEFT(TRIM(`brand`), 255) AS `brand`, MAX(TRIM(`generic`)) AS `generic` FROM `{raw}` GROUP BY LEFT(TRIM(`brand`), 255)) g
                          ON g.`brand` = b.`brand`
                        GROUP BY b.`brand`, g.`generic`
                        """, ct);
                }, ct);
                context.Log.Information("Part D brand prescribers: {Rows:N0} for {Brands:N0} brands", counts["part_d_brand_prescriber"], counts["part_d_brand"]);
                await ProductPrescribingBuilder.RebuildAsync(connection, context, ct);
                return counts["part_d_brand_prescriber"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }, ct);
}
