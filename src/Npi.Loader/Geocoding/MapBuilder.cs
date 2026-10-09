using System.Diagnostics;
using System.Globalization;
using Dapper;
using Npi.Loader.Datasets;
using Npi.Loader.Db;
using Serilog;

namespace Npi.Loader.Geocoding;

/// <summary>
/// Builds <c>provider_map</c> (CLAUDE.md §7 Stage 5.5 item 10): one point per provider and street address, at the
/// geocoded address or, when there is none, the ZIP centroid (approximate), plus <c>provider_map_specialty</c>
/// (each point's taxonomy codes by 0.1° grid cell, for specialty map searches). Staging + RENAME of both together,
/// so the map page never sees a half-built table. Its build time is kept in <c>reference_data</c> (source <c>provider_map</c>).
/// </summary>
public sealed class MapBuilder(Database database, ILogger log, double minRowRatio)
{
    public const string Source = "provider_map";

    private const string FillSql =
        """
        INSERT INTO `provider_map_staging` (`npi`, `addr_key`, `lat`, `lon`, `approximate`, `source`, `pt`)
        SELECT x.npi, x.addr_key, x.lat, x.lon, x.approximate, x.source, ST_SRID(POINT(x.lon, x.lat), 0)
        FROM (
          -- Best first: Overture (the building), the Census geocoder (interpolated along the street), the ZIP centroid.
          SELECT l.npi, l.addr_key,
                 CAST(COALESCE(o.lat, g.lat, c.lat) AS DOUBLE) AS lat, CAST(COALESCE(o.lon, g.lon, c.lon) AS DOUBLE) AS lon,
                 o.lat IS NULL AND g.lat IS NULL AS approximate,
                 CASE WHEN o.lat IS NOT NULL THEN o.source WHEN g.lat IS NOT NULL THEN 'census' ELSE 'zip' END AS source
          FROM (SELECT DISTINCT `npi`, `addr_key`, `zip5` FROM `provider_location` WHERE `addr_key` IS NOT NULL) l
          LEFT JOIN `address_point` o ON o.`addr_key` = l.`addr_key`
          LEFT JOIN `address_geocode` g ON g.`addr_key` = l.`addr_key` AND g.`status` = 'Match'
          LEFT JOIN `zip_centroid` c ON c.`zip5` = l.`zip5`
        ) x
        WHERE x.lat IS NOT NULL
        """;

    // The cell formula is MapBounds.Cells' (Npi.Core); keep them in step.
    private const string FillSpecialtySql =
        """
        INSERT INTO `provider_map_specialty_staging` (`taxonomy_code`, `cell`, `npi`)
        SELECT DISTINCT t.`taxonomy_code`, FLOOR((m.`lat` + 90) * 10) * 3600 + FLOOR((m.`lon` + 180) * 10), m.`npi`
        FROM `provider_map_staging` m
        JOIN `provider_taxonomy` t ON t.`npi` = m.`npi`
        """;

    /// <summary>True when the projection or the geocodes changed since the last build.</summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT NOT EXISTS (SELECT 1 FROM `reference_data` WHERE `source` = @source)
                OR NOT EXISTS (SELECT 1 FROM `provider_map_specialty`)
                OR (SELECT `loaded_at` FROM `reference_data` WHERE `source` = @source)
                   < GREATEST(COALESCE((SELECT `projected_at` FROM `data_version` WHERE `id` = 1), '1970-01-01'),
                              COALESCE((SELECT MAX(`geocoded_at`) FROM `address_geocode`), '1970-01-01'),
                              COALESCE((SELECT MAX(`matched_at`) FROM `address_point`), '1970-01-01'))
            """, new { source = Source }, cancellationToken: ct)) != 0;
    }

    public async Task<long> BuildAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        await using var connection = await database.OpenAsync(ct);

        // INSERT … SELECT under REPEATABLE READ locks every row it reads, which would block the geocoder and the Overture
        // matcher from saving into address_geocode / address_point for the whole build. READ COMMITTED reads without locks.
        await Database.ExecuteAsync(connection, "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED", ct);

        // Both builds would use provider_map_staging: wait for another process's build of this database to finish first.
        if (await connection.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT GET_LOCK(CONCAT('getnpidata.', DATABASE(), '.provider_map'), 7200)", cancellationToken: ct)) != 1)
        {
            throw new TimeoutException("Another provider_map build is still running after 2 hours.");
        }

        try
        {
            return await BuildLockedAsync(connection, watch, ct);
        }
        finally
        {
            await connection.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT RELEASE_LOCK(CONCAT('getnpidata.', DATABASE(), '.provider_map'))", cancellationToken: CancellationToken.None));
        }
    }

    private async Task<long> BuildLockedAsync(MySqlConnector.MySqlConnection connection, Stopwatch watch, CancellationToken ct)
    {
        var counts = await TableSwap.ReplaceAsync(connection, ["provider_map", "provider_map_specialty", "provider_map_credential"], minRowRatio, async () =>
        {
            await Database.ExecuteAsync(connection, FillSql, ct);
            await Database.ExecuteAsync(connection, FillSpecialtySql, ct);
            await Database.ExecuteAsync(connection,
                Projection.CredentialBuilder.MapCredentialSql("provider_map_credential_staging", "provider_credential", "provider_map_staging"), ct);
        }, ct);
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
        return rows;
    }
}
