using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>
/// Where the map search page (CLAUDE.md §7 Stage 5.5 item 10) opens when it comes from a search with location
/// filters ("View on map"): the area those filters cover, from the ZIP centroids.
/// </summary>
public sealed class MapService(string connectionString)
{
    private const double MilesPerDegreeLat = 69.0;

    private sealed record Box(double? South, double? West, double? North, double? East);

    /// <summary>The area for the filter's ZIP (+ radius), county, city or state, or null when it has none of them.</summary>
    public async Task<MapBounds?> GetStartAreaAsync(SearchFilter filter, CancellationToken ct)
    {
        string sql;
        object args;
        double padMiles = 1;
        if (filter.Zip5 is not null)
        {
            sql = "SELECT CAST(lat AS DOUBLE) AS South, CAST(lon AS DOUBLE) AS West, CAST(lat AS DOUBLE) AS North, CAST(lon AS DOUBLE) AS East FROM zip_centroid WHERE zip5 = @zip";
            args = new { zip = filter.Zip5 };
            padMiles = filter.RadiusMiles ?? 3;
        }
        else if (filter.CountyFips is not null)
        {
            sql = "SELECT MIN(CAST(c.lat AS DOUBLE)) AS South, MIN(CAST(c.lon AS DOUBLE)) AS West, MAX(CAST(c.lat AS DOUBLE)) AS North, MAX(CAST(c.lon AS DOUBLE)) AS East FROM zip_county z JOIN zip_centroid c ON c.zip5 = z.zip5 WHERE z.county_fips = @fips";
            args = new { fips = filter.CountyFips };
        }
        else if (filter.City is not null && filter.State is not null)
        {
            sql = "SELECT MIN(CAST(c.lat AS DOUBLE)) AS South, MIN(CAST(c.lon AS DOUBLE)) AS West, MAX(CAST(c.lat AS DOUBLE)) AS North, MAX(CAST(c.lon AS DOUBLE)) AS East FROM zip_county z JOIN zip_centroid c ON c.zip5 = z.zip5 WHERE z.usps_city = @city AND z.usps_state = @state";
            args = new { city = filter.City, state = filter.State };
        }
        else if (filter.State is not null)
        {
            sql = "SELECT MIN(CAST(c.lat AS DOUBLE)) AS South, MIN(CAST(c.lon AS DOUBLE)) AS West, MAX(CAST(c.lat AS DOUBLE)) AS North, MAX(CAST(c.lon AS DOUBLE)) AS East FROM zip_county z JOIN county k ON k.county_fips = z.county_fips JOIN zip_centroid c ON c.zip5 = z.zip5 WHERE k.state = @state";
            args = new { state = filter.State };
        }
        else
        {
            return null;
        }

        await using var connection = new MySqlConnection(connectionString);
        var box = await connection.QuerySingleOrDefaultAsync<Box>(new CommandDefinition(
            sql, args, cancellationToken: ct));
        if (box is not { South: { } s, West: { } w, North: { } n, East: { } e })
        {
            return null;
        }

        var dLat = padMiles / MilesPerDegreeLat;
        var dLon = padMiles / (MilesPerDegreeLat * Math.Max(Math.Cos((s + n) / 2 * Math.PI / 180), 0.01));
        return new MapBounds(s - dLat, w - dLon, n + dLat, e + dLon);
    }
}
