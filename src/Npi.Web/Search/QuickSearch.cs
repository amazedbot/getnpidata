using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Npi.Web.Search;

/// <summary>
/// The search box in the site header (docs/PRODUCT_PLAN.md steps 1.2–1.3): an NPI opens its profile; any other text goes to the
/// search page as <c>q</c>, which reads it as a specialty, a place and/or a name (<see cref="Npi.Core.Search.QueryInterpreter"/>).
/// </summary>
public static partial class QuickSearch
{
    /// <summary>The URL the header search box sends <paramref name="text"/> to.</summary>
    public static string Target(string? text)
    {
        var q = (text ?? "").Trim();
        if (q.Length == 0)
        {
            return "/";
        }

        if (NpiLike().IsMatch(q) && q.Count(char.IsAsciiDigit) == 10)
        {
            return "/provider/" + new string(q.Where(char.IsAsciiDigit).ToArray());
        }

        return QueryHelpers.AddQueryString("/search", "q", q);
    }

    // Ten digits, optionally written with spaces or dashes ("1234 567 893").
    [GeneratedRegex(@"^\d[\d\s-]{8,14}\d$")]
    private static partial Regex NpiLike();
}
