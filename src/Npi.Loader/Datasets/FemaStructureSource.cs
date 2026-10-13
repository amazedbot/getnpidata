using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using DuckDB.NET.Data;
using MySqlConnector;
using Npi.Loader.Db;
using Npi.Loader.Projection;

namespace Npi.Loader.Datasets;

/// <summary>
/// FEMA USA Structures (Oak Ridge National Laboratory; CLAUDE.md §7 Stage 5.5 item 18): every US building over 450 sq ft
/// with its occupancy class and type. The GeoParquet copy on Source Cooperative (~10 files, ~20 GB) is downloaded one
/// file at a time; DuckDB finds the building under each mapped practice address (or the nearest within about 30 m)
/// → <c>address_building</c>; then <see cref="AddressTypeBuilder"/> labels the addresses. Addresses mapped only to a ZIP
/// centroid aren't looked up. The version includes the number of geocoded addresses in steps of
/// <see cref="AddressStep"/>, so new practice addresses get their buildings at the next step.
/// </summary>
public sealed partial class FemaStructureSource : DatasetSource
{
    /// <summary>Reload when this many more addresses have been geocoded since the last load.</summary>
    public const int AddressStep = 50_000;

    /// <summary>"Near" = within this many degrees of the point (≈ 33 m north–south, ≈ 25 m east–west at 40° N).</summary>
    private const double NearDegrees = 0.0003;

    private const double MetresPerDegree = 111_320;

    public override string Name => "fema_structures";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    private sealed record Part(string Key, string ETag, DateTime Modified);

    private static async Task<List<Part>> ListAsync(DatasetContext context, CancellationToken ct)
    {
        var xml = await context.Http.GetStringAsync(new Uri(context.Options.FemaStructuresListingUrl), ct);
        var parts = Contents().Matches(xml)
            .Select(m => new Part(m.Groups["key"].Value, m.Groups["etag"].Value.Replace("&quot;", "", StringComparison.Ordinal).Trim('"'),
                DateTime.Parse(m.Groups["modified"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)))
            .Where(p => p.Key.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToList();
        return parts.Count > 0 ? parts : throw new InvalidDataException($"No .parquet files listed at {context.Options.FemaStructuresListingUrl}.");
    }

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var parts = await ListAsync(context, ct);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts.Select(p => p.Key + " " + p.ETag)))))[..12];
        await using var connection = await context.Database.OpenAsync(ct);
        var geocoded = await Database.CountAsync(connection, "address_geocode", ct);
        var step = geocoded / AddressStep * AddressStep;
        return new DatasetRelease(
            string.Create(CultureInfo.InvariantCulture, $"USA Structures {parts.Max(p => p.Modified):yyyy-MM-dd} {hash}; {step:N0}+ addresses"),
            new Uri(context.Options.FemaStructuresBaseUrl + parts[0].Key));
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var parts = await ListAsync(context, ct);
        var folder = Path.Combine(context.Options.ResolvedWorkFolder, "datasets", "fema");
        Directory.CreateDirectory(folder);
        var points = Path.Combine(folder, "points.csv");
        var results = Path.Combine(folder, "buildings.csv");
        var duckFile = Path.Combine(folder, "match.duckdb");
        try
        {
            await using var connection = await context.Database.OpenAsync(ct);
            var pointCount = await WritePointsAsync(connection, points, ct);
            context.Log.Information("FEMA structures: {Points:N0} mapped practice addresses to place on buildings", pointCount);

            File.Delete(duckFile);
            using (var duck = new DuckDBConnection($"DataSource={duckFile}"))
            {
                duck.Open();
                Execute(duck, $"INSTALL spatial; LOAD spatial; SET memory_limit = '3GB'; SET temp_directory = '{Sql(Path.Combine(folder, "tmp"))}';");
                Execute(duck, $"CREATE TABLE pts AS SELECT addr_key, ST_Point(lon, lat) AS g FROM read_csv('{Sql(points)}', header = true, " +
                              "columns = {'addr_key': 'VARCHAR', 'lat': 'DOUBLE', 'lon': 'DOUBLE'})");
                Execute(duck, "CREATE TABLE inside (addr_key VARCHAR, occ_cls VARCHAR, prim_occ VARCHAR, sqft DOUBLE)");
                Execute(duck, "CREATE TABLE near (addr_key VARCHAR, d DOUBLE, occ_cls VARCHAR, prim_occ VARCHAR, sqft DOUBLE)");
                var downloader = new FileDownloader(context.Http, context.Log, context.Options.DownloadAttempts);
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

                // Inside a building wins; otherwise the nearest one; otherwise none (asked again only at the next load).
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
            }

            var counts = await TableSwap.ReplaceAsync(connection, ["address_building"], context.Options.MinRowRatio, async () =>
                await CsvTableLoader.LoadAsync(connection, results, "address_building_staging",
                [
                    new("addr_key", "addr_key"), new("matched", "matched"), new("distance_m", "distance_m", CsvValue.OptionalWholeNumber),
                    new("occ_cls", "occ_cls"), new("prim_occ", "prim_occ"), new("sqft", "sqft", CsvValue.OptionalWholeNumber),
                ], ct), ct);
            var byMatch = (await connection.QueryAsync<(string Matched, long Rows)>(new CommandDefinition(
                "SELECT `matched`, COUNT(*) FROM `address_building` GROUP BY `matched` ORDER BY `matched`", cancellationToken: ct))).ToList();
            context.Log.Information("FEMA structures: {Rows:N0} addresses ({Matches}) in {Minutes:N1} min", counts["address_building"],
                string.Join(", ", byMatch.Select(m => $"{m.Matched} {m.Rows:N0}")), watch.Elapsed.TotalMinutes);

            await new AddressTypeBuilder(context.Database, context.Log, context.Options.MinRowRatio).BuildAsync(ct, full: true);
            return counts["address_building"];
        }
        finally
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
    }

    /// <summary>Every practice address placed on the map at a building or street (not a ZIP centroid), once.</summary>
    private static async Task<long> WritePointsAsync(MySqlConnection connection, string path, CancellationToken ct)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16);
        await writer.WriteLineAsync("addr_key,lat,lon");
        await using var command = new MySqlCommand(
            "SELECT `addr_key`, MIN(`lat`), MIN(`lon`) FROM `provider_map` WHERE `source` <> 'zip' GROUP BY `addr_key`", connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        long count = 0;
        while (await reader.ReadAsync(ct))
        {
            await writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{reader.GetString(0)},{reader.GetDouble(1)},{reader.GetDouble(2)}"));
            count++;
        }

        return count;
    }

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
