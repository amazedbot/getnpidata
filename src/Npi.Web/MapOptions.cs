namespace Npi.Web;

/// <summary>
/// Map tiles for the map search page (CLAUDE.md §7 Stage 5.5 item 10), configuration section <c>Map</c>. The default is the
/// OpenStreetMap standard tile server, which asks for attribution and allows only light use; a busy public site should
/// switch <see cref="TileUrl"/> to a hosted tile provider (an API key in the URL is a secret: set it in user-secrets /
/// App Service configuration, never in appsettings.json).
/// </summary>
public sealed class MapOptions
{
    public const string Section = "Map";

    /// <summary>Leaflet tile URL template with {z}/{x}/{y}; empty turns the map off.</summary>
    public string TileUrl { get; set; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    /// <summary>Attribution shown on the map (HTML), as the tile provider requires.</summary>
    public string Attribution { get; set; } = "&copy; <a href=\"https://www.openstreetmap.org/copyright\">OpenStreetMap</a> contributors";

    public int MaxZoom { get; set; } = 19;

    public bool Enabled => !string.IsNullOrWhiteSpace(TileUrl);
}
