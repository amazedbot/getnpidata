using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Npi.Web.Search;

/// <summary>
/// The search box in the site header (docs/PRODUCT_PLAN.md step 1.2): an NPI opens its profile; any other text searches
/// names with "similar" matching, as an organization when it has an organization word (LLC, HOSPITAL, CLINIC …), else as a
/// person ("John Smith" = first and last name). Step 1.3 replaces this with full intent detection (specialty + place).
/// </summary>
public static partial class QuickSearch
{
    private static readonly HashSet<string> OrganizationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "LLC", "INC", "PC", "PLLC", "PA", "LLP", "LTD", "CORP", "CORPORATION", "CO", "COMPANY", "HOSPITAL", "HOSPITALS", "CLINIC", "CLINICS",
        "CENTER", "CENTRE", "CENTERS", "GROUP", "ASSOCIATES", "ASSOC", "MEDICAL", "HEALTH", "HEALTHCARE", "CARE", "PHARMACY", "LAB", "LABS",
        "LABORATORY", "LABORATORIES", "SERVICES", "PRACTICE", "PARTNERS", "DENTAL", "CHIROPRACTIC", "THERAPY", "UNIVERSITY", "SYSTEM",
        "FOUNDATION", "INSTITUTE", "HOME", "HOSPICE", "AMBULANCE", "IMAGING", "RADIOLOGY", "SURGERY", "SURGICAL", "URGENT", "REHAB",
        "REHABILITATION", "NURSING", "SUPPLY", "SUPPLIES", "OF", "THE", "AND",
    };

    /// <summary>The URL the header search box sends <paramref name="text"/> to.</summary>
    public static string Target(string? text)
    {
        var q = (text ?? "").Trim();
        if (q.Length == 0)
        {
            return "/";
        }

        var digits = NpiLike().IsMatch(q) ? new string(q.Where(char.IsAsciiDigit).ToArray()) : null;
        if (digits is { Length: 10 })
        {
            return "/provider/" + digits;
        }

        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var query = new Dictionary<string, string?> { ["nameMatch"] = "similar" };
        if (words.Any(w => OrganizationWords.Contains(w.Trim(',', '.', '&'))) || words.Length > 3)
        {
            query["orgName"] = q;
        }
        else if (words.Length == 1)
        {
            query["lastName"] = words[0];
        }
        else
        {
            query["firstName"] = string.Join(' ', words[..^1]);
            query["lastName"] = words[^1];
        }

        return QueryHelpers.AddQueryString("/", query);
    }

    // Ten digits, optionally written with spaces or dashes ("1234 567 893").
    [GeneratedRegex(@"^\d[\d\s-]{8,14}\d$")]
    private static partial Regex NpiLike();
}
