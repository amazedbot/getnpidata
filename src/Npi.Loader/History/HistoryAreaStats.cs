using Dapper;
using MySqlConnector;
using Npi.Core.Search;
using Npi.Loader.Db;

namespace Npi.Loader.History;

/// <summary>
/// The area figures of one archived month (docs/PRODUCT_PLAN.md step 3.4), from the versions that file started and the ones
/// it closed, written to area_month_stat with basis 3 in one transaction with <c>history_period.stats_done</c>. Counted by
/// primary practice location, in today's county boundaries (the HUD crosswalk; a ZIP counts in every county it overlaps).
/// <list type="bullet">
/// <item>providers (per category too): carried from the month before in history_area_count, each version +1 where it starts
/// and −1 where it ends (active and located: a state of at most 5 characters, as the area counts).</item>
/// <item>new_providers (per category too): NPIs first seen in this file, active, by their enumeration month (so a month
/// without a file still gets its exact count, from the next file), from the archive's first month on.</item>
/// <item>deactivated: active before and deactivated (listed, or no longer in the file) now, where they practised.</item>
/// <item>moved_in / moved_out: an active provider's state or county before and now differ (states and counties).</item>
/// <item>specialty_changes: an active provider's primary taxonomy changed, where it practises now.</item>
/// </list>
/// The months of the recorded series (from area_history_meta.tracked_from) belong to step 3.3's builder: nothing is
/// written there, but the counts carry on. A derived row (basis 1) in a month the archive covers is replaced.
/// </summary>
public static class HistoryAreaStats
{
    private const string Step = "history_step";
    private const string Side = "history_side";

    /// <summary>SQL: a version side (n or o) of history_step counts in an area: a state of at most 5 characters, as the area counts.</summary>
    public static string Located(string side) => $"s.`{side}_state` IS NOT NULL AND CHAR_LENGTH(s.`{side}_state`) <= 5";

    /// <summary>Writes one period's figures. <paramref name="period"/> is its month, <paramref name="dataDate"/> its versions' from_period.</summary>
    /// <returns>The area_month_stat rows written for the month (0 in a recorded month).</returns>
    public static async Task<long> WriteAsync(MySqlConnection connection, DateOnly period, DateOnly dataDate, DateOnly firstMonth, CancellationToken ct)
    {
        var trackedFrom = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT `tracked_from` FROM `area_history_meta` WHERE `id` = 1", cancellationToken: ct));
        var tracked = trackedFrom is { } t ? DateOnly.FromDateTime(t) : DateOnly.MaxValue;
        var write = period < tracked;
        var param = new
        {
            p = dataDate.ToDateTime(TimeOnly.MinValue),
            month = period.ToDateTime(TimeOnly.MinValue),
            first = firstMonth.ToDateTime(TimeOnly.MinValue),
            tracked = (tracked == DateOnly.MaxValue ? new DateOnly(9999, 12, 1) : tracked).ToDateTime(TimeOnly.MinValue),
        };
        Task<int> RunAsync(string sql, MySqlTransaction? tx = null) =>
            connection.ExecuteAsync(new CommandDefinition(sql, param, tx, commandTimeout: 0, cancellationToken: ct));

        await RunAsync($"DROP TABLE IF EXISTS `{Step}`");
        await RunAsync(
            $"""
            CREATE TABLE `{Step}` (
              `npi` INT UNSIGNED NOT NULL, `n_status` TINYINT NOT NULL, `n_state` VARCHAR(40) NULL, `n_zip` CHAR(5) NULL, `n_tax` VARCHAR(10) NULL,
              `n_set` INT UNSIGNED NULL, `n_enum` DATE NULL, `has_o` TINYINT NOT NULL, `o_status` TINYINT NULL, `o_state` VARCHAR(40) NULL,
              `o_zip` CHAR(5) NULL, `o_tax` VARCHAR(10) NULL, `o_set` INT UNSIGNED NULL,
              PRIMARY KEY (`npi`)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
            """);
        await RunAsync(
            $"""
            INSERT INTO `{Step}`
            SELECT n.`npi`, n.`status`, n.`state`, n.`zip5`, n.`primary_taxonomy`, n.`taxonomy_set`, n.`enumeration_date`, o.`npi` IS NOT NULL,
                   o.`status`, o.`state`, o.`zip5`, o.`primary_taxonomy`, o.`taxonomy_set`
            FROM `npi_history` n LEFT JOIN `npi_history` o ON o.`npi` = n.`npi` AND o.`to_period` = n.`from_period`
            WHERE n.`from_period` = DATE(@p)
            """);

        // Where each started (n) and ended (o) active version counts: the US, its state, its counties.
        await RunAsync($"DROP TABLE IF EXISTS `{Side}`");
        await RunAsync(
            $"""
            CREATE TABLE `{Side}` (`side` CHAR(1) NOT NULL, `npi` INT UNSIGNED NOT NULL, `area_type` VARCHAR(6) NOT NULL, `area_id` VARCHAR(5) NOT NULL,
              PRIMARY KEY (`side`, `area_type`, `area_id`, `npi`), KEY (`npi`)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
            """);
        foreach (var side in new[] { "n", "o" })
        {
            var active = side == "n" ? "s.`n_status` = 0" : "s.`has_o` = 1 AND s.`o_status` = 0";
            var located = Located(side);
            await RunAsync($"INSERT IGNORE INTO `{Side}` SELECT '{side}', s.`npi`, 'us', 'US' FROM `{Step}` s WHERE {active} AND {located}");
            await RunAsync($"INSERT IGNORE INTO `{Side}` SELECT '{side}', s.`npi`, 'state', s.`{side}_state` FROM `{Step}` s WHERE {active} AND {located}");
            await RunAsync(
                $"""
                INSERT IGNORE INTO `{Side}` SELECT '{side}', s.`npi`, 'county', z.`county_fips`
                FROM `{Step}` s JOIN `zip_county` z ON z.`zip5` = s.`{side}_zip` WHERE {active} AND {located}
                """);
        }

        await using var tx = await connection.BeginTransactionAsync(ct);

        // The provider counts carried on: +1 where a version starts, −1 where one ends.
        const string upsertCount = "ON DUPLICATE KEY UPDATE `providers` = `history_area_count`.`providers` + x.`delta`";
        await RunAsync(
            $"""
            INSERT INTO `history_area_count` (`area_type`, `area_id`, `category`, `providers`)
            SELECT * FROM (SELECT `area_type`, `area_id`, '' AS c, CAST(SUM(IF(`side` = 'n', 1, -1)) AS SIGNED) AS `delta` FROM `{Side}` GROUP BY `area_type`, `area_id`) x
            {upsertCount}
            """, tx);
        await RunAsync(
            $"""
            INSERT INTO `history_area_count` (`area_type`, `area_id`, `category`, `providers`)
            SELECT * FROM (
              SELECT s.`area_type`, s.`area_id`, c.`category`, CAST(SUM(IF(s.`side` = 'n', 1, -1)) AS SIGNED) AS `delta`
              FROM `{Side}` s JOIN `{Step}` t ON t.`npi` = s.`npi`
              JOIN `npi_history_set_category` c ON c.`set_id` = IF(s.`side` = 'n', t.`n_set`, t.`o_set`)
              GROUP BY s.`area_type`, s.`area_id`, c.`category`) x
            {upsertCount}
            """, tx);

        long rows = 0;
        if (write)
        {
            // This month and earlier ones are the archive's: derived rows there go (the recorded months never reach here).
            await RunAsync("DELETE FROM `area_month_stat` WHERE `month` = DATE(@month) OR (`basis` <> 3 AND `month` <= DATE(@month) AND `month` < DATE(@tracked))", tx);
            const string keys = "`area_type`, `area_id`, `category`, `subcategory`, `month`, `basis`";
            // The month's counts: every provider (the US, every state and county, 0 when none) and per category; the change counts start at 0.
            rows += await RunAsync(
                $"""
                INSERT INTO `area_month_stat` ({keys}, `providers`, `deactivated`, `moved_in`, `moved_out`, `specialty_changes`)
                SELECT `area_type`, `area_id`, `category`, '', DATE(@month), 3, `providers`,
                       IF(`category` = '', 0, NULL), IF(`category` = '' AND `area_type` <> 'us', 0, NULL), IF(`category` = '' AND `area_type` <> 'us', 0, NULL),
                       IF(`category` = '', 0, NULL)
                FROM `history_area_count` WHERE `category` = '' OR `providers` <> 0
                """, tx);
            rows += await RunAsync(
                $"""
                INSERT IGNORE INTO `area_month_stat` ({keys}, `providers`, `deactivated`, `moved_in`, `moved_out`, `specialty_changes`)
                SELECT 'us', 'US', '', '', DATE(@month), 3, 0, 0, NULL, NULL, 0
                UNION ALL SELECT DISTINCT 'state', `state`, '', '', DATE(@month), 3, 0, 0, 0, 0, 0 FROM `county`
                UNION ALL SELECT 'county', `county_fips`, '', '', DATE(@month), 3, 0, 0, 0, 0, 0 FROM `county`
                """, tx);

            string Update(string column, string select) =>
                $"""
                INSERT INTO `area_month_stat` ({keys}, `{column}`)
                SELECT * FROM (SELECT x.t, x.i, '' AS c, '' AS s, DATE(@month) AS m, 3 AS b, x.n FROM ({select}) x) y
                ON DUPLICATE KEY UPDATE `{column}` = y.n
                """;
            // Deactivated: where the provider practised before.
            await RunAsync(Update("deactivated",
                $"""
                SELECT o.`area_type` AS t, o.`area_id` AS i, COUNT(*) AS n FROM `{Side}` o JOIN `{Step}` s ON s.`npi` = o.`npi`
                WHERE o.`side` = 'o' AND s.`n_status` <> 0 GROUP BY o.`area_type`, o.`area_id`
                """), tx);
            // Moves: an area of the active provider before (or now) that isn't one now (or before).
            foreach (var (column, from, to) in new[] { ("moved_in", "n", "o"), ("moved_out", "o", "n") })
            {
                await RunAsync(Update(column,
                    $"""
                    SELECT a.`area_type` AS t, a.`area_id` AS i, COUNT(*) AS n FROM `{Side}` a JOIN `{Step}` s ON s.`npi` = a.`npi`
                    WHERE a.`side` = '{from}' AND a.`area_type` <> 'us' AND s.`has_o` = 1 AND s.`o_status` = 0 AND s.`n_status` = 0
                      AND NOT EXISTS (SELECT 1 FROM `{Side}` b WHERE b.`side` = '{to}' AND b.`area_type` = a.`area_type` AND b.`area_id` = a.`area_id` AND b.`npi` = a.`npi`)
                    GROUP BY a.`area_type`, a.`area_id`
                    """), tx);
            }

            await RunAsync(Update("specialty_changes",
                $"""
                SELECT a.`area_type` AS t, a.`area_id` AS i, COUNT(*) AS n FROM `{Side}` a JOIN `{Step}` s ON s.`npi` = a.`npi`
                WHERE a.`side` = 'n' AND s.`has_o` = 1 AND s.`o_status` = 0 AND NOT (s.`n_tax` <=> s.`o_tax`)
                GROUP BY a.`area_type`, a.`area_id`
                """), tx);
        }

        // NPIs first seen here, by their enumeration month (earlier months too: a month without a file gets its count from the next).
        string NewSql(string category, string join, string group) =>
            $"""
            SELECT a.`area_type` AS t, a.`area_id` AS i, {category} AS c, CAST(DATE_FORMAT(s.`n_enum`, '%Y-%m-01') AS DATE) AS m, COUNT(*) AS n
            FROM `{Side}` a JOIN `{Step}` s ON s.`npi` = a.`npi` {join}
            WHERE a.`side` = 'n' AND s.`has_o` = 0 AND s.`n_enum` >= DATE(@first) AND s.`n_enum` < DATE(@month) + INTERVAL 1 MONTH AND s.`n_enum` < DATE(@tracked)
            GROUP BY a.`area_type`, a.`area_id`, {group}m
            """;
        foreach (var (category, join, group) in new[] { ("''", "", ""), ("c.`category`", "JOIN `npi_history_set_category` c ON c.`set_id` = s.`n_set`", "c.`category`, ") })
        {
            await RunAsync(
                $"""
                INSERT INTO `area_month_stat` (`area_type`, `area_id`, `category`, `subcategory`, `month`, `basis`, `new_providers`)
                SELECT * FROM (SELECT x.t, x.i, x.c, '' AS s, x.m, 3 AS b, x.n FROM ({NewSql(category, join, group)}) x) y
                ON DUPLICATE KEY UPDATE `new_providers` = COALESCE(`area_month_stat`.`new_providers`, 0) + y.n
                """, tx);
        }

        if (write)
        {
            await RunAsync(
                """
                INSERT INTO `area_history_meta` (`id`, `archive_from`, `archive_to`) VALUES (1, DATE(@first), DATE(@month)) AS new
                ON DUPLICATE KEY UPDATE `archive_from` = LEAST(COALESCE(`area_history_meta`.`archive_from`, new.`archive_from`), new.`archive_from`),
                  `archive_to` = GREATEST(COALESCE(`area_history_meta`.`archive_to`, new.`archive_to`), new.`archive_to`)
                """, tx);
        }

        await RunAsync("UPDATE `history_period` SET `stats_done` = 1 WHERE `period` = DATE(@month)", tx);
        await tx.CommitAsync(ct);
        foreach (var table in new[] { Step, Side })
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{table}`", CancellationToken.None);
        }

        return rows;
    }

    /// <summary>The plain specialty categories a set of taxonomy codes belongs to (codes not in the list: Other).</summary>
    public static IReadOnlyList<string> CategoriesOf(string codes)
    {
        var categories = SpecialtyCategories.Default;
        return codes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(code => categories.Categories.Where(c => categories.Contains(c, null, code)).Select(c => c.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
