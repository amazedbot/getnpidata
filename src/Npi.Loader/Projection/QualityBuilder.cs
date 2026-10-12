using System.Diagnostics;
using Dapper;
using MySqlConnector;
using Npi.Core.Search;
using Npi.Loader.Geocoding;
using Npi.Loader.Datasets;
using Npi.Loader.Db;
using Serilog;

namespace Npi.Loader.Projection;

/// <summary>
/// Labels every active provider's record current, check or stale (docs/PRODUCT_PLAN.md step 3.1; migration 083) by the rules of
/// <see cref="RecordQuality"/>: the age of the NPPES record, signs of current practice in the other datasets, the primary practice
/// address (mapped, PO box, home), the phone, whether Care Compare agrees on address and profession, the state license records and
/// NPI reactivation. Rebuilt after each projection and whenever one of its inputs reloads.
/// </summary>
public sealed class QualityBuilder(Database database, ILogger log, double minRowRatio)
{
    public const string Source = "provider_quality";

    /// <summary>The <c>reference_data</c> sources the label reads; a newer load of any of them makes it stale.</summary>
    public static readonly IReadOnlyList<string> Inputs =
    [
        "cc_clinicians", "cms_physician_by_provider", "cms_part_d_by_provider", "medicare_enrollment", "cms_order_referring", "open_payments_years",
        "cms_facility_enrollments", "state_licenses", "provider_map", "address_type", "cc_mips", "cc_facility_affiliations", "cms_otp_providers",
        "cms_revalidation", "cms_dme_suppliers", "cms_provider_of_services", "overture",
    ];

    // A state license status that means the license can't be used (the states' own wording; matched case-insensitively).
    internal const string InactiveStatus =
        "DECEASED|REVOK|SURRENDER|VOID|CANC|TERMINAT|INACTIVE|NOT ACTIVE|EXPIRED|NOT RENEWED|RELINQUISH|SUPERSEDED|CLOSED|DEACTIVATED|SUSPEND|DELINQUENT|^TEXAS RETIRED|^RETIRED$";

    // A status that means a usable license when the state gives no expiration date.
    internal const string ActiveStatus = "^(ACTIVE|CLEAR|APPROVED|PERMIT ISSUED|PROBATION|MILITARY ACTIVE|RETIRED ACTIVE|A$)";

    // A PO box in the street line ("PO BOX 12", "P.O. Box", "POST OFFICE BOX").
    internal const string PoBox = "(^|[^A-Z])(P[ .]*O[ .]*BOX|POST OFFICE BOX)([^A-Z]|$)";

    /// <summary>
    /// Organizations matched by address and name (<see cref="BusinessNames"/>) to a business Overture lists at the address (open: a sign
    /// of operation; only permanently closed: <see cref="QualityReason.ListedClosed"/>) and to a terminated certified facility in the
    /// Provider of Services file at the same street address (<see cref="QualityReason.FacilityClosed"/>, unless the organization holds a
    /// current certified facility, and only when its NPI predates the termination). Ownership changes (code 07) aren't closures.
    /// </summary>
    private async Task MatchBusinessesAsync(MySqlConnection connection, string activity, string flags, string businesses, CancellationToken ct)
    {
        var open = new HashSet<string>(StringComparer.Ordinal);
        var closed = new HashSet<string>(StringComparer.Ordinal);
        var words = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        IReadOnlySet<string> Words(string name)
        {
            if (!words.TryGetValue(name, out var w))
            {
                words[name] = w = BusinessNames.Words(name);
            }

            return w;
        }

        await foreach (var row in connection.QueryUnbufferedAsync<(string Npi, string OrgName, string PlaceName, string? Status)>(
                           """
                           SELECT o.`npi`, o.`name`, ap.`name`, ap.`operating_status`
                           FROM `provider` p JOIN `provider_location` l ON l.`npi` = p.`npi`
                           JOIN `address_place` ap ON ap.`addr_key` = l.`addr_key`
                           JOIN `provider_org_name` o ON o.`npi` = p.`npi`
                           WHERE p.`entity_type` = 2
                           """).WithCancellation(ct))
        {
            if (BusinessNames.Same(Words(row.OrgName), Words(row.PlaceName)))
            {
                (row.Status == "permanently_closed" ? closed : open).Add(row.Npi);
            }
        }

        // Terminated facilities by ZIP, house number and street; none where a certified facility is still active (a hospital's old
        // swing-bed or home health unit closes while the hospital, and the groups practising in it, carry on).
        var terminated = new Dictionary<(string, string, string), List<(IReadOnlySet<string> Words, DateTime? Date)>>();
        var stillCertified = new HashSet<(string, string, string)>();
        foreach (var f in await connection.QueryAsync<(string Name, string Address, string Zip5, sbyte Active, DateTime? Date)>(new CommandDefinition(
                     """
                     SELECT `name`, `address`, `zip5`, `active`, `termination_date` FROM `cms_pos_facility`
                     WHERE `zip5` IS NOT NULL AND `name` IS NOT NULL AND `address` IS NOT NULL AND (`active` = 1 OR COALESCE(`termination_code`, '') <> '07')
                     """, cancellationToken: ct)))
        {
            if (AddressNormalizer.Split(f.Address) is var (number, street))
            {
                var key = (f.Zip5, number, street);
                if (f.Active == 1)
                {
                    stillCertified.Add(key);
                    continue;
                }

                if (!terminated.TryGetValue(key, out var list))
                {
                    terminated[key] = list = [];
                }

                list.Add((BusinessNames.Words(f.Name), f.Date));
            }
        }

        foreach (var key in stillCertified)
        {
            terminated.Remove(key);
        }

        var facilityClosed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var row in connection.QueryUnbufferedAsync<(string Npi, string? Address1, string Zip5, string OrgName, DateTime? Enumerated)>(
                           """
                           SELECT l.`npi`, l.`address1`, l.`zip5`, o.`name`, p.`enumeration_date`
                           FROM `provider_location` l JOIN `provider` p ON p.`npi` = l.`npi` AND p.`entity_type` = 2
                           JOIN `provider_org_name` o ON o.`npi` = l.`npi`
                           WHERE l.`zip5` IN (SELECT DISTINCT f.`zip5` FROM `cms_pos_facility` f WHERE f.`active` = 0)
                           """).WithCancellation(ct))
        {
            if (AddressNormalizer.Split(row.Address1) is var (number, street) && terminated.TryGetValue((row.Zip5, number, street), out var here)
                && here.Any(f => (f.Date is null || row.Enumerated is null || row.Enumerated <= f.Date) && BusinessNames.Same(Words(row.OrgName), f.Words)))
            {
                facilityClosed.Add(row.Npi);
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            $"CREATE TABLE `{businesses}` (`npi` CHAR(10) NOT NULL, `kind` TINYINT NOT NULL, PRIMARY KEY (`npi`, `kind`)) ENGINE=InnoDB", cancellationToken: ct));
        var rows = open.Select(n => (Npi: n, Kind: 1)).Concat(closed.Except(open).Select(n => (Npi: n, Kind: 2)))
            .Concat(facilityClosed.Select(n => (Npi: n, Kind: 3))).ToList();
        foreach (var chunk in rows.Chunk(1000))
        {
            var p = new DynamicParameters();
            var values = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                values.Add($"(@n{i}, {chunk[i].Kind})");
                p.Add($"n{i}", chunk[i].Npi);
            }

            await connection.ExecuteAsync(new CommandDefinition($"INSERT INTO `{businesses}` (`npi`, `kind`) VALUES {string.Join(", ", values)}", p,
                cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            $"INSERT INTO `{activity}` (`npi`, `bits`) SELECT `npi`, {(int)QualityActivity.MapListing} FROM `{businesses}` WHERE `kind` = 1 " +
            $"ON DUPLICATE KEY UPDATE `bits` = `bits` | {(int)QualityActivity.MapListing}", commandTimeout: 0, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            $"INSERT INTO `{flags}` (`npi`, `bits`) SELECT `npi`, {(int)QualityReason.ListedClosed} FROM `{businesses}` WHERE `kind` = 2 " +
            $"ON DUPLICATE KEY UPDATE `bits` = `bits` | {(int)QualityReason.ListedClosed}", commandTimeout: 0, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            $"INSERT INTO `{flags}` (`npi`, `bits`) SELECT b.`npi`, {(int)QualityReason.FacilityClosed} FROM `{businesses}` b " +
            $"LEFT JOIN `{activity}` a ON a.`npi` = b.`npi` WHERE b.`kind` = 3 AND (a.`npi` IS NULL OR a.`bits` & {(int)QualityActivity.CertifiedFacility} = 0) " +
            $"ON DUPLICATE KEY UPDATE `{flags}`.`bits` = `{flags}`.`bits` | {(int)QualityReason.FacilityClosed}", commandTimeout: 0, cancellationToken: ct));
        log.Information("Record quality: {Open:N0} organizations listed as open businesses, {Closed:N0} only as permanently closed, {Facility:N0} at a closed certified facility",
            open.Count, closed.Except(open).Count(), facilityClosed.Count);
    }

    /// <summary>True when the projection, or one of the <see cref="Inputs"/>, was rebuilt since the labels were.</summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT NOT EXISTS (SELECT 1 FROM `reference_data` WHERE `source` = @source)
                OR (SELECT `loaded_at` FROM `reference_data` WHERE `source` = @source)
                   < GREATEST(COALESCE((SELECT `projected_at` FROM `data_version` WHERE `id` = 1), '1970-01-01'),
                              COALESCE((SELECT MAX(`loaded_at`) FROM `reference_data` WHERE `source` IN @inputs), '1970-01-01'))
            """, new { source = Source, inputs = Inputs }, cancellationToken: ct)) != 0;
    }

    public async Task<long> BuildAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        await using var connection = await database.OpenAsync(ct);
        Task RunAsync(string sql, object? param = null) => connection.ExecuteAsync(new CommandDefinition(sql, param, commandTimeout: 0, cancellationToken: ct));

        var asOf = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT `as_of_date` FROM `data_version` WHERE `id` = 1", cancellationToken: ct)) ?? DateTime.UtcNow.Date;
        var dates = new { asOf, old5 = asOf.AddYears(-RecordQuality.CheckYears), old10 = asOf.AddYears(-RecordQuality.StaleYears) };

        const string activity = "quality_activity_scratch";
        const string flags = "quality_flag_scratch";
        const string specialties = "quality_cc_specialty_scratch";
        const string businesses = "quality_business_scratch";
        string[] scratch = [activity, flags, specialties, businesses];
        try
        {
            foreach (var table in scratch)
            {
                await RunAsync($"DROP TABLE IF EXISTS `{table}`");
            }

            await RunAsync($"CREATE TABLE `{activity}` (`npi` CHAR(10) NOT NULL PRIMARY KEY, `bits` SMALLINT NOT NULL) ENGINE=InnoDB");
            await RunAsync($"CREATE TABLE `{flags}` (`npi` CHAR(10) NOT NULL PRIMARY KEY, `bits` INT NOT NULL) ENGINE=InnoDB");
            await RunAsync($"""
                CREATE TABLE `{specialties}` (`specialty` VARCHAR(100) NOT NULL, `category` VARCHAR(80) NOT NULL, PRIMARY KEY (`specialty`, `category`))
                ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
                """);
            foreach (var row in RecordQuality.CareCompareCategories)
            {
                await RunAsync($"INSERT IGNORE INTO `{specialties}` (`specialty`, `category`) VALUES (@Specialty, @Category)",
                    new { row.Specialty, row.Category });
            }

            // Signs of current practice, one OR per source.
            const string currentLicense =
                "l.`kind` = 'license' AND ((l.`expiration_date` >= @asOf AND COALESCE(l.`status`, '') NOT REGEXP @inactive) " +
                "OR (l.`expiration_date` IS NULL AND l.`status` REGEXP @active))";
            (QualityActivity Bit, string Select)[] signs =
            [
                (QualityActivity.MedicareBilling, "SELECT `npi` FROM `medicare_utilization`"),
                (QualityActivity.Prescribing, "SELECT `npi` FROM `medicare_part_d`"),
                (QualityActivity.CareCompare, "SELECT `npi` FROM `cc_clinician`"),
                (QualityActivity.MedicareEnrollment, "SELECT DISTINCT `npi` FROM `medicare_enrollment`"),
                (QualityActivity.MedicareEnrollment, "SELECT `npi` FROM `medicare_revalidation`"),
                (QualityActivity.OrderRefer, "SELECT DISTINCT `npi` FROM `medicare_order_referring` WHERE `npi` IS NOT NULL"),
                (QualityActivity.GroupReassignment, "SELECT DISTINCT `npi` FROM `medicare_reassignment` WHERE `npi` IS NOT NULL"),
                (QualityActivity.MipsScore, "SELECT DISTINCT `npi` FROM `cc_mips` WHERE `npi` IS NOT NULL"),
                (QualityActivity.FacilityAffiliation, "SELECT DISTINCT `npi` FROM `cc_facility_affiliation`"),
                // Payments in the newest two program years (a practising clinician often misses one).
                (QualityActivity.IndustryPayments,
                    "SELECT DISTINCT `npi` FROM `open_payments_year` WHERE `program_year` >= (SELECT MAX(`program_year`) - 1 FROM `open_payments_year`)"),
                (QualityActivity.CertifiedFacility, "SELECT DISTINCT `npi` FROM `cms_facility_npi`"),
                (QualityActivity.EquipmentBilling, "SELECT `npi` FROM `medicare_dme_supplier`"),
                (QualityActivity.OpioidTreatment, "SELECT `npi` FROM `cms_otp_provider`"),
                (QualityActivity.CurrentLicense, $"SELECT DISTINCT l.`npi` FROM `provider_state_license` l WHERE {currentLicense}"),
            ];
            var statuses = new { dates.asOf, inactive = InactiveStatus, active = ActiveStatus };
            foreach (var (bit, select) in signs)
            {
                await RunAsync($"INSERT INTO `{activity}` (`npi`, `bits`) SELECT x.`npi`, {(int)bit} FROM ({select}) x " +
                               $"ON DUPLICATE KEY UPDATE `bits` = `bits` | {(int)bit}", statuses);
            }

            // Reasons from other tables: a lapsed or deceased license (state records), a reactivated NPI (raw NPPES).
            (QualityReason Bit, string Select)[] found =
            [
                (QualityReason.LicenseLapsed,
                    $"SELECT DISTINCT l.`npi` FROM `provider_state_license` l LEFT JOIN `{activity}` a ON a.`npi` = l.`npi` " +
                    "WHERE l.`kind` = 'license' AND (l.`expiration_date` < @asOf OR l.`status` REGEXP @inactive) " +
                    $"AND (a.`npi` IS NULL OR a.`bits` & {(int)QualityActivity.CurrentLicense} = 0)"),
                (QualityReason.Deceased, "SELECT DISTINCT l.`npi` FROM `provider_state_license` l WHERE l.`kind` = 'license' AND l.`status` REGEXP 'DECEASED'"),
                (QualityReason.Reactivated, "SELECT n.`NPI` AS `npi` FROM `npidata` n WHERE n.`NPI_Reactivation_Date` IS NOT NULL AND n.`Is_Deactivated` = 0"),
            ];
            foreach (var (bit, select) in found)
            {
                await RunAsync($"INSERT INTO `{flags}` (`npi`, `bits`) SELECT x.`npi`, {(int)bit} FROM ({select}) x " +
                               $"ON DUPLICATE KEY UPDATE `bits` = `bits` | {(int)bit}", statuses);
            }

            await MatchBusinessesAsync(connection, activity, flags, businesses, ct);

            static int I(QualityReason r) => (int)r;
            var counts = await TableSwap.ReplaceAsync(connection, ["provider_quality", "quality_search"], minRowRatio, async () =>
            {
                await RunAsync(
                $"""
                INSERT INTO `provider_quality_staging` (`npi`, `label`, `reasons`, `activity`)
                SELECT x.`npi`, {RecordQuality.LabelSql("x.`reasons`")}, x.`reasons`, x.`activity`
                FROM (
                  SELECT p.`npi`, COALESCE(a.`bits`, 0) AS `activity`,
                    IF(p.`last_update_date` < @old5, {I(QualityReason.NotUpdated5Years)}, 0)
                    | IF(p.`last_update_date` < @old10, {I(QualityReason.NotUpdated10Years)}, 0)
                    | IF(a.`npi` IS NULL, {I(QualityReason.NoActivity)}, 0)
                    | IF(l.`zip5` IS NOT NULL AND COALESCE(m.`source`, 'zip') = 'zip', {I(QualityReason.NotMapped)}, 0)
                    | IF(UPPER(COALESCE(l.`address1`, '')) REGEXP @poBox, {I(QualityReason.PoBox)}, 0)
                    | IF(COALESCE(l.`phone`, p.`phone`) IS NULL, {I(QualityReason.NoPhone)}, 0)
                    | IF(EXISTS (SELECT 1 FROM `cc_clinician_location` c JOIN `zip_centroid` cz ON cz.`zip5` = c.`zip5` WHERE c.`npi` = p.`npi`)
                         AND NOT EXISTS (SELECT 1 FROM `cc_clinician_location` c JOIN `provider_location` pl ON pl.`npi` = c.`npi`
                                         LEFT JOIN `zip_centroid` cz ON cz.`zip5` = c.`zip5` LEFT JOIN `zip_centroid` pz ON pz.`zip5` = pl.`zip5`
                                         WHERE c.`npi` = p.`npi`
                                           AND (pl.`zip5` = c.`zip5` OR (pl.`state` = c.`state` AND pl.`city` = c.`city`)
                                                OR ST_Distance_Sphere(POINT(cz.`lon`, cz.`lat`), POINT(pz.`lon`, pz.`lat`)) < @nearMeters)),
                         {I(QualityReason.CareCompareAddress)}, 0)
                    | IF(cc.`primary_specialty` IS NOT NULL
                         AND EXISTS (SELECT 1 FROM `{specialties}` s WHERE s.`specialty` = cc.`primary_specialty`)
                         AND NOT EXISTS (SELECT 1 FROM `provider_taxonomy` t
                                         JOIN `specialty_code_category` sc ON sc.`code` = t.`taxonomy_code`
                                         JOIN `{specialties}` s ON s.`category` = sc.`category` AND s.`specialty` = cc.`primary_specialty`
                                         WHERE t.`npi` = p.`npi`), {I(QualityReason.CareCompareSpecialty)}, 0)
                    | IF(bt.`building_type` = 'residential', {I(QualityReason.HomeAddress)}, 0)
                    | COALESCE(f.`bits`, 0) AS `reasons`
                  FROM `provider` p
                  LEFT JOIN `{activity}` a ON a.`npi` = p.`npi`
                  LEFT JOIN `{flags}` f ON f.`npi` = p.`npi`
                  LEFT JOIN `provider_location` l ON l.`npi` = p.`npi` AND l.`is_primary` = 1
                  LEFT JOIN `provider_map` m ON m.`npi` = l.`npi` AND m.`addr_key` = l.`addr_key`
                  LEFT JOIN `address_type` bt ON bt.`addr_key` = l.`addr_key`
                  LEFT JOIN `cc_clinician` cc ON cc.`npi` = p.`npi`
                ) x
                """, new { dates.old5, dates.old10, poBox = PoBox, nearMeters = RecordQuality.CareCompareMiles * 1609.344 });
                // Label × location, like building_search: the search's location conditions apply as they are.
                await RunAsync(
                    """
                    INSERT INTO `quality_search_staging` (`label`, `state`, `city`, `zip5`, `npi`)
                    SELECT DISTINCT q.`label`, IFNULL(l.`state`, ''), IFNULL(l.`city`, ''), IFNULL(l.`zip5`, ''), l.`npi`
                    FROM `provider_location` l JOIN `provider_quality_staging` q ON q.`npi` = l.`npi`
                    """);
            }, ct);

            var byLabel = (await connection.QueryAsync<(sbyte Label, long Rows)>(new CommandDefinition(
                "SELECT `label`, COUNT(*) FROM `provider_quality` GROUP BY `label` ORDER BY `label`", cancellationToken: ct))).ToList();
            var now = DateTime.UtcNow;
            await Database.ExecuteAsync(connection,
                """
                INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
                VALUES (@source, @version, '', @rows, @now, @now) AS new
                ON DUPLICATE KEY UPDATE `version` = new.`version`, `rows_loaded` = new.`rows_loaded`, `loaded_at` = new.`loaded_at`, `checked_at` = new.`checked_at`
                """, ct, param: new
                {
                    source = Source, version = "as of " + asOf.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    rows = counts["provider_quality"], now,
                });
            log.Information("Record quality: {Rows:N0} providers ({Labels}) in {Seconds:N0}s", counts["provider_quality"],
                string.Join(", ", byLabel.Select(b => $"{RecordQuality.Name((QualityLabel)b.Label)} {b.Rows:N0}")), watch.Elapsed.TotalSeconds);
            return counts["provider_quality"];
        }
        finally
        {
            foreach (var table in scratch)
            {
                await connection.ExecuteAsync(new CommandDefinition($"DROP TABLE IF EXISTS `{table}`", cancellationToken: CancellationToken.None));
            }
        }
    }
}
