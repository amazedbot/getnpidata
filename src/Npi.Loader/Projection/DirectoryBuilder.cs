using System.Diagnostics;
using Dapper;
using Npi.Core.Search;
using Npi.Loader.Datasets;
using Npi.Loader.Db;
using Npi.Loader.Reference;
using Serilog;

namespace Npi.Loader.Projection;

/// <summary>
/// Builds the tables behind the Facilities &amp; hospitals and Groups &amp; organizations pages (docs/PRODUCT_PLAN.md step 3.6;
/// migration 101), so the site reads a few small tables instead of aggregating millions of rows when it starts:
/// <list type="bullet">
/// <item><c>directory_facility</c>: every certified facility, the Provider of Services file merged with the Care Compare
/// listings in C# (<see cref="FacilityDirectory.ReadSourcesAsync"/>, ~130,000 rows);</item>
/// <item><c>directory_group</c>: every Medicare group with an active clinician (~210,000): clinicians as its page counts them
/// (reassigned to it or listed with it in Care Compare), organization NPIs enrolled under it, accepts assignment, group MIPS;</item>
/// <item><c>directory_org_count</c>: active organization NPIs by primary taxonomy, primary practice state and record-quality label;</item>
/// <item><c>directory_org_billing</c>: active organization NPIs' own Medicare Part B payments.</item>
/// </list>
/// Rebuilt in full (staging + RENAME) after the record quality when the projection, the labels or one of the datasets it reads
/// changed since its last build. The site rereads the tables when this builder's <c>reference_data</c> row moves.
/// </summary>
public sealed class DirectoryBuilder(Database database, ILogger log, double minRowRatio)
{
    public const string Source = DataStamp.DirectorySource;

    public static readonly string[] Tables = ["directory_facility", "directory_group", "directory_org_count", "directory_org_billing"];

    // Datasets and reference data the tables read: their reloads count.
    private static readonly string[] Inputs =
    [
        "medicare_enrollment", "cc_clinicians", "cc_mips", "cc_hospitals", "cc_nursing_homes", "cc_home_health", "cc_hospices",
        "cms_facility_enrollments", "cms_provider_of_services", "cms_physician_by_provider", "census_gazetteer",
    ];

    /// <summary>
    /// The active clinicians of every group (the heaviest step: ~5.9M group memberships, each checked against the projection), its
    /// name (Care Compare's first, as on the group page), state, organization NPIs (enrollments with an organization name) and the
    /// group's MIPS score. Groups with no active clinician are left out.
    /// </summary>
    internal const string GroupSql =
        """
        INSERT INTO `directory_group_staging` (`pac_id`, `name`, `state`, `clinicians`, `organization_npis`, `accepts_assignment`, `mips_score`, `mips_year`)
        SELECT c.`pac`, COALESCE(cc.`name`, re.`name`, CONCAT('Group ', c.`pac`)), COALESCE(re.`state`, cc.`state`), c.`clinicians`, COALESCE(o.`npis`, 0),
               cc.`accepts`, ROUND(m.`score`, 2), IF(m.`score` IS NULL, NULL, m.`year`)
        FROM (SELECT g.`pac`, COUNT(*) AS `clinicians`
              FROM (SELECT `group_pac_id` AS `pac`, `npi` FROM `medicare_reassignment` WHERE `npi` IS NOT NULL
                    UNION SELECT `org_pac_id`, `npi` FROM `cc_group`) g
              JOIN `provider` p ON p.`npi` = g.`npi`
              GROUP BY g.`pac`) c
        LEFT JOIN (SELECT `org_pac_id` AS `pac`, MAX(`group_name`) AS `name`, MAX(`state`) AS `state`, MAX(`accepts_assignment`) AS `accepts`
                   FROM `cc_group` GROUP BY `org_pac_id`) cc ON cc.`pac` = c.`pac`
        LEFT JOIN (SELECT `group_pac_id` AS `pac`, MAX(`group_name`) AS `name`, MAX(`group_state`) AS `state`
                   FROM `medicare_reassignment` GROUP BY `group_pac_id`) re ON re.`pac` = c.`pac`
        LEFT JOIN (SELECT e.`pac_id` AS `pac`, COUNT(DISTINCT e.`npi`) AS `npis`
                   FROM `medicare_enrollment` e JOIN `provider` p ON p.`npi` = e.`npi`
                   WHERE e.`pac_id` IS NOT NULL AND e.`org_name` IS NOT NULL
                   GROUP BY e.`pac_id`) o ON o.`pac` = c.`pac`
        LEFT JOIN (SELECT `org_pac_id` AS `pac`, MAX(`program_year`) AS `year`, MAX(`final_score`) AS `score`
                   FROM `cc_mips` WHERE `source` = 'group' AND `org_pac_id` IS NOT NULL GROUP BY `org_pac_id`) m ON m.`pac` = c.`pac`
        """;

    /// <summary>Every active organization NPI counted by primary taxonomy, primary practice state and label: a pass over provider.</summary>
    internal const string OrganizationCountSql =
        """
        INSERT INTO `directory_org_count_staging` (`taxonomy_code`, `state`, `label`, `organizations`)
        SELECT COALESCE(p.`primary_taxonomy_code`, '') AS `code`, IF(CHAR_LENGTH(l.`state`) = 2, UPPER(l.`state`), '') AS `st`,
               COALESCE(q.`label`, 0) AS `lbl`, COUNT(*)
        FROM `provider` p
        LEFT JOIN `provider_location` l ON l.`npi` = p.`npi` AND l.`is_primary` = 1
        LEFT JOIN `provider_quality` q ON q.`npi` = p.`npi`
        WHERE p.`entity_type` = 2
        GROUP BY `code`, `st`, `lbl`
        """;

    /// <summary>Organizations' own Medicare Part B payments (most Part B rows are individuals'; ~1.3M rows checked against the projection).</summary>
    internal const string BillingSql =
        """
        INSERT INTO `directory_org_billing_staging` (`npi`, `data_year`, `payments`, `beneficiaries`)
        SELECT u.`npi`, u.`data_year`, u.`payment_amount`, u.`beneficiaries`
        FROM `medicare_utilization` u JOIN `provider` p ON p.`npi` = u.`npi`
        WHERE p.`entity_type` = 2 AND u.`payment_amount` > 0
        """;

    /// <summary>True when the tables were never built, or the projection, the labels or a dataset they read changed since.</summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT NOT EXISTS (SELECT 1 FROM `reference_data` WHERE `source` = @source)
                OR (SELECT `loaded_at` FROM `reference_data` WHERE `source` = @source)
                   < GREATEST(COALESCE((SELECT `projected_at` FROM `data_version` WHERE `id` = 1), '1970-01-01'),
                              COALESCE((SELECT GREATEST(`loaded_at`, `checked_at`) FROM `reference_data` WHERE `source` = @quality), '1970-01-01'),
                              COALESCE((SELECT MAX(`loaded_at`) FROM `reference_data` WHERE `source` IN @inputs), '1970-01-01'))
            """, new { source = Source, quality = QualityBuilder.Source, inputs = Inputs }, cancellationToken: ct)) != 0;
    }

    /// <returns>The rows written per table.</returns>
    public async Task<IReadOnlyDictionary<string, long>> BuildAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var started = DateTime.UtcNow;
        await using var connection = await database.OpenAsync(ct);
        var counts = await TableSwap.ReplaceAsync(connection, Tables, minRowRatio, async () =>
        {
            var step = Stopwatch.StartNew();
            var facilities = await FacilityDirectory.ReadSourcesAsync(connection, ct);
            await ReferenceTables.CopyToStagingAsync(connection, "directory_facility", ReferenceTables.ToDataTable(facilities,
                ("ccn", typeof(string), f => f.Ccn), ("type", typeof(string), f => f.Type), ("kind", typeof(string), f => Clip(f.Kind, 80)),
                ("name", typeof(string), f => Clip(f.Name, 200)), ("address", typeof(string), f => Clip(f.Address, 200)),
                ("city", typeof(string), f => Clip(f.City, 100)), ("state", typeof(string), f => f.State is { Length: 2 } s ? s : null),
                ("zip5", typeof(string), f => f.Zip), ("phone", typeof(string), f => Clip(f.Phone, 20)), ("ownership", typeof(string), f => Clip(f.Ownership, 100)),
                ("beds", typeof(int), f => f.Beds), ("rating", typeof(double), f => f.Rating), ("active", typeof(sbyte), f => (sbyte)(f.Active ? 1 : 0)),
                ("termination_date", typeof(DateTime), f => f.TerminationDate?.ToDateTime(TimeOnly.MinValue)),
                ("termination_reason", typeof(string), f => f.TerminationReason), ("billing_npis", typeof(int), f => f.BillingNpis),
                ("lat", typeof(double), f => f.Latitude), ("lon", typeof(double), f => f.Longitude)), ct);
            log.Information("Directories: {Rows:N0} facilities in {Seconds:N1}s", facilities.Count, step.Elapsed.TotalSeconds);

            foreach (var (what, sql) in new[] { ("groups", GroupSql), ("organization counts", OrganizationCountSql), ("organization Part B payments", BillingSql) })
            {
                step.Restart();
                await Database.ExecuteAsync(connection, sql, ct);
                log.Information("Directories: {What} in {Seconds:N1}s", what, step.Elapsed.TotalSeconds);
            }
        }, ct);

        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
            VALUES (@source, @version, 'directory tables', @rows, @started, UTC_TIMESTAMP()) AS new
            ON DUPLICATE KEY UPDATE `version` = new.`version`, `rows_loaded` = new.`rows_loaded`, `loaded_at` = new.`loaded_at`, `checked_at` = new.`checked_at`
            """, ct, param: new
            {
                source = Source, started,
                version = $"{counts["directory_facility"]} facilities, {counts["directory_group"]} groups",
                rows = (int)counts.Values.Sum(),
            });
        log.Information("Directories built in {Seconds:N0}s: {Facilities:N0} facilities, {Groups:N0} groups, {Counts:N0} organization count rows, {Billing:N0} organizations paid by Part B",
            watch.Elapsed.TotalSeconds, counts["directory_facility"], counts["directory_group"], counts["directory_org_count"], counts["directory_org_billing"]);
        return counts;
    }

    private static string? Clip(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
}
