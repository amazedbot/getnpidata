namespace Npi.Core.Search;

/// <summary>
/// The kind of building at a practice address (CLAUDE.md §7 Stage 5.5 item 18), from FEMA USA Structures and signs of a
/// business at the address; the loader's AddressTypeBuilder sets them. Used by the <c>addressType</c> filter.
/// </summary>
public static class AddressTypes
{
    public const string Commercial = "commercial";
    public const string Residential = "residential";
    public const string Care = "care";
    public const string Unknown = "unknown";

    /// <summary>The filter values with their labels, in the order the form lists them.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> All =
    [
        (Commercial, "Commercial or institutional building"),
        (Residential, "Residential building"),
        (Care, "Residential care facility (nursing home, dormitory)"),
        (Unknown, "Unknown"),
    ];

    public static bool IsValid(string value) => All.Any(t => t.Value == value);

    /// <summary>A short label for the grid, CSV and provider page; null for no value.</summary>
    public static string? Label(string? type) => type switch
    {
        Commercial => "Commercial",
        Residential => "Residential",
        Care => "Care facility",
        Unknown => "Unknown",
        _ => null,
    };
}
