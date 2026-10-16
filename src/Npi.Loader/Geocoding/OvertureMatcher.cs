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

/// <summary>What <see cref="OvertureMatcher.RefreshAsync"/> did: a full pass for a new release, or the new addresses only.</summary>
public sealed record OvertureRefresh(string Release, bool Full, int Addresses, int Matched);

/// <summary>
/// Places practice addresses at their building with Overture Maps data (CLAUDE.md §7 Stage 5.5 item 10), ahead of the
/// Census geocoder's street interpolation: address points (the US DOT National Address Database), the addresses of
/// places, and place names. The public GeoParquet files are read in place on S3 with DuckDB, filtered to the area's
/// bounding box, so only that area is downloaded. Results go to <c>address_point</c>; every address looked at is kept in
/// <c>overture_checked</c> with the release, so <c>run</c> places only new addresses between releases (<see cref="RefreshAsync"/>).
/// </summary>
public sealed partial class OvertureMatcher(Database database, HttpClient http, ILogger log, LoaderOptions options)
{
    private sealed record Candidate(string AddrKey, string? Address1, string? City, string Zip5);

    private sealed record Box(double? South, double? West, double? North, double? East);

    private sealed record NewCandidate(string AddrKey, string? Address1, string? City, string? Zip5, double? Lat, double? Lon);

    /// <summary>The <c>reference_data</c> row whose version is the release of the last full pass (every state).</summary>
    public const string FullSource = "overture_full";

    /// <summary>Overture data within a grid cell of this size around a new address's point (or a neighbouring cell) is read.</summary>
    private const double CellDegrees = 0.5;

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
    public async Task<OvertureOutcome> MatchCountyAsync(string countyFips, CancellationToken ct) =>
        await MatchAreaAsync(await FindLatestReleaseAsync(ct), $"county {countyFips}",
            "l.`zip5` IN (SELECT z.`zip5` FROM `zip_county` z WHERE z.`county_fips` = @area)",
            "z.`county_fips` = @area", countyFips, ct);

    /// <summary>Matches the practice addresses of one state or territory (2-letter code).</summary>
    public async Task<OvertureOutcome> MatchStateAsync(string state, CancellationToken ct) =>
        await MatchStateAsync(await FindLatestReleaseAsync(ct), state, ct);

    /// <summary>
    /// Every state and territory, one at a time (a state's area is read from S3 once; only the points its addresses can
    /// use are kept in memory). A state that fails is logged and skipped; rerunning redoes it.
    /// </summary>
    /// <returns>The outcomes of the states that completed, and the states that failed.</returns>
    public async Task<(IReadOnlyList<OvertureOutcome> Done, IReadOnlyList<string> Failed)> MatchAllAsync(CancellationToken ct) =>
        await MatchAllAsync(await FindLatestReleaseAsync(ct), ct);

    private async Task<(IReadOnlyList<OvertureOutcome> Done, IReadOnlyList<string> Failed)> MatchAllAsync(string release, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        var states = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT `state` FROM `county` ORDER BY `state`", cancellationToken: ct))).ToList();
        var done = new List<OvertureOutcome>();
        var failed = new List<string>();
        var watch = Stopwatch.StartNew();
        foreach (var (state, i) in states.Select((x, i) => (x, i + 1)))
        {
            try
            {
                done.Add(await MatchStateAsync(release, state, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.Error(ex, "Overture: state {State} failed; rerun `overture {State}` later", state, state);
                failed.Add(state);
            }

            log.Information("Overture: {Done} of {Total} states done in {Minutes:N0} min", i, states.Count, watch.Elapsed.TotalMinutes);
        }

        log.Information("Overture, all states: placed {Matched:N0} of {Count:N0} addresses ({Share:P1}) in {Minutes:N0} min; failed: {Failed}",
            done.Sum(o => o.Matched), done.Sum(o => o.Addresses), done.Sum(o => o.Addresses) == 0 ? 0 : (double)done.Sum(o => o.Matched) / done.Sum(o => o.Addresses),
            watch.Elapsed.TotalMinutes, failed.Count == 0 ? "none" : string.Join(", ", failed));
        // A failed state's addresses stay unchecked, so the next run places them as new addresses.
        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
            VALUES (@source, @release, @url, @rows, UTC_TIMESTAMP(), UTC_TIMESTAMP()) AS new
            ON DUPLICATE KEY UPDATE `version` = new.`version`, `source_url` = new.`source_url`, `rows_loaded` = new.`rows_loaded`,
              `loaded_at` = new.`loaded_at`, `checked_at` = new.`checked_at`
            """, ct, param: new { source = FullSource, release, url = options.OvertureBaseUrl + release, rows = done.Sum(o => o.Matched) });
        return (done, failed);
    }

    /// <summary>
    /// For <c>run</c> (docs/PRODUCT_PLAN.md §7 speed item 5): every state when Overture published a release newer than the
    /// last full pass; otherwise, when the projection changed since the last pass, only the practice addresses no pass of
    /// the current release has looked at (<see cref="MatchNewAsync"/>). Null when nothing was due.
    /// </summary>
    public async Task<OvertureRefresh?> RefreshAsync(CancellationToken ct)
    {
        var release = await FindLatestReleaseAsync(ct);
        await using var connection = await database.OpenAsync(ct);
        var lastFull = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT `version` FROM `reference_data` WHERE `source` = @source", new { source = FullSource }, cancellationToken: ct));
        if (lastFull != release)
        {
            log.Information("Overture: release {Release} is newer than the last full pass ({Last}): placing every state's addresses", release, lastFull ?? "none");
            var (done, _) = await MatchAllAsync(release, ct);
            return new OvertureRefresh(release, true, done.Sum(o => o.Addresses), done.Sum(o => o.Matched));
        }

        var due = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT IFNULL((SELECT `checked_at` FROM `reference_data` WHERE `source` = 'overture'), '1970-01-01')
                   < IFNULL((SELECT `projected_at` FROM `data_version` WHERE `id` = 1), '1970-01-01')
            """, cancellationToken: ct)) != 0;
        return due ? await MatchNewAsync(release, ct) : null;
    }

    private const string NewKeys = "overture_new_keys";
    private const string NewLocations = "overture_new_locations";

    /// <summary>
    /// Places the practice addresses no pass of <paramref name="release"/> has looked at, in one DuckDB pass per theme over
    /// the whole release that reads only the rows in their ZIPs or cities, inside their overall box and in a grid cell at or
    /// next to one of them (a state pass reads its whole area); the match itself is a state pass's. Those that got a point or
    /// a listed business are queued, with the providers there, for the address type and record quality labels; a new point
    /// reaches the map (and through it the FEMA building) at the map's next update.
    /// </summary>
    private async Task<OvertureRefresh> MatchNewAsync(string release, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        await using var connection = await database.OpenAsync(ct);
        var candidates = (await connection.QueryAsync<NewCandidate>(new CommandDefinition(
            """
            SELECT k.AddrKey, k.Address1, k.City, k.Zip5, CAST(COALESCE(g.`lat`, z.`lat`) AS DOUBLE) AS Lat, CAST(COALESCE(g.`lon`, z.`lon`) AS DOUBLE) AS Lon
            FROM (SELECT l.`addr_key` AS AddrKey, MIN(l.`address1`) AS Address1, MIN(l.`city`) AS City, MIN(l.`zip5`) AS Zip5
                  FROM `provider_location` l LEFT JOIN `overture_checked` c ON c.`addr_key` = l.`addr_key` AND c.`release` = @release
                  WHERE l.`addr_key` IS NOT NULL AND c.`addr_key` IS NULL
                  GROUP BY l.`addr_key`) k
            LEFT JOIN `address_geocode` g ON g.`addr_key` = k.AddrKey AND g.`status` = 'Match'
            LEFT JOIN `zip_centroid` z ON z.`zip5` = k.Zip5
            """, new { release }, commandTimeout: 0, cancellationToken: ct))).ToList();
        var placeable = candidates.Where(c => c is { Lat: not null, Lon: not null }).ToList();
        var matches = new List<(string Key, PointMatch Match)>();
        var listed = new List<(string Key, ListedPlace Place)>();
        if (placeable.Count > 0)
        {
            log.Information("Overture {Release}: placing {Count:N0} new practice addresses ({Without:N0} without a point or ZIP centre aren't looked up)",
                release, placeable.Count, candidates.Count - placeable.Count);
            var index = await Task.Run(() => LoadNewIndex(release, placeable), ct);
            log.Information("Overture, new addresses: {Points:N0} usable address keys of {Places:N0} places read, in {Seconds:N0}s",
                index.Points, index.Places, watch.Elapsed.TotalSeconds);
            foreach (var c in placeable)
            {
                if (index.Match(c.Address1, c.City, c.Zip5 ?? "") is { } match)
                {
                    matches.Add((c.AddrKey, match));
                }

                listed.AddRange(index.PlacesAt(c.Address1, c.Zip5 ?? "").Select(p => (c.AddrKey, p)));
            }
        }

        var keys = candidates.Select(c => c.AddrKey).ToList();
        await SaveAsync(connection, matches, release, ct);
        await SavePlacesAsync(connection, keys, listed, release, ct);
        await QueueLabelsAsync(connection, matches.Select(m => m.Key).Concat(listed.Select(l => l.Key)).Distinct(StringComparer.Ordinal).ToList(), ct);
        await SaveCheckedAsync(connection, keys, release, ct);
        // Only checked_at moves: a newer loaded_at would make the record quality relabel everyone (the queue covers these).
        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
            VALUES ('overture', @version, @url, @rows, UTC_TIMESTAMP(), UTC_TIMESTAMP()) AS new
            ON DUPLICATE KEY UPDATE `checked_at` = new.`checked_at`
            """, ct, param: new { version = $"{release} new addresses", url = options.OvertureBaseUrl + release, rows = matches.Count });
        log.Information("Overture placed {Matched:N0} of {Count:N0} new addresses ({Address:N0} by address point, {Place:N0} by place address, {Name:N0} by place name); {Listed:N0} with a listed business; in {Seconds:N0}s",
            matches.Count, candidates.Count, matches.Count(m => m.Match.Source == PointSource.Address), matches.Count(m => m.Match.Source == PointSource.Place),
            matches.Count(m => m.Match.Source == PointSource.PlaceName), listed.Select(l => l.Key).Distinct(StringComparer.Ordinal).Count(), watch.Elapsed.TotalSeconds);
        return new OvertureRefresh(release, false, candidates.Count, matches.Count);
    }

    /// <summary>A gap this wide (degrees) between new addresses splits their box (Guam, Hawaii, Alaska, Puerto Rico, the mainland).</summary>
    private const double BoxGapDegrees = 5;

    /// <summary>
    /// Boxes around the points, split at every gap wider than <see cref="BoxGapDegrees"/> in longitude or latitude, so the
    /// files' bounding-box statistics can skip the rest of the world: one box around Guam and Maine would cover the globe.
    /// </summary>
    public static List<(double South, double West, double North, double East)> Boxes(IReadOnlyList<(double Lat, double Lon)> points)
    {
        if (points.Count == 0)
        {
            return [];
        }

        foreach (var axis in new Func<(double Lat, double Lon), double>[] { p => p.Lon, p => p.Lat })
        {
            var sorted = points.OrderBy(axis).ToList();
            var (gap, at) = (0.0, 0);
            for (var i = 1; i < sorted.Count; i++)
            {
                if (axis(sorted[i]) - axis(sorted[i - 1]) > gap)
                {
                    (gap, at) = (axis(sorted[i]) - axis(sorted[i - 1]), i);
                }
            }

            if (gap > BoxGapDegrees)
            {
                return [.. Boxes(sorted[..at]), .. Boxes(sorted[at..])];
            }
        }

        return [(points.Min(p => p.Lat), points.Min(p => p.Lon), points.Max(p => p.Lat), points.Max(p => p.Lon))];
    }

    private OvertureIndex LoadNewIndex(string release, IReadOnlyList<NewCandidate> candidates)
    {
        var index = new OvertureIndex(candidates.Select(c => (c.Address1, c.City, c.Zip5 ?? "")));
        var root = options.OvertureBaseUrl + release;
        using var duck = new DuckDBConnection("DataSource=:memory:");
        duck.Open();
        Execute(duck, "INSTALL httpfs; LOAD httpfs; SET s3_region = 'us-west-2'; SET memory_limit = '2GB';");
        Execute(duck, "CREATE TABLE zips (zip VARCHAR); CREATE TABLE cities (city VARCHAR); CREATE TABLE cells (cy INTEGER, cx INTEGER);");
        Append(duck, "zips", candidates.Select(c => c.Zip5).OfType<string>().Where(z => z.Length == 5).Distinct(StringComparer.Ordinal), (r, z) => r.AppendValue(z));
        Append(duck, "cities", candidates.Select(c => c.City?.Trim().ToUpperInvariant()).OfType<string>().Where(c => c.Length > 0).Distinct(StringComparer.Ordinal),
            (r, c) => r.AppendValue(c));
        Append(duck, "cells", candidates
                .Select(c => (Y: (int)Math.Floor(c.Lat!.Value / CellDegrees), X: (int)Math.Floor(c.Lon!.Value / CellDegrees)))
                .SelectMany(c => from dy in new[] { -1, 0, 1 } from dx in new[] { -1, 0, 1 } select (Y: c.Y + dy, X: c.X + dx))
                .Distinct(),
            (r, c) => r.AppendValue(c.Y).AppendValue(c.X));
        string Near(string alias) => FormattableString.Invariant(
            $"EXISTS (SELECT 1 FROM cells k WHERE k.cy = CAST(floor({alias}.bbox.ymin / {CellDegrees}) AS INTEGER) AND k.cx = CAST(floor({alias}.bbox.xmin / {CellDegrees}) AS INTEGER))");
        foreach (var (south, west, north, east) in Boxes(candidates.Select(c => (c.Lat!.Value, c.Lon!.Value)).ToList()))
        {
            var margin = 2 * CellDegrees;
            var inBox = FormattableString.Invariant(
                $"bbox.xmin BETWEEN {west - margin} AND {east + margin} AND bbox.ymin BETWEEN {south - margin} AND {north + margin}");
            using (var command = duck.CreateCommand())
            {
                command.CommandText =
                    $"SELECT number, street, postcode, postal_city, bbox.ymin, bbox.xmin FROM read_parquet('{root}/theme=addresses/type=address/*', hive_partitioning = 1) a " +
                    $"WHERE country = 'US' AND {inBox} AND (left(postcode, 5) IN (SELECT zip FROM zips) OR upper(trim(postal_city)) IN (SELECT city FROM cities)) AND {Near("a")}";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    index.AddPoint(Text(reader, 0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Number(reader, 4), Number(reader, 5));
                }
            }

            using (var command = duck.CreateCommand())
            {
                command.CommandText =
                    "SELECT names.primary, addresses[1].freeform, addresses[1].postcode, addresses[1].locality, bbox.ymin, bbox.xmin, confidence, " +
                    "operating_status, " + PlaceSourcesSql + " " +
                    $"FROM read_parquet('{root}/theme=places/type=place/*', hive_partitioning = 1) p " +
                    FormattableString.Invariant($"WHERE {inBox} AND confidence >= {options.OverturePlaceMinConfidence} ") +
                    $"AND (left(addresses[1].postcode, 5) IN (SELECT zip FROM zips) OR upper(trim(addresses[1].locality)) IN (SELECT city FROM cities)) AND {Near("p")}";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    index.AddPlace(Text(reader, 0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Number(reader, 4), Number(reader, 5), Number(reader, 6),
                        Text(reader, 7), PlaceUpdated(Text(reader, 8)), Text(reader, 8));
                }
            }
        }

        return index;
    }

    private static void Append<T>(DuckDBConnection duck, string table, IEnumerable<T> rows, Func<IDuckDBAppenderRow, T, IDuckDBAppenderRow> fill)
    {
        using var appender = duck.CreateAppender(table);
        foreach (var row in rows)
        {
            fill(appender.CreateRow(), row).EndRow();
        }
    }

    /// <summary>Records that <paramref name="release"/> looked at these addresses (matched or not).</summary>
    private static async Task SaveCheckedAsync(MySqlConnection connection, List<string> keys, string release, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var chunk in keys.Chunk(5000))
        {
            await Database.ExecuteAsync(connection,
                "INSERT INTO `overture_checked` (`addr_key`, `release`, `checked_at`) SELECT k, @release, @now FROM JSON_TABLE(@keys, '$[*]' COLUMNS (k CHAR(32) PATH '$')) j " +
                "ON DUPLICATE KEY UPDATE `release` = @release, `checked_at` = @now", ct,
                param: new { keys = System.Text.Json.JsonSerializer.Serialize(chunk), release, now });
        }
    }

    /// <summary>Queues these addresses and the providers there for the address type and record quality labels.</summary>
    private static async Task QueueLabelsAsync(MySqlConnection connection, List<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
        {
            return;
        }

        await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{NewKeys}`", ct);
        await Database.ExecuteAsync(connection, $"CREATE TABLE `{NewKeys}` (`addr_key` CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY) ENGINE=InnoDB", ct);
        try
        {
            foreach (var chunk in keys.Chunk(5000))
            {
                await Database.ExecuteAsync(connection,
                    $"INSERT IGNORE INTO `{NewKeys}` SELECT k FROM JSON_TABLE(@keys, '$[*]' COLUMNS (k CHAR(32) PATH '$')) j", ct,
                    param: new { keys = System.Text.Json.JsonSerializer.Serialize(chunk) });
            }

            // provider_location has no index on its virtual addr_key: one pass finds the new keys' locations.
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{NewLocations}`", ct);
            await Database.ExecuteAsync(connection,
                $"CREATE TABLE `{NewLocations}` ENGINE=InnoDB SELECT l.`npi`, l.`addr_key`, l.`zip5` FROM `provider_location` l " +
                $"JOIN `{NewKeys}` k ON k.`addr_key` = l.`addr_key` WHERE l.`zip5` IS NOT NULL", ct);
            await Database.ExecuteAsync(connection, $"INSERT IGNORE INTO `address_pending` (`addr_key`, `zip5`) SELECT `addr_key`, MIN(`zip5`) FROM `{NewLocations}` GROUP BY `addr_key`", ct);
            await Database.ExecuteAsync(connection, $"INSERT IGNORE INTO `builder_pending` (`builder`, `npi`) SELECT DISTINCT @builder, `npi` FROM `{NewLocations}`", ct,
                param: new { builder = Projection.BuilderPending.AddressTypes });
            await Database.ExecuteAsync(connection, $"INSERT IGNORE INTO `{Projection.QualityBuilder.Pending}` (`npi`) SELECT DISTINCT `npi` FROM `{NewLocations}`", ct);
        }
        finally
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{NewKeys}`", CancellationToken.None);
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{NewLocations}`", CancellationToken.None);
        }
    }

    private Task<OvertureOutcome> MatchStateAsync(string release, string state, CancellationToken ct) =>
        MatchAreaAsync(release, $"state {state}", "l.`state` = @area",
            "z.`county_fips` IN (SELECT k.`county_fips` FROM `county` k WHERE k.`state` = @area)", state, ct);

    /// <param name="locationFilter">SQL condition on provider_location <c>l</c> selecting the area's addresses (parameter @area).</param>
    /// <param name="zipFilter">SQL condition on zip_county <c>z</c> selecting the area's ZIPs, whose centroids give the bounding box.</param>
    private async Task<OvertureOutcome> MatchAreaAsync(string release, string label, string locationFilter, string zipFilter, string area, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        await using var connection = await database.OpenAsync(ct);
        var candidates = (await connection.QueryAsync<Candidate>(new CommandDefinition(
            $"""
            SELECT l.`addr_key` AS AddrKey, MIN(l.`address1`) AS Address1, MIN(l.`city`) AS City, MIN(l.`zip5`) AS Zip5
            FROM `provider_location` l
            WHERE l.`addr_key` IS NOT NULL AND {locationFilter}
            GROUP BY l.`addr_key`
            """, new { area }, cancellationToken: ct))).ToList();
        var box = await connection.QuerySingleAsync<Box>(new CommandDefinition(
            $"""
            SELECT MIN(CAST(c.lat AS DOUBLE)) AS South, MIN(CAST(c.lon AS DOUBLE)) AS West, MAX(CAST(c.lat AS DOUBLE)) AS North, MAX(CAST(c.lon AS DOUBLE)) AS East
            FROM zip_county z JOIN zip_centroid c ON c.zip5 = z.zip5 WHERE {zipFilter}
            """, new { area }, cancellationToken: ct));
        if (candidates.Count == 0 || box is not { South: { } s, West: { } w, North: { } n, East: { } e })
        {
            log.Information("Overture {Release}: {Area} has no practice addresses or no ZIP centroids; skipped", release, label);
            return new OvertureOutcome(release, 0, 0, 0, 0);
        }

        log.Information("Overture {Release}: placing {Count:N0} practice addresses of {Area}", release, candidates.Count, label);
        var index = await Task.Run(() => LoadIndex(release, candidates, s - MarginDegrees, w - MarginDegrees, n + MarginDegrees, e + MarginDegrees), ct);
        log.Information("Overture {Area} loaded: {Points:N0} usable address keys of {Places:N0} places read, in {Seconds:N0}s", label, index.Points, index.Places, watch.Elapsed.TotalSeconds);

        var matches = new List<(string Key, PointMatch Match)>();
        var listed = new List<(string Key, ListedPlace Place)>();
        foreach (var c in candidates)
        {
            if (index.Match(c.Address1, c.City, c.Zip5) is { } match)
            {
                matches.Add((c.AddrKey, match));
            }

            listed.AddRange(index.PlacesAt(c.Address1, c.Zip5).Select(p => (c.AddrKey, p)));
        }

        await SaveAsync(connection, matches, release, ct);
        await SavePlacesAsync(connection, candidates.Select(c => c.AddrKey).ToList(), listed, release, ct);
        await SaveCheckedAsync(connection, candidates.Select(c => c.AddrKey).ToList(), release, ct);
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
            """, ct, param: new { version = $"{release} {label}", url = options.OvertureBaseUrl + release, rows = outcome.Matched, now });
        log.Information("Overture placed {Matched:N0} of {Count:N0} addresses of {Area} ({Share:P1}): {Address:N0} by address point, {Place:N0} by place address, {Name:N0} by place name, in {Seconds:N0}s",
            outcome.Matched, outcome.Addresses, label, (double)outcome.Matched / outcome.Addresses, outcome.ByAddress, outcome.ByPlace, outcome.ByPlaceName, watch.Elapsed.TotalSeconds);
        return outcome;
    }

    private OvertureIndex LoadIndex(string release, IReadOnlyList<Candidate> candidates, double south, double west, double north, double east)
    {
        var index = new OvertureIndex(candidates.Select(c => (c.Address1, c.City, c.Zip5)));
        var root = options.OvertureBaseUrl + release;
        var inBox = FormattableString.Invariant($"bbox.xmin BETWEEN {west} AND {east} AND bbox.ymin BETWEEN {south} AND {north}");
        using var duck = new DuckDBConnection("DataSource=:memory:");
        duck.Open();
        Execute(duck, "INSTALL httpfs; LOAD httpfs; SET s3_region = 'us-west-2'; SET memory_limit = '2GB';");

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
                $"SELECT names.primary, addresses[1].freeform, addresses[1].postcode, addresses[1].locality, bbox.ymin, bbox.xmin, confidence, " +
                "operating_status, " + PlaceSourcesSql + " " +
                $"FROM read_parquet('{root}/theme=places/type=place/*', hive_partitioning = 1) " +
                FormattableString.Invariant($"WHERE {inBox} AND confidence >= {options.OverturePlaceMinConfidence}");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                index.AddPlace(Text(reader, 0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Number(reader, 4), Number(reader, 5), Number(reader, 6),
                    Text(reader, 7), PlaceUpdated(Text(reader, 8)), Text(reader, 8));
            }
        }

        return index;
    }

    // matched_at is MySQL's clock when the row is written: MapBuilder compares it with its own start, read from MySQL too.
    private static async Task SaveAsync(MySqlConnection connection, List<(string Key, PointMatch Match)> matches, string release, CancellationToken ct)
    {
        foreach (var chunk in matches.Chunk(1000))
        {
            var p = new DynamicParameters();
            var values = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                var (key, m) = chunk[i];
                values.Add($"(@k{i}, @s{i}, @lat{i}, @lon{i}, @m{i}, @release, UTC_TIMESTAMP())");
                p.Add($"k{i}", key);
                p.Add($"s{i}", m.Source);
                p.Add($"lat{i}", Math.Round(m.Lat, 6));
                p.Add($"lon{i}", Math.Round(m.Lon, 6));
                p.Add($"m{i}", m.Matched.Length <= 255 ? m.Matched : m.Matched[..255]);
            }

            p.Add("release", release);
            await Database.ExecuteAsync(connection,
                "INSERT INTO `address_point` (`addr_key`, `source`, `lat`, `lon`, `matched`, `release`, `matched_at`) VALUES " + string.Join(", ", values) +
                " AS new ON DUPLICATE KEY UPDATE `source` = new.`source`, `lat` = new.`lat`, `lon` = new.`lon`, `matched` = new.`matched`, " +
                "`release` = new.`release`, `matched_at` = new.`matched_at`",
                ct, param: p);
        }
    }

    /// <summary>Replaces the listed businesses of the area's practice addresses (address_place, migration 087).</summary>
    private static async Task SavePlacesAsync(MySqlConnection connection, List<string> keys, List<(string Key, ListedPlace Place)> listed, string release,
        CancellationToken ct)
    {
        foreach (var chunk in keys.Chunk(5000))
        {
            await Database.ExecuteAsync(connection, "DELETE FROM `address_place` WHERE `addr_key` IN @keys", ct, param: new { keys = chunk });
        }

        foreach (var chunk in listed.Chunk(1000))
        {
            var p = new DynamicParameters();
            var values = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                var (key, place) = chunk[i];
                values.Add($"(@k{i}, @n{i}, @s{i}, @src{i}, @u{i}, @release)");
                p.Add($"k{i}", key);
                p.Add($"n{i}", place.Name.Length <= 255 ? place.Name : place.Name[..255]);
                p.Add($"s{i}", place.OperatingStatus is { Length: <= 20 } s ? s : null);
                p.Add($"u{i}", place.Updated?.ToDateTime(TimeOnly.MinValue));
                p.Add($"src{i}", place.Sources is { Length: > 255 } all ? all[..255] : place.Sources);
            }

            p.Add("release", release);
            await Database.ExecuteAsync(connection,
                "INSERT IGNORE INTO `address_place` (`addr_key`, `name`, `operating_status`, `sources`, `updated`, `release`) VALUES " + string.Join(", ", values),
                ct, param: p);
        }
    }

    /// <summary>
    /// A place's sources with their own dates, "dataset yyyy-mm-dd" joined by ";" (<see cref="ListedPlace.Sources"/>, migration 099):
    /// Overture's own rows ("Overture", "Overture-signals") are dated the release and say nothing about the business, so they're left out.
    /// </summary>
    private const string PlaceSourcesSql =
        "array_to_string(list_transform(list_filter(sources, s -> s.dataset NOT IN ('Overture', 'Overture-signals')), " +
        "s -> s.dataset || ' ' || coalesce(left(s.update_time, 10), '')), ';')";

    /// <summary>The newest date in <see cref="PlaceSourcesSql"/>'s list, or null.</summary>
    internal static DateOnly? PlaceUpdated(string? sources) =>
        sources?.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim() is var t && t.LastIndexOf(' ') is var i and > 0
                         && DateOnly.TryParseExact(t[(i + 1)..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null)
            .Max();

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
