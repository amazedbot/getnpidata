using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using DuckDB.NET.Data;
using MySqlConnector;
using Npi.Loader.Db;
using Serilog;

namespace Npi.Loader.Geocoding;

/// <summary>What placing one area's addresses with Overture data did.</summary>
public sealed record OvertureOutcome(string Release, int Addresses, int ByAddress, int ByPlace, int ByPlaceName)
{
    public int Matched => ByAddress + ByPlace + ByPlaceName;
}

/// <summary>
/// Places practice addresses at their building with Overture Maps data (CLAUDE.md §7 Stage 5.5 item 10), ahead of the
/// Census geocoder's street interpolation: address points (the US DOT National Address Database), the addresses of
/// places, and place names. The public GeoParquet files are read in place on S3 with DuckDB, filtered to the area's
/// bounding box, so only that area is downloaded. Results go to <c>address_point</c>.
/// </summary>
public sealed partial class OvertureMatcher(Database database, HttpClient http, ILogger log, LoaderOptions options)
{
    private sealed record Candidate(string AddrKey, string? Address1, string? City, string Zip5);

    private sealed record Box(double? South, double? West, double? North, double? East);

    /// <summary>Margin around the area's ZIP centroids, so addresses at the edge of a ZIP are still inside (about 10 miles).</summary>
    private const double MarginDegrees = 0.15;

    /// <summary>The newest Overture release ("2026-09-23.1"), from the S3 listing of release folders.</summary>
    public async Task<string> FindLatestReleaseAsync(CancellationToken ct)
    {
        var listing = await http.GetStringAsync(options.OvertureReleasesUrl, ct);
        return ReleaseFolder().Matches(listing).Select(m => m.Groups["release"].Value)
                   .OrderBy(r => r[..10], StringComparer.Ordinal)
                   .ThenBy(r => int.Parse(r[11..], CultureInfo.InvariantCulture))
                   .LastOrDefault()
               ?? throw new InvalidDataException($"No Overture releases listed at {options.OvertureReleasesUrl}");
    }

    /// <summary>Matches the practice addresses of one county (5-digit FIPS) and stores the matches.</summary>
    public async Task<OvertureOutcome> MatchCountyAsync(string countyFips, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var release = await FindLatestReleaseAsync(ct);
        await using var connection = await database.OpenAsync(ct);
        var candidates = (await connection.QueryAsync<Candidate>(new CommandDefinition(
            """
            SELECT l.`addr_key` AS AddrKey, MIN(l.`address1`) AS Address1, MIN(l.`city`) AS City, MIN(l.`zip5`) AS Zip5
            FROM `provider_location` l
            WHERE l.`addr_key` IS NOT NULL AND l.`zip5` IN (SELECT z.`zip5` FROM `zip_county` z WHERE z.`county_fips` = @fips)
            GROUP BY l.`addr_key`
            """, new { fips = countyFips }, cancellationToken: ct))).ToList();
        var box = await connection.QuerySingleAsync<Box>(new CommandDefinition(
            """
            SELECT MIN(CAST(c.lat AS DOUBLE)) AS South, MIN(CAST(c.lon AS DOUBLE)) AS West, MAX(CAST(c.lat AS DOUBLE)) AS North, MAX(CAST(c.lon AS DOUBLE)) AS East
            FROM zip_county z JOIN zip_centroid c ON c.zip5 = z.zip5 WHERE z.county_fips = @fips
            """, new { fips = countyFips }, cancellationToken: ct));
        if (candidates.Count == 0 || box is not { South: { } s, West: { } w, North: { } n, East: { } e })
        {
            throw new InvalidOperationException($"County {countyFips} has no practice addresses or no ZIP centroids.");
        }

        log.Information("Overture {Release}: placing {Count:N0} practice addresses of county {Fips}", release, candidates.Count, countyFips);
        var index = await Task.Run(() => LoadIndex(release, s - MarginDegrees, w - MarginDegrees, n + MarginDegrees, e + MarginDegrees), ct);
        log.Information("Overture area loaded: {Points:N0} address keys, {Places:N0} places in {Seconds:N0}s", index.Points, index.Places, watch.Elapsed.TotalSeconds);

        var matches = new List<(string Key, PointMatch Match)>();
        foreach (var c in candidates)
        {
            if (index.Match(c.Address1, c.City, c.Zip5) is { } match)
            {
                matches.Add((c.AddrKey, match));
            }
        }

        await SaveAsync(connection, matches, release, ct);
        var outcome = new OvertureOutcome(release, candidates.Count,
            matches.Count(m => m.Match.Source == PointSource.Address), matches.Count(m => m.Match.Source == PointSource.Place),
            matches.Count(m => m.Match.Source == PointSource.PlaceName));
        var now = DateTime.UtcNow;
        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
            VALUES ('overture', @version, @url, @rows, @now, @now) AS new
            ON DUPLICATE KEY UPDATE `version` = new.`version`, `source_url` = new.`source_url`, `rows_loaded` = new.`rows_loaded`,
              `loaded_at` = new.`loaded_at`, `checked_at` = new.`checked_at`
            """, ct, param: new { version = $"{release} county {countyFips}", url = options.OvertureBaseUrl + release, rows = outcome.Matched, now });
        log.Information("Overture placed {Matched:N0} of {Count:N0} addresses ({Share:P1}): {Address:N0} by address point, {Place:N0} by place address, {Name:N0} by place name, in {Seconds:N0}s",
            outcome.Matched, outcome.Addresses, (double)outcome.Matched / outcome.Addresses, outcome.ByAddress, outcome.ByPlace, outcome.ByPlaceName, watch.Elapsed.TotalSeconds);
        return outcome;
    }

    private OvertureIndex LoadIndex(string release, double south, double west, double north, double east)
    {
        var index = new OvertureIndex();
        var root = options.OvertureBaseUrl + release;
        var inBox = FormattableString.Invariant($"bbox.xmin BETWEEN {west} AND {east} AND bbox.ymin BETWEEN {south} AND {north}");
        using var duck = new DuckDBConnection("DataSource=:memory:");
        duck.Open();
        Execute(duck, "INSTALL httpfs; LOAD httpfs; SET s3_region = 'us-west-2';");

        using (var command = duck.CreateCommand())
        {
            command.CommandText =
                $"SELECT number, street, postcode, postal_city, bbox.ymin, bbox.xmin FROM read_parquet('{root}/theme=addresses/type=address/*', hive_partitioning = 1) " +
                $"WHERE country = 'US' AND {inBox}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                index.AddPoint(Text(reader, 0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Number(reader, 4), Number(reader, 5));
            }
        }

        using (var command = duck.CreateCommand())
        {
            command.CommandText =
                $"SELECT names.primary, addresses[1].freeform, addresses[1].postcode, addresses[1].locality, bbox.ymin, bbox.xmin, confidence " +
                $"FROM read_parquet('{root}/theme=places/type=place/*', hive_partitioning = 1) " +
                FormattableString.Invariant($"WHERE {inBox} AND confidence >= {options.OverturePlaceMinConfidence}");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                index.AddPlace(Text(reader, 0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Number(reader, 4), Number(reader, 5), Number(reader, 6));
            }
        }

        return index;
    }

    private static async Task SaveAsync(MySqlConnection connection, List<(string Key, PointMatch Match)> matches, string release, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var chunk in matches.Chunk(1000))
        {
            var p = new DynamicParameters();
            var values = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                var (key, m) = chunk[i];
                values.Add($"(@k{i}, @s{i}, @lat{i}, @lon{i}, @m{i}, @release, @now)");
                p.Add($"k{i}", key);
                p.Add($"s{i}", m.Source);
                p.Add($"lat{i}", Math.Round(m.Lat, 6));
                p.Add($"lon{i}", Math.Round(m.Lon, 6));
                p.Add($"m{i}", m.Matched.Length <= 255 ? m.Matched : m.Matched[..255]);
            }

            p.Add("release", release);
            p.Add("now", now);
            await Database.ExecuteAsync(connection,
                "INSERT INTO `address_point` (`addr_key`, `source`, `lat`, `lon`, `matched`, `release`, `matched_at`) VALUES " + string.Join(", ", values) +
                " AS new ON DUPLICATE KEY UPDATE `source` = new.`source`, `lat` = new.`lat`, `lon` = new.`lon`, `matched` = new.`matched`, " +
                "`release` = new.`release`, `matched_at` = new.`matched_at`",
                ct, param: p);
        }
    }

    private static void Execute(DuckDBConnection duck, string sql)
    {
        using var command = duck.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string? Text(System.Data.Common.DbDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();

    private static double Number(System.Data.Common.DbDataReader reader, int i) =>
        reader.IsDBNull(i) ? 0 : Convert.ToDouble(reader.GetValue(i), CultureInfo.InvariantCulture);

    [GeneratedRegex(@"<Prefix>release/(?<release>\d{4}-\d{2}-\d{2}\.\d+)/</Prefix>")]
    private static partial Regex ReleaseFolder();
}
