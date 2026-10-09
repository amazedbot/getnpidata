using System.Globalization;

namespace Npi.Core.Search;

/// <summary>
/// The visible map area for the map search page (CLAUDE.md §7 Stage 5.5 item 10). Parsed from Leaflet's
/// <c>toBBoxString()</c> order: "west,south,east,north" in degrees.
/// </summary>
public sealed record MapBounds(double South, double West, double North, double East)
{
    /// <summary>Largest searchable area, about 170 × 180 miles: bigger views ask the visitor to zoom in.</summary>
    public const double MaxLatSpan = 2.5;

    public const double MaxLonSpan = 3.5;

    public double CenterLat => (South + North) / 2;

    public double CenterLon => (West + East) / 2;

    /// <summary>The area as WKT for MBRContains (x = longitude, y = latitude).</summary>
    public string Polygon => string.Create(CultureInfo.InvariantCulture,
        $"POLYGON(({West:R} {South:R}, {East:R} {South:R}, {East:R} {North:R}, {West:R} {North:R}, {West:R} {South:R}))");

    /// <summary>
    /// The 0.1° grid cells the area touches, as filed in provider_map_specialty:
    /// FLOOR((lat + 90) * 10) * 3600 + FLOOR((lon + 180) * 10). At most 26 × 36 for the largest searchable area.
    /// </summary>
    public IReadOnlyList<int> Cells
    {
        get
        {
            var cells = new List<int>();
            for (var row = (int)Math.Floor((South + 90) * 10); row <= (int)Math.Floor((North + 90) * 10); row++)
            {
                for (var col = (int)Math.Floor((West + 180) * 10); col <= (int)Math.Floor((East + 180) * 10); col++)
                {
                    cells.Add(row * 3600 + col);
                }
            }

            return cells;
        }
    }

    /// <summary>The box with the same centre and <paramref name="side"/> times the width and height.</summary>
    public MapBounds Around(double side)
    {
        var halfLat = (North - South) * side / 2;
        var halfLon = (East - West) * side / 2;
        return new MapBounds(CenterLat - halfLat, CenterLon - halfLon, CenterLat + halfLat, CenterLon + halfLon);
    }

    /// <summary>Why the area can't be searched, or null when it can.</summary>
    public string? Problem =>
        !(double.IsFinite(South) && double.IsFinite(West) && double.IsFinite(North) && double.IsFinite(East))
        || South < -90 || North > 90 || West < -180 || East > 180 || South >= North || West >= East
            ? "The map area is invalid."
            : North - South > MaxLatSpan || East - West > MaxLonSpan
                ? "Zoom in to search this area: the map shows too large a region."
                : null;

    /// <summary>Parses "west,south,east,north"; null when it isn't four numbers.</summary>
    public static MapBounds? Parse(string? value)
    {
        var parts = value?.Split(',');
        if (parts is not { Length: 4 })
        {
            return null;
        }

        var n = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]))
            {
                return null;
            }
        }

        return new MapBounds(South: n[1], West: n[0], North: n[3], East: n[2]);
    }
}

/// <summary>One provider at one street address inside the map area.</summary>
/// <param name="Approximate">True when the point is the ZIP centroid (address not geocoded or not matched).</param>
/// <param name="DistanceMiles">From the centre of the map area.</param>
public sealed record AreaProvider(ProviderSummary Provider, double Lat, double Lon, bool Approximate, double DistanceMiles);

/// <summary>The providers in a map area, nearest the centre first. <see cref="Truncated"/>: there were more than <see cref="Limit"/>.</summary>
public sealed record AreaResult(IReadOnlyList<AreaProvider> Items, bool Truncated, int Limit, DateOnly? DataAsOf);
