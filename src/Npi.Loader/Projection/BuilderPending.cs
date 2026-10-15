using Dapper;
using MySqlConnector;
using Npi.Loader.Db;

namespace Npi.Loader.Projection;

/// <summary>
/// The queue between an incremental projection and the tables built on it (migration 091). The projection queues the NPIs
/// it rebuilt for every builder (<see cref="Builders"/>; record quality in <c>quality_pending</c>) and keeps their locations
/// and taxonomy codes as they were before (the earliest state while an NPI is queued), so each builder can find the rows it
/// wrote for them by their exact keys and replace only those. A full projection clears the queue: the builders then
/// rebuild in full (<see cref="FullProjectedAt"/> is newer than their own build).
/// </summary>
public static class BuilderPending
{
    public const string Credentials = "credentials";
    public const string AddressTypes = "address_types";
    public const string Areas = "areas";
    public const string Map = "map";
    public const string AreaHistory = "area_history";

    /// <summary>
    /// Every builder fed by the projection's queue: <c>run</c> calls the first three side by side (<see cref="BuilderSteps"/>),
    /// then the map, then the area history. Each takes and empties only its own rows.
    /// </summary>
    public static readonly IReadOnlyList<string> Builders = [Credentials, AddressTypes, Areas, Map, AreaHistory];

    /// <summary>The <c>reference_data</c> source whose <c>loaded_at</c> is the last full projection build.</summary>
    public const string ProjectionSource = "projection";

    /// <summary>
    /// SQL: when the projection was last built in full. Without a record (no full build since migration 091), never: the
    /// builders' own last full builds stand. data_version.projected_at is no stand-in, since every incremental build moves it.
    /// </summary>
    public const string FullProjectedAt =
        "COALESCE((SELECT `loaded_at` FROM `reference_data` WHERE `source` = 'projection'), '1970-01-01')";

    private const string Marker = "-1"; // builder_old_location.is_primary of the row that marks an NPI as snapshotted

    /// <summary>
    /// Inside the projection's transaction, before the batch's rows are replaced: keeps the old locations (with their address
    /// label inputs) and taxonomy codes of the batch's NPIs not snapshotted yet.
    /// </summary>
    internal static async Task SnapshotAsync(MySqlConnection connection, string batch, MySqlTransaction tx, CancellationToken ct)
    {
        Task RunAsync(string sql) => connection.ExecuteAsync(new CommandDefinition(sql, transaction: tx, commandTimeout: 0, cancellationToken: ct));
        var fresh = $"JOIN `{batch}` b ON b.`npi` = x.`npi` WHERE NOT EXISTS (SELECT 1 FROM `builder_old_location` o WHERE o.`npi` = x.`npi` AND o.`is_primary` = {Marker})";
        await RunAsync(
            $"""
            INSERT INTO `builder_old_location` (`npi`, `addr_key`, `state`, `city`, `zip5`, `is_primary`, `label_input`)
            SELECT x.`npi`, x.`addr_key`, IFNULL(x.`state`, ''), IFNULL(x.`city`, ''), IFNULL(x.`zip5`, ''), x.`is_primary`, {LabelInput("x", "p")}
            FROM `provider_location` x JOIN `provider` p ON p.`npi` = x.`npi` {fresh}
            """);
        await RunAsync($"INSERT IGNORE INTO `builder_old_taxonomy` (`npi`, `taxonomy_code`) SELECT x.`npi`, x.`taxonomy_code` FROM `provider_taxonomy` x {fresh}");
        await RunAsync($"INSERT INTO `builder_old_location` (`npi`, `is_primary`) SELECT x.`npi`, {Marker} FROM `{batch}` x {fresh.Replace($"JOIN `{batch}` b ON b.`npi` = x.`npi` ", "", StringComparison.Ordinal)}");
    }

    /// <summary>Inside the projection's transaction, after the batch's rows were replaced: queues the NPIs and the addresses whose label inputs changed.</summary>
    internal static async Task QueueAsync(MySqlConnection connection, string batch, MySqlTransaction tx, CancellationToken ct)
    {
        await QueueAddressesAsync(connection, tx, ct);
        foreach (var builder in Builders)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"INSERT IGNORE INTO `builder_pending` (`builder`, `npi`) SELECT @builder, `npi` FROM `{batch}`", new { builder }, tx, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            $"INSERT IGNORE INTO `{QualityBuilder.Pending}` (`npi`) SELECT `npi` FROM `{batch}`", transaction: tx, cancellationToken: ct));
    }

    // SQL: a location's address label inputs (migration 092): 1 = a suite or floor in the address, 2 = the provider is an organization.
    private static string LabelInput(string location, string provider) =>
        $"({AddressTypeBuilder.SuiteSql(location)}) + 2 * ({provider}.`entity_type` = 2)";

    // The addresses where an (NPI, address, label inputs) row appeared or went between the snapshots and now, for every
    // snapshotted NPI (the batch, a temporary table, can't be read twice in one statement; the earlier queued NPIs only
    // repeat addresses already queued). An NPI that kept its addresses queues none: its addresses' labels can't have changed.
    private static Task<int> QueueAddressesAsync(MySqlConnection connection, MySqlTransaction tx, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT IGNORE INTO `address_pending` (`addr_key`, `zip5`)
            SELECT g.`addr_key`, MIN(g.`zip5`) FROM (
              SELECT d.`addr_key`, MIN(d.`zip5`) AS `zip5`
              FROM (SELECT o.`npi`, o.`addr_key`, o.`zip5`, o.`label_input`, -1 AS `side` FROM `builder_old_location` o
                    WHERE o.`is_primary` <> {Marker} AND o.`addr_key` IS NOT NULL
                    UNION ALL
                    SELECT l.`npi`, l.`addr_key`, l.`zip5`, {LabelInput("l", "p")}, 1 FROM `provider_location` l
                    JOIN `provider` p ON p.`npi` = l.`npi`
                    JOIN (SELECT DISTINCT `npi` FROM `builder_old_location` WHERE `is_primary` = {Marker}) m ON m.`npi` = l.`npi`
                    WHERE l.`addr_key` IS NOT NULL) d
              GROUP BY d.`npi`, d.`addr_key`, d.`label_input`
              HAVING SUM(d.`side`) <> 0) g
            GROUP BY g.`addr_key`
            """, transaction: tx, commandTimeout: 0, cancellationToken: ct));

    /// <summary>After a full projection every builder rebuilds in full: the queue and the snapshots are moot.</summary>
    internal static async Task ClearAllAsync(MySqlConnection connection, CancellationToken ct)
    {
        foreach (var table in new[] { "builder_pending", "builder_old_location", "builder_old_taxonomy", "address_pending" })
        {
            await Database.ExecuteAsync(connection, $"DELETE FROM `{table}`", ct);
        }
    }

    /// <summary>Copies a builder's queue to <c>{builder}_batch</c> (a plain table, so one statement can read it twice).</summary>
    /// <returns>The batch's table name and its size.</returns>
    public static async Task<(string Table, long Count)> TakeAsync(MySqlConnection connection, string builder, CancellationToken ct)
    {
        var table = BatchTable(builder);
        await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{table}`", ct);
        await connection.ExecuteAsync(new CommandDefinition(
            $"CREATE TABLE `{table}` (PRIMARY KEY (`npi`)) ENGINE=InnoDB SELECT `npi` FROM `builder_pending` WHERE `builder` = @builder",
            new { builder }, cancellationToken: ct));
        return (table, await Database.CountAsync(connection, table, ct));
    }

    public static string BatchTable(string builder) => $"{builder}_batch";

    /// <summary>Adds NPIs to a batch taken with <see cref="TakeAsync"/> (e.g. the map's NPIs at newly geocoded addresses).</summary>
    public static Task<int> AddAsync(MySqlConnection connection, string table, string selectNpis, CancellationToken ct, object? param = null) =>
        connection.ExecuteAsync(new CommandDefinition($"INSERT IGNORE INTO `{table}` (`npi`) {selectNpis}", param, commandTimeout: 0, cancellationToken: ct));

    /// <summary>
    /// Removes the batch's NPIs from the builder's queue, in the builder's transaction. The builder drops the snapshots nobody
    /// needs any more after its commit (<see cref="CleanAsync"/> without a transaction): builders run side by side (speed item
    /// 13), and a cleanup inside each one's transaction locked every snapshot row the others were reading (a deadlock).
    /// </summary>
    public static Task DoneAsync(MySqlConnection connection, string builder, MySqlTransaction tx, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            $"DELETE p FROM `builder_pending` p JOIN `{BatchTable(builder)}` b ON b.`npi` = p.`npi` WHERE p.`builder` = @builder",
            new { builder }, tx, commandTimeout: 0, cancellationToken: ct));

    /// <summary>A full build of one builder: its queue is done.</summary>
    public static async Task ClearAsync(MySqlConnection connection, string builder, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM `builder_pending` WHERE `builder` = @builder", new { builder }, cancellationToken: ct));
        await CleanAsync(connection, null, ct);
    }

    public static Task DropBatchAsync(MySqlConnection connection, string builder) =>
        Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{BatchTable(builder)}`", CancellationToken.None);

    /// <summary>
    /// Drops the snapshots of NPIs no builder (nor record quality) still has to update: inside <paramref name="tx"/> (record
    /// quality, which runs alone), or else in a READ COMMITTED transaction of its own, one cleanup at a time. That reads the
    /// queues without locking them and keeps locks only on the rows it deletes, which no builder running alongside reads (their
    /// NPIs are still queued); at most it waits for a builder's transaction to commit. A cleanup that waits 10 minutes for
    /// another, or loses a lock conflict, is left to the next one (the next builder, record quality or incremental projection).
    /// </summary>
    public static async Task CleanAsync(MySqlConnection connection, MySqlTransaction? tx, CancellationToken ct)
    {
        if (tx is not null)
        {
            await CleanInAsync(connection, tx, ct);
            return;
        }

        if (await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT GET_LOCK(CONCAT('getnpidata.', DATABASE(), '.builder_clean'), 600)", cancellationToken: ct)) != 1)
        {
            return;
        }

        try
        {
            await using var own = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            await CleanInAsync(connection, own, ct);
            await own.CommitAsync(ct);
        }
        catch (MySqlException ex) when (BuilderSteps.IsLockConflict(ex))
        {
            // Left to the next cleanup: the caller's own work is committed.
        }
        finally
        {
            await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT RELEASE_LOCK(CONCAT('getnpidata.', DATABASE(), '.builder_clean'))", cancellationToken: CancellationToken.None));
        }
    }

    private static async Task CleanInAsync(MySqlConnection connection, MySqlTransaction tx, CancellationToken ct)
    {
        const string unused = "NOT EXISTS (SELECT 1 FROM `builder_pending` p WHERE p.`npi` = o.`npi`) AND NOT EXISTS (SELECT 1 FROM `quality_pending` q WHERE q.`npi` = o.`npi`)";
        await connection.ExecuteAsync(new CommandDefinition($"DELETE o FROM `builder_old_location` o WHERE {unused}", transaction: tx, commandTimeout: 0, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition($"DELETE o FROM `builder_old_taxonomy` o WHERE {unused}", transaction: tx, commandTimeout: 0, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE a FROM `address_pending` a WHERE NOT EXISTS (SELECT 1 FROM `builder_pending` p WHERE p.`builder` = 'address_types')",
            transaction: tx, commandTimeout: 0, cancellationToken: ct));
    }

    /// <summary>
    /// SQL (a derived table: npi, addr_key, state, city, zip5, is_primary; NULL where the projection has NULL) of the batch's
    /// locations as the builders last saw them: the snapshot of a snapshotted NPI, else its current locations.
    /// </summary>
    public static string OldLocations(string batch) =>
        $"""
        (SELECT o.`npi`, o.`addr_key`, NULLIF(o.`state`, '') AS `state`, NULLIF(o.`city`, '') AS `city`, NULLIF(o.`zip5`, '') AS `zip5`, o.`is_primary`
         FROM `builder_old_location` o JOIN `{batch}` b1 ON b1.`npi` = o.`npi` WHERE o.`is_primary` <> {Marker}
         UNION ALL
         SELECT l.`npi`, l.`addr_key`, l.`state`, l.`city`, l.`zip5`, l.`is_primary`
         FROM `provider_location` l JOIN `{batch}` b2 ON b2.`npi` = l.`npi`
         WHERE NOT EXISTS (SELECT 1 FROM `builder_old_location` m WHERE m.`npi` = l.`npi` AND m.`is_primary` = {Marker}))
        """;

    /// <summary>SQL (a derived table: npi, taxonomy_code) of the batch's taxonomy codes as the builders last saw them.</summary>
    public static string OldTaxonomies(string batch) =>
        $"""
        (SELECT o.`npi`, o.`taxonomy_code` FROM `builder_old_taxonomy` o JOIN `{batch}` b1 ON b1.`npi` = o.`npi`
         UNION ALL
         SELECT t.`npi`, t.`taxonomy_code` FROM `provider_taxonomy` t JOIN `{batch}` b2 ON b2.`npi` = t.`npi`
         WHERE NOT EXISTS (SELECT 1 FROM `builder_old_location` m WHERE m.`npi` = t.`npi` AND m.`is_primary` = {Marker}))
        """;
}
