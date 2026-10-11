using System.Diagnostics;
using Dapper;
using Npi.Core.Search;
using Npi.Loader.Datasets;
using Npi.Loader.Db;
using Serilog;

namespace Npi.Loader.Projection;

/// <summary>
/// Counts active providers per area and plain specialty for the area pages (docs/PRODUCT_PLAN.md step 1.6; migration 079):
/// the US, each state and each county (by the HUD ZIP crosswalk), in total and per category and subcategory. A provider counts
/// once per area and category however many locations or taxonomies it has there. Rebuilt after each projection.
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

    // New providers: in total, per subcategory, per whole category.
    private static readonly (string TaxonomyJoin, string Category, string Subcategory, string Group)[] NewLevels =
    [
        ("", "''", "''", ""),
        ("JOIN `provider_taxonomy` tx ON tx.`npi` = n.`npi` JOIN `specialty_code_category_staging` m ON m.`code` = tx.`taxonomy_code`",
            "m.`category`", "m.`subcategory`", ", m.`category`, m.`subcategory`"),
        ("JOIN `provider_taxonomy` tx ON tx.`npi` = n.`npi` JOIN `specialty_code_category_staging` m ON m.`code` = tx.`taxonomy_code`",
            "m.`category`", "''", ", m.`category`"),
    ];

    // Per subcategory, then per whole category (subcategory '').
    private static readonly string[] Levels = ["m.`subcategory`", "''"];

    /// <summary>True when the projection was rebuilt since the counts were.</summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
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

            // Of those, the providers whose NPI was issued in the 90 days before the data's as-of date: few, so from the newest NPIs out.
            foreach (var (type, join, id) in Areas)
            {
                foreach (var (taxonomyJoin, category, subcategory, group) in NewLevels)
                {
                    await RunAsync(
                        $"""
                        INSERT INTO `area_provider_count_staging` (`area_type`, `area_id`, `category`, `subcategory`, `providers`, `new_providers`)
                        SELECT * FROM (
                          SELECT '{type}' AS t, {id} AS a, {category} AS c, {subcategory} AS sc, 0 AS p, COUNT(DISTINCT n.`npi`) AS n
                          FROM `provider` n
                          JOIN `provider_location` s ON s.`npi` = n.`npi` {join} {taxonomyJoin}
                          WHERE n.`enumeration_date` >= (SELECT DATE_SUB(COALESCE(MAX(`as_of_date`), CURDATE()), INTERVAL {NewDays} DAY) FROM `data_version`)
                            AND s.`state` IS NOT NULL AND CHAR_LENGTH({id}) <= 5
                          GROUP BY {id}{group}) x
                        ON DUPLICATE KEY UPDATE `new_providers` = x.n
                        """);
                }
            }
        }, ct);

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
        return counts["area_provider_count"];
    }
}
