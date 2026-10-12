using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// Signs of current practice for the record-quality label (docs/PRODUCT_PLAN.md step 3.1; owner's OK 2026-10-12): small CMS
/// files from the data.cms.gov catalog, each loaded through a scratch table (rows without a valid NPI dropped, one row per NPI)
/// and swapped in (staging + RENAME).
/// </summary>
public abstract class PracticeSignalSource : DatasetSource
{
    protected abstract string CatalogTitle { get; }

    protected abstract string Table { get; }

    protected abstract string FileName { get; }

    protected abstract IReadOnlyList<CsvColumn> Columns { get; }

    /// <summary>The scratch table's columns (DDL), named as in <see cref="Columns"/>.</summary>
    protected abstract string ScratchColumns { get; }

    /// <summary>INSERT … SELECT from the scratch table (named {0}) into <c>{Table}_staging</c>, one row per NPI.</summary>
    protected abstract string Summarize(DatasetRelease release);

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
        (await context.CmsCatalogAsync(ct)).FindLatest(CatalogTitle)
        ?? throw new InvalidDataException($"data.cms.gov lists no \"{CatalogTitle}\" CSV.");

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, FileName, async (connection, path) =>
        {
            var raw = Table + "_raw_staging";
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection,
                    $"CREATE TABLE `{raw}` ({ScratchColumns}) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci", ct);
                await CsvTableLoader.LoadAsync(connection, path, raw, Columns, ct, "latin1"); // CMS writes these files in Latin-1
                var counts = await TableSwap.ReplaceAsync(connection, [Table], context.Options.MinRowRatio,
                    () => Database.ExecuteAsync(connection, string.Format(System.Globalization.CultureInfo.InvariantCulture, Summarize(release), $"`{raw}`"), ct), ct);
                return counts[Table];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }, ct);
}

/// <summary>CMS "Opioid Treatment Program Providers": programs certified to treat opioid use disorder under Medicare.</summary>
public sealed class OpioidTreatmentSource : PracticeSignalSource
{
    public const string Title = "Opioid Treatment Program Providers";

    public override string Name => "cms_otp_providers";

    protected override string CatalogTitle => Title;

    protected override string Table => "cms_otp_provider";

    protected override string FileName => "cms_otp_providers.csv";

    protected override IReadOnlyList<CsvColumn> Columns { get; } =
    [
        new("NPI", "npi", CsvValue.Npi), new("PROVIDER NAME", "name"), new("MEDICARE ID EFFECTIVE DATE", "effective_date", CsvValue.DateMdy),
    ];

    protected override string ScratchColumns => "`npi` CHAR(10) NULL, `name` VARCHAR(255) NULL, `effective_date` DATE NULL";

    protected override string Summarize(DatasetRelease release) =>
        "INSERT INTO `cms_otp_provider_staging` (`npi`, `name`, `effective_date`) " +
        "SELECT `npi`, LEFT(MAX(`name`), 150), MIN(`effective_date`) FROM {0} WHERE `npi` IS NOT NULL GROUP BY `npi`";
}

/// <summary>CMS "Revalidation Due Date List": every provider with a current Medicare enrollment, with its next revalidation.</summary>
public sealed class RevalidationSource : PracticeSignalSource
{
    public const string Title = "Revalidation Due Date List";

    public override string Name => "cms_revalidation";

    protected override string CatalogTitle => Title;

    protected override string Table => "medicare_revalidation";

    protected override string FileName => "cms_revalidation.csv";

    protected override IReadOnlyList<CsvColumn> Columns { get; } =
    [
        new("National Provider Identifier", "npi", CsvValue.Npi), new("Revalidation Due Date", "due_date", CsvValue.DateMdy),
        new("Adjusted Due Date", "adjusted_due_date", CsvValue.DateMdy),
    ];

    protected override string ScratchColumns => "`npi` CHAR(10) NULL, `due_date` DATE NULL, `adjusted_due_date` DATE NULL";

    protected override string Summarize(DatasetRelease release) =>
        "INSERT INTO `medicare_revalidation_staging` (`npi`, `due_date`) " +
        "SELECT `npi`, MAX(COALESCE(`adjusted_due_date`, `due_date`)) FROM {0} WHERE `npi` IS NOT NULL GROUP BY `npi`";
}

/// <summary>
/// CMS "Medicare Durable Medical Equipment, Devices &amp; Supplies - by Supplier": every supplier NPI that billed Medicare for
/// equipment, prosthetics or supplies in the data year (the supplier directory is no longer published). Yearly.
/// </summary>
public sealed class DmeSupplierSource : PracticeSignalSource
{
    public const string Title = "Medicare Durable Medical Equipment, Devices & Supplies - by Supplier";

    public override string Name => "cms_dme_suppliers";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    protected override string CatalogTitle => Title;

    protected override string Table => "medicare_dme_supplier";

    protected override string FileName => "cms_dme_suppliers.csv";

    protected override IReadOnlyList<CsvColumn> Columns { get; } =
    [
        new("Suplr_NPI", "npi", CsvValue.Npi), new("Tot_Suplr_Benes", "beneficiaries", CsvValue.OptionalWholeNumber),
        new("Tot_Suplr_Clms", "claims", CsvValue.OptionalWholeNumber),
    ];

    protected override string ScratchColumns => "`npi` CHAR(10) NULL, `beneficiaries` INT NULL, `claims` INT NULL";

    protected override string Summarize(DatasetRelease release) =>
        "INSERT INTO `medicare_dme_supplier_staging` (`npi`, `data_year`, `beneficiaries`, `claims`) " +
        $"SELECT `npi`, {release.DataYear ?? DateTime.UtcNow.Year - 2}, MAX(`beneficiaries`), MAX(`claims`) FROM {{0}} WHERE `npi` IS NOT NULL GROUP BY `npi`";
}

/// <summary>
/// CMS Provider of Services file: every Medicare-certified facility (CCN), active or terminated, in two parts: facilities
/// still in the older survey system (QIES: hospitals, nursing homes, clinics …) and those moved to iQIES (home health,
/// hospice and others). An iQIES row wins over a QIES row for the same CCN. For closures in the record-quality label.
/// </summary>
public sealed class ProviderOfServicesSource : DatasetSource
{
    public const string QiesTitle = "Provider of Services File - Quality Improvement and Evaluation System";
    public const string IqiesTitle = "Provider of Services File - Internet Quality Improvement and Evaluation System";

    public override string Name => "cms_provider_of_services";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    private static readonly CsvColumn[] QiesColumns =
    [
        new("PRVDR_NUM", "ccn"), new("FAC_NAME", "name"), new("PRVDR_CTGRY_CD", "category"), new("ST_ADR", "address"), new("CITY_NAME", "city"),
        new("STATE_CD", "state"), new("ZIP_CD", "zip"), new("PGM_TRMNTN_CD", "termination_code"), new("TRMNTN_EXPRTN_DT", "termination_date", CsvValue.DateYmd),
    ];

    private static readonly CsvColumn[] IqiesColumns =
    [
        new("prvdr_num", "ccn"), new("fac_name", "name"), new("prvdr_type_id", "category"), new("st_adr", "address"), new("city_name", "city"),
        new("state_cd", "state"), new("zip_cd", "zip"), new("pgm_trmntn_cd", "termination_code"),
        new("trmntn_exprtn_dt", "termination_date", CsvValue.FlexibleDate), new("fed_crtfctn_stus_name", "status"),
    ];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var (qies, iqies) = await FindBothAsync(context, ct);
        return new DatasetRelease($"{qies.Version} | {iqies.Version}", qies.Url);
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var (qies, iqies) = await FindBothAsync(context, ct);
        var qiesPath = await context.DownloadAsync(qies, "cms_pos_qies.csv", ct);
        string? iqiesPath = null;
        const string raw = "cms_pos_raw_staging";
        try
        {
            iqiesPath = await context.DownloadAsync(iqies, "cms_pos_iqies.csv", ct);
            await using var connection = await context.Database.OpenAsync(ct);
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection,
                    $"""
                    CREATE TABLE `{raw}` (`part` TINYINT NOT NULL, `ccn` VARCHAR(10) NULL, `name` VARCHAR(255) NULL, `category` VARCHAR(10) NULL,
                      `address` VARCHAR(255) NULL, `city` VARCHAR(100) NULL, `state` VARCHAR(2) NULL, `zip` VARCHAR(10) NULL,
                      `termination_code` VARCHAR(40) NULL, `termination_date` DATE NULL, `status` VARCHAR(60) NULL)
                    DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
                    """, ct);
                await CsvTableLoader.LoadAsync(connection, qiesPath, raw, QiesColumns, ct, "latin1", new Dictionary<string, string> { ["part"] = "1" });
                await CsvTableLoader.LoadAsync(connection, iqiesPath, raw, IqiesColumns, ct, "latin1", new Dictionary<string, string> { ["part"] = "2" });

                // Active: QIES termination code 00; iQIES certification status other than Terminated. iQIES first, so it wins.
                var counts = await TableSwap.ReplaceAsync(connection, ["cms_pos_facility"], context.Options.MinRowRatio, () =>
                    Database.ExecuteAsync(connection,
                        $"""
                        INSERT IGNORE INTO `cms_pos_facility_staging` (`ccn`, `name`, `category`, `address`, `city`, `state`, `zip5`, `active`,
                          `termination_code`, `termination_date`)
                        SELECT `ccn`, LEFT(`name`, 150), `category`, LEFT(`address`, 150), LEFT(`city`, 60), `state`, IF(`zip` REGEXP '^[0-9][0-9][0-9][0-9][0-9]', LEFT(`zip`, 5), NULL),
                          IF(`part` = 2, COALESCE(`status`, '') <> 'Terminated', COALESCE(`termination_code`, '00') = '00'),
                          IF(`termination_code` REGEXP '^[0-9][0-9]$', `termination_code`, NULL), `termination_date`
                        FROM `{raw}` WHERE `ccn` IS NOT NULL ORDER BY `part` DESC
                        """, ct), ct);
                return counts["cms_pos_facility"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }
        finally
        {
            File.Delete(qiesPath);
            if (iqiesPath is not null)
            {
                File.Delete(iqiesPath);
            }
        }
    }

    private static async Task<(DatasetRelease Qies, DatasetRelease Iqies)> FindBothAsync(DatasetContext context, CancellationToken ct)
    {
        var catalog = await context.CmsCatalogAsync(ct);
        return (catalog.FindLatest(QiesTitle) ?? throw new InvalidDataException($"data.cms.gov lists no \"{QiesTitle}\" CSV."),
            catalog.FindLatest(IqiesTitle) ?? throw new InvalidDataException($"data.cms.gov lists no \"{IqiesTitle}\" CSV."));
    }
}
