using System.Globalization;
using System.Text;
using Dapper;
using MySqlConnector;
using Npi.Core.Search;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>Loading Care Compare's long measure files (one row per facility and measure) into the compact per-CCN tables.</summary>
internal static class MeasureLoad
{
    /// <summary>The scratch table every long file is loaded into: all text but the dates, converted when the kept rows are copied.</summary>
    public const string RawColumns =
        "`ccn` VARCHAR(10) NULL, `code` VARCHAR(100) NULL, `compared` VARCHAR(255) NULL, `cases` VARCHAR(255) NULL, `score` VARCHAR(255) NULL, " +
        "`start_date` DATE NULL, `end_date` DATE NULL";

    /// <summary>A number written with or without thousands separators ("1,458", "0.506", "-2.6"); anything else ("Not Available", "-") → NULL.</summary>
    public static string Number(string column) =>
        $"IF(REPLACE({column}, ',', '') REGEXP '^-?[0-9]+([.][0-9]+)?$', CAST(REPLACE({column}, ',', '') AS DOUBLE), NULL)";

    /// <summary>
    /// Copies the hospital rows of <paramref name="raw"/> whose measure is in <see cref="FacilityMeasures.Hospital"/> (IDs compared with "-" read
    /// as "_": the hospital file writes MSPB-1, the national file MSPB_1) into <paramref name="target"/>, one row per CCN and measure.
    /// </summary>
    public static Task KeepAsync(MySqlConnection connection, string raw, string target, IReadOnlyList<FacilityMeasureInfo> measures, CancellationToken ct) =>
        Database.ExecuteAsync(connection,
            $"""
            INSERT INTO `{target}` (`ccn`, `measure_id`, `score`, `compared`, `cases`, `start_date`, `end_date`)
            SELECT UPPER(`ccn`), REPLACE(`code`, '-', '_') AS `id`, MAX({Number("`score`")}), LEFT(MAX(`compared`), 60), ROUND(MAX({Number("`cases`")})),
                   MAX(`start_date`), MAX(`end_date`)
            FROM `{raw}`
            WHERE `ccn` IS NOT NULL AND REPLACE(`code`, '-', '_') IN @ids
            GROUP BY UPPER(`ccn`), `id`
            """, ct, param: new { ids = measures.Select(m => m.Id).ToList() });

    /// <summary>
    /// Copies the measures of <paramref name="measures"/> from <paramref name="raw"/>, whose rows each carry one value of a measure (its score,
    /// CMS's comparison, the patients or stays, the national value) under its own code, into one row per CCN and measure of <paramref name="target"/>.
    /// </summary>
    public static Task PivotAsync(MySqlConnection connection, string raw, string target, IReadOnlyList<PostAcuteMeasureInfo> measures, CancellationToken ct)
    {
        var codes = new StringBuilder();
        var param = new DynamicParameters();
        for (var i = 0; i < measures.Count; i++)
        {
            var m = measures[i];
            codes.Append(i == 0 ? "" : " UNION ALL ")
                .Append(CultureInfo.InvariantCulture, $"SELECT @id{i} AS `id`, @score{i} AS `score_code`, @compare{i} AS `compare_code`, @cases{i} AS `cases_code`, @national{i} AS `national_code`");
            param.Add($"id{i}", m.Info.Id);
            param.Add($"score{i}", m.ScoreCode);
            param.Add($"compare{i}", m.CompareCode);
            param.Add($"cases{i}", m.CasesCode);
            param.Add($"national{i}", m.NationalCode);
        }

        const string value = "r.`code` IN (m.`score_code`, m.`national_code`)";
        return Database.ExecuteAsync(connection,
            $"""
            INSERT INTO `{target}` (`ccn`, `measure_id`, `score`, `compared`, `cases`, `start_date`, `end_date`)
            SELECT UPPER(r.`ccn`), m.`id`, MAX(IF({value}, {Number("r.`score`")}, NULL)), LEFT(MAX(IF(r.`code` = m.`compare_code`, r.`score`, NULL)), 60),
                   ROUND(MAX(IF(r.`code` = m.`cases_code`, {Number("r.`score`")}, NULL))),
                   MAX(IF({value}, r.`start_date`, NULL)), MAX(IF({value}, r.`end_date`, NULL))
            FROM `{raw}` r
            JOIN ({codes}) m ON r.`code` IN (m.`score_code`, m.`compare_code`, m.`cases_code`, m.`national_code`)
            WHERE r.`ccn` IS NOT NULL
            GROUP BY UPPER(r.`ccn`), m.`id`
            """, ct, param: param);
    }
}

/// <summary>
/// Hospital measures beyond the star rating (docs/PRODUCT_PLAN.md step 3.6 part 2), from the Provider Data Catalog: "Complications and
/// Deaths", "Healthcare Associated Infections", "Unplanned Hospital Visits", "Timely and Effective Care" and "Medicare Spending Per
/// Beneficiary", each for every hospital and for the nation (ten files, ~115 MB). Only <see cref="FacilityMeasures.Hospital"/>'s measures are
/// kept, per CCN: the value, CMS's comparison with the nation, the patients (denominator or sample) and the period
/// (<c>cms_hospital_measure</c>, the national values under CCN "NATION").
/// </summary>
public sealed class HospitalMeasureSource : DatasetSource
{
    public const string ComplicationsId = "ynj2-r877";
    public const string ComplicationsNationalId = "qqw3-t4ie";
    public const string InfectionsId = "77hc-ibv8";
    public const string InfectionsNationalId = "yd3s-jyhd";
    public const string UnplannedId = "632h-zaca";
    public const string UnplannedNationalId = "cvcs-xecj";
    public const string TimelyId = "yv7e-xc69";
    public const string TimelyNationalId = "isrn-hqyy";
    public const string SpendingId = "rrqw-56er";
    public const string SpendingNationalId = "3n5g-6b7f";

    public override string Name => "cc_hospital_measures";

    private static CsvColumn Dates(string header, string column) => new(header, column, CsvValue.FlexibleDate);

    private static readonly CsvColumn[] Hospital = [new("Facility ID", "ccn"), new("Measure ID", "code"), new("Score", "score"), Dates("Start Date", "start_date"), Dates("End Date", "end_date")];

    private static readonly CsvColumn[] National = [new("Measure ID", "code"), Dates("Start Date", "start_date"), Dates("End Date", "end_date")];

    /// <summary>Each file: its dataset ID, the name it is saved under and its columns (beyond those every hospital or national file has).</summary>
    private static readonly (string Id, string File, CsvColumn[] Columns, bool National)[] Files =
    [
        (ComplicationsId, "cc_complications_deaths.csv", [.. Hospital, new("Compared to National", "compared"), new("Denominator", "cases")], false),
        (ComplicationsNationalId, "cc_complications_deaths_national.csv", [.. National, new("National Rate", "score")], true),
        (InfectionsId, "cc_infections.csv", [.. Hospital, new("Compared to National", "compared")], false),
        (InfectionsNationalId, "cc_infections_national.csv", [.. National, new("Score", "score")], true),
        (UnplannedId, "cc_unplanned_visits.csv", [.. Hospital, new("Compared to National", "compared"), new("Denominator", "cases")], false),
        (UnplannedNationalId, "cc_unplanned_visits_national.csv", [.. National, new("National Rate", "score")], true),
        (TimelyId, "cc_timely_effective.csv", [.. Hospital, new("Sample", "cases")], false),
        (TimelyNationalId, "cc_timely_effective_national.csv", [.. National, new("Score", "score")], true),
        (SpendingId, "cc_mspb.csv", Hospital, false),
        (SpendingNationalId, "cc_mspb_national.csv", [.. National, new("Score", "score")], true),
    ];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
        DialysisFacilitySource.Combined(await ReleasesAsync(context, ct));

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var releases = await ReleasesAsync(context, ct);
        return await WithDownloadsAsync(context, [.. releases.Select((r, i) => (Release: r, FileName: Files[i].File))], async (connection, paths) =>
        {
            const string raw = "cms_hospital_measure_raw_staging";
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` ({MeasureLoad.RawColumns}) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci", ct);
                for (var i = 0; i < Files.Length; i++)
                {
                    await CsvTableLoader.LoadAsync(connection, paths[i], raw, Files[i].Columns, ct,
                        constants: Files[i].National ? new Dictionary<string, string> { ["ccn"] = FacilityMeasures.National } : null);
                }

                var counts = await TableSwap.ReplaceAsync(connection, ["cms_hospital_measure"], context.Options.MinRowRatio,
                    () => MeasureLoad.KeepAsync(connection, raw, "cms_hospital_measure_staging", FacilityMeasures.Hospital, ct), ct);
                return counts["cms_hospital_measure"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }, ct);
    }

    private static async Task<IReadOnlyList<DatasetRelease>> ReleasesAsync(DatasetContext context, CancellationToken ct)
    {
        var releases = new List<DatasetRelease>();
        foreach (var file in Files)
        {
            releases.Add(await context.ProviderDataAsync(file.Id, ct));
        }

        return releases;
    }
}

/// <summary>
/// A hospital's Medicare fee-for-service volume (step 3.6 part 2), from the data.cms.gov catalog's newest data year of "Medicare Inpatient
/// Hospitals - by Provider" (discharges, patients, covered days, payments per CCN), "Medicare Inpatient Hospitals - by Provider and
/// Service" (discharges and average payments per DRG) and "Medicare Outpatient Hospitals - by Provider and Service" (services and average
/// payments per APC; CMS publishes no outpatient by-provider file, so the totals are summed over the APCs it lists): one row per CCN in
/// <c>cms_hospital_volume</c>, the top ten DRGs and APCs in <c>cms_hospital_service</c>.
/// </summary>
public sealed class HospitalVolumeSource : DatasetSource
{
    public const string InpatientTitle = "Medicare Inpatient Hospitals - by Provider";
    public const string InpatientServiceTitle = "Medicare Inpatient Hospitals - by Provider and Service";
    public const string OutpatientServiceTitle = "Medicare Outpatient Hospitals - by Provider and Service";

    /// <summary>Top services kept per hospital and setting.</summary>
    public const int TopServices = 10;

    private static readonly string[] Titles = [InpatientTitle, InpatientServiceTitle, OutpatientServiceTitle];

    public override string Name => "cms_hospital_volume";

    private static readonly CsvColumn[] InpatientColumns =
    [
        new("Rndrng_Prvdr_CCN", "ccn"), new("Tot_Benes", "beneficiaries", CsvValue.OptionalWholeNumber), new("Tot_Dschrgs", "discharges", CsvValue.OptionalWholeNumber),
        new("Tot_Cvrd_Days", "covered_days", CsvValue.OptionalWholeNumber), new("Tot_Pymt_Amt", "total_payments", CsvValue.OptionalNumber),
        new("Tot_Mdcr_Pymt_Amt", "medicare_payments", CsvValue.OptionalNumber),
    ];

    private static readonly CsvColumn[] InpatientServiceColumns =
    [
        new("Rndrng_Prvdr_CCN", "ccn"), new("DRG_Cd", "code"), new("DRG_Desc", "description"), new("Tot_Dschrgs", "cases", CsvValue.OptionalWholeNumber),
        new("Avg_Mdcr_Pymt_Amt", "avg_medicare_payment", CsvValue.OptionalNumber),
    ];

    private static readonly CsvColumn[] OutpatientServiceColumns =
    [
        new("Rndrng_Prvdr_CCN", "ccn"), new("APC_Cd", "code"), new("APC_Desc", "description"), new("Bene_Cnt", "beneficiaries", CsvValue.OptionalWholeNumber),
        new("CAPC_Srvcs", "cases", CsvValue.OptionalWholeNumber), new("Avg_Mdcr_Pymt_Amt", "avg_medicare_payment", CsvValue.OptionalNumber),
    ];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
        DialysisFacilitySource.Combined(await ReleasesAsync(context, ct));

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var r = await ReleasesAsync(context, ct);
        var (inpatientYear, outpatientYear) = (r[0].DataYear, r[2].DataYear);
        return await WithDownloadsAsync(context,
            [(r[0], "cms_inpatient_by_provider.csv"), (r[1], "cms_inpatient_by_service.csv"), (r[2], "cms_outpatient_by_service.csv")], async (connection, paths) =>
        {
            const string totals = "cms_inpatient_total_raw_staging";
            const string services = "cms_hospital_service_raw_staging";
            try
            {
                foreach (var table in new[] { totals, services })
                {
                    await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{table}`", ct);
                }

                await Database.ExecuteAsync(connection,
                    $"""
                    CREATE TABLE `{totals}` (`ccn` VARCHAR(10) NULL, `beneficiaries` INT NULL, `discharges` INT NULL, `covered_days` INT NULL,
                      `total_payments` DOUBLE NULL, `medicare_payments` DOUBLE NULL) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
                    """, ct);
                await Database.ExecuteAsync(connection,
                    $"""
                    CREATE TABLE `{services}` (`setting` CHAR(1) NOT NULL, `ccn` VARCHAR(10) NULL, `code` VARCHAR(20) NULL, `description` VARCHAR(500) NULL,
                      `beneficiaries` INT NULL, `cases` INT NULL, `avg_medicare_payment` DOUBLE NULL) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
                    """, ct);
                await CsvTableLoader.LoadAsync(connection, paths[0], totals, InpatientColumns, ct);
                await CsvTableLoader.LoadAsync(connection, paths[1], services, InpatientServiceColumns, ct, constants: new Dictionary<string, string> { ["setting"] = "I" });
                await CsvTableLoader.LoadAsync(connection, paths[2], services, OutpatientServiceColumns, ct, constants: new Dictionary<string, string> { ["setting"] = "O" });

                var counts = await TableSwap.ReplaceAsync(connection, ["cms_hospital_volume", "cms_hospital_service"], context.Options.MinRowRatio, async () =>
                {
                    await Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `cms_hospital_volume_staging` (`ccn`, `inpatient_year`, `discharges`, `beneficiaries`, `covered_days`, `medicare_payments`, `total_payments`)
                        SELECT UPPER(`ccn`), @year, MAX(`discharges`), MAX(`beneficiaries`), MAX(`covered_days`), MAX(`medicare_payments`), MAX(`total_payments`)
                        FROM `{totals}` WHERE `ccn` IS NOT NULL GROUP BY UPPER(`ccn`)
                        """, ct, param: new { year = inpatientYear });
                    await Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `cms_hospital_volume_staging` (`ccn`, `outpatient_year`, `outpatient_services`, `outpatient_payments`)
                        SELECT * FROM (
                          SELECT UPPER(`ccn`) AS `o_ccn`, @year AS `o_year`, SUM(`cases`) AS `o_services`, SUM(`cases` * `avg_medicare_payment`) AS `o_payments`
                          FROM `{services}` WHERE `setting` = 'O' AND `ccn` IS NOT NULL GROUP BY UPPER(`ccn`)) o
                        ON DUPLICATE KEY UPDATE `outpatient_year` = `o_year`, `outpatient_services` = `o_services`, `outpatient_payments` = `o_payments`
                        """, ct, param: new { year = outpatientYear });
                    await Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `cms_hospital_service_staging` (`ccn`, `setting`, `service_rank`, `code`, `description`, `cases`, `beneficiaries`, `avg_medicare_payment`)
                        SELECT `c`, `setting`, `rk`, `code`, LEFT(`description`, 255), `cases`, `beneficiaries`, `avg_medicare_payment`
                        FROM (SELECT UPPER(`ccn`) AS `c`, `setting`, `code`, `description`, `cases`, `beneficiaries`, `avg_medicare_payment`,
                                     ROW_NUMBER() OVER (PARTITION BY UPPER(`ccn`), `setting` ORDER BY `cases` DESC, `code`) AS `rk`
                              FROM `{services}` WHERE `ccn` IS NOT NULL AND `code` IS NOT NULL AND `cases` IS NOT NULL) x
                        WHERE `rk` <= {TopServices}
                        """, ct);
                }, ct);
                return counts["cms_hospital_volume"];
            }
            finally
            {
                foreach (var table in new[] { totals, services })
                {
                    await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{table}`", CancellationToken.None);
                }
            }
        }, ct);
    }

    private static async Task<IReadOnlyList<DatasetRelease>> ReleasesAsync(DatasetContext context, CancellationToken ct)
    {
        var catalog = await context.CmsCatalogAsync(ct);
        return [.. Titles.Select(t => catalog.FindLatest(t) ?? throw new InvalidDataException($"data.cms.gov lists no \"{t}\" CSV."))];
    }
}
