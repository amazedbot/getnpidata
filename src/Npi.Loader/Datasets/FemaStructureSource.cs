using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using DuckDB.NET.Data;
using MySqlConnector;
using Npi.Loader.Db;
using Npi.Loader.Projection;
using Serilog;

namespace Npi.Loader.Datasets;

/// <summary>
/// FEMA USA Structures (Oak Ridge National Laboratory; CLAUDE.md §7 Stage 5.5 item 18): every US building over 450 sq ft
/// with its occupancy class and type. The GeoParquet copy on Source Cooperative (~10 files, ~20 GB) is downloaded one
/// file at a time; DuckDB finds the building under each mapped practice address (or the nearest within about 30 m)
/// → <c>address_building</c>, with the point each address was matched from; then <see cref="AddressTypeBuilder"/> labels
/// the addresses. Addresses mapped only to a ZIP centroid aren't looked up. A new release reloads everything; between
/// releases <see cref="MatchNewAsync"/> looks up only the addresses that are new or whose point moved, reading the files in
/// place (docs/PRODUCT_PLAN.md §7 speed item 4).
/// </summary>
public sealed partial class FemaStructureSource : DatasetSource
{
    /// <summary>The <c>reference_data</c> row of the matches: <c>loaded_at</c> the last full match, <c>checked_at</c> the last match of any kind.</summary>
    public const string MatchSource = "address_building";

    /// <summary>More new or moved addresses than this (a full Overture pass) are matched by a full load instead.</summary>
    public const int IncrementalLimit = 200_000;

    /// <summary>"Near" = within this many degrees of the point (≈ 33 m north–south, ≈ 25 m east–west at 40° N).</summary>
    private const double NearDegrees = 0.0003;

    private const double MetresPerDegree = 111_320;

    private const string Points = "fema_points";
    private const string Changed = "fema_changed";
    private const string Gone = "fema_gone";
    private const string NewRows = "fema_new_rows";

    public override string Name => "fema_structures";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    private sealed record Part(string Key, string ETag, DateTime Modified);

    private static async Task<List<Part>> ListAsync(HttpClient http, LoaderOptions options, CancellationToken ct)
    {
        var xml = await http.GetStringAsync(new Uri(options.FemaStructuresListingUrl), ct);
        var parts = Contents().Matches(xml)
            .Select(m => new Part(m.Groups["key"].Value, m.Groups["etag"].Value.Replace("&quot;", "", StringComparison.Ordinal).Trim('"'),
                DateTime.Parse(m.Groups["modified"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)))
            .Where(p => p.Key.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToList();
        return parts.Count > 0 ? parts : throw new InvalidDataException($"No .parquet files listed at {options.FemaStructuresListingUrl}.");
    }

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var parts = await ListAsync(context.Http, context.Options, ct);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts.Select(p => p.Key + " " + p.ETag)))))[..12];
        return new DatasetRelease(
            string.Create(CultureInfo.InvariantCulture, $"USA Structures {parts.Max(p => p.Modified):yyyy-MM-dd} {hash}"),
            new Uri(context.Options.FemaStructuresBaseUrl + parts[0].Key));
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var parts = await ListAsync(context.Http, context.Options, ct);
        var folder = Path.Combine(context.Options.ResolvedWorkFolder, "datasets", "fema");
        Directory.CreateDirectory(folder);
        var points = Path.Combine(folder, "points.csv");
        var results = Path.Combine(folder, "buildings.csv");
        var duckFile = Path.Combine(folder, "match.duckdb");
        try
        {
            await using var connection = await context.Database.OpenAsync(ct);
            await PointsTableAsync(connection, ct);
            var pointCount = await WritePointsAsync(connection, Points, points, ct);
            context.Log.Information("FEMA structures: {Points:N0} mapped practice addresses to place on buildings", pointCount);

            File.Delete(duckFile);
            using (var duck = new DuckDBConnection($"DataSource={duckFile}"))
            {
                duck.Open();
                Execute(duck, $"INSTALL spatial; LOAD spatial; SET memory_limit = '3GB'; SET temp_directory = '{Sql(Path.Combine(folder, "tmp"))}';");
                CreatePoints(duck, points);
                Execute(duck, "CREATE TABLE inside (addr_key VARCHAR, occ_cls VARCHAR, prim_occ VARCHAR, sqft DOUBLE)");
                Execute(duck, "CREATE TABLE near (addr_key VARCHAR, d DOUBLE, occ_cls VARCHAR, prim_occ VARCHAR, sqft DOUBLE)");
                var downloader = context.Downloader();
                foreach (var part in parts)
                {
                    var file = await downloader.DownloadAsync(new Uri(context.Options.FemaStructuresBaseUrl + part.Key), Path.GetFileName(part.Key), folder, ct);
                    try
                    {
                        var started = watch.Elapsed;
                        MatchPart(duck, file);
                        context.Log.Information("FEMA structures: {File} matched in {Seconds:N0}s", Path.GetFileName(part.Key), (watch.Elapsed - started).TotalSeconds);
                    }
                    finally
                    {
                        File.Delete(file);
                    }
                }

                WriteResults(duck, results);
            }

            var counts = await TableSwap.ReplaceAsync(connection, ["address_building"], context.Options.MinRowRatio,
                () => LoadResultsAsync(connection, results, "address_building_staging", ct), ct);
            var byMatch = (await connection.QueryAsync<(string Matched, long Rows)>(new CommandDefinition(
                "SELECT `matched`, COUNT(*) FROM `address_building` GROUP BY `matched` ORDER BY `matched`", cancellationToken: ct))).ToList();
            context.Log.Information("FEMA structures: {Rows:N0} addresses ({Matches}) in {Minutes:N1} min", counts["address_building"],
                string.Join(", ", byMatch.Select(m => $"{m.Matched} {m.Rows:N0}")), watch.Elapsed.TotalMinutes);

            await RecordMatchAsync(connection, counts["address_building"], full: true, ct);
            await new AddressTypeBuilder(context.Database, context.Log, context.Options.MinRowRatio).BuildAsync(ct, full: true);
            return counts["address_building"];
        }
        finally
        {
            await DropScratchAsync(context.Database);
            Cleanup(folder, points, results, duckFile);
        }
    }

    /// <summary>
    /// After the map changed: looks up the buildings of the practice addresses mapped since the last match or whose point
    /// moved, reading only the columns the match needs from the FEMA files in place (no download), drops the rows of
    /// addresses no longer mapped, and relabels those addresses (<see cref="AddressTypeBuilder"/>, incrementally). Nothing
    /// happens before the first full load, or when the map hasn't changed since the last match.
    /// </summary>
    /// <returns>The number of addresses looked up.</returns>
    public static async Task<long> MatchNewAsync(LoaderOptions options, Database database, HttpClient http, ILogger log, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        var due = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM `address_building`)
               AND IFNULL((SELECT `checked_at` FROM `reference_data` WHERE `source` = @match), '1970-01-01')
                   < IFNULL((SELECT `checked_at` FROM `reference_data` WHERE `source` = 'provider_map'), '1970-01-01')
            """, new { match = MatchSource }, cancellationToken: ct)) != 0;
        if (!due)
        {
            return 0;
        }

        var watch = Stopwatch.StartNew();
        var folder = Path.Combine(options.ResolvedWorkFolder, "datasets", "fema");
        Directory.CreateDirectory(folder);
        var points = Path.Combine(folder, "new-points.csv");
        var results = Path.Combine(folder, "new-buildings.csv");
        var duckFile = Path.Combine(folder, "new-match.duckdb");
        try
        {
            await PointsTableAsync(connection, ct);
            await RunAsync(connection,
                $"""
                CREATE TABLE `{Changed}` (PRIMARY KEY (`addr_key`)) ENGINE=InnoDB
                SELECT p.`addr_key`, p.`lat`, p.`lon` FROM `{Points}` p LEFT JOIN `address_building` b ON b.`addr_key` = p.`addr_key`
                WHERE b.`addr_key` IS NULL OR NOT (b.`lat` <=> p.`lat` AND b.`lon` <=> p.`lon`)
                """, ct);
            await RunAsync(connection,
                $"""
                CREATE TABLE `{Gone}` (PRIMARY KEY (`addr_key`)) ENGINE=InnoDB
                SELECT b.`addr_key` FROM `address_building` b LEFT JOIN `{Points}` p ON p.`addr_key` = b.`addr_key` WHERE p.`addr_key` IS NULL
                """, ct);
            var changed = await Database.CountAsync(connection, Changed, ct);
            var gone = await Database.CountAsync(connection, Gone, ct);
            if (changed > IncrementalLimit)
            {
                log.Information("FEMA structures: {Changed:N0} new or moved addresses, more than {Limit:N0}: matching every address", changed, IncrementalLimit);
                var context = new DatasetContext(options, database, http, log);
                var source = new FemaStructureSource();
                return await source.LoadAsync(context, await source.FindLatestAsync(context, ct), ct);
            }

            if (changed > 0)
            {
                await WritePointsAsync(connection, Changed, points, ct);
                var baseUrl = options.FemaStructuresReadBaseUrl ?? options.FemaStructuresBaseUrl;
                var files = string.Join(", ", (await ListAsync(http, options, ct)).Select(p => $"'{Sql(baseUrl + p.Key)}'"));
                File.Delete(duckFile);
                using (var duck = new DuckDBConnection($"DataSource={duckFile}"))
                {
                    duck.Open();
                    Execute(duck, $"INSTALL httpfs; LOAD httpfs; INSTALL spatial; LOAD spatial; SET memory_limit = '3GB'; SET temp_directory = '{Sql(Path.Combine(folder, "tmp"))}';");
                    CreatePoints(duck, points);
                    // One pass over every file (only the columns named are read): the buildings within reach of each point,
                    // then, as in a full load, inside or else the nearest among them.
                    Execute(duck, string.Create(CultureInfo.InvariantCulture, $"""
                        CREATE TABLE cand AS
                        SELECT p.addr_key, ST_Contains(b.geometry, p.g) AS is_inside, ST_Distance(b.geometry, p.g) AS d,
                               b.OCC_CLS AS occ_cls, b.PRIM_OCC AS prim_occ, b.SQFEET AS sqft
                        FROM (SELECT addr_key, g, ST_Buffer(g, {NearDegrees}) AS reach FROM pts) p
                        JOIN (SELECT geometry, OCC_CLS, PRIM_OCC, SQFEET FROM read_parquet([{files}])) b ON ST_Intersects(b.geometry, p.reach)
                        """));
                    Execute(duck, "CREATE TABLE inside AS SELECT addr_key, occ_cls, prim_occ, sqft FROM cand WHERE is_inside");
                    Execute(duck, "CREATE TABLE near AS SELECT addr_key, d, occ_cls, prim_occ, sqft FROM cand WHERE NOT is_inside");
                    WriteResults(duck, results);
                }

                await RunAsync(connection, $"DROP TABLE IF EXISTS `{NewRows}`", ct);
                await RunAsync(connection, $"CREATE TABLE `{NewRows}` LIKE `address_building`", ct);
                await LoadResultsAsync(connection, results, NewRows, ct);
            }

            await using (var tx = await connection.BeginTransactionAsync(ct))
            {
                await RunAsync(connection, $"DELETE b FROM `address_building` b JOIN `{Changed}` c ON c.`addr_key` = b.`addr_key`", ct, tx);
                await RunAsync(connection, $"DELETE b FROM `address_building` b JOIN `{Gone}` g ON g.`addr_key` = b.`addr_key`", ct, tx);
                if (changed > 0)
                {
                    await RunAsync(connection, $"INSERT INTO `address_building` SELECT * FROM `{NewRows}`", ct, tx);
                }

                // The addresses whose building changed or went, and the providers there, for the address type labels.
                const string touched = $"(SELECT `addr_key` FROM `{Changed}` UNION ALL SELECT `addr_key` FROM `{Gone}`)";
                await RunAsync(connection,
                    $"""
                    INSERT IGNORE INTO `address_pending` (`addr_key`, `zip5`)
                    SELECT l.`addr_key`, MIN(l.`zip5`) FROM `provider_location` l JOIN {touched} t ON t.`addr_key` = l.`addr_key`
                    WHERE l.`zip5` IS NOT NULL GROUP BY l.`addr_key`
                    """, ct, tx);
                await connection.ExecuteAsync(new CommandDefinition(
                    $"""
                    INSERT IGNORE INTO `builder_pending` (`builder`, `npi`)
                    SELECT DISTINCT @builder, l.`npi` FROM `provider_location` l JOIN {touched} t ON t.`addr_key` = l.`addr_key`
                    WHERE l.`zip5` IS NOT NULL
                    """, new { builder = BuilderPending.AddressTypes }, tx, commandTimeout: 0, cancellationToken: ct));
                await RecordMatchAsync(connection, await Database.CountAsync(connection, "address_building", ct, tx), full: false, ct, tx);
                await tx.CommitAsync(ct);
            }

            log.Information("FEMA structures: {Changed:N0} new or moved addresses looked up in the files in place, {Gone:N0} no longer mapped, in {Seconds:N0}s",
                changed, gone, watch.Elapsed.TotalSeconds);
            if (changed + gone > 0)
            {
                await new AddressTypeBuilder(database, log, options.MinRowRatio).BuildAsync(ct);
            }

            return changed;
        }
        finally
        {
            await DropScratchAsync(database);
            Cleanup(folder, points, results, duckFile);
        }
    }

    /// <summary>Every practice address placed on the map at a building or street (not a ZIP centroid), once, with its point.</summary>
    private static async Task PointsTableAsync(MySqlConnection connection, CancellationToken ct)
    {
        await RunAsync(connection, $"DROP TABLE IF EXISTS `{Points}`", ct);
        await RunAsync(connection,
            $"""
            CREATE TABLE `{Points}` (PRIMARY KEY (`addr_key`)) ENGINE=InnoDB
            SELECT `addr_key`, MIN(`lat`) AS `lat`, MIN(`lon`) AS `lon` FROM `provider_map` WHERE `source` <> 'zip' GROUP BY `addr_key`
            """, ct);
    }

    private static async Task<long> WritePointsAsync(MySqlConnection connection, string table, string path, CancellationToken ct)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16);
        await writer.WriteLineAsync("addr_key,lat,lon");
        await using var command = new MySqlCommand($"SELECT `addr_key`, `lat`, `lon` FROM `{table}`", connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        long count = 0;
        while (await reader.ReadAsync(ct))
        {
            await writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{reader.GetString(0)},{reader.GetDouble(1)},{reader.GetDouble(2)}"));
            count++;
        }

        return count;
    }

    /// <summary>Loads the match results into <paramref name="table"/> with the points they were matched from.</summary>
    private static async Task LoadResultsAsync(MySqlConnection connection, string results, string table, CancellationToken ct)
    {
        await CsvTableLoader.LoadAsync(connection, results, table,
        [
            new("addr_key", "addr_key"), new("matched", "matched"), new("distance_m", "distance_m", CsvValue.OptionalWholeNumber),
            new("occ_cls", "occ_cls"), new("prim_occ", "prim_occ"), new("sqft", "sqft", CsvValue.OptionalWholeNumber),
        ], ct);
        // The points come from MySQL itself, so the next comparison with provider_map is exact.
        await RunAsync(connection, $"UPDATE `{table}` b JOIN `{Points}` p ON p.`addr_key` = b.`addr_key` SET b.`lat` = p.`lat`, b.`lon` = p.`lon`", ct);
    }

    private static Task<int> RecordMatchAsync(MySqlConnection connection, long rows, bool full, CancellationToken ct, MySqlTransaction? tx = null) =>
        connection.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
            VALUES (@source, '', '', @rows, UTC_TIMESTAMP(), UTC_TIMESTAMP()) AS new
            ON DUPLICATE KEY UPDATE `rows_loaded` = new.`rows_loaded`, `checked_at` = new.`checked_at`{(full ? ", `loaded_at` = new.`loaded_at`" : "")}
            """, new { source = MatchSource, rows }, tx, cancellationToken: ct));

    private static void CreatePoints(DuckDBConnection duck, string points) =>
        Execute(duck, $"CREATE TABLE pts AS SELECT addr_key, ST_Point(lon, lat) AS g FROM read_csv('{Sql(points)}', header = true, " +
                      "columns = {'addr_key': 'VARCHAR', 'lat': 'DOUBLE', 'lon': 'DOUBLE'})");

    /// <summary>Inside a building wins; otherwise the nearest one; otherwise none (asked again only at the next full load).</summary>
    private static void WriteResults(DuckDBConnection duck, string results) =>
        Execute(duck, string.Create(CultureInfo.InvariantCulture, $"""
            COPY (
              SELECT p.addr_key,
                     CASE WHEN i.addr_key IS NOT NULL THEN 'inside' WHEN n.addr_key IS NOT NULL THEN 'near' ELSE 'none' END AS matched,
                     CASE WHEN i.addr_key IS NULL AND n.addr_key IS NOT NULL THEN CAST(round(n.d * {MetresPerDegree}) AS INTEGER) END AS distance_m,
                     COALESCE(i.occ_cls, n.occ_cls) AS occ_cls, COALESCE(i.prim_occ, n.prim_occ) AS prim_occ,
                     CAST(round(COALESCE(i.sqft, n.sqft)) AS INTEGER) AS sqft
              FROM pts p
              LEFT JOIN (SELECT * FROM inside QUALIFY row_number() OVER (PARTITION BY addr_key ORDER BY sqft) = 1) i ON i.addr_key = p.addr_key
              LEFT JOIN (SELECT * FROM near QUALIFY row_number() OVER (PARTITION BY addr_key ORDER BY d, sqft) = 1) n ON n.addr_key = p.addr_key
            ) TO '{Sql(results)}' (HEADER, DELIMITER ',')
            """));

    /// <summary>Adds the buildings of one file containing a point, and for the points outside them, the buildings within reach.</summary>
    private static void MatchPart(DuckDBConnection duck, string file)
    {
        var source = $"(SELECT geometry, OCC_CLS AS occ_cls, PRIM_OCC AS prim_occ, SQFEET AS sqft FROM read_parquet('{Sql(file)}'))";
        Execute(duck, $"CREATE OR REPLACE TABLE part_inside AS SELECT p.addr_key, b.occ_cls, b.prim_occ, b.sqft FROM pts p JOIN {source} b ON ST_Contains(b.geometry, p.g)");
        Execute(duck, "INSERT INTO inside SELECT * FROM part_inside");
        Execute(duck, string.Create(CultureInfo.InvariantCulture, $"""
            INSERT INTO near
            SELECT p.addr_key, ST_Distance(b.geometry, p.g), b.occ_cls, b.prim_occ, b.sqft
            FROM (SELECT addr_key, g, ST_Buffer(g, {NearDegrees}) AS reach FROM pts WHERE addr_key NOT IN (SELECT addr_key FROM part_inside)) p
            JOIN {source} b ON ST_Intersects(b.geometry, p.reach)
            """));
    }

    private static Task<int> RunAsync(MySqlConnection connection, string sql, CancellationToken ct, MySqlTransaction? tx = null) =>
        connection.ExecuteAsync(new CommandDefinition(sql, transaction: tx, commandTimeout: 0, cancellationToken: ct));

    private static async Task DropScratchAsync(Database database)
    {
        await using var connection = await database.OpenAsync(CancellationToken.None);
        foreach (var table in new[] { Points, Changed, Gone, NewRows })
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{table}`", CancellationToken.None);
        }
    }

    private static void Cleanup(string folder, string points, string results, string duckFile)
    {
        foreach (var path in new[] { points, results, duckFile, duckFile + ".wal" })
        {
            File.Delete(path);
        }

        if (Directory.Exists(Path.Combine(folder, "tmp")))
        {
            Directory.Delete(Path.Combine(folder, "tmp"), recursive: true);
        }
    }

    private static void Execute(DuckDBConnection duck, string sql)
    {
        using var command = duck.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Sql(string path) => path.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);

    [GeneratedRegex(@"<Contents><Key>(?<key>[^<]+)</Key><LastModified>(?<modified>[^<]+)</LastModified><ETag>(?<etag>[^<]+)</ETag>")]
    private static partial Regex Contents();
}
