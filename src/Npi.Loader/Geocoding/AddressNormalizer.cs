using System.Text.RegularExpressions;

namespace Npi.Loader.Geocoding;

/// <summary>
/// Normalizes US street addresses so NPPES practice addresses compare equal to address-point and place data
/// (CLAUDE.md §7 Stage 5.5 item 10): upper case, punctuation removed, USPS-style abbreviations for street types,
/// directions and ordinals, and the unit (suite, floor, room …) dropped, because points are per building.
/// </summary>
public static partial class AddressNormalizer
{
    private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        ["STREET"] = "ST", ["STR"] = "ST", ["AVENUE"] = "AVE", ["AV"] = "AVE", ["ROAD"] = "RD", ["DRIVE"] = "DR", ["LANE"] = "LN",
        ["BOULEVARD"] = "BLVD", ["HIGHWAY"] = "HWY", ["PARKWAY"] = "PKWY", ["TURNPIKE"] = "TPKE", ["PLACE"] = "PL", ["COURT"] = "CT",
        ["CIRCLE"] = "CIR", ["TERRACE"] = "TER", ["EXPRESSWAY"] = "EXPY", ["PLAZA"] = "PLZ", ["SQUARE"] = "SQ", ["TRAIL"] = "TRL",
        ["CENTER"] = "CTR", ["CENTRE"] = "CTR", ["ROUTE"] = "RTE", ["MOUNT"] = "MT", ["SAINT"] = "ST", ["NORTH"] = "N", ["SOUTH"] = "S",
        ["EAST"] = "E", ["WEST"] = "W", ["NORTHEAST"] = "NE", ["NORTHWEST"] = "NW", ["SOUTHEAST"] = "SE", ["SOUTHWEST"] = "SW",
        ["FIRST"] = "1ST", ["SECOND"] = "2ND", ["THIRD"] = "3RD", ["FOURTH"] = "4TH", ["FIFTH"] = "5TH", ["SIXTH"] = "6TH",
        ["SEVENTH"] = "7TH", ["EIGHTH"] = "8TH", ["NINTH"] = "9TH", ["TENTH"] = "10TH", ["COUNTY"] = "CO", ["MEMORIAL"] = "MEML",
        ["JUNCTION"] = "JCT", ["ALLEY"] = "ALY", ["EXTENSION"] = "EXT", ["HEIGHTS"] = "HTS", ["POINT"] = "PT", ["VALLEY"] = "VLY",
        ["ISLAND"] = "IS", ["LANDING"] = "LNDG", ["CROSSING"] = "XING", ["FREEWAY"] = "FWY", ["CAUSEWAY"] = "CSWY",
    };

    // Name-only addresses ("STONY BROOK UNIVERSITY HOSPITAL") are matched to place names by these word forms.
    private static readonly Dictionary<string, string[]> NameWords = new(StringComparer.Ordinal)
    {
        ["CTR"] = ["CENTER"], ["CNTR"] = ["CENTER"], ["HOSP"] = ["HOSPITAL"], ["MED"] = ["MEDICAL"], ["UNIV"] = ["UNIVERSITY"],
        ["DEPT"] = ["DEPARTMENT"], ["HLTH"] = ["HEALTH"], ["MEML"] = ["MEMORIAL"], ["ST"] = ["SAINT"], ["MT"] = ["MOUNT"],
        ["SUNY"] = ["STATE", "UNIVERSITY", "NEW", "YORK"], ["HSC"] = ["HEALTH", "SCIENCES", "CENTER"], ["VA"] = ["VETERANS"],
        ["SCIENCE"] = ["SCIENCES"],
    };

    private static readonly HashSet<string> NameStopWords = new(StringComparer.Ordinal)
    {
        "THE", "OF", "AT", "AND", "FOR", "IN", "ON", "LEVEL", "FLOOR", "FL", "SUITE", "STE", "ROOM", "RM", "BLDG", "BUILDING", "UNIT",
        "DEPARTMENT", "INC", "LLC", "PC", "PLLC",
    };

    /// <summary>"101 Nicolls Road, Suite 2" → ("101", "NICOLLS RD"); null when it doesn't start with a house number.</summary>
    public static (string Number, string Street)? Split(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var m = HouseNumber().Match(NonAddressCharacters().Replace(address, " ").Trim().ToUpperInvariant());
        if (!m.Success)
        {
            return null;
        }

        var street = Street(m.Groups["street"].Value);
        return street.Length == 0 ? null : (m.Groups["number"].Value, street);
    }

    /// <summary>The street part, normalized: "N. Country Road Ste 4" → "N COUNTRY RD".</summary>
    public static string Street(string street)
    {
        var s = NonAlphanumerics().Replace(street.ToUpperInvariant().Replace("#", " STE ", StringComparison.Ordinal), " ");
        s = Unit().Replace(s, "");
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => Words.GetValueOrDefault(w, w)));
    }

    /// <summary>The distinguishing words of a place or building name, for name matching ("Univ. Hospital, L4" → UNIVERSITY, HOSPITAL).</summary>
    public static IReadOnlySet<string> NameTokens(string? name)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(name))
        {
            return tokens;
        }

        foreach (var word in NonAlphanumerics().Replace(name.ToUpperInvariant(), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var w in NameWords.GetValueOrDefault(word, [word]))
            {
                // Skip filler words, numbers and unit codes like L4, 2B, W5510.
                if (!NameStopWords.Contains(w) && w.Length > 1 && !w.Any(char.IsAsciiDigit))
                {
                    tokens.Add(w);
                }
            }
        }

        return tokens;
    }

    // Queens-style "104-23" is one house number, kept whole (address data writes it that way).
    [GeneratedRegex(@"^(?<number>\d+[A-Z]?(?:-\d+[A-Z]?)?)\s+(?<street>.+)$")]
    private static partial Regex HouseNumber();

    [GeneratedRegex(@"[^A-Za-z0-9 #-]+")]
    private static partial Regex NonAddressCharacters();

    [GeneratedRegex(@"[^A-Z0-9]+")]
    private static partial Regex NonAlphanumerics();

    [GeneratedRegex(@"\b(STE|SUITE|APT|UNIT|RM|ROOM|FL|FLR|FLOOR|BLDG|BUILDING|DEPT|LEVEL|LOWR|UPPR|LBBY|OFC|OFFICE|SPC|BSMT|PH)\b.*$")]
    private static partial Regex Unit();
}
