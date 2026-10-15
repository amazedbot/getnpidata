using MySqlConnector;
using Npi.Core.Search;
using Npi.Loader.Csv;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// Who owns and runs each Medicare-certified facility (docs/PRODUCT_PLAN.md step 3.6 part 2): the data.cms.gov "All Owners" files of
/// hospitals, skilled nursing facilities, home health agencies, hospices, FQHCs and rural health clinics (each ~10–55 MB, one row per
/// enrollment, owner and role; Latin-1; the newest release by its period). → facility_owner and facility_owner_entity, replaced
/// together. Owners join to facilities by enrollment ID through cms_facility_enrollment when a page is read, so a reload of either
/// side needs nothing else.
/// </summary>
public sealed class FacilityOwnerSource : DatasetSource
{
    /// <summary>Catalog title → facility type (Npi.Core FacilityTypes codes).</summary>
    public static readonly IReadOnlyList<(string Title, string Kind)> Files =
    [
        ("Hospital All Owners", FacilityTypes.Hospital),
        ("Skilled Nursing Facility All Owners", FacilityTypes.NursingHome),
        ("Home Health Agency All Owners", FacilityTypes.HomeHealth),
        ("Hospice All Owners", FacilityTypes.Hospice),
        ("Federally Qualified Health Center All Owners", FacilityTypes.Fqhc),
        ("Rural Health Clinic All Owners", FacilityTypes.Rhc),
    ];

    /// <summary>The columns every All Owners and Change of Ownership owner file has.</summary>
    internal static readonly CsvColumn[] OwnerColumns =
    [
        new("ENROLLMENT ID", "enrollment_id"), new("ASSOCIATE ID", "facility_associate_id"), new("ORGANIZATION NAME", "facility_name"),
        new("ASSOCIATE ID - OWNER", "owner_id"), new("TYPE - OWNER", "owner_type"), new("ROLE CODE - OWNER", "role_code"),
        new("ROLE TEXT - OWNER", "role_text"), new("ASSOCIATION DATE - OWNER", "association_date", CsvValue.FlexibleDate),
        new("FIRST NAME - OWNER", "first_name"), new("MIDDLE NAME - OWNER", "middle_name"), new("LAST NAME - OWNER", "last_name"),
        new("TITLE - OWNER", "title"), new("ORGANIZATION NAME - OWNER", "org_name"), new("DOING BUSINESS AS NAME - OWNER", "dba_name"),
        new("CITY - OWNER", "city"), new("STATE - OWNER", "state"), new("PERCENTAGE OWNERSHIP", "percentage", CsvValue.OptionalNumber),
    ];

    /// <summary>
    /// The owner-type columns (Y/N, organizations only) → <see cref="OwnerKinds"/> codes. Not every file has every one: the nursing home
    /// file adds trust and parent company, the change-of-ownership owner files lack private equity, REIT and chain home office.
    /// </summary>
    public static readonly IReadOnlyList<(string Header, string Code)> KindColumns =
    [
        ("PRIVATE EQUITY COMPANY - OWNER", "private_equity"), ("REIT - OWNER", "reit"), ("INVESTMENT FIRM - OWNER", "investment_firm"),
        ("HOLDING COMPANY - OWNER", "holding_company"), ("CHAIN HOME OFFICE - OWNER", "chain_home_office"),
        ("MANAGEMENT SERVICES COMPANY - OWNER", "management_company"), ("MEDICAL STAFFING COMPANY - OWNER", "staffing_company"),
        ("FINANCIAL INSTITUTION - OWNER", "financial_institution"), ("CONSULTING FIRM - OWNER", "consulting_firm"),
        ("MEDICAL PROVIDER SUPPLIER - OWNER", "medical_provider"), ("CREATED FOR ACQUISITION - OWNER", "created_for_acquisition"),
        ("PARENT COMPANY - OWNER", "parent_company"), ("TRUST OR TRUSTEE - OWNER", "trust"), ("FOR PROFIT - OWNER", "for_profit"),
        ("NON PROFIT - OWNER", "non_profit"), ("CORPORATION - OWNER", "corporation"), ("LLC - OWNER", "llc"),
    ];

    public override string Name => "cms_facility_owners";

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var releases = await FindAsync(context, Files, ct);
        return new DatasetRelease(string.Join(" | ", releases.Select(r => r.Release.Version)), releases[0].Release.Url);
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var files = new List<(string Path, string Kind, string? PeriodEnd)>();
        var loaded = false;
        const string raw = "facility_owner_raw_staging";
        try
        {
            foreach (var (found, kind) in await FindAsync(context, Files, ct))
            {
                files.Add((await context.DownloadAsync(found, $"cms_{kind}_all_owners.csv", ct), kind, found.PeriodEnd));
            }

            await using var connection = await context.Database.OpenAsync(ct);
            try
            {
                await CreateOwnerRawAsync(connection, raw, ct);
                foreach (var (path, kind, periodEnd) in files)
                {
                    var rows = await LoadOwnerFileAsync(connection, path, raw, kind, periodEnd, ct);
                    context.Log.Information("Loaded {Rows:N0} {Kind} owner rows", rows, kind);
                }

                var counts = await TableSwap.ReplaceAsync(connection, ["facility_owner", "facility_owner_entity"], context.Options.MinRowRatio, async () =>
                {
                    await Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `facility_owner_staging` (`enrollment_id`, `owner_id`, `role_code`, `kind`, `facility_associate_id`, `facility_name`,
                          `owner_type`, `role_text`, `association_date`, `name`, `title`, `dba_name`, `city`, `state`, `percentage`, `owner_kinds`, `period_end`)
                        SELECT `enrollment_id`, LPAD(`owner_id`, 10, '0'), `role_code`, MIN(`kind`), MAX(LPAD(`facility_associate_id`, 10, '0')),
                          LEFT(MAX(`facility_name`), 200), MAX(`owner_type`), LEFT(MAX(`role_text`), 100), MIN(`association_date`), LEFT(MAX({NameSql}), 200),
                          LEFT(MAX(`title`), 100), LEFT(MAX(`dba_name`), 200), LEFT(MAX(`city`), 60), LEFT(MAX(`state`), 2), MAX(`percentage`),
                          MAX({KindsSql()}), MAX(`period_end`)
                        FROM `{raw}`
                        WHERE {ValidOwnerSql}
                        GROUP BY `enrollment_id`, LPAD(`owner_id`, 10, '0'), `role_code`
                        """, ct);
                    await Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `facility_owner_entity_staging` (`owner_id`, `owner_type`, `name`, `dba_name`, `city`, `state`, `owner_kinds`,
                          `facilities`, `owned`)
                        SELECT `owner_id`, MAX(`owner_type`), COALESCE(MAX(`name`), CONCAT('Owner ', `owner_id`)), MAX(`dba_name`), MAX(`city`), MAX(`state`),
                          MAX(`owner_kinds`), COUNT(DISTINCT `enrollment_id`),
                          COUNT(DISTINCT CASE WHEN `role_code` IN ({CodeList(OwnerRoles.OwnershipCodes)}) THEN `enrollment_id` END)
                        FROM `facility_owner_staging` GROUP BY `owner_id`
                        """, ct);
                }, ct, log: context.Log);
                loaded = true;
                return counts["facility_owner"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }
        finally
        {
            // Kept after a failed load: the next attempt downloads them again only if they changed.
            if (loaded)
            {
                foreach (var (path, _, _) in files)
                {
                    FileDownloader.Delete(path);
                }
            }
        }
    }

    /// <summary>An owner row worth keeping: an enrollment, a role, an owner ID of digits and an individual (I) or organization (O).</summary>
    internal const string ValidOwnerSql =
        "`enrollment_id` IS NOT NULL AND CHAR_LENGTH(`enrollment_id`) <= 20 AND `role_code` IS NOT NULL AND CHAR_LENGTH(`role_code`) <= 4 " +
        "AND `owner_id` REGEXP '^[0-9]+$' AND CHAR_LENGTH(`owner_id`) <= 10 AND `owner_type` IN ('I', 'O')";

    /// <summary>A person's first, middle and last name; an organization's name (else its doing-business-as name).</summary>
    internal const string NameSql =
        "CASE WHEN `owner_type` = 'I' THEN NULLIF(CONCAT_WS(' ', `first_name`, `middle_name`, `last_name`), '') ELSE COALESCE(`org_name`, `dba_name`) END";

    /// <summary>The owner kinds marked Y, as a comma list of codes (NULL when none).</summary>
    internal static string KindsSql() =>
        $"NULLIF(CONCAT_WS(',', {string.Join(", ", KindColumns.Select(k => $"IF(`f_{k.Code}` = 1, '{k.Code}', NULL)"))}), '')";

    internal static string CodeList(IEnumerable<string> codes) => string.Join(", ", codes.Select(c => $"'{c}'"));

    /// <summary>The scratch table owner files load into (every column text, wide enough that nothing is cut).</summary>
    internal static async Task CreateOwnerRawAsync(MySqlConnection connection, string table, CancellationToken ct)
    {
        await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{table}`", ct);
        await Database.ExecuteAsync(connection,
            $"""
            CREATE TABLE `{table}` (
              `kind` VARCHAR(20) NOT NULL, `period_end` DATE NULL,
              `enrollment_id` VARCHAR(40) NULL, `facility_associate_id` VARCHAR(20) NULL, `facility_name` VARCHAR(255) NULL,
              `owner_id` VARCHAR(20) NULL, `owner_type` VARCHAR(4) NULL, `role_code` VARCHAR(8) NULL, `role_text` VARCHAR(255) NULL,
              `association_date` DATE NULL, `first_name` VARCHAR(100) NULL, `middle_name` VARCHAR(100) NULL, `last_name` VARCHAR(100) NULL,
              `title` VARCHAR(255) NULL, `org_name` VARCHAR(255) NULL, `dba_name` VARCHAR(255) NULL, `city` VARCHAR(100) NULL, `state` VARCHAR(10) NULL,
              `percentage` DOUBLE NULL,
              {string.Join(", ", KindColumns.Select(k => $"`f_{k.Code}` TINYINT NULL"))}
            ) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
            """, ct);
    }

    /// <summary>Loads one owner file into the scratch table: the common columns and whichever owner-type columns it has.</summary>
    internal static async Task<long> LoadOwnerFileAsync(MySqlConnection connection, string path, string table, string kind, string? periodEnd, CancellationToken ct)
    {
        CsvHeader header;
        await using (var stream = File.OpenRead(path))
        {
            header = CsvHeader.Read(stream);
        }

        var present = header.Columns.Select(c => c.Trim().TrimStart('﻿').Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = OwnerColumns
            .Concat(KindColumns.Where(k => present.Contains(k.Header)).Select(k => new CsvColumn(k.Header, "f_" + k.Code, CsvValue.YesNo)))
            .ToList();
        var constants = new Dictionary<string, string> { ["kind"] = kind };
        if (periodEnd is { Length: 10 })
        {
            constants["period_end"] = periodEnd;
        }

        return await CsvTableLoader.LoadAsync(connection, path, table, columns, ct, "latin1", constants); // CMS writes these files in Latin-1
    }

    internal static async Task<IReadOnlyList<(DatasetRelease Release, string Kind)>> FindAsync(DatasetContext context,
        IReadOnlyList<(string Title, string Kind)> files, CancellationToken ct)
    {
        var catalog = await context.CmsCatalogAsync(ct);
        return files.Select(f => (catalog.FindLatest(f.Title) ?? throw new InvalidDataException($"data.cms.gov lists no \"{f.Title}\" CSV."), f.Kind))
            .ToList();
    }
}

/// <summary>
/// Facilities' ownership history (docs/PRODUCT_PLAN.md step 3.6 part 2): the data.cms.gov Hospital and Skilled Nursing Facility
/// "Change of Ownership" files (every change since 2016, buyer and seller; under 2 MB) → facility_ownership_change, and their "Owner
/// Information" files (the owners of every buyer and seller; 4 and 22 MB) → facility_ownership_change_owner, the sellers' owners in an
/// ownership or control role (the previous owners; the buyers' are the current ones). CMS publishes no such file for the other
/// facility types. Replaced together.
/// </summary>
public sealed class OwnershipChangeSource : DatasetSource
{
    public static readonly IReadOnlyList<(string Title, string Kind)> ChangeFiles =
    [
        ("Hospital Change of Ownership", FacilityTypes.Hospital),
        ("Skilled Nursing Facility Change of Ownership", FacilityTypes.NursingHome),
    ];

    public static readonly IReadOnlyList<(string Title, string Kind)> OwnerFiles =
    [
        ("Hospital Change of Ownership - Owner Information", FacilityTypes.Hospital),
        ("Skilled Nursing Facility Change of Ownership - Owner Information", FacilityTypes.NursingHome),
    ];

    private static readonly CsvColumn[] ChangeColumns =
    [
        new("ENROLLMENT ID - BUYER", "buyer_enrollment_id"), new("CCN - BUYER", "buyer_ccn"), new("NPI - BUYER", "buyer_npi", CsvValue.Npi),
        new("ASSOCIATE ID - BUYER", "buyer_associate_id"), new("ORGANIZATION NAME - BUYER", "buyer_name"),
        new("DOING BUSINESS AS NAME - BUYER", "buyer_dba_name"), new("CHOW TYPE CODE", "chow_type_code"), new("CHOW TYPE TEXT", "chow_type"),
        new("EFFECTIVE DATE", "effective_date", CsvValue.FlexibleDate), new("ENROLLMENT ID - SELLER", "seller_enrollment_id"),
        new("CCN - SELLER", "seller_ccn"), new("NPI - SELLER", "seller_npi", CsvValue.Npi), new("ASSOCIATE ID - SELLER", "seller_associate_id"),
        new("ORGANIZATION NAME - SELLER", "seller_name"), new("DOING BUSINESS AS NAME - SELLER", "seller_dba_name"),
    ];

    public override string Name => "cms_ownership_changes";

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var releases = await FindAllAsync(context, ct);
        return new DatasetRelease(string.Join(" | ", releases.Select(r => r.Release.Version)), releases[0].Release.Url);
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var files = new List<(string Path, string Kind, string? PeriodEnd, bool Owners)>();
        var loaded = false;
        const string raw = "facility_ownership_change_owner_raw_staging";
        try
        {
            var found = await FindAllAsync(context, ct);
            foreach (var (r, kind, owners) in found)
            {
                files.Add((await context.DownloadAsync(r, $"cms_{kind}_{(owners ? "chow_owners" : "chow")}.csv", ct), kind, r.PeriodEnd, owners));
            }

            await using var connection = await context.Database.OpenAsync(ct);
            try
            {
                await FacilityOwnerSource.CreateOwnerRawAsync(connection, raw, ct);
                foreach (var (path, kind, periodEnd, _) in files.Where(f => f.Owners))
                {
                    await FacilityOwnerSource.LoadOwnerFileAsync(connection, path, raw, kind, periodEnd, ct);
                }

                var counts = await TableSwap.ReplaceAsync(connection, ["facility_ownership_change", "facility_ownership_change_owner"], context.Options.MinRowRatio,
                    async () =>
                    {
                        foreach (var (path, kind, periodEnd, _) in files.Where(f => !f.Owners))
                        {
                            var constants = new Dictionary<string, string> { ["kind"] = kind };
                            if (periodEnd is { Length: 10 })
                            {
                                constants["period_end"] = periodEnd;
                            }

                            await CsvTableLoader.LoadAsync(connection, path, "facility_ownership_change_staging", ChangeColumns, ct, "latin1", constants);
                        }

                        // The files drop leading zeros of some associate IDs; stored as ten digits, like the owners'.
                        await Database.ExecuteAsync(connection,
                            """
                            UPDATE `facility_ownership_change_staging`
                            SET `buyer_associate_id` = LPAD(`buyer_associate_id`, 10, '0'), `seller_associate_id` = LPAD(`seller_associate_id`, 10, '0')
                            """, ct);
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `facility_ownership_change_owner_staging` (`enrollment_id`, `owner_id`, `role_code`, `owner_type`, `name`, `role_text`, `percentage`)
                            SELECT `enrollment_id`, LPAD(`owner_id`, 10, '0'), `role_code`, MAX(`owner_type`), LEFT(MAX({FacilityOwnerSource.NameSql}), 200),
                              LEFT(MAX(`role_text`), 100), MAX(`percentage`)
                            FROM `{raw}`
                            WHERE {FacilityOwnerSource.ValidOwnerSql}
                              AND `role_code` IN ({FacilityOwnerSource.CodeList(OwnerRoles.OwnershipCodes.Concat(OwnerRoles.ControlCodes))})
                              AND `enrollment_id` IN (SELECT `seller_enrollment_id` FROM `facility_ownership_change_staging` WHERE `seller_enrollment_id` IS NOT NULL)
                            GROUP BY `enrollment_id`, LPAD(`owner_id`, 10, '0'), `role_code`
                            """, ct);
                    }, ct, log: context.Log);
                loaded = true;
                return counts["facility_ownership_change"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }
        finally
        {
            if (loaded)
            {
                foreach (var file in files)
                {
                    FileDownloader.Delete(file.Path);
                }
            }
        }
    }

    private static async Task<IReadOnlyList<(DatasetRelease Release, string Kind, bool Owners)>> FindAllAsync(DatasetContext context, CancellationToken ct)
    {
        var changes = await FacilityOwnerSource.FindAsync(context, ChangeFiles, ct);
        var owners = await FacilityOwnerSource.FindAsync(context, OwnerFiles, ct);
        return [.. changes.Select(c => (c.Release, c.Kind, false)), .. owners.Select(o => (o.Release, o.Kind, true))];
    }
}

/// <summary>
/// Nursing home inspections and penalties (docs/PRODUCT_PLAN.md step 3.6 part 2), from the Provider Data Catalog: "Health
/// Deficiencies" (r5ix-sfxw, ~420,000 citations, 165 MB), "Fire Safety Deficiencies" (ifjz-ge4w, ~200,000, 70 MB), the last three
/// standard inspection cycles, and "Penalties" (g6vv-u9sr, ~15,000 fines and payment denials of the last three years). The citations
/// are summarized per CCN: every inspection, counts by cycle and scope and severity, the ten most serious and ten newest citations,
/// and each tag's wording once. UTF-8. Replaced together.
/// </summary>
public sealed class NursingHomeInspectionSource : DatasetSource
{
    public const string HealthDatasetId = "r5ix-sfxw";
    public const string FireSafetyDatasetId = "ifjz-ge4w";
    public const string PenaltiesDatasetId = "g6vv-u9sr";

    /// <summary>How many of the most serious, and of the newest, citations are kept per nursing home.</summary>
    public const int CitationsKept = 10;

    private static readonly CsvColumn[] DeficiencyColumns =
    [
        new("CMS Certification Number (CCN)", "ccn"), new("Survey Date", "survey_date", CsvValue.FlexibleDate), new("Deficiency Prefix", "prefix"),
        new("Deficiency Category", "category"), new("Deficiency Tag Number", "tag"), new("Deficiency Description", "description"),
        new("Scope Severity Code", "scope_severity"), new("Deficiency Corrected", "corrected"), new("Correction Date", "correction_date", CsvValue.FlexibleDate),
        new("Inspection Cycle", "cycle", CsvValue.OptionalWholeNumber), new("Complaint Deficiency", "complaint", CsvValue.YesNo),
        new("Infection Control Inspection Deficiency", "infection_control", CsvValue.YesNo),
    ];

    private static readonly CsvColumn[] PenaltyColumns =
    [
        new("CMS Certification Number (CCN)", "ccn"), new("Penalty Date", "penalty_date", CsvValue.FlexibleDate), new("Penalty Type", "penalty_type"),
        new("Fine Amount", "fine_amount", CsvValue.OptionalNumber), new("Payment Denial Start Date", "denial_start", CsvValue.FlexibleDate),
        new("Payment Denial Length in Days", "denial_days", CsvValue.OptionalWholeNumber),
    ];

    public override string Name => OwnershipService.InspectionsSource;

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var (health, fire, penalties) = await FindAllAsync(context, ct);
        return new DatasetRelease($"{health.Version} | {fire.Version} | {penalties.Version}", health.Url);
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var (health, fire, penalties) = await FindAllAsync(context, ct);
        var paths = new List<string>();
        var loaded = false;
        const string raw = "nh_deficiency_raw_staging";
        const string penaltyRaw = "nh_penalty_raw_staging";
        try
        {
            var healthPath = await context.DownloadAsync(health, "nh_health_deficiencies.csv", ct);
            paths.Add(healthPath);
            var firePath = await context.DownloadAsync(fire, "nh_fire_safety_deficiencies.csv", ct);
            paths.Add(firePath);
            var penaltiesPath = await context.DownloadAsync(penalties, "nh_penalties.csv", ct);
            paths.Add(penaltiesPath);

            await using var connection = await context.Database.OpenAsync(ct);
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{penaltyRaw}`", ct);
                await Database.ExecuteAsync(connection,
                    $"""
                    CREATE TABLE `{raw}` (
                      `kind` VARCHAR(6) NOT NULL, `ccn` VARCHAR(20) NULL, `survey_date` DATE NULL, `prefix` VARCHAR(10) NULL, `category` VARCHAR(255) NULL,
                      `tag` VARCHAR(20) NULL, `description` VARCHAR(2000) NULL, `scope_severity` VARCHAR(4) NULL, `corrected` VARCHAR(255) NULL,
                      `correction_date` DATE NULL, `cycle` INT NULL, `complaint` TINYINT NULL, `infection_control` TINYINT NULL
                    ) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
                    """, ct);
                await Database.ExecuteAsync(connection,
                    $"""
                    CREATE TABLE `{penaltyRaw}` (
                      `ccn` VARCHAR(20) NULL, `penalty_date` DATE NULL, `penalty_type` VARCHAR(100) NULL, `fine_amount` DOUBLE NULL, `denial_start` DATE NULL,
                      `denial_days` INT NULL
                    ) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
                    """, ct);
                var rows = await CsvTableLoader.LoadAsync(connection, healthPath, raw, DeficiencyColumns, ct,
                    constants: new Dictionary<string, string> { ["kind"] = InspectionSeverity.Health });
                rows += await CsvTableLoader.LoadAsync(connection, firePath, raw, DeficiencyColumns, ct,
                    constants: new Dictionary<string, string> { ["kind"] = InspectionSeverity.FireSafety });
                context.Log.Information("Loaded {Rows:N0} nursing home citations", rows);
                await CsvTableLoader.LoadAsync(connection, penaltiesPath, penaltyRaw, PenaltyColumns, ct);

                var counts = await TableSwap.ReplaceAsync(connection, ["nh_survey", "nh_deficiency_count", "nh_citation", "nh_deficiency_tag", "nh_penalty"],
                    context.Options.MinRowRatio, () => FillAsync(connection, raw, penaltyRaw, ct), ct, log: context.Log);
                loaded = true;
                return counts["nh_citation"] + counts["nh_penalty"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{penaltyRaw}`", CancellationToken.None);
            }
        }
        finally
        {
            if (loaded)
            {
                foreach (var path in paths)
                {
                    FileDownloader.Delete(path);
                }
            }
        }
    }

    private static async Task FillAsync(MySqlConnection connection, string raw, string penaltyRaw, CancellationToken ct)
    {
        // Citations with what every summary needs: a CCN, a date, a tag and a scope and severity of one letter.
        var valid = "`ccn` IS NOT NULL AND CHAR_LENGTH(`ccn`) <= 10 AND `survey_date` IS NOT NULL AND `prefix` IS NOT NULL AND CHAR_LENGTH(`prefix`) <= 2 " +
                    "AND `tag` IS NOT NULL AND CHAR_LENGTH(`tag`) <= 6 AND `scope_severity` REGEXP '^[A-L]$'";
        await Database.ExecuteAsync(connection,
            $"""
            INSERT INTO `nh_deficiency_tag_staging` (`prefix`, `tag`, `category`, `description`)
            SELECT `prefix`, `tag`, LEFT(MAX(`category`), 120), LEFT(MAX(`description`), 600) FROM `{raw}` WHERE {valid} GROUP BY `prefix`, `tag`
            """, ct);
        await Database.ExecuteAsync(connection,
            $"""
            INSERT INTO `nh_deficiency_count_staging` (`ccn`, `kind`, `cycle`, `scope_severity`, `deficiencies`, `complaint`)
            SELECT `ccn`, `kind`, COALESCE(`cycle`, 0), `scope_severity`, COUNT(*), COALESCE(SUM(`complaint` = 1), 0)
            FROM `{raw}` WHERE {valid} GROUP BY `ccn`, `kind`, COALESCE(`cycle`, 0), `scope_severity`
            """, ct);
        await Database.ExecuteAsync(connection,
            $"""
            INSERT INTO `nh_survey_staging` (`ccn`, `kind`, `survey_date`, `cycle`, `deficiencies`, `complaint`, `most_severe`)
            SELECT `ccn`, `kind`, `survey_date`, COALESCE(MIN(`cycle`), 0), COUNT(*), COALESCE(SUM(`complaint` = 1), 0), MAX(`scope_severity`)
            FROM `{raw}` WHERE {valid} GROUP BY `ccn`, `kind`, `survey_date`
            """, ct);
        // Per nursing home the most serious citations (the latest letter, then the newest) and the newest ones, ten of each.
        await Database.ExecuteAsync(connection,
            $"""
            INSERT INTO `nh_citation_staging` (`ccn`, `kind`, `survey_date`, `prefix`, `tag`, `cycle`, `scope_severity`, `corrected`, `correction_date`,
              `complaint`, `infection_control`)
            SELECT `ccn`, `kind`, `survey_date`, `prefix`, `tag`, `cycle`, `scope_severity`, `corrected`, `correction_date`, `complaint`, `infection_control`
            FROM (
              SELECT d.*,
                ROW_NUMBER() OVER (PARTITION BY `ccn` ORDER BY `scope_severity` DESC, `survey_date` DESC, `kind`, `prefix`, `tag`) AS serious,
                ROW_NUMBER() OVER (PARTITION BY `ccn` ORDER BY `survey_date` DESC, `scope_severity` DESC, `kind`, `prefix`, `tag`) AS newest
              FROM (
                SELECT `ccn`, `kind`, `survey_date`, `prefix`, `tag`, COALESCE(MIN(`cycle`), 0) AS `cycle`, MAX(`scope_severity`) AS `scope_severity`,
                  LEFT(MAX(`corrected`), 80) AS `corrected`, MAX(`correction_date`) AS `correction_date`, COALESCE(MAX(`complaint`), 0) AS `complaint`,
                  COALESCE(MAX(`infection_control`), 0) AS `infection_control`
                FROM `{raw}` WHERE {valid} GROUP BY `ccn`, `kind`, `survey_date`, `prefix`, `tag`
              ) d
            ) ranked
            WHERE serious <= {CitationsKept} OR newest <= {CitationsKept}
            """, ct);
        await Database.ExecuteAsync(connection,
            $"""
            INSERT INTO `nh_penalty_staging` (`ccn`, `penalty_date`, `penalty_type`, `fine_amount`, `denial_start`, `denial_days`)
            SELECT `ccn`, `penalty_date`, LEFT(`penalty_type`, 40), `fine_amount`, `denial_start`, `denial_days`
            FROM `{penaltyRaw}` WHERE `ccn` IS NOT NULL AND CHAR_LENGTH(`ccn`) <= 10
            ORDER BY `ccn`, `penalty_date`
            """, ct);
    }

    private static async Task<(DatasetRelease Health, DatasetRelease Fire, DatasetRelease Penalties)> FindAllAsync(DatasetContext context, CancellationToken ct) =>
        (await context.ProviderDataAsync(HealthDatasetId, ct), await context.ProviderDataAsync(FireSafetyDatasetId, ct),
            await context.ProviderDataAsync(PenaltiesDatasetId, ct));
}
