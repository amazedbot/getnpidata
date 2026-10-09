using System.Globalization;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>Shared lookup for the yearly CMS "Medicare … by Provider" datasets (CLAUDE.md §7 Stage 5.5 item 5).</summary>
public abstract class MedicareYearlySource : DatasetSource
{
    protected abstract string CatalogTitle { get; }

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
        (await context.CmsCatalogAsync(ct)).FindLatest(CatalogTitle)
        ?? throw new InvalidDataException($"data.cms.gov lists no \"{CatalogTitle}\" CSV.");

    protected static Dictionary<string, string> DataYear(DatasetRelease release) => new()
    {
        ["data_year"] = (release.DataYear ?? throw new InvalidDataException($"{release.Url} has no data period in the catalog."))
            .ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>Medicare Physician &amp; Other Practitioners - by Provider: Part B totals per NPI (item 5a).</summary>
public sealed class MedicareUtilizationSource : MedicareYearlySource
{
    public override string Name => "cms_physician_by_provider";

    protected override string CatalogTitle => "Medicare Physician & Other Practitioners - by Provider";

    private static readonly CsvColumn[] Columns =
    [
        new("Rndrng_NPI", "npi", CsvValue.Npi), new("Rndrng_Prvdr_Type", "provider_type"),
        new("Rndrng_Prvdr_Mdcr_Prtcptg_Ind", "participating", CsvValue.YesNo), new("Tot_HCPCS_Cds", "distinct_services", CsvValue.Number),
        new("Tot_Benes", "beneficiaries", CsvValue.Number), new("Tot_Srvcs", "services", CsvValue.Number),
        new("Tot_Sbmtd_Chrg", "submitted_charges", CsvValue.Number), new("Tot_Mdcr_Alowd_Amt", "allowed_amount", CsvValue.Number),
        new("Tot_Mdcr_Pymt_Amt", "payment_amount", CsvValue.Number), new("Bene_Avg_Age", "avg_beneficiary_age", CsvValue.Number),
        new("Bene_Avg_Risk_Scre", "avg_risk_score", CsvValue.Number),
    ];

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "cms_physician_by_provider.csv", async (connection, path) =>
        {
            var counts = await TableSwap.ReplaceAsync(connection, ["medicare_utilization"], context.Options.MinRowRatio,
                () => CsvTableLoader.LoadAsync(connection, path, "medicare_utilization_staging", Columns, ct, constants: DataYear(release)), ct);
            return counts["medicare_utilization"];
        }, ct);
}

/// <summary>
/// Medicare Physician &amp; Other Practitioners - by Provider and Service (~10M rows): only each NPI's five most
/// frequent services are kept, in medicare_top_service (item 5a).
/// </summary>
public sealed class MedicareServicesSource : MedicareYearlySource
{
    public const int TopServices = 5;

    public override string Name => "cms_physician_by_service";

    protected override string CatalogTitle => "Medicare Physician & Other Practitioners - by Provider and Service";

    private static readonly CsvColumn[] Columns =
    [
        new("Rndrng_NPI", "npi", CsvValue.Npi), new("HCPCS_Cd", "hcpcs"), new("HCPCS_Desc", "description"),
        new("HCPCS_Drug_Ind", "is_drug", CsvValue.YesNo), new("Place_Of_Srvc", "place_of_service"),
        new("Tot_Benes", "beneficiaries", CsvValue.Number), new("Tot_Srvcs", "services", CsvValue.Number),
        new("Avg_Mdcr_Pymt_Amt", "avg_payment", CsvValue.Number),
    ];

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "cms_physician_by_service.csv", async (connection, path) =>
        {
            const string raw = "medicare_service_raw_staging";
            var year = DataYear(release)["data_year"];
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` LIKE `medicare_service_raw`", ct);
                var rows = await CsvTableLoader.LoadAsync(connection, path, raw, Columns, ct);
                context.Log.Information("Loaded {Rows:N0} Medicare provider/service rows", rows);

                var counts = await TableSwap.ReplaceAsync(connection, ["medicare_top_service"], context.Options.MinRowRatio, () =>
                    Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `medicare_top_service_staging` (`npi`, `service_rank`, `data_year`, `hcpcs`, `description`, `is_drug`, `place_of_service`,
                          `beneficiaries`, `services`, `avg_payment`)
                        SELECT `npi`, `rnk`, {int.Parse(year, CultureInfo.InvariantCulture)}, `hcpcs`, `description`, `is_drug`, `place_of_service`,
                          `beneficiaries`, `services`, `avg_payment`
                        FROM (
                          SELECT r.*, ROW_NUMBER() OVER (PARTITION BY `npi` ORDER BY `services` DESC, `beneficiaries` DESC, `hcpcs`, `place_of_service`) AS `rnk`
                          FROM `{raw}` r
                        ) ranked
                        WHERE `rnk` <= {TopServices}
                        """, ct), ct);
                return counts["medicare_top_service"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }, ct);
}

/// <summary>Medicare Part D Prescribers - by Provider: prescribing totals per NPI (item 5b).</summary>
public sealed class PartDPrescriberSource : MedicareYearlySource
{
    public override string Name => "cms_part_d_by_provider";

    protected override string CatalogTitle => "Medicare Part D Prescribers - by Provider";

    private static readonly CsvColumn[] Columns =
    [
        new("Prscrbr_NPI", "npi", CsvValue.Npi), new("Prscrbr_Type", "prescriber_type"), new("Tot_Clms", "claims", CsvValue.Number),
        new("Tot_30day_Fills", "fills_30day", CsvValue.Number), new("Tot_Drug_Cst", "drug_cost", CsvValue.Number),
        new("Tot_Day_Suply", "day_supply", CsvValue.Number), new("Tot_Benes", "beneficiaries", CsvValue.Number),
        new("Brnd_Tot_Clms", "brand_claims", CsvValue.Number), new("Gnrc_Tot_Clms", "generic_claims", CsvValue.Number),
        new("Opioid_Tot_Clms", "opioid_claims", CsvValue.Number), new("Opioid_Prscrbr_Rate", "opioid_rate", CsvValue.Number),
        new("Antbtc_Tot_Clms", "antibiotic_claims", CsvValue.Number), new("Bene_Avg_Age", "avg_beneficiary_age", CsvValue.Number),
    ];

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "cms_part_d_by_provider.csv", async (connection, path) =>
        {
            var counts = await TableSwap.ReplaceAsync(connection, ["medicare_part_d"], context.Options.MinRowRatio,
                () => CsvTableLoader.LoadAsync(connection, path, "medicare_part_d_staging", Columns, ct, constants: DataYear(release)), ct);
            return counts["medicare_part_d"];
        }, ct);
}
