using System.Globalization;

namespace Npi.Loader.Projection;

/// <summary>
/// Whether an open Overture listing confirms that a business operates (owner, 2026-10-15). Overture merges one source's record
/// per place, and not every source says the business is still there: BrightQuery's records come from business registrations
/// and are dated the release, so a closed practice whose company was never dissolved stays "open" (3 Village Chiropractic,
/// Setauket). Counted: listings kept by the business or read from its own site (Meta, AllThePlaces, PinMeTo, DAC, RenderSEO),
/// and Microsoft (Bing) or Foursquare listings their source updated within <see cref="MaxAgeYears"/> years of the release.
/// Not counted: BrightQuery, any other source, and a listing whose sources weren't recorded (before migration 099).
/// </summary>
public static class BusinessListing
{
    /// <summary>A Microsoft or Foursquare listing older than this (before the release) no longer confirms anything.</summary>
    public const int MaxAgeYears = 3;

    private static readonly HashSet<string> Current = new(StringComparer.OrdinalIgnoreCase) { "meta", "AllThePlaces", "PinMeTo", "DAC", "RenderSEO" };

    private static readonly HashSet<string> Dated = new(StringComparer.OrdinalIgnoreCase) { "Microsoft", "Foursquare" };

    /// <summary>
    /// <paramref name="sources"/> as stored in <c>address_place.sources</c>: "dataset yyyy-mm-dd" joined by ";" (the date may be
    /// missing); <paramref name="release"/> the Overture release ("2026-09-23.1").
    /// </summary>
    public static bool Confirms(string? sources, string? release)
    {
        if (string.IsNullOrWhiteSpace(sources))
        {
            return false;
        }

        var released = release is { Length: >= 10 } && DateOnly.TryParseExact(release[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var r)
            ? r
            : DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var entry in sources.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = entry.IndexOf(' ', StringComparison.Ordinal);
            var dataset = space < 0 ? entry : entry[..space];
            if (Current.Contains(dataset))
            {
                return true;
            }

            if (Dated.Contains(dataset) && space > 0
                && DateOnly.TryParseExact(entry[(space + 1)..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated)
                && updated >= released.AddYears(-MaxAgeYears))
            {
                return true;
            }
        }

        return false;
    }
}
