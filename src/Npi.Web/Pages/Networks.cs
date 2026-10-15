namespace Npi.Web.Pages;

/// <summary>
/// What <c>_HealthSystemNote</c> looks up (docs/PRODUCT_PLAN.md step 3.6 part 2): the health systems of an organization NPI
/// (<see cref="Npi"/>: the facilities it bills for) or of a Medicare group (<see cref="Pac"/>: the facilities an NPI under its PAC ID bills for).
/// </summary>
public sealed record HealthSystemNote(string Kind, string Id)
{
    public const string Npi = "npi";
    public const string Pac = "pac";
}

/// <summary>Small formatting for the health system, ACO and health center pages.</summary>
public static class NetworkFormat
{
    /// <summary>A website as a link target: "www.example.org" → "https://www.example.org"; anything that isn't a web address → null.</summary>
    public static string? Website(string? site)
    {
        var text = site?.Trim();
        if (string.IsNullOrEmpty(text) || text.Contains(' ', StringComparison.Ordinal) || text.Contains('@', StringComparison.Ordinal))
        {
            return null;
        }

        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Contains('.', StringComparison.Ordinal)
            ? uri.ToString()
            : null;
    }

    /// <summary>"AZ, NV" → "Arizona, Nevada" (codes it doesn't know stay as written).</summary>
    public static string States(string? serviceArea) =>
        string.Join(", ", (serviceArea ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Npi.Core.Search.GeographyCatalog.StateName(s.ToUpperInvariant()) ?? s));
}
