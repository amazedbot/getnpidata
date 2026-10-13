using System.Globalization;
using System.Text;
using Dapper;
using MySqlConnector;
using Npi.Core.Sql;
using Npi.Loader.Db;
using Serilog;

namespace Npi.Loader.Projection;

/// <summary>
/// Builds the search projection (CLAUDE.md §6.2, §7 Stage 3.1 and Stage 5.5 item 1): provider,
/// provider_taxonomy, provider_location, provider_other_name, provider_profile, provider_identifier
/// and provider_endpoint, from npidata / practice_locations / other_names / endpoints.
/// Deactivated NPIs are left out. Everything is built in *_staging tables and swapped in with one
/// atomic RENAME, so searches never see a partial projection.
/// <para>
/// Incremental: the loads queue the NPIs whose raw data changed in <c>projection_pending</c> (a weekly's NPIs, a monthly's
/// differences, changed deactivation flags). When there are few of them, only their rows are rebuilt, in the same staging
/// tables with the same SQL, and replaced in the live tables in one transaction (readers see the old rows until it commits):
/// a weekly's ~50,000 NPIs instead of all ~9.5M. A first build, a large queue, or <c>Npi.Loader project</c> rebuilds everything.
/// </para>
/// </summary>
public sealed class ProjectionBuilder(Database database, ILogger log, double minRowRatio, TimeProvider? clock = null)
{
    public static readonly string[] Tables =
    [
        "provider", "provider_taxonomy", "provider_location", "provider_other_name", "provider_search",
        "provider_profile", "provider_identifier", "provider_endpoint", "provider_org_name", "provider_deactivated",
    ];

    /// <summary>NPIs whose NPPES data changed since the projection was built (migration 089).</summary>
    public const string Pending = "projection_pending";

    /// <summary>Above this share of the providers, rebuilding everything is cheaper than replacing rows NPI by NPI.</summary>
    public const double IncrementalLimit = 0.10;

    // This build's NPIs: the queue as it was when the build started (a load may add more meanwhile).
    private const string Batch = "projection_batch";
    private static readonly string BatchScope = $"JOIN `{Batch}` b ON b.`npi` = n.`NPI`";

    private const int TaxonomySlots = 15;
    private const int IdentifierSlots = 50;
    private const string Active = "n.`Is_Deactivated` = 0 AND n.`Entity_Type_Code` IN ('1', '2')";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // Every statement that reads the NPPES tables (npidata, other_names, practice_locations, endpoints), as one text.
    private static readonly Lazy<string> SourceText = new(() => string.Join(" ",
        ProviderSql(""), TaxonomySql(""), PrimaryLocationSql(""), SecondaryLocationSql, OtherNameSql, ProfileSql, IdentifierSql(),
        EndpointSql, DeactivatedSql("")));

    /// <summary>
    /// True when the projection reads this NPPES column, so a change in it changes the projection. A monthly compares only
    /// these columns.
    /// </summary>
    public static bool ReadsColumn(string column) =>
        SourceText.Value.Contains(SqlIdentifier.Quote(column), StringComparison.OrdinalIgnoreCase);

    /// <summary>The largest queue, as a share of the providers, rebuilt NPI by NPI (tests raise it).</summary>
    public double IncrementalShare { get; init; } = IncrementalLimit;

    /// <summary>
    /// True when the projection is missing or older than the last completed NPPES load or NUCC/HUD/Census
    /// reload. The Stage 5.5 datasets live in their own tables and never require a rebuild.
    /// </summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT NOT EXISTS (SELECT 1 FROM `data_version` WHERE `id` = 1)
                OR (SELECT `projected_at` FROM `data_version` WHERE `id` = 1) <
                   GREATEST(COALESCE((SELECT MAX(`completed_at`) FROM `downlog` WHERE `status` = 'Completed'), '1000-01-01'),
                            COALESCE((SELECT MAX(`loaded_at`) FROM `reference_data`
                                      WHERE `source` IN ('nucc', 'hud_zip_county', 'census_gazetteer')), '1000-01-01'))
            """, cancellationToken: ct));
    }

    /// <param name="full">Rebuild every provider (the <c>project</c> command); otherwise only the queued NPIs when there are few.</param>
    /// <returns>The number of providers in the projection afterwards.</returns>
    public async Task<long> BuildAsync(CancellationToken ct, bool full = false)
    {
        await using var connection = await database.OpenAsync(ct);
        await Database.ExecuteAsync(connection, "SET SESSION group_concat_max_len = 16777216", ct);
        await Database.ExecuteAsync(connection, $"DROP TEMPORARY TABLE IF EXISTS `{Batch}`", ct);
        await Database.ExecuteAsync(connection, $"CREATE TEMPORARY TABLE `{Batch}` (PRIMARY KEY (`npi`)) SELECT `npi` FROM `{Pending}`", ct);
        try
        {
            var queued = await Database.CountAsync(connection, Batch, ct);
            var current = await Database.CountAsync(connection, "provider", ct);
            if (full || current == 0 || queued > current * IncrementalShare || await UnqueuedLoadAsync(connection, queued, ct))
            {
                return await BuildAllAsync(connection, current, ct);
            }

            return await BuildQueuedAsync(connection, queued, ct);
        }
        finally
        {
            await Database.ExecuteAsync(connection, $"DROP TEMPORARY TABLE IF EXISTS `{Batch}`", CancellationToken.None);
        }
    }

    /// <summary>
    /// True when nothing is queued but an NPPES file finished loading after the projection was built and before the queue
    /// existed (migration 089 applied): only a full build covers such a load. A load since then that changed nothing queues
    /// nothing, and needs nothing rebuilt. downlog times are UTC; schema_migrations.applied_at is the server's local time.
    /// </summary>
    private static async Task<bool> UnqueuedLoadAsync(MySqlConnection connection, long queued, CancellationToken ct) =>
        queued == 0 && await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT EXISTS (
              SELECT 1 FROM `downlog`
              WHERE `status` = 'Completed'
                AND `completed_at` > COALESCE((SELECT `projected_at` FROM `data_version` WHERE `id` = 1), '1000-01-01')
                AND `completed_at` < COALESCE((SELECT `applied_at` - INTERVAL TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(), NOW()) SECOND
                                               FROM `schema_migrations` WHERE `version` = 89), '9999-12-31'))
            """, cancellationToken: ct)) != 0;

    private static async Task CreateStagingAsync(MySqlConnection connection, CancellationToken ct)
    {
        foreach (var table in Tables)
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {Staging(table)}", ct);
            await Database.ExecuteAsync(connection, $"CREATE TABLE {Staging(table)} LIKE {SqlIdentifier.Quote(table)}", ct);
        }
    }

    // The staging tables, for every provider (scope "") or only the batch's NPIs.
    private async Task FillStagingAsync(MySqlConnection connection, string scope, CancellationToken ct)
    {
        await StepAsync(connection, "provider", ProviderSql(scope), ct);
        await StepAsync(connection, "provider_taxonomy", TaxonomySql(scope), ct);
        await StepAsync(connection, "provider_location (primary)", PrimaryLocationSql(scope), ct);
        await StepAsync(connection, "provider_location (secondary)", SecondaryLocationSql, ct);
        await StepAsync(connection, "provider_other_name", OtherNameSql, ct);
        await StepAsync(connection, "provider_org_name", OrgNameSql, ct);
        await StepAsync(connection, "provider_search", SearchSql, ct);
        await StepAsync(connection, "provider_profile", ProfileSql, ct);
        await StepAsync(connection, "provider_identifier", IdentifierSql(), ct);
        await StepAsync(connection, "provider_endpoint", EndpointSql, ct);
        await StepAsync(connection, "row_hash", RowHashSql, ct);
        await StepAsync(connection, "provider_deactivated", DeactivatedSql(scope), ct);
    }

    /// <summary>
    /// Rebuilds only the batch's NPIs: their rows are built in the staging tables, compared with the live ones for the change
    /// log, then the live rows of those NPIs are replaced in one transaction, with data_version and the queue.
    /// </summary>
    private async Task<long> BuildQueuedAsync(MySqlConnection connection, long queued, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        try
        {
            await CreateStagingAsync(connection, ct);
            await FillStagingAsync(connection, BatchScope, ct);
            var now = _clock.GetUtcNow().UtcDateTime;
            await RecordChangesAsync(connection, now, Changes("provider_staging", "provider", "provider_location_staging", "provider_location", Batch), ct);

            var columns = new Dictionary<string, string>();
            foreach (var table in Tables)
            {
                columns[table] = await InsertableColumnsAsync(connection, table, ct);
            }

            await using var tx = await connection.BeginTransactionAsync(ct);
            Task<int> RunAsync(string sql, object? param = null) =>
                connection.ExecuteAsync(new CommandDefinition(sql, param, tx, commandTimeout: 0, cancellationToken: ct));

            // provider_search has no index starting with npi: its rows are found by their whole key, from the live
            // taxonomies and locations, so this goes first.
            await RunAsync(
                $"""
                DELETE s FROM `provider_search` s
                JOIN (SELECT DISTINCT t.`taxonomy_code`, IFNULL(l.`state`, '') AS `state`, IFNULL(l.`city`, '') AS `city`, IFNULL(l.`zip5`, '') AS `zip5`, t.`npi`
                      FROM `{Batch}` b JOIN `provider_taxonomy` t ON t.`npi` = b.`npi` JOIN `provider_location` l ON l.`npi` = b.`npi`) k
                  ON s.`taxonomy_code` = k.`taxonomy_code` AND s.`state` = k.`state` AND s.`city` = k.`city` AND s.`zip5` = k.`zip5` AND s.`npi` = k.`npi`
                """);
            var replaced = 0L;
            foreach (var table in Tables)
            {
                var t = SqlIdentifier.Quote(table);
                if (table != "provider_search")
                {
                    await RunAsync($"DELETE x FROM {t} x JOIN `{Batch}` b ON b.`npi` = x.`npi`");
                }

                replaced += await RunAsync($"INSERT INTO {t} ({columns[table]}) SELECT {columns[table]} FROM {Staging(table)}");
            }

            var providers = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM `provider`", transaction: tx, cancellationToken: ct));
            await RunAsync(DataVersionSql, new { now, providers });
            await RunAsync($"DELETE q FROM `{Pending}` q JOIN `{Batch}` b ON b.`npi` = q.`npi`");
            await tx.CommitAsync(ct);
            log.Information("Projection updated: {Queued:N0} NPIs rebuilt ({Rows:N0} rows), {Providers:N0} providers, in {Duration}", queued, replaced, providers,
                (DateTime.UtcNow - started).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
            return providers;
        }
        finally
        {
            await DropStagingAsync(connection);
        }
    }

    // A table's columns that can be inserted: not generated, not AUTO_INCREMENT (new ids are given).
    private static async Task<string> InsertableColumnsAsync(MySqlConnection connection, string table, CancellationToken ct) =>
        string.Join(", ", (await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = DATABASE() AND table_name = @table AND extra NOT LIKE '%GENERATED%' AND extra NOT LIKE '%auto_increment%'
            ORDER BY ordinal_position
            """, new { table }, cancellationToken: ct))).Select(SqlIdentifier.Quote));

    private static async Task DropStagingAsync(MySqlConnection connection)
    {
        foreach (var table in Tables)
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {Staging(table)}", CancellationToken.None);
        }
    }

    /// <summary>Rebuilds every provider in the staging tables and swaps them in with one RENAME.</summary>
    private async Task<long> BuildAllAsync(MySqlConnection connection, long current, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        try
        {
            await CreateStagingAsync(connection, ct);
            await FillStagingAsync(connection, "", ct);

            var providers = await Database.CountAsync(connection, "provider_staging", ct);
            if (current > 0 && providers < current * minRowRatio)
            {
                throw new InvalidDataException(
                    $"New projection has {providers:N0} providers, fewer than {minRowRatio:P0} of the current {current:N0}; not replacing it.");
            }

            foreach (var table in Tables)
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {SqlIdentifier.Quote(table + "_old")}", ct);
            }

            var renames = Tables.SelectMany(t => new[]
            {
                $"{SqlIdentifier.Quote(t)} TO {SqlIdentifier.Quote(t + "_old")}",
                $"{Staging(t)} TO {SqlIdentifier.Quote(t)}",
            });
            await Database.ExecuteAsync(connection, $"RENAME TABLE {string.Join(", ", renames)}", ct);
            var now = _clock.GetUtcNow().UtcDateTime;
            if (current > 0)
            {
                await RecordChangesAsync(connection, now, Changes("provider", "provider_old", "provider_location", "provider_location_old", null), ct);
            }

            foreach (var table in Tables)
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE {SqlIdentifier.Quote(table + "_old")}", ct);
            }

            await Database.ExecuteAsync(connection, DataVersionSql, ct, param: new { now, providers });
            // Everyone was rebuilt, so the queue as it was at the start is done.
            await Database.ExecuteAsync(connection, $"DELETE q FROM `{Pending}` q JOIN `{Batch}` b ON b.`npi` = q.`npi`", ct);
            log.Information("Projection built: {Providers:N0} providers in {Duration}", providers,
                (DateTime.UtcNow - started).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
            return providers;
        }
        finally
        {
            await DropStagingAsync(connection);
        }
    }

    /// <summary>
    /// Change log (Stage 5.5 item 11): compares the new projection with the old one (after a full build, the *_old tables
    /// it just replaced; in an incremental build, the staging rows with the live ones, before they are replaced) and
    /// appends to provider_change. Only NPIs whose row_hash changed are compared field by field. A failure here is logged,
    /// not thrown: a missed entry is not worth failing the run.
    /// </summary>
    private async Task RecordChangesAsync(MySqlConnection connection, DateTime now, string[] changeSql, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        try
        {
            await Database.ExecuteAsync(connection, "DROP TEMPORARY TABLE IF EXISTS `changed_npi`", ct);
            await Database.ExecuteAsync(connection, changeSql[0], ct);
            foreach (var sql in changeSql.Skip(1))
            {
                await connection.ExecuteAsync(new CommandDefinition(sql, new { now }, cancellationToken: ct));
            }

            // Counted from the table: the drivers' affected-row counts came back 0 for these INSERT … SELECTs on full data.
            var rows = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM `provider_change` WHERE `detected_at` = @now", new { now }, cancellationToken: ct));

            log.Information("Projection changes: {Rows:N0} recorded in {Seconds:N0}s", rows, (DateTime.UtcNow - started).TotalSeconds);
        }
        catch (MySqlException e)
        {
            log.Error(e, "Projection changes could not be recorded; the new projection is in place");
        }
        finally
        {
            await Database.ExecuteAsync(connection, "DROP TEMPORARY TABLE IF EXISTS `changed_npi`", CancellationToken.None);
        }
    }

    // The primary practice address as one line, as compared and recorded.
    private static string AddressLine(string alias) =>
        $"LEFT(CONCAT_WS(', ', {alias}.`address1`, {alias}.`city`, CONCAT_WS(' ', {alias}.`state`, {alias}.`zip5`)), 300)";

    /// <summary>
    /// The change log's statements: first the temporary <c>changed_npi</c> (NPIs in both whose row_hash differs), then the
    /// provider_change inserts. <paramref name="batch"/> limits "removed" to the batch's NPIs in an incremental build (the
    /// new tables hold only those NPIs, so every other live NPI would look removed).
    /// </summary>
    private static string[] Changes(string newProvider, string oldProvider, string newLocation, string oldLocation, string? batch)
    {
        var inBatch = batch is null ? "" : $"JOIN `{batch}` b ON b.`npi` = o.`npi`";
        return
        [
            $"""
            CREATE TEMPORARY TABLE `changed_npi` (PRIMARY KEY (`npi`))
            SELECT n.`npi` FROM `{newProvider}` n JOIN `{oldProvider}` o ON o.`npi` = n.`npi` WHERE NOT (n.`row_hash` <=> o.`row_hash`)
            """,
            $"""
            INSERT INTO `provider_change` (`npi`, `detected_at`, `change_type`, `old_value`, `new_value`)
            SELECT n.`npi`, @now, 'added', NULL, LEFT(n.`sort_name`, 300)
            FROM `{newProvider}` n LEFT JOIN `{oldProvider}` o ON o.`npi` = n.`npi` WHERE o.`npi` IS NULL
            """,
            // Gone from the projection: deactivated, or (rarely) no longer in the NPPES file.
            $"""
            INSERT INTO `provider_change` (`npi`, `detected_at`, `change_type`, `old_value`, `new_value`)
            SELECT o.`npi`, @now,
              IF(EXISTS (SELECT 1 FROM `npidata` d WHERE d.`NPI` = o.`npi` AND d.`Is_Deactivated` = 1), 'deactivated', 'removed'),
              LEFT(o.`sort_name`, 300), NULL
            FROM `{oldProvider}` o {inBatch} LEFT JOIN `{newProvider}` n ON n.`npi` = o.`npi` WHERE n.`npi` IS NULL
            """,
            $"""
            INSERT INTO `provider_change` (`npi`, `detected_at`, `change_type`, `old_value`, `new_value`)
            SELECT c.`npi`, @now, 'name', LEFT(o.`sort_name`, 300), LEFT(n.`sort_name`, 300)
            FROM `changed_npi` c JOIN `{newProvider}` n ON n.`npi` = c.`npi` JOIN `{oldProvider}` o ON o.`npi` = c.`npi`
            WHERE NOT (n.`sort_name` <=> o.`sort_name`)
            """,
            $"""
            INSERT INTO `provider_change` (`npi`, `detected_at`, `change_type`, `old_value`, `new_value`)
            SELECT c.`npi`, @now, 'credential', o.`credential`, n.`credential`
            FROM `changed_npi` c JOIN `{newProvider}` n ON n.`npi` = c.`npi` JOIN `{oldProvider}` o ON o.`npi` = c.`npi`
            WHERE NOT (n.`credential` <=> o.`credential`)
            """,
            $"""
            INSERT INTO `provider_change` (`npi`, `detected_at`, `change_type`, `old_value`, `new_value`)
            SELECT c.`npi`, @now, 'specialty', o.`primary_taxonomy_code`, n.`primary_taxonomy_code`
            FROM `changed_npi` c JOIN `{newProvider}` n ON n.`npi` = c.`npi` JOIN `{oldProvider}` o ON o.`npi` = c.`npi`
            WHERE NOT (n.`primary_taxonomy_code` <=> o.`primary_taxonomy_code`)
            """,
            $"""
            INSERT INTO `provider_change` (`npi`, `detected_at`, `change_type`, `old_value`, `new_value`)
            SELECT c.`npi`, @now, 'address', {AddressLine("ol")}, {AddressLine("nl")}
            FROM `changed_npi` c
            JOIN `{newLocation}` nl ON nl.`npi` = c.`npi` AND nl.`is_primary` = 1
            JOIN `{oldLocation}` ol ON ol.`npi` = c.`npi` AND ol.`is_primary` = 1
            WHERE NOT ({AddressLine("nl")} <=> {AddressLine("ol")})
            """,
        ];
    }

    private async Task StepAsync(MySqlConnection connection, string what, string sql, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
        log.Information("Projection {Step}: {Rows:N0} rows in {Seconds:N0}s", what, rows, (DateTime.UtcNow - started).TotalSeconds);
    }

    private static string Staging(string table) => SqlIdentifier.Quote(table + "_staging");

    /// <summary>NULL for empty or whitespace-only text.</summary>
    private static string Text(string column) => $"NULLIF(TRIM({column}), '')";

    /// <summary>MM/DD/YYYY text → DATE.</summary>
    private static string Date(string column) => $"STR_TO_DATE({Text(column)}, '%m/%d/%Y')";

    // ZIP+4 from a raw US postal code ("117011234", "11701", "11701-1234"); NULL for foreign addresses.
    private static string IsUsZip(string postal, string country) =>
        $"(COALESCE({Text(country)}, 'US') = 'US' AND REGEXP_REPLACE(COALESCE({postal}, ''), '[^0-9]', '') REGEXP '^[0-9]{{5}}([0-9]{{4}})?$')";

    private static string Zip5(string postal, string country) =>
        $"IF({IsUsZip(postal, country)}, LEFT(REGEXP_REPLACE({postal}, '[^0-9]', ''), 5), NULL)";

    private static string Zip4(string postal, string country) =>
        $"IF({IsUsZip(postal, country)} AND CHAR_LENGTH(REGEXP_REPLACE({postal}, '[^0-9]', '')) = 9, RIGHT(REGEXP_REPLACE({postal}, '[^0-9]', ''), 4), NULL)";

    // Deactivated NPIs and their dates, for the site's "Deactivated on [date]" status (migration 077).
    private static string DeactivatedSql(string scope) => $"""
        INSERT INTO `provider_deactivated_staging` (`npi`, `deactivation_date`)
        SELECT n.`NPI`, {Date("n.`NPI_Deactivation_Date`")}
        FROM `npidata` n
        {scope}
        WHERE n.`Is_Deactivated` = 1
        """;

    // The tables built from npidata take a scope (a JOIN limiting n to the batch's NPIs, or ""); the others join
    // provider_staging, so they follow it.
    private static string ProviderSql(string scope)
    {
        var last = Text("n.`Provider_Last_Name_Legal_Name`");
        var first = Text("n.`Provider_First_Name`");
        var middle = Text("n.`Provider_Middle_Name`");
        var org = Text("n.`Provider_Organization_Name_Legal_Business_Name`");
        var primary = new StringBuilder("COALESCE(CASE");
        for (var slot = 1; slot <= TaxonomySlots; slot++)
        {
            primary.Append(CultureInfo.InvariantCulture,
                $" WHEN n.`Healthcare_Provider_Primary_Taxonomy_Switch_{slot}` = 'Y' AND {Text($"n.`Healthcare_Provider_Taxonomy_Code_{slot}`")} IS NOT NULL THEN {Text($"n.`Healthcare_Provider_Taxonomy_Code_{slot}`")}");
        }

        primary.Append(CultureInfo.InvariantCulture, $" END, {Text("n.`Healthcare_Provider_Taxonomy_Code_1`")})");

        return $"""
            INSERT INTO `provider_staging` (`npi`, `entity_type`, `last_name`, `first_name`, `middle_name`, `name_prefix`, `name_suffix`,
              `credential`, `credential_key`, `org_name`, `sort_name`, `gender`, `primary_taxonomy_code`, `phone`, `enumeration_date`, `last_update_date`)
            SELECT n.`NPI`,
              CAST(n.`Entity_Type_Code` AS UNSIGNED),
              {last}, {first}, {middle},
              {Text("n.`Provider_Name_Prefix_Text`")}, {Text("n.`Provider_Name_Suffix_Text`")},
              {Text("n.`Provider_Credential_Text`")},
              NULLIF(REGEXP_REPLACE(UPPER(COALESCE(n.`Provider_Credential_Text`, '')), '[^A-Z0-9]', ''), ''),
              {org},
              CASE WHEN n.`Entity_Type_Code` = '2' THEN COALESCE({org}, '')
                   ELSE CONCAT(COALESCE({last}, ''), IF(COALESCE({first}, {middle}) IS NULL, '', CONCAT(', ', CONCAT_WS(' ', {first}, {middle})))) END,
              LEFT({Text("n.`Provider_Gender_Code`")}, 1),
              {primary},
              {Text("n.`Provider_Business_Practice_Location_Address_Telephone_Number`")},
              {Date("n.`Provider_Enumeration_Date`")},
              {Date("n.`Last_Update_Date`")}
            FROM `npidata` n
            {scope}
            WHERE {Active}
            """;
    }

    // One pass over npidata: each row is crossed with the slot numbers 1-15 and the slot's columns
    // picked with CASE, instead of 15 separate scans. CMS writes the literal text "NULL" in ~16,500
    // license state codes (Sep 2026); that, and only that column, is treated as empty.
    private static string TaxonomySql(string scope)
    {
        string Pick(string prefix) => "CASE s.slot " + string.Join(" ", Enumerable.Range(1, TaxonomySlots)
            .Select(i => $"WHEN {i} THEN n.`{prefix}_{i}`")) + " END";
        var slots = string.Join(" UNION ALL ", Enumerable.Range(1, TaxonomySlots).Select(i => $"SELECT {i} AS slot"));
        return $"""
            INSERT INTO `provider_taxonomy_staging` (`npi`, `slot`, `taxonomy_code`, `is_primary`, `license_no`, `license_state`)
            SELECT x.npi, x.slot, x.code, x.is_primary, x.license_no, x.license_state
            FROM (
              SELECT n.`NPI` AS npi, s.slot,
                {Text(Pick("Healthcare_Provider_Taxonomy_Code"))} AS code,
                COALESCE({Pick("Healthcare_Provider_Primary_Taxonomy_Switch")} = 'Y', 0) AS is_primary,
                {Text(Pick("Provider_License_Number"))} AS license_no,
                NULLIF({Text(Pick("Provider_License_Number_State_Code"))}, 'NULL') AS license_state
              FROM `npidata` n
              {scope}
              CROSS JOIN ({slots}) s
              WHERE {Active}
            ) x
            WHERE x.code IS NOT NULL
            """;
    }

    private static string PrimaryLocationSql(string scope) => $"""
        INSERT INTO `provider_location_staging` (`npi`, `is_primary`, `address1`, `address2`, `city`, `state`, `zip5`, `zip4`, `postal_code`, `country_code`, `phone`)
        SELECT n.`NPI`, 1,
          {Text("n.`Provider_First_Line_Business_Practice_Location_Address`")},
          {Text("n.`Provider_Second_Line_Business_Practice_Location_Address`")},
          {Text("n.`Provider_Business_Practice_Location_Address_City_Name`")},
          {Text("n.`Provider_Business_Practice_Location_Address_State_Name`")},
          {Zip5("n.`Provider_Business_Practice_Location_Address_Postal_Code`", "n.`Provider_Business_Practice_Location_Address_Country_Code`")},
          {Zip4("n.`Provider_Business_Practice_Location_Address_Postal_Code`", "n.`Provider_Business_Practice_Location_Address_Country_Code`")},
          {Text("n.`Provider_Business_Practice_Location_Address_Postal_Code`")},
          {Text("n.`Provider_Business_Practice_Location_Address_Country_Code`")},
          {Text("n.`Provider_Business_Practice_Location_Address_Telephone_Number`")}
        FROM `npidata` n
        {scope}
        WHERE {Active}
        """;

    private static readonly string SecondaryLocationSql = $"""
        INSERT INTO `provider_location_staging` (`npi`, `is_primary`, `address1`, `address2`, `city`, `state`, `zip5`, `zip4`, `postal_code`, `country_code`, `phone`)
        SELECT l.`NPI`, 0,
          {Text("l.`Provider_Secondary_Practice_Location_Address_Line_1`")},
          {Text("l.`Provider_Secondary_Practice_Location_Address_Line_2`")},
          {Text("l.`Provider_Secondary_Practice_Location_Address_City_Name`")},
          {Text("l.`Provider_Secondary_Practice_Location_Address_State_Name`")},
          {Zip5("l.`Provider_Secondary_Practice_Location_Address_Postal_Code`", "l.`Provider_Secondary_Practice_Location_Address_Country_Code`")},
          {Zip4("l.`Provider_Secondary_Practice_Location_Address_Postal_Code`", "l.`Provider_Secondary_Practice_Location_Address_Country_Code`")},
          {Text("l.`Provider_Secondary_Practice_Location_Address_Postal_Code`")},
          {Text("l.`Provider_Secondary_Practice_Location_Address_Country_Code`")},
          {Text("l.`Provider_Secondary_Practice_Location_Address_Telephone_Number`")}
        FROM `practice_locations` l
        JOIN `provider_staging` p ON p.`npi` = l.`NPI`
        ORDER BY l.`NPI`, l.`ID`
        """;

    private static readonly string OtherNameSql = $"""
        INSERT INTO `provider_other_name_staging` (`npi`, `name`, `type_code`)
        SELECT o.`NPI`, {Text("o.`Provider_Other_Organization_Name`")}, {Text("o.`Provider_Other_Organization_Name_Type_Code`")}
        FROM `other_names` o
        JOIN `provider_staging` p ON p.`npi` = o.`NPI`
        WHERE {Text("o.`Provider_Other_Organization_Name`")} IS NOT NULL
        ORDER BY o.`NPI`, o.`ID`
        """;

    private static readonly string ProfileSql = $"""
        INSERT INTO `provider_profile_staging` (`npi`, `is_sole_proprietor`, `is_subpart`, `parent_org_name`,
          `official_prefix`, `official_first_name`, `official_middle_name`, `official_last_name`, `official_suffix`, `official_credential`,
          `official_title`, `official_phone`, `mailing_address1`, `mailing_address2`, `mailing_city`, `mailing_state`, `mailing_postal_code`,
          `mailing_country_code`, `mailing_phone`, `mailing_fax`, `practice_fax`)
        SELECT n.`NPI`,
          CASE {Text("n.`Is_Sole_Proprietor`")} WHEN 'Y' THEN 1 WHEN 'N' THEN 0 END,
          CASE {Text("n.`Is_Organization_Subpart`")} WHEN 'Y' THEN 1 WHEN 'N' THEN 0 END,
          {Text("n.`Parent_Organization_LBN`")},
          {Text("n.`Authorized_Official_Name_Prefix_Text`")}, {Text("n.`Authorized_Official_First_Name`")},
          {Text("n.`Authorized_Official_Middle_Name`")}, {Text("n.`Authorized_Official_Last_Name`")},
          {Text("n.`Authorized_Official_Name_Suffix_Text`")}, {Text("n.`Authorized_Official_Credential_Text`")},
          {Text("n.`Authorized_Official_Title_or_Position`")}, {Text("n.`Authorized_Official_Telephone_Number`")},
          {Text("n.`Provider_First_Line_Business_Mailing_Address`")}, {Text("n.`Provider_Second_Line_Business_Mailing_Address`")},
          {Text("n.`Provider_Business_Mailing_Address_City_Name`")}, {Text("n.`Provider_Business_Mailing_Address_State_Name`")},
          {Text("n.`Provider_Business_Mailing_Address_Postal_Code`")}, {Text("n.`Provider_Business_Mailing_Address_Country_Code`")},
          {Text("n.`Provider_Business_Mailing_Address_Telephone_Number`")}, {Text("n.`Provider_Business_Mailing_Address_Fax_Number`")},
          {Text("n.`Provider_Business_Practice_Location_Address_Fax_Number`")}
        FROM `npidata` n
        JOIN `provider_staging` p ON p.`npi` = n.`NPI`
        """;

    // Same one-pass slot unpivot as the taxonomies, over the 50 other-identifier slots.
    private static string IdentifierSql()
    {
        string Pick(string prefix) => "CASE s.slot " + string.Join(" ", Enumerable.Range(1, IdentifierSlots)
            .Select(i => $"WHEN {i} THEN n.`{prefix}_{i}`")) + " END";
        var slots = string.Join(" UNION ALL ", Enumerable.Range(1, IdentifierSlots).Select(i => $"SELECT {i} AS slot"));
        return $"""
            INSERT INTO `provider_identifier_staging` (`npi`, `slot`, `identifier`, `type_code`, `state`, `issuer`)
            SELECT x.npi, x.slot, x.identifier, x.type_code, x.state, x.issuer
            FROM (
              SELECT n.`NPI` AS npi, s.slot,
                {Text(Pick("Other_Provider_Identifier"))} AS identifier,
                {Text(Pick("Other_Provider_Identifier_Type_Code"))} AS type_code,
                {Text(Pick("Other_Provider_Identifier_State"))} AS state,
                {Text(Pick("Other_Provider_Identifier_Issuer"))} AS issuer
              FROM `npidata` n
              JOIN `provider_staging` p ON p.`npi` = n.`NPI`
              CROSS JOIN ({slots}) s
              WHERE n.`Other_Provider_Identifier_1` IS NOT NULL
            ) x
            WHERE x.identifier IS NOT NULL
            """;
    }

    private static readonly string EndpointSql = $"""
        INSERT INTO `provider_endpoint_staging` (`npi`, `endpoint_type`, `endpoint_type_description`, `endpoint`, `endpoint_description`,
          `use_description`, `content_description`, `affiliation_name`, `affiliation_city`, `affiliation_state`)
        SELECT e.`NPI`, {Text("e.`Endpoint_Type`")}, {Text("e.`Endpoint_Type_Description`")}, {Text("e.`Endpoint`")},
          {Text("e.`Endpoint_Description`")},
          COALESCE({Text("e.`Use_Description`")}, {Text("e.`Other_Use_Description`")}),
          COALESCE({Text("e.`Content_Description`")}, {Text("e.`Other_Content_Description`")}),
          {Text("e.`Affiliation_Legal_Business_Name`")}, {Text("e.`Affiliation_Address_City`")}, {Text("e.`Affiliation_Address_State`")}
        FROM `endpoints` e
        JOIN `provider_staging` p ON p.`npi` = e.`NPI`
        WHERE {Text("e.`Endpoint`")} IS NOT NULL
        ORDER BY e.`NPI`, e.`ID`
        """;

    // Organization legal and other names, one row each, for the word-anywhere search (Stage 5.5 item 9, FULLTEXT).
    // Derived from provider and provider_other_name, so not part of row_hash.
    private const string OrgNameSql = """
        INSERT INTO `provider_org_name_staging` (`npi`, `name`)
        SELECT x.`npi`, x.`name`
        FROM (
          SELECT p.`npi`, p.`org_name` AS `name` FROM `provider_staging` p WHERE p.`org_name` IS NOT NULL
          UNION
          SELECT o.`npi`, o.`name` FROM `provider_other_name_staging` o
        ) x
        ORDER BY x.`npi`
        """;

    // Every (taxonomy code, location area) pair of each provider; DISTINCT because two locations can
    // share state/city/ZIP. Derived entirely from the two tables above, so not part of row_hash.
    private const string SearchSql = """
        INSERT INTO `provider_search_staging` (`taxonomy_code`, `state`, `city`, `zip5`, `npi`)
        SELECT DISTINCT t.taxonomy_code, IFNULL(l.state, ''), IFNULL(l.city, ''), IFNULL(l.zip5, ''), t.npi
        FROM `provider_taxonomy_staging` t
        JOIN `provider_location_staging` l ON l.npi = t.npi
        """;

    // One MD5 per provider over its own columns and all its child rows (ordered by content, not by
    // id), so the publisher (Stage 6) can find changed NPIs by comparing hashes.
    private const string RowHashSql = """
        UPDATE `provider_staging` p
        LEFT JOIN (
          SELECT npi, MD5(GROUP_CONCAT(CONCAT_WS('|', slot, taxonomy_code, is_primary, IFNULL(license_no, ''), IFNULL(license_state, ''))
                                       ORDER BY slot SEPARATOR '\n')) AS h
          FROM `provider_taxonomy_staging` GROUP BY npi) t ON t.npi = p.npi
        LEFT JOIN (
          SELECT npi, MD5(GROUP_CONCAT(CONCAT_WS('|', is_primary, IFNULL(address1, ''), IFNULL(address2, ''), IFNULL(city, ''), IFNULL(state, ''),
                                                 IFNULL(zip5, ''), IFNULL(zip4, ''), IFNULL(postal_code, ''), IFNULL(country_code, ''), IFNULL(phone, ''))
                                       ORDER BY is_primary DESC, address1, address2, city, state, postal_code, phone SEPARATOR '\n')) AS h
          FROM `provider_location_staging` GROUP BY npi) l ON l.npi = p.npi
        LEFT JOIN (
          SELECT npi, MD5(GROUP_CONCAT(CONCAT_WS('|', name, IFNULL(type_code, '')) ORDER BY name, type_code SEPARATOR '\n')) AS h
          FROM `provider_other_name_staging` GROUP BY npi) o ON o.npi = p.npi
        LEFT JOIN `provider_profile_staging` pp ON pp.npi = p.npi
        LEFT JOIN (
          SELECT npi, MD5(GROUP_CONCAT(CONCAT_WS('|', slot, identifier, IFNULL(type_code, ''), IFNULL(state, ''), IFNULL(issuer, ''))
                                       ORDER BY slot SEPARATOR '\n')) AS h
          FROM `provider_identifier_staging` GROUP BY npi) i ON i.npi = p.npi
        LEFT JOIN (
          SELECT npi, MD5(GROUP_CONCAT(CONCAT_WS('|', IFNULL(endpoint_type, ''), endpoint, IFNULL(endpoint_description, ''), IFNULL(use_description, ''),
                                                 IFNULL(content_description, ''), IFNULL(affiliation_name, ''), IFNULL(affiliation_city, ''), IFNULL(affiliation_state, ''))
                                       ORDER BY endpoint_type, endpoint, affiliation_name SEPARATOR '\n')) AS h
          FROM `provider_endpoint_staging` GROUP BY npi) e ON e.npi = p.npi
        SET p.row_hash = UNHEX(MD5(CONCAT_WS('#', p.entity_type, IFNULL(p.last_name, ''), IFNULL(p.first_name, ''), IFNULL(p.middle_name, ''),
            IFNULL(p.name_prefix, ''), IFNULL(p.name_suffix, ''), IFNULL(p.credential, ''), IFNULL(p.org_name, ''), IFNULL(p.gender, ''),
            IFNULL(p.primary_taxonomy_code, ''), IFNULL(p.phone, ''), IFNULL(p.enumeration_date, ''), IFNULL(p.last_update_date, ''),
            IFNULL(t.h, ''), IFNULL(l.h, ''), IFNULL(o.h, ''),
            MD5(CONCAT_WS('|', IFNULL(pp.is_sole_proprietor, ''), IFNULL(pp.is_subpart, ''), IFNULL(pp.parent_org_name, ''),
              IFNULL(pp.official_prefix, ''), IFNULL(pp.official_first_name, ''), IFNULL(pp.official_middle_name, ''), IFNULL(pp.official_last_name, ''),
              IFNULL(pp.official_suffix, ''), IFNULL(pp.official_credential, ''), IFNULL(pp.official_title, ''), IFNULL(pp.official_phone, ''),
              IFNULL(pp.mailing_address1, ''), IFNULL(pp.mailing_address2, ''), IFNULL(pp.mailing_city, ''), IFNULL(pp.mailing_state, ''),
              IFNULL(pp.mailing_postal_code, ''), IFNULL(pp.mailing_country_code, ''), IFNULL(pp.mailing_phone, ''), IFNULL(pp.mailing_fax, ''),
              IFNULL(pp.practice_fax, ''))),
            IFNULL(i.h, ''), IFNULL(e.h, ''))))
        """;

    private const string DataVersionSql = """
        REPLACE INTO `data_version` (`id`, `as_of_date`, `monthly_file`, `weekly_file`, `deactivation_file`,
          `nucc_version`, `hud_version`, `census_version`, `provider_count`, `projected_at`, `published_at`)
        SELECT 1,
          (SELECT MAX(`last_update_date`) FROM `provider`),
          (SELECT `filename` FROM `downlog` WHERE `status` = 'Completed' AND `kind` = 'Monthly' ORDER BY `file_date` DESC LIMIT 1),
          (SELECT `filename` FROM `downlog` WHERE `status` = 'Completed' AND `kind` = 'Weekly' ORDER BY `file_date` DESC LIMIT 1),
          (SELECT `filename` FROM `downlog` WHERE `status` = 'Completed' AND `kind` = 'Deactivation' ORDER BY `file_date` DESC LIMIT 1),
          (SELECT `version` FROM `reference_data` WHERE `source` = 'nucc'),
          (SELECT `version` FROM `reference_data` WHERE `source` = 'hud_zip_county'),
          (SELECT `version` FROM `reference_data` WHERE `source` = 'census_gazetteer'),
          @providers, @now, NULL
        """;
}
