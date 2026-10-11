using System.Globalization;

namespace Npi.Web.Pages;

/// <summary>One numbered pin on a <see cref="StaticMap"/>, placed in percent of the map's width and height.</summary>
public sealed record MapPin(string Label, double LeftPercent, double TopPercent, bool Approximate);

/// <summary>One map tile image, placed in percent of the map's width and height.</summary>
public sealed record MapTile(string Url, double LeftPercent, double TopPercent, double WidthPercent, double HeightPercent);

/// <summary>
/// A map preview without a map library (docs/PRODUCT_PLAN.md step 1.4): the few OpenStreetMap tiles around a provider's
/// practice locations as plain images, with numbered pins on top, at the largest zoom that shows every point. It scales with
/// the page (positions are percentages), needs no script, and links to the full map page.
/// </summary>
public sealed class StaticMap
{
    public const int Width = 768;
    public const int Height = 300;
    private const int TileSize = 256;
    private const int MaxZoom = 16;
    private const int MinZoom = 3;
    private const int Margin = 48;

    private StaticMap(IReadOnlyList<MapTile> tiles, IReadOnlyList<MapPin> pins, int zoom, double centerLat, double centerLon)
    {
        Tiles = tiles;
        Pins = pins;
        Zoom = zoom;
        CenterLat = centerLat;
        CenterLon = centerLon;
    }

    public IReadOnlyList<MapTile> Tiles { get; }

    public IReadOnlyList<MapPin> Pins { get; }

    public int Zoom { get; }

    public double CenterLat { get; }

    public double CenterLon { get; }

    /// <summary>The map around the points (label, latitude, longitude, approximate); null without points or a tile URL.</summary>
    public static StaticMap? Create(IEnumerable<(string Label, double Lat, double Lon, bool Approximate)> points, string? tileUrl)
    {
        var list = points.Where(p => Math.Abs(p.Lat) <= 85 && Math.Abs(p.Lon) <= 180).ToList();
        if (list.Count == 0 || string.IsNullOrWhiteSpace(tileUrl))
        {
            return null;
        }

        // The largest zoom at which every point fits inside the margins.
        var zoom = MaxZoom;
        for (; zoom > MinZoom; zoom--)
        {
            var xs = list.Select(p => WorldX(p.Lon, zoom)).ToList();
            var ys = list.Select(p => WorldY(p.Lat, zoom)).ToList();
            if (xs.Max() - xs.Min() <= Width - 2 * Margin && ys.Max() - ys.Min() <= Height - 2 * Margin)
            {
                break;
            }
        }

        var centerX = (list.Min(p => WorldX(p.Lon, zoom)) + list.Max(p => WorldX(p.Lon, zoom))) / 2;
        var centerY = (list.Min(p => WorldY(p.Lat, zoom)) + list.Max(p => WorldY(p.Lat, zoom))) / 2;
        var left = centerX - Width / 2.0;
        var top = centerY - Height / 2.0;
        var tilesPerSide = 1 << zoom;

        var tiles = new List<MapTile>();
        for (var tx = (int)Math.Floor(left / TileSize); tx * TileSize < left + Width; tx++)
        {
            for (var ty = (int)Math.Floor(top / TileSize); ty * TileSize < top + Height; ty++)
            {
                if (ty < 0 || ty >= tilesPerSide)
                {
                    continue;
                }

                var wrappedX = ((tx % tilesPerSide) + tilesPerSide) % tilesPerSide;
                var url = tileUrl.Replace("{z}", zoom.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{x}", wrappedX.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{y}", ty.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{s}", "a", StringComparison.Ordinal);
                tiles.Add(new MapTile(url, Percent(tx * TileSize - left, Width), Percent(ty * TileSize - top, Height),
                    Percent(TileSize, Width), Percent(TileSize, Height)));
            }
        }

        var pins = list.Select(p => new MapPin(p.Label, Percent(WorldX(p.Lon, zoom) - left, Width), Percent(WorldY(p.Lat, zoom) - top, Height), p.Approximate))
            .ToList();
        var centerLat = list.Average(p => p.Lat);
        var centerLon = list.Average(p => p.Lon);
        return new StaticMap(tiles, pins, zoom, centerLat, centerLon);
    }

    /// <summary>"12.3456" for an inline style.</summary>
    public static string Css(double percent) => percent.ToString("0.####", CultureInfo.InvariantCulture);

    private static double Percent(double pixels, int size) => pixels * 100.0 / size;

    // Web Mercator world pixels at a zoom level (256-pixel tiles).
    internal static double WorldX(double lon, int zoom) => (lon + 180) / 360 * TileSize * (1 << zoom);

    internal static double WorldY(double lat, int zoom)
    {
        var rad = lat * Math.PI / 180;
        return (1 - Math.Log(Math.Tan(rad) + 1 / Math.Cos(rad)) / Math.PI) / 2 * TileSize * (1 << zoom);
    }
}
