using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// Care Compare Doctors and Clinicians National Downloadable File (CLAUDE.md §7 Stage 5.5 item 3a). The file
/// has one row per clinician × enrollment × group × address (~3.4M rows for ~1.6M NPIs); it is loaded raw
/// and summarized into cc_clinician (one row per NPI) and cc_group (one row per NPI and group practice).
/// </summary>
public sealed class CareCompareClinicianSource : DatasetSource
{
    public const string DatasetId = "mj5m-pzi6";

    public override string Name => "cc_clinicians";

    private static readonly CsvColumn[] Columns =
    [
        new("NPI", "npi", CsvValue.Npi), new("Ind_PAC_ID", "ind_pac_id"), new("Med_sch", "medical_school"),
        new("Grd_yr", "graduation_year", CsvValue.OptionalWholeNumber), new("pri_spec", "primary_specialty"),
        new("sec_spec_all", "secondary_specialties"), new("Telehlth", "telehealth"), new("Facility Name", "group_name"),
        new("org_pac_id", "org_pac_id"), new("num_org_mem", "group_members", CsvValue.OptionalWholeNumber),
        new("City/Town", "city"), new("State", "state"), new("ind_assgn", "ind_assignment"), new("grp_assgn", "grp_assignment"),
    ];

    public override Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) => context.ProviderDataAsync(DatasetId, ct);

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "cc_clinicians.csv", async (connection, path) =>
        {
            const string raw = "cc_dac_raw_staging";
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` LIKE `cc_dac_raw`", ct);
                var rows = await CsvTableLoader.LoadAsync(connection, path, raw, Columns, ct);
                context.Log.Information("Loaded {Rows:N0} Care Compare clinician rows", rows);

                var counts = await TableSwap.ReplaceAsync(connection, ["cc_clinician", "cc_group"], context.Options.MinRowRatio, async () =>
                {
                    await Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `cc_clinician_staging` (`npi`, `ind_pac_id`, `medical_school`, `graduation_year`, `primary_specialty`,
                          `secondary_specialties`, `accepts_assignment`, `telehealth`)
                        SELECT `npi`, MAX(`ind_pac_id`), MAX(`medical_school`), MAX(`graduation_year`), MAX(`primary_specialty`),
                          MAX(`secondary_specialties`), COALESCE(MAX(`ind_assignment` = 'Y'), 0), COALESCE(MAX(`telehealth` = 'Y'), 0)
                        FROM `{raw}` GROUP BY `npi`
                        """, ct);
                    await Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `cc_group_staging` (`npi`, `org_pac_id`, `group_name`, `members`, `accepts_assignment`, `city`, `state`)
                        SELECT `npi`, `org_pac_id`, MAX(`group_name`), MAX(`group_members`), COALESCE(MAX(`grp_assignment` = 'Y'), 0), MIN(`city`), MIN(`state`)
                        FROM `{raw}` WHERE `org_pac_id` IS NOT NULL GROUP BY `npi`, `org_pac_id`
                        """, ct);
                }, ct);
                return counts["cc_clinician"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }, ct);
}

/// <summary>Care Compare Facility Affiliation Data: clinician NPI → facility CCN (Stage 5.5 item 3b).</summary>
public sealed class FacilityAffiliationSource : DatasetSource
{
    public const string DatasetId = "27ea-46a8";

    public override string Name => "cc_facility_affiliations";

    private static readonly CsvColumn[] Columns =
    [
        new("NPI", "npi", CsvValue.Npi), new("facility_type", "facility_type"),
        new("Facility Affiliations Certification Number", "ccn"), new("Facility Type Certification Number", "parent_ccn"),
    ];

    public override Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) => context.ProviderDataAsync(DatasetId, ct);

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        ReplaceOneAsync(context, release, "cc_facility_affiliations.csv", "cc_facility_affiliation", Columns, ct);

    internal static Task<long> ReplaceOneAsync(DatasetContext context, DatasetRelease release, string fileName, string table,
        IReadOnlyList<CsvColumn> columns, CancellationToken ct, string characterSet = "utf8mb4") =>
        WithDownloadAsync(context, release, fileName, async (connection, path) =>
        {
            var counts = await TableSwap.ReplaceAsync(connection, [table], context.Options.MinRowRatio,
                () => CsvTableLoader.LoadAsync(connection, path, table + "_staging", columns, ct, characterSet), ct);
            return counts[table];
        }, ct);
}

/// <summary>Care Compare Hospital General Information: type, ownership, emergency services, star rating (Stage 5.5 item 4a).</summary>
public sealed class HospitalSource : DatasetSource
{
    public const string DatasetId = "xubh-q36u";

    public override string Name => "cc_hospitals";

    private static readonly CsvColumn[] Columns =
    [
        new("Facility ID", "ccn"), new("Facility Name", "name"), new("Address", "address"), new("City/Town", "city"), new("State", "state"),
        new("ZIP Code", "zip"), new("Telephone Number", "phone"), new("Hospital Type", "hospital_type"), new("Hospital Ownership", "ownership"),
        new("Emergency Services", "emergency_services", CsvValue.YesNo), new("Hospital overall rating", "overall_rating", CsvValue.OptionalWholeNumber),
    ];

    public override Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) => context.ProviderDataAsync(DatasetId, ct);

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        FacilityAffiliationSource.ReplaceOneAsync(context, release, "cc_hospitals.csv", "cms_hospital", Columns, ct);
}

/// <summary>Care Compare Nursing Home Provider Information: beds, ownership and the five-star ratings (Stage 5.5 item 4a).</summary>
public sealed class NursingHomeSource : DatasetSource
{
    public const string DatasetId = "4pq5-n9py";

    public override string Name => "cc_nursing_homes";

    private static readonly CsvColumn[] Columns =
    [
        new("CMS Certification Number (CCN)", "ccn"), new("Provider Name", "name"), new("Provider Address", "address"), new("City/Town", "city"),
        new("State", "state"), new("ZIP Code", "zip"), new("Telephone Number", "phone"), new("Provider Type", "provider_type"),
        new("Ownership Type", "ownership"), new("Number of Certified Beds", "certified_beds", CsvValue.OptionalWholeNumber),
        new("Overall Rating", "overall_rating", CsvValue.OptionalWholeNumber), new("Health Inspection Rating", "inspection_rating", CsvValue.OptionalWholeNumber),
        new("Staffing Rating", "staffing_rating", CsvValue.OptionalWholeNumber), new("QM Rating", "quality_rating", CsvValue.OptionalWholeNumber),
    ];

    public override Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) => context.ProviderDataAsync(DatasetId, ct);

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        FacilityAffiliationSource.ReplaceOneAsync(context, release, "cc_nursing_homes.csv", "cms_nursing_home", Columns, ct);
}

/// <summary>
/// CMS Hospital Enrollments + Skilled Nursing Facility Enrollments: which organization NPI holds which CCN
/// (Stage 5.5 item 4b). Both files are Windows-1252, and both feed cms_facility_npi, so they are one source.
/// </summary>
public sealed class FacilityEnrollmentSource : DatasetSource
{
    public const string HospitalTitle = "Hospital Enrollments";
    public const string SnfTitle = "Skilled Nursing Facility Enrollments";

    public override string Name => "cms_facility_enrollments";

    private static readonly CsvColumn[] Columns = [new("NPI", "npi", CsvValue.Npi), new("CCN", "ccn")];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var (hospitals, snfs) = await FindBothAsync(context, ct);
        return new DatasetRelease($"{hospitals.Version} | {snfs.Version}", hospitals.Url);
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var (hospitals, snfs) = await FindBothAsync(context, ct);
        var hospitalFile = await context.DownloadAsync(hospitals, "cms_hospital_enrollments.csv", ct);
        var snfFile = await context.DownloadAsync(snfs, "cms_snf_enrollments.csv", ct);
        try
        {
            await using var connection = await context.Database.OpenAsync(ct);
            var counts = await TableSwap.ReplaceAsync(connection, ["cms_facility_npi"], context.Options.MinRowRatio, async () =>
            {
                await CsvTableLoader.LoadAsync(connection, hospitalFile, "cms_facility_npi_staging", Columns, ct, "latin1",
                    new Dictionary<string, string> { ["kind"] = "hospital" });
                await CsvTableLoader.LoadAsync(connection, snfFile, "cms_facility_npi_staging", Columns, ct, "latin1",
                    new Dictionary<string, string> { ["kind"] = "nursing_home" });
            }, ct);
            return counts["cms_facility_npi"];
        }
        finally
        {
            File.Delete(hospitalFile);
            File.Delete(snfFile);
        }
    }

    private static async Task<(DatasetRelease Hospitals, DatasetRelease Snfs)> FindBothAsync(DatasetContext context, CancellationToken ct)
    {
        var catalog = await context.CmsCatalogAsync(ct);
        return (catalog.FindLatest(HospitalTitle) ?? throw new InvalidDataException($"data.cms.gov lists no \"{HospitalTitle}\" CSV."),
            catalog.FindLatest(SnfTitle) ?? throw new InvalidDataException($"data.cms.gov lists no \"{SnfTitle}\" CSV."));
    }
}
