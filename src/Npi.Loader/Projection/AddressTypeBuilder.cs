using System.Diagnostics;
using Dapper;
using Npi.Loader.Datasets;
using Npi.Loader.Db;
using Serilog;

namespace Npi.Loader.Projection;

/// <summary>
/// Labels every US practice address (CLAUDE.md §7 Stage 5.5 item 18) as commercial, residential, care (nursing home,
/// dormitory) or unknown, from the FEMA building under it (<c>address_building</c>) and signs of a business at the
/// address: a suite or floor in it, an Overture place whose address it is, five or more providers, or three or more
/// with an organization among them. A business sign turns a residential (or unknown) building into commercial: FEMA
/// labels some strip malls and offices in former houses as dwellings. Rebuilt after each FEMA load and projection.
/// </summary>
public sealed class AddressTypeBuilder(Database database, ILogger log, double minRowRatio)
{
    public const string Source = "address_type";

    /// <summary>True when the projection was rebuilt since the labels were (new addresses, providers at an address).</summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        if (await Database.CountAsync(connection, "address_building", ct) == 0)
        {
            return false; // nothing to label until FEMA structures are loaded
        }

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT NOT EXISTS (SELECT 1 FROM `reference_data` WHERE `source` = @source)
                OR (SELECT `loaded_at` FROM `reference_data` WHERE `source` = @source)
                   < COALESCE((SELECT `projected_at` FROM `data_version` WHERE `id` = 1), '1970-01-01')
            """, new { source = Source }, cancellationToken: ct)) != 0;
    }

    public async Task<long> BuildAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        await using var connection = await database.OpenAsync(ct);
        var counts = await TableSwap.ReplaceAsync(connection, ["address_type"], minRowRatio, async () =>
            await Database.ExecuteAsync(connection,
                """
                INSERT INTO `address_type_staging` (`addr_key`, `building_type`, `basis`)
                SELECT x.`addr_key`,
                  CASE
                    WHEN x.`occ_cls` = 'Residential' AND x.`prim_occ` IN ('Nursing Home', 'Institutional Dormitory') THEN 'care'
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
                      MAX(UPPER(CONCAT_WS(' ', l.`address1`, l.`address2`)) REGEXP '\\b(STE|SUITE|FL|FLR|FLOOR|BLDG|BUILDING|RM|ROOM)\\b') AS `suite`,
                      COUNT(DISTINCT l.`npi`) AS `providers`,
                      MAX(p.`entity_type` = 2) AS `org`
                    FROM `provider_location` l JOIN `provider` p ON p.`npi` = l.`npi`
                    WHERE l.`addr_key` IS NOT NULL
                    GROUP BY l.`addr_key`
                  ) s
                  LEFT JOIN `address_building` b ON b.`addr_key` = s.`addr_key`
                  LEFT JOIN `address_point` ap ON ap.`addr_key` = s.`addr_key`
                ) x
                """, ct), ct);

        var byType = (await connection.QueryAsync<(string Type, long Rows)>(new CommandDefinition(
            "SELECT `building_type`, COUNT(*) FROM `address_type` GROUP BY `building_type` ORDER BY 2 DESC", cancellationToken: ct))).ToList();
        var now = DateTime.UtcNow;
        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
            VALUES (@source, @version, '', @rows, @now, @now) AS new
            ON DUPLICATE KEY UPDATE `version` = new.`version`, `rows_loaded` = new.`rows_loaded`, `loaded_at` = new.`loaded_at`, `checked_at` = new.`checked_at`
            """, ct, param: new { source = Source, version = now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), rows = counts["address_type"], now });
        log.Information("Practice address types: {Rows:N0} addresses ({Types}) in {Seconds:N0}s", counts["address_type"],
            string.Join(", ", byType.Select(t => $"{t.Type} {t.Rows:N0}")), watch.Elapsed.TotalSeconds);
        return counts["address_type"];
    }
}
