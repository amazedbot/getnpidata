using System.Diagnostics;
using Dapper;
using MySqlConnector;
using Npi.Loader.Datasets;
using Npi.Loader.Db;
using Serilog;

namespace Npi.Loader.Projection;

/// <summary>
/// Labels every US practice address (CLAUDE.md §7 Stage 5.5 item 18) as commercial (or institutional), residential or
/// unknown, from the FEMA building under it (<c>address_building</c>) and signs of a business at the
/// address: a suite or floor in it, an Overture place whose address it is, five or more providers, or three or more
/// with an organization among them. A business sign turns a residential (or unknown) building into commercial: FEMA
/// labels some strip malls and offices in former houses as dwellings. FEMA's Nursing Home and Institutional Dormitory
/// types count as commercial (institutional): as a category of their own they were often not care facilities at all
/// (owner, 2026-10-11). Rebuilt after each FEMA load and full projection; after an incremental projection only the addresses
/// its NPIs left or moved to (<c>address_pending</c>) are relabelled, and the building_search rows of the providers there replaced.
/// </summary>
public sealed class AddressTypeBuilder(Database database, ILogger log, double minRowRatio)
{
    public const string Source = "address_type";

    /// <summary>SQL: 1 when a location's address names a suite, floor, building or room (a sign of a business), else 0.</summary>
    // Verbatim: MySQL must receive '\\b' (a word boundary); a plain C# string sends '\b', a backspace, and nothing matched
    // (2026-10-13 to 10-14, found when a full relabel moved 106k addresses out of commercial).
    public static string SuiteSql(string location) =>
        $@"UPPER(CONCAT_WS(' ', {location}.`address1`, {location}.`address2`)) REGEXP '\\b(STE|SUITE|FL|FLR|FLOOR|BLDG|BUILDING|RM|ROOM)\\b'";

    // Each address's label from its providers, FEMA building and Overture point; {scope} narrows the locations read.
    private static string LabelSql(string target, string scope) => $$"""
        INSERT INTO `{{target}}` (`addr_key`, `building_type`, `basis`)
        SELECT x.`addr_key`,
          CASE
            WHEN x.`occ_cls` = 'Residential' AND x.`prim_occ` IN ('Nursing Home', 'Institutional Dormitory') THEN 'commercial'
            WHEN x.`business` THEN 'commercial'
            WHEN x.`occ_cls` = 'Residential' THEN 'residential'
            WHEN x.`occ_cls` IS NULL OR x.`occ_cls` = 'Unclassified' THEN 'unknown'
            ELSE 'commercial'
          END,
          LEFT(CONCAT_WS('; ',
            CASE
              WHEN x.`matched` IS NULL THEN 'not placed on a building (mapped to its ZIP code only)'
              WHEN x.`matched` = 'none' THEN 'no building found at the mapped point'
              ELSE CONCAT('FEMA USA Structures: ',
                IF(x.`prim_occ` IS NULL OR x.`prim_occ` = 'Unclassified', CONCAT(COALESCE(x.`occ_cls`, 'Unclassified'), ' building'), TRIM(x.`prim_occ`)),
                IF(x.`sqft` IS NULL, '', CONCAT(', ', FORMAT(x.`sqft`, 0), ' sq ft')),
                IF(x.`matched` = 'near', CONCAT(', the nearest building (about ', x.`distance_m`, ' m)'), ''))
            END,
            IF(x.`suite`, 'a suite or floor in the address', NULL),
            IF(x.`place`, 'a business is listed at this address (Overture Maps)', NULL),
            IF(x.`providers` >= 5 OR (x.`org` AND x.`providers` >= 3),
               CONCAT(x.`providers`, ' providers', IF(x.`org`, ' including an organization', ''), ' at this address'), NULL)), 255)
        FROM (
          SELECT s.`addr_key`, s.`suite`, s.`providers`, s.`org`, b.`matched`, b.`distance_m`, b.`occ_cls`, b.`prim_occ`, b.`sqft`,
            COALESCE(ap.`source` IN ('place', 'place_name'), 0) AS `place`,
            s.`suite` OR COALESCE(ap.`source` IN ('place', 'place_name'), 0) OR s.`providers` >= 5 OR (s.`org` AND s.`providers` >= 3) AS `business`
          FROM (
            SELECT l.`addr_key`,
              MAX({{SuiteSql("l")}}) AS `suite`,
              COUNT(DISTINCT l.`npi`) AS `providers`,
              MAX(p.`entity_type` = 2) AS `org`
            FROM `provider_location` l JOIN `provider` p ON p.`npi` = l.`npi`
            WHERE l.`addr_key` IS NOT NULL{{scope}}
            GROUP BY l.`addr_key`
          ) s
          LEFT JOIN `address_building` b ON b.`addr_key` = s.`addr_key`
          LEFT JOIN `address_point` ap ON ap.`addr_key` = s.`addr_key`
        ) x
        """;

    /// <summary>True when the projection was rebuilt in full since the labels were, or addresses are queued.</summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        if (await Database.CountAsync(connection, "address_building", ct) == 0)
        {
            // Nothing to label until FEMA structures are loaded; a queue is only discarded.
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM `builder_pending` WHERE `builder` = @builder)", new { builder = BuilderPending.AddressTypes }, cancellationToken: ct)) != 0;
        }

        return await NeedsFullBuildAsync(connection, ct)
               || await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                   "SELECT EXISTS (SELECT 1 FROM `builder_pending` WHERE `builder` = @builder)", new { builder = BuilderPending.AddressTypes }, cancellationToken: ct)) != 0;
    }

    private static async Task<bool> NeedsFullBuildAsync(MySqlConnection connection, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"""
            SELECT NOT EXISTS (SELECT 1 FROM `reference_data` WHERE `source` = @source)
                OR (SELECT `loaded_at` FROM `reference_data` WHERE `source` = @source) < {BuilderPending.FullProjectedAt}
            """, new { source = Source }, cancellationToken: ct)) != 0;

    /// <param name="full">Relabel every address (a FEMA load, the <c>address-types</c> command); otherwise only the queued ones when that is enough.</param>
    public async Task<long> BuildAsync(CancellationToken ct, bool full = false)
    {
        var watch = Stopwatch.StartNew();
        await using var connection = await database.OpenAsync(ct);
        if (!full && await Database.CountAsync(connection, "address_building", ct) == 0)
        {
            await BuilderPending.ClearAsync(connection, BuilderPending.AddressTypes, ct);
            await Database.ExecuteAsync(connection, "DELETE FROM `address_pending`", ct);
            return 0;
        }

        if (!full && !await NeedsFullBuildAsync(connection, ct))
        {
            try
            {
                return await BuildQueuedAsync(connection, watch, ct);
            }
            finally
            {
                await BuilderPending.DropBatchAsync(connection, BuilderPending.AddressTypes);
                foreach (var table in Scratch)
                {
                    await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{table}`", CancellationToken.None);
                }
            }
        }

        var counts = await TableSwap.ReplaceAsync(connection, ["address_type", "building_search"], minRowRatio, async () =>
        {
            await Database.ExecuteAsync(connection, LabelSql("address_type_staging", ""), ct);
            // Building × location, like provider_search: the same location columns, so the search's location conditions apply as they are.
            await Database.ExecuteAsync(connection,
                """
                INSERT INTO `building_search_staging` (`building_type`, `state`, `city`, `zip5`, `npi`)
                SELECT DISTINCT t.`building_type`, IFNULL(l.`state`, ''), IFNULL(l.`city`, ''), IFNULL(l.`zip5`, ''), l.`npi`
                FROM `provider_location` l JOIN `address_type_staging` t ON t.`addr_key` = l.`addr_key`
                """, ct);
        }, ct, deferIndexes: true, log: log);

        var byType = (await connection.QueryAsync<(string Type, long Rows)>(new CommandDefinition(
            "SELECT `building_type`, COUNT(*) FROM `address_type` GROUP BY `building_type` ORDER BY 2 DESC", cancellationToken: ct))).ToList();
        var now = DateTime.UtcNow;
        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
            VALUES (@source, @version, '', @rows, @now, @now) AS new
            ON DUPLICATE KEY UPDATE `version` = new.`version`, `rows_loaded` = new.`rows_loaded`, `loaded_at` = new.`loaded_at`, `checked_at` = new.`checked_at`
            """, ct, param: new { source = Source, version = now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), rows = counts["address_type"], now });
        await BuilderPending.ClearAsync(connection, BuilderPending.AddressTypes, ct);
        await Database.ExecuteAsync(connection, "DELETE FROM `address_pending`", ct);
        log.Information("Practice address types: {Rows:N0} addresses ({Types}) in {Seconds:N0}s", counts["address_type"],
            string.Join(", ", byType.Select(t => $"{t.Type} {t.Rows:N0}")), watch.Elapsed.TotalSeconds);
        return counts["address_type"];
    }

    private const string Addresses = "address_types_addresses";
    private const string Npis = "address_types_npis";
    private const string Labels = "address_types_labels";
    private const string Relabelled = "address_types_relabelled";
    private static readonly string[] Scratch = [Addresses, Npis, Labels, Relabelled];

    /// <summary>
    /// Relabels the queued addresses (those the queued NPIs left or moved to) and replaces the building_search rows of every
    /// provider at them; providers at an address whose label changed are queued for record quality (the home address reason).
    /// </summary>
    private async Task<long> BuildQueuedAsync(MySqlConnection connection, Stopwatch watch, CancellationToken ct)
    {
        var (batch, queued) = await BuilderPending.TakeAsync(connection, BuilderPending.AddressTypes, ct);
        Task<int> RunAsync(string sql, MySqlTransaction? tx = null) =>
            connection.ExecuteAsync(new CommandDefinition(sql, transaction: tx, commandTimeout: 0, cancellationToken: ct));
        foreach (var table in Scratch)
        {
            await RunAsync($"DROP TABLE IF EXISTS `{table}`");
        }

        await RunAsync($"CREATE TABLE `{Addresses}` (PRIMARY KEY (`addr_key`)) ENGINE=InnoDB SELECT `addr_key`, `zip5` FROM `address_pending`");
        await RunAsync($"CREATE TABLE `{Labels}` LIKE `address_type`");
        // provider_location has no index on addr_key, but on zip5, which is part of it.
        var atAddresses = $" AND l.`zip5` IN (SELECT `zip5` FROM `{Addresses}`) AND l.`addr_key` IN (SELECT `addr_key` FROM `{Addresses}`)";
        await RunAsync(LabelSql(Labels, atAddresses));
        // Search rows to redo: the batch's, and those of everyone at an address whose label changed (not everyone at the
        // queued addresses: a hospital's thousands of providers keep their rows when its label stays).
        await RunAsync($"CREATE TABLE `{Npis}` (PRIMARY KEY (`npi`)) ENGINE=InnoDB SELECT `npi` FROM `{batch}`");
        await RunAsync(
            $"""
            CREATE TABLE `{Relabelled}` (PRIMARY KEY (`npi`)) ENGINE=InnoDB
            SELECT DISTINCT l.`npi` FROM `provider_location` l
            JOIN `{Addresses}` a ON a.`addr_key` = l.`addr_key`
            LEFT JOIN `address_type` o ON o.`addr_key` = a.`addr_key` LEFT JOIN `{Labels}` n ON n.`addr_key` = a.`addr_key`
            WHERE l.`zip5` IN (SELECT `zip5` FROM `{Addresses}`) AND NOT (o.`building_type` <=> n.`building_type`)
            """);
        await RunAsync($"INSERT IGNORE INTO `{Npis}` (`npi`) SELECT `npi` FROM `{Relabelled}`");
        var addresses = await Database.CountAsync(connection, Addresses, ct);

        await using var tx = await connection.BeginTransactionAsync(ct);
        // Their building_search rows by key: the labels as stored × their locations then and now.
        await RunAsync(
            $"""
            DELETE s FROM `building_search` s
            JOIN (SELECT DISTINCT t.`building_type`, IFNULL(l.`state`, '') AS `state`, IFNULL(l.`city`, '') AS `city`, IFNULL(l.`zip5`, '') AS `zip5`, l.`npi`
                  FROM (SELECT ol.`npi`, ol.`addr_key`, ol.`state`, ol.`city`, ol.`zip5` FROM {BuilderPending.OldLocations(Npis)} ol
                        UNION SELECT x.`npi`, x.`addr_key`, x.`state`, x.`city`, x.`zip5` FROM `provider_location` x JOIN `{Npis}` n ON n.`npi` = x.`npi`) l
                  JOIN `address_type` t ON t.`addr_key` = l.`addr_key`) k
              ON s.`building_type` = k.`building_type` AND s.`state` = k.`state` AND s.`city` = k.`city` AND s.`zip5` = k.`zip5` AND s.`npi` = k.`npi`
            """, tx);
        // A changed label can change the record quality reason "home address" of everyone there.
        await RunAsync($"INSERT IGNORE INTO `{QualityBuilder.Pending}` (`npi`) SELECT `npi` FROM `{Relabelled}`", tx);
        await RunAsync($"DELETE t FROM `address_type` t JOIN `{Addresses}` a ON a.`addr_key` = t.`addr_key`", tx);
        await RunAsync($"INSERT INTO `address_type` SELECT * FROM `{Labels}`", tx);
        await RunAsync(
            $"""
            INSERT INTO `building_search` (`building_type`, `state`, `city`, `zip5`, `npi`)
            SELECT DISTINCT t.`building_type`, IFNULL(l.`state`, ''), IFNULL(l.`city`, ''), IFNULL(l.`zip5`, ''), l.`npi`
            FROM `provider_location` l JOIN `{Npis}` n ON n.`npi` = l.`npi` JOIN `address_type` t ON t.`addr_key` = l.`addr_key`
            """, tx);
        await RunAsync($"DELETE p FROM `address_pending` p JOIN `{Addresses}` a ON a.`addr_key` = p.`addr_key`", tx);
        await BuilderPending.DoneAsync(connection, BuilderPending.AddressTypes, tx, ct);
        await connection.ExecuteAsync(new CommandDefinition("UPDATE `reference_data` SET `checked_at` = UTC_TIMESTAMP() WHERE `source` = @source",
            new { source = Source }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        log.Information("Practice address types: {Addresses:N0} addresses relabelled for {Queued:N0} queued providers in {Seconds:N0}s",
            addresses, queued, watch.Elapsed.TotalSeconds);
        return addresses;
    }
}
