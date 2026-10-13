using System.Diagnostics;
using Dapper;
using MySqlConnector;
using Npi.Core.Search;
using Npi.Loader.Datasets;
using Npi.Loader.Db;
using Serilog;

namespace Npi.Loader.Projection;

/// <summary>
/// Counts active providers per area and plain specialty for the area pages (docs/PRODUCT_PLAN.md step 1.6; migration 079):
/// the US, each state and each county (by the HUD ZIP crosswalk), in total and per category and subcategory. A provider counts
/// once per area and category however many locations or taxonomies it has there. Rebuilt after each full projection and NUCC
/// or HUD reload; after an incremental projection each queued NPI's counts before and after are compared and only the
/// difference is applied (and the new providers recounted, as the as-of date moves).
/// </summary>
public sealed class AreaCountBuilder(Database database, ILogger log, double minRowRatio)
{
    public const string Source = "area_counts";

    // The areas a provider_search row (s) lies in: (area_type, area_id) per row; a county by its ZIP.
    private static readonly (string Type, string Join, string Id)[] Areas =
    [
        ("us", "", "'US'"),
        ("state", "", "s.`state`"),
        ("county", "JOIN `zip_county` z ON z.`zip5` = s.`zip5`", "z.`county_fips`"),
    ];

    /// <summary>"New" providers: NPIs issued in this many days before the data's as-of date.</summary>
    public const int NewDays = 90;

    // Reloads that change what every count means: the codes' categories (NUCC) and the ZIPs' counties (HUD).
    private static readonly string[] Inputs = ["nucc", "hud_zip_county"];

    // Per subcategory, then per whole category (subcategory '').
    private static readonly string[] Levels = ["m.`subcategory`", "''"];

    /// <summary>True when the projection was rebuilt in full (or NUCC or HUD reloaded) since the counts were, or NPIs are queued.</summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await NeedsFullBuildAsync(connection, ct)
               || await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                   "SELECT EXISTS (SELECT 1 FROM `builder_pending` WHERE `builder` = @builder)", new { builder = BuilderPending.Areas }, cancellationToken: ct)) != 0;
    }

    private static async Task<bool> NeedsFullBuildAsync(MySqlConnection connection, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"""
            SELECT NOT EXISTS (SELECT 1 FROM `reference_data` WHERE `source` = @source)
                OR (SELECT `loaded_at` FROM `reference_data` WHERE `source` = @source)
                   < GREATEST({BuilderPending.FullProjectedAt},
                              COALESCE((SELECT MAX(`loaded_at`) FROM `reference_data` WHERE `source` IN @inputs), '1970-01-01'))
            """, new { source = Source, inputs = Inputs }, cancellationToken: ct)) != 0;

    /// <param name="full">Recount everything (the <c>areas</c> command); otherwise only the queued NPIs' difference when that is enough.</param>
    public async Task<long> BuildAsync(CancellationToken ct, bool full = false)
    {
        var watch = Stopwatch.StartNew();
        await using var connection = await database.OpenAsync(ct);
        if (!full && !await NeedsFullBuildAsync(connection, ct))
        {
            try
            {
                if (await BuildQueuedAsync(connection, watch, ct) is { } rows)
                {
                    return rows;
                }
            }
            finally
            {
                await BuilderPending.DropBatchAsync(connection, BuilderPending.Areas);
                foreach (var table in Scratch)
                {
                    await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{table}`", CancellationToken.None);
                }
            }
        }

        var codes = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT `taxonomy_code` FROM `provider_taxonomy`", cancellationToken: ct))).ToList();
        var categories = SpecialtyCategories.Default;
        var mapping = new List<(string Code, string Category, string Subcategory)>();
        foreach (var code in codes)
        {
            foreach (var category in categories.Categories)
            {
                foreach (var sub in category.Subcategories.Where(s => categories.Contains(category, s, code)))
                {
                    mapping.Add((code, category.Name, sub.Name));
                }
            }
        }

        // The grouping queries read all of provider_search (~13M rows) once per area level: no command timeout.
        Task RunAsync(string sql) => connection.ExecuteAsync(new CommandDefinition(sql, commandTimeout: 0, cancellationToken: ct));

        var counts = await TableSwap.ReplaceAsync(connection, ["specialty_code_category", "area_provider_count"], minRowRatio, async () =>
        {
            foreach (var chunk in mapping.Chunk(500))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO `specialty_code_category_staging` (`code`, `category`, `subcategory`) VALUES (@Code, @Category, @Subcategory)",
                    chunk.Select(m => new { m.Code, m.Category, m.Subcategory }), cancellationToken: ct));
            }

            foreach (var (type, join, id) in Areas)
            {
                // Every provider: from the locations (a provider with no taxonomy row still counts).
                await RunAsync(
                    $"""
                    INSERT INTO `area_provider_count_staging` (`area_type`, `area_id`, `category`, `subcategory`, `providers`)
                    SELECT '{type}', {id}, '', '', COUNT(DISTINCT s.`npi`)
                    FROM `provider_location` s {join}
                    WHERE s.`state` IS NOT NULL AND CHAR_LENGTH({id}) <= 5
                    GROUP BY {id}
                    """);
                foreach (var level in Levels)
                {
                    await RunAsync(
                        $"""
                        INSERT INTO `area_provider_count_staging` (`area_type`, `area_id`, `category`, `subcategory`, `providers`)
                        SELECT '{type}', {id}, m.`category`, {level}, COUNT(DISTINCT s.`npi`)
                        FROM `provider_search` s {join}
                        JOIN `specialty_code_category_staging` m ON m.`code` = s.`taxonomy_code`
                        WHERE s.`state` <> '' AND CHAR_LENGTH({id}) <= 5
                        GROUP BY {id}, m.`category`{(level == "''" ? "" : ", m.`subcategory`")}
                        """);
                }
            }

            await PrepareNewAsync(connection, "specialty_code_category_staging", ct);
            await CountNewAsync(connection, "area_provider_count_staging", null, ct);
        }, ct);
        await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{NewRows}`", ct);

        var now = DateTime.UtcNow;
        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
            VALUES (@source, @version, '', @rows, @now, @now) AS new
            ON DUPLICATE KEY UPDATE `version` = new.`version`, `rows_loaded` = new.`rows_loaded`, `loaded_at` = new.`loaded_at`, `checked_at` = new.`checked_at`
            """, ct, param: new { source = Source, version = now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                rows = counts["area_provider_count"], now });
        log.Information("Area counts: {Rows:N0} rows ({Codes:N0} taxonomy codes in {Mapped:N0} category rows) in {Seconds:N0}s",
            counts["area_provider_count"], codes.Count, mapping.Count, watch.Elapsed.TotalSeconds);
        await BuilderPending.ClearAsync(connection, BuilderPending.Areas, ct);
        return counts["area_provider_count"];
    }

    // Of the providers counted, those whose NPI was issued in the 90 days before the data's as-of date (~180,000): their area
    // rows are worked out once into a scratch table (outside any transaction: it is DDL), then counted per area and level.
    // Joining them to their locations, counties and categories once per level took ~8 min; once in all, about a minute.
    private const string NewRows = "areas_new_providers";

    private static async Task PrepareNewAsync(MySqlConnection connection, string categories, CancellationToken ct)
    {
        await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{NewRows}`", ct);
        await Database.ExecuteAsync(connection, ScratchTableSql(NewRows), ct);
        static string Recent(string alias) =>
            $"JOIN `provider` n ON n.`npi` = {alias}.`npi` WHERE n.`enumeration_date` >= (SELECT DATE_SUB(COALESCE(MAX(`as_of_date`), CURDATE()), INTERVAL {NewDays} DAY) FROM `data_version`)";
        foreach (var sql in ContributionSql(NewRows,
                     $"(SELECT l.`npi`, l.`state`, l.`zip5` FROM `provider_location` l {Recent("l")})",
                     $"(SELECT t.`npi`, t.`taxonomy_code` FROM `provider_taxonomy` t {Recent("t")})",
                     categories))
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, commandTimeout: 0, cancellationToken: ct));
        }
    }

    private static Task<int> CountNewAsync(MySqlConnection connection, string target, MySqlTransaction? tx, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO `{target}` (`area_type`, `area_id`, `category`, `subcategory`, `providers`, `new_providers`)
            SELECT * FROM (
              SELECT `area_type`, `area_id`, `category`, `subcategory`, 0 AS p, COUNT(*) AS n FROM `{NewRows}`
              GROUP BY `area_type`, `area_id`, `category`, `subcategory`) x
            ON DUPLICATE KEY UPDATE `new_providers` = x.n
            """, transaction: tx, commandTimeout: 0, cancellationToken: ct));

    private static string ScratchTableSql(string table) =>
        $"""
        CREATE TABLE `{table}` (
          `area_type` VARCHAR(10) NOT NULL, `area_id` VARCHAR(40) NOT NULL, `category` VARCHAR(80) NOT NULL, `subcategory` VARCHAR(80) NOT NULL,
          `npi` CHAR(10) NOT NULL, PRIMARY KEY (`area_type`, `area_id`, `category`, `subcategory`, `npi`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
        """;

    private const string Old = "areas_old";
    private const string New = "areas_new";
    private static readonly string[] Scratch = [Old, New, NewRows];

    // Every (area, category, subcategory) a provider counts in, from its locations l (npi, state, zip5) and taxonomy codes t
    // (npi, taxonomy_code), as the full count does: in total from the locations, per category from location × taxonomy.
    private static IEnumerable<string> ContributionSql(string target, string locations, string taxonomies, string categories = "specialty_code_category")
    {
        foreach (var (type, join, id) in Areas)
        {
            yield return $"""
                INSERT IGNORE INTO `{target}` (`area_type`, `area_id`, `category`, `subcategory`, `npi`)
                SELECT '{type}', {id}, '', '', s.`npi` FROM {locations} s {join}
                WHERE s.`state` IS NOT NULL AND CHAR_LENGTH({id}) <= 5
                """;
            foreach (var level in Levels)
            {
                yield return $"""
                    INSERT IGNORE INTO `{target}` (`area_type`, `area_id`, `category`, `subcategory`, `npi`)
                    SELECT '{type}', {id}, m.`category`, {level}, s.`npi`
                    FROM (SELECT l.`npi`, l.`state`, l.`zip5`, t.`taxonomy_code` FROM {locations} l JOIN {taxonomies} t ON t.`npi` = l.`npi`) s {join}
                    JOIN `{categories}` m ON m.`code` = s.`taxonomy_code`
                    WHERE s.`state` IS NOT NULL AND CHAR_LENGTH({id}) <= 5
                    """;
            }
        }
    }

    /// <summary>
    /// Applies the queued NPIs' difference: their contributions as the counts last saw them (the projection's snapshot) are
    /// taken off, their current ones added; then the new providers are recounted. Null (nothing changed) when a queued NPI
    /// has a taxonomy code the code → category table doesn't have yet, which only a full count adds.
    /// </summary>
    private async Task<long?> BuildQueuedAsync(MySqlConnection connection, Stopwatch watch, CancellationToken ct)
    {
        var (batch, queued) = await BuilderPending.TakeAsync(connection, BuilderPending.Areas, ct);
        var missing = await connection.QueryAsync<string>(new CommandDefinition(
            $"""
            SELECT DISTINCT t.`taxonomy_code` FROM `provider_taxonomy` t JOIN `{batch}` b ON b.`npi` = t.`npi`
            WHERE NOT EXISTS (SELECT 1 FROM `specialty_code_category` m WHERE m.`code` = t.`taxonomy_code`)
            """, cancellationToken: ct));
        var categories = SpecialtyCategories.Default;
        if (missing.Any(code => categories.Categories.Any(c => c.Subcategories.Any(sub => categories.Contains(c, sub, code)))))
        {
            log.Information("Area counts: a queued provider has a taxonomy code not counted yet; counting everything");
            return null;
        }

        Task<int> RunAsync(string sql, MySqlTransaction? tx = null) =>
            connection.ExecuteAsync(new CommandDefinition(sql, transaction: tx, commandTimeout: 0, cancellationToken: ct));
        foreach (var table in new[] { Old, New })
        {
            await RunAsync($"DROP TABLE IF EXISTS `{table}`");
            await RunAsync(ScratchTableSql(table));
        }

        foreach (var sql in ContributionSql(Old, BuilderPending.OldLocations(batch), BuilderPending.OldTaxonomies(batch)))
        {
            await RunAsync(sql);
        }

        foreach (var sql in ContributionSql(New,
                     $"(SELECT l.`npi`, l.`state`, l.`zip5` FROM `provider_location` l JOIN `{batch}` b ON b.`npi` = l.`npi`)",
                     $"(SELECT t.`npi`, t.`taxonomy_code` FROM `provider_taxonomy` t JOIN `{batch}` b ON b.`npi` = t.`npi`)"))
        {
            await RunAsync(sql);
        }

        await PrepareNewAsync(connection, "specialty_code_category", ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var changed = await RunAsync(
            $"""
            INSERT INTO `area_provider_count` (`area_type`, `area_id`, `category`, `subcategory`, `providers`)
            SELECT * FROM (
              SELECT `area_type`, `area_id`, `category`, `subcategory`, SUM(`d`) AS `delta`
              FROM (SELECT `area_type`, `area_id`, `category`, `subcategory`, 1 AS `d` FROM `{New}`
                    UNION ALL SELECT `area_type`, `area_id`, `category`, `subcategory`, -1 FROM `{Old}`) u
              GROUP BY `area_type`, `area_id`, `category`, `subcategory`
              HAVING `delta` <> 0) x
            ON DUPLICATE KEY UPDATE `providers` = `providers` + x.`delta`
            """, tx);
        await RunAsync("DELETE FROM `area_provider_count` WHERE `providers` <= 0", tx);
        // Codes nobody has any more leave the code → category table, as a full count would.
        await RunAsync(
            $"""
            DELETE m FROM `specialty_code_category` m
            WHERE m.`code` IN (SELECT `taxonomy_code` FROM `builder_old_taxonomy` o JOIN `{batch}` b ON b.`npi` = o.`npi`)
              AND NOT EXISTS (SELECT 1 FROM `provider_taxonomy` t WHERE t.`taxonomy_code` = m.`code`)
            """, tx);
        await RunAsync("UPDATE `area_provider_count` SET `new_providers` = 0 WHERE `new_providers` <> 0", tx);
        await CountNewAsync(connection, "area_provider_count", tx, ct);
        await BuilderPending.DoneAsync(connection, BuilderPending.Areas, tx, ct);
        await connection.ExecuteAsync(new CommandDefinition("UPDATE `reference_data` SET `checked_at` = UTC_TIMESTAMP() WHERE `source` = @source",
            new { source = Source }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        log.Information("Area counts: {Queued:N0} queued providers, {Changed:N0} counts changed, in {Seconds:N0}s", queued, changed, watch.Elapsed.TotalSeconds);
        return changed;
    }
}
