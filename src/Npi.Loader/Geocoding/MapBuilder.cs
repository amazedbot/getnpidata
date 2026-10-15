using System.Diagnostics;
using System.Globalization;
using Dapper;
using MySqlConnector;
using Npi.Loader.Datasets;
using Npi.Loader.Db;
using Npi.Loader.Projection;
using Serilog;

namespace Npi.Loader.Geocoding;

/// <summary>
/// Builds <c>provider_map</c> (CLAUDE.md §7 Stage 5.5 item 10): one point per provider and street address, at the
/// geocoded address or, when there is none, the ZIP centroid (approximate), plus <c>provider_map_specialty</c>
/// (each point's taxonomy codes by 0.1° grid cell, for specialty map searches). Staging + RENAME of both together,
/// so the map page never sees a half-built table. Its build time is kept in <c>reference_data</c> (source <c>provider_map</c>:
/// <c>loaded_at</c> the last full build, <c>checked_at</c> the last update of any kind). After an incremental projection, or
/// when addresses were geocoded or placed since, only those providers' rows are replaced (<see cref="BuilderPending"/>).
/// <c>provider_map_credential</c> (credential × cell) is shared with <see cref="CredentialBuilder"/>: a full map build fills it,
/// and so does an update when the credentials were rebuilt in full without filling it (<see cref="CredentialBuilder.MapCellsOwedSql"/>).
/// </summary>
public sealed class MapBuilder(Database database, ILogger log, double minRowRatio)
{
    public const string Source = "provider_map";

    private static string FillSql(string target, string locations) =>
        $"""
        INSERT INTO `{target}` (`npi`, `addr_key`, `lat`, `lon`, `approximate`, `source`, `pt`)
        SELECT x.npi, x.addr_key, x.lat, x.lon, x.approximate, x.source, ST_SRID(POINT(x.lon, x.lat), 0)
        FROM (
          -- Best first: Overture (the building), the Census geocoder (interpolated along the street), the ZIP centroid.
          SELECT l.npi, l.addr_key,
                 CAST(COALESCE(o.lat, g.lat, c.lat) AS DOUBLE) AS lat, CAST(COALESCE(o.lon, g.lon, c.lon) AS DOUBLE) AS lon,
                 o.lat IS NULL AND g.lat IS NULL AS approximate,
                 CASE WHEN o.lat IS NOT NULL THEN o.source WHEN g.lat IS NOT NULL THEN 'census' ELSE 'zip' END AS source
          FROM (SELECT DISTINCT `npi`, `addr_key`, `zip5` FROM {locations} WHERE `addr_key` IS NOT NULL) l
          LEFT JOIN `address_point` o ON o.`addr_key` = l.`addr_key`
          LEFT JOIN `address_geocode` g ON g.`addr_key` = l.`addr_key` AND g.`status` = 'Match'
          LEFT JOIN `zip_centroid` c ON c.`zip5` = l.`zip5`
        ) x
        WHERE x.lat IS NOT NULL
        """;

    // The cell formula is MapBounds.Cells' (Npi.Core); keep them in step.
    private const string Cell = "FLOOR((m.`lat` + 90) * 10) * 3600 + FLOOR((m.`lon` + 180) * 10)";

    private static string FillSpecialtySql(string target, string map) =>
        $"""
        INSERT INTO `{target}` (`taxonomy_code`, `cell`, `npi`)
        SELECT DISTINCT t.`taxonomy_code`, {Cell}, m.`npi`
        FROM {map} m
        JOIN `provider_taxonomy` t ON t.`npi` = m.`npi`
        """;

    // Addresses geocoded or placed since @since.
    private const string ChangedAddresses =
        "SELECT `addr_key` FROM `address_geocode` WHERE `geocoded_at` >= @since UNION SELECT `addr_key` FROM `address_point` WHERE `matched_at` >= @since";

    /// <summary>
    /// True when the projection changed (in full, or NPIs are queued), addresses were geocoded or placed since the last build,
    /// or the credentials were rebuilt in full and left provider_map_credential to the map.
    /// </summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await NeedsFullBuildAsync(connection, ct) || await CredentialBuilder.MapCellsOwedAsync(connection, ct) || await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM `builder_pending` WHERE `builder` = @builder)
                OR (SELECT `checked_at` FROM `reference_data` WHERE `source` = @source)
                   < GREATEST(COALESCE((SELECT MAX(`geocoded_at`) FROM `address_geocode`), '1970-01-01'),
                              COALESCE((SELECT MAX(`matched_at`) FROM `address_point`), '1970-01-01'))
            """, new { source = Source, builder = BuilderPending.Map }, cancellationToken: ct)) != 0;
    }

    /// <summary>
    /// Waits for another process's build of this database's map tables (provider_map_credential included: the credential
    /// builder takes the lock too) to finish, then holds the lock until <see cref="UnlockAsync"/>.
    /// </summary>
    internal static async Task LockAsync(MySqlConnection connection, CancellationToken ct)
    {
        if (await connection.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT GET_LOCK(CONCAT('getnpidata.', DATABASE(), '.provider_map'), 7200)", cancellationToken: ct)) != 1)
        {
            throw new TimeoutException("Another provider_map build is still running after 2 hours.");
        }
    }

    internal static Task UnlockAsync(MySqlConnection connection) =>
        connection.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT RELEASE_LOCK(CONCAT('getnpidata.', DATABASE(), '.provider_map'))", cancellationToken: CancellationToken.None));

    private static async Task<bool> NeedsFullBuildAsync(MySqlConnection connection, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"""
            SELECT NOT EXISTS (SELECT 1 FROM `reference_data` WHERE `source` = @source)
                OR NOT EXISTS (SELECT 1 FROM `provider_map_specialty`)
                OR (SELECT `loaded_at` FROM `reference_data` WHERE `source` = @source) < {BuilderPending.FullProjectedAt}
            """, new { source = Source }, cancellationToken: ct)) != 0;

    /// <param name="full">Rebuild every point (Overture for every state); otherwise only the changed providers when that is enough.</param>
    public async Task<long> BuildAsync(CancellationToken ct, bool full = false)
    {
        var watch = Stopwatch.StartNew();
        await using var connection = await database.OpenAsync(ct);

        // INSERT … SELECT under REPEATABLE READ locks every row it reads, which would block the geocoder and the Overture
        // matcher from saving into address_geocode / address_point for the whole build. READ COMMITTED reads without locks.
        await Database.ExecuteAsync(connection, "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED", ct);

        // Both builds would use provider_map_staging: wait for another process's build of this database to finish first.
        await LockAsync(connection, ct);
        try
        {
            if (!full && !await NeedsFullBuildAsync(connection, ct))
            {
                // The credentials were rebuilt in full and left provider_map_credential to the map: it is filled once, after the
                // points moved, so the update leaves the batch's credential cells alone.
                var cellsOwed = await CredentialBuilder.MapCellsOwedAsync(connection, ct);
                long rows;
                try
                {
                    rows = await BuildQueuedAsync(connection, watch, credentialCells: !cellsOwed, ct);
                }
                finally
                {
                    await BuilderPending.DropBatchAsync(connection, BuilderPending.Map);
                }

                if (cellsOwed)
                {
                    await CredentialBuilder.FillMapCellsAsync(connection, minRowRatio, log, ct);
                }

                return rows;
            }

            return await BuildLockedAsync(connection, watch, ct);
        }
        finally
        {
            await UnlockAsync(connection);
        }
    }

    private async Task<long> BuildLockedAsync(MySqlConnector.MySqlConnection connection, Stopwatch watch, CancellationToken ct)
    {
        // provider_map_credential is filled from the credentials as they are now: the time they were read is kept with it.
        var cellsAt = DateTime.UtcNow;
        var counts = await TableSwap.ReplaceAsync(connection, ["provider_map", "provider_map_specialty", "provider_map_credential"], minRowRatio, async () =>
        {
            await Database.ExecuteAsync(connection, FillSql("provider_map_staging", "`provider_location`"), ct);
            await Database.ExecuteAsync(connection, FillSpecialtySql("provider_map_specialty_staging", "`provider_map_staging`"), ct);
            await Database.ExecuteAsync(connection,
                CredentialBuilder.MapCredentialSql("provider_map_credential_staging", "provider_credential", "provider_map_staging"), ct);
        }, ct);
        await CredentialBuilder.RecordMapCellsAsync(connection, cellsAt, counts["provider_map_credential"], "filled by the map", ct);
        var rows = counts["provider_map"];
        var exact = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM `provider_map` WHERE `approximate` = 0", cancellationToken: ct));
        var bySource = string.Join(", ", (await connection.QueryAsync<(string Source, long Rows)>(new CommandDefinition(
            "SELECT `source`, COUNT(*) FROM `provider_map` GROUP BY `source` ORDER BY COUNT(*) DESC", cancellationToken: ct)))
            .Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Source} {r.Rows:N0}")));
        var now = DateTime.UtcNow;
        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
            VALUES (@source, @version, '', @rows, @now, @now) AS new
            ON DUPLICATE KEY UPDATE `version` = new.`version`, `rows_loaded` = new.`rows_loaded`, `loaded_at` = new.`loaded_at`, `checked_at` = new.`checked_at`
            """, ct, param: new { source = Source, version = string.Create(CultureInfo.InvariantCulture, $"{exact:N0} of {rows:N0} at the street address"), rows, now });
        log.Information("provider_map: {Rows:N0} provider addresses, {Exact:N0} at the street address ({BySource}), in {Seconds:N0}s",
            rows, exact, bySource, watch.Elapsed.TotalSeconds);
        await BuilderPending.ClearAsync(connection, BuilderPending.Map, ct);
        return rows;
    }

    /// <summary>
    /// Replaces the points of the queued NPIs and of everyone at an address geocoded or placed since the last update, with their
    /// specialty and credential cells; those providers are queued for record quality (the "not mapped" reason).
    /// </summary>
    /// <param name="credentialCells">Replace the providers' provider_map_credential rows too (false: the caller refills that table).</param>
    private async Task<long> BuildQueuedAsync(MySqlConnection connection, Stopwatch watch, bool credentialCells, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var (batch, queued) = await BuilderPending.TakeAsync(connection, BuilderPending.Map, ct);
        var since = await connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT `checked_at` FROM `reference_data` WHERE `source` = @source", new { source = Source }, cancellationToken: ct));
        var placed = await BuilderPending.AddAsync(connection, batch,
            $"SELECT DISTINCT l.`npi` FROM `provider_location` l JOIN ({ChangedAddresses}) a ON a.`addr_key` = l.`addr_key`", ct, new { since });

        await using var tx = await connection.BeginTransactionAsync(ct);
        Task<int> RunAsync(string sql) => connection.ExecuteAsync(new CommandDefinition(sql, transaction: tx, commandTimeout: 0, cancellationToken: ct));
        var points = $"(SELECT m.* FROM `provider_map` m JOIN `{batch}` b ON b.`npi` = m.`npi`)";
        await RunAsync(
            $"""
            DELETE s FROM `provider_map_specialty` s
            JOIN (SELECT DISTINCT t.`taxonomy_code`, {Cell} AS `cell`, m.`npi`
                  FROM {points} m
                  JOIN (SELECT ot.`npi`, ot.`taxonomy_code` FROM {BuilderPending.OldTaxonomies(batch)} ot
                        UNION SELECT x.`npi`, x.`taxonomy_code` FROM `provider_taxonomy` x JOIN `{batch}` b3 ON b3.`npi` = x.`npi`) t ON t.`npi` = m.`npi`) k
              ON s.`taxonomy_code` = k.`taxonomy_code` AND s.`cell` = k.`cell` AND s.`npi` = k.`npi`
            """);
        // The credential cells by key: the credentials as they are now (an incremental credential build already replaced the
        // batch's and their cells at these points, or left the cells of NPIs queued here to this update) at the old points.
        if (credentialCells)
        {
            await RunAsync(
                $"""
                DELETE s FROM `provider_map_credential` s
                JOIN (SELECT DISTINCT c.`credential`, {Cell} AS `cell`, m.`npi` FROM {points} m JOIN `provider_credential` c ON c.`npi` = m.`npi`) k
                  ON s.`credential` = k.`credential` AND s.`cell` = k.`cell` AND s.`npi` = k.`npi`
                """);
        }

        await RunAsync($"DELETE m FROM `provider_map` m JOIN `{batch}` b ON b.`npi` = m.`npi`");
        var rows = await RunAsync(FillSql("provider_map",
            $"(SELECT l.`npi`, l.`addr_key`, l.`zip5` FROM `provider_location` l JOIN `{batch}` b ON b.`npi` = l.`npi`) bl"));
        await RunAsync(FillSpecialtySql("provider_map_specialty", points));
        if (credentialCells)
        {
            await RunAsync(
                $"""
                INSERT INTO `provider_map_credential` (`credential`, `cell`, `npi`)
                SELECT DISTINCT c.`credential`, {Cell}, m.`npi` FROM {points} m JOIN `provider_credential` c ON c.`npi` = m.`npi`
                """);
        }

        await RunAsync($"INSERT IGNORE INTO `{QualityBuilder.Pending}` (`npi`) SELECT `npi` FROM `{batch}`");
        await BuilderPending.DoneAsync(connection, BuilderPending.Map, tx, ct);
        await connection.ExecuteAsync(new CommandDefinition("UPDATE `reference_data` SET `checked_at` = @started WHERE `source` = @source",
            new { source = Source, started }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        await BuilderPending.CleanAsync(connection, null, ct);
        log.Information("provider_map: {Npis:N0} providers updated ({Queued:N0} queued, {Placed:N0} more at newly placed addresses), {Rows:N0} points, in {Seconds:N0}s",
            queued + placed, queued, placed, rows, watch.Elapsed.TotalSeconds);
        return rows;
    }
}
