using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>A ZIP code's Census ZCTA centroid.</summary>
public sealed record ZipPoint(string Zip, double Lat, double Lon);

/// <summary>The ZIP whose centroid is closest to a point, and how far away it is.</summary>
public sealed record NearestZip(string Zip, double DistanceMiles);

/// <summary>One provider listed under a map pin.</summary>
public sealed record MapEntry(string Npi, string Name, string? Specialty, string? Address);

/// <summary>A map pin: every provider on the page whose matching location is in this ZIP.</summary>
public sealed record MapPin(string Zip, double Lat, double Lon, IReadOnlyList<MapEntry> Providers);

/// <summary>
/// Map of the results page and "near me" (CLAUDE.md §7 Stage 5.5 item 10). Providers are placed at their ZIP's
/// Census centroid (<c>zip_centroid</c>), never at a geocoded street address.
/// </summary>
public sealed class MapService(string connectionString)
{
    /// <summary>"Near me" finds a ZIP only within this distance (the largest search radius).</summary>
    public const double MaxNearestMiles = 100;

    public static bool IsValidCoordinate(double lat, double lon) =>
        double.IsFinite(lat) && double.IsFinite(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;

    /// <summary>Centroids of the given ZIPs; ZIPs without a ZCTA are left out.</summary>
    public async Task<IReadOnlyDictionary<string, ZipPoint>> GetCentroidsAsync(IEnumerable<string> zips, CancellationToken ct)
    {
        var wanted = zips.Where(z => z is { Length: 5 } && z.All(char.IsAsciiDigit)).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return new Dictionary<string, ZipPoint>();
        }

        await using var connection = new MySqlConnection(connectionString);
        var rows = await connection.QueryAsync<ZipPoint>(new CommandDefinition(
            "SELECT zip5 AS Zip, CAST(lat AS DOUBLE) AS Lat, CAST(lon AS DOUBLE) AS Lon FROM zip_centroid WHERE zip5 IN @wanted",
            new { wanted }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Zip, StringComparer.Ordinal);
    }

    /// <summary>The ZIP centroid nearest to the point, or null when none is within <see cref="MaxNearestMiles"/>.</summary>
    public async Task<NearestZip?> FindNearestZipAsync(double lat, double lon, CancellationToken ct)
    {
        if (!IsValidCoordinate(lat, lon))
        {
            throw new ArgumentOutOfRangeException(nameof(lat), "Latitude must be -90..90 and longitude -180..180.");
        }

        // Bounding box first (about MaxNearestMiles in every direction), then the exact great-circle distance.
        var latDelta = MaxNearestMiles / 69.0;
        var lonDelta = MaxNearestMiles / (69.0 * Math.Max(0.01, Math.Cos(lat * Math.PI / 180)));
        await using var connection = new MySqlConnection(connectionString);
        var nearest = await connection.QueryFirstOrDefaultAsync<NearestZip>(new CommandDefinition(
            """
            SELECT zip5 AS Zip,
                   3958.8 * 2 * ASIN(SQRT(POWER(SIN(RADIANS(lat - @lat) / 2), 2)
                       + COS(RADIANS(@lat)) * COS(RADIANS(lat)) * POWER(SIN(RADIANS(lon - @lon) / 2), 2))) AS DistanceMiles
            FROM zip_centroid
            WHERE lat BETWEEN @latMin AND @latMax AND lon BETWEEN @lonMin AND @lonMax
            ORDER BY DistanceMiles, zip5
            LIMIT 1
            """,
            new { lat, lon, latMin = lat - latDelta, latMax = lat + latDelta, lonMin = lon - lonDelta, lonMax = lon + lonDelta },
            cancellationToken: ct));
        return nearest is not null && nearest.DistanceMiles <= MaxNearestMiles ? nearest : null;
    }

    /// <summary>Groups a results page into one pin per ZIP, in the order the ZIPs first appear. Providers without a centroid are skipped.</summary>
    public static IReadOnlyList<MapPin> GroupPins(IEnumerable<ProviderSummary> providers, IReadOnlyDictionary<string, ZipPoint> centroids) =>
        providers
            .Select(p => (Provider: p, Zip: Zip5(p.Zip)))
            .Where(x => x.Zip is not null && centroids.ContainsKey(x.Zip))
            .GroupBy(x => x.Zip!, StringComparer.Ordinal)
            .Select(g =>
            {
                var point = centroids[g.Key];
                return new MapPin(g.Key, point.Lat, point.Lon, g.Select(x => new MapEntry(x.Provider.Npi, x.Provider.Name, x.Provider.PrimarySpecialty,
                    string.Join(", ", new[] { x.Provider.Address1, x.Provider.City }.Where(s => !string.IsNullOrEmpty(s))))).ToList());
            })
            .ToList();

    /// <summary>The 5-digit ZIP of a "12345" or "12345-6789" value, else null.</summary>
    public static string? Zip5(string? zip) =>
        zip is { Length: >= 5 } && !zip.AsSpan(0, 5).ContainsAnyExceptInRange('0', '9') && (zip.Length == 5 || zip[5] == '-') ? zip[..5] : null;
}
