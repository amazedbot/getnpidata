using Npi.Core.Search;

namespace Npi.Loader.Projection;

/// <summary>
/// Whether two business names at the same address are the same business (record quality, docs/PRODUCT_PLAN.md step 3.1): an
/// organization's NPPES name against a Provider of Services facility or an Overture place. Names are compared by their
/// distinctive words (<see cref="CompanyNames.Key"/> without words every clinic shares, such as MEDICAL or CENTER): at least one
/// in common, and at least half of the shorter name's distinctive words. "SUNRISE FAMILY DENTAL PC" = "Sunrise Dental";
/// "SMITH CHIROPRACTIC PC" ≠ "Jones Chiropractic". A place name alone ("Brooklyn …") can still join two businesses at one address,
/// so a match is a reason to check, never proof.
/// </summary>
public static class BusinessNames
{
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "HEALTH", "HEALTHCARE", "MEDICAL", "MEDICINE", "CENTER", "CENTRE", "CENTERS", "CARE", "CLINIC", "CLINICS", "HOSPITAL", "HOSPITALS",
        "SERVICES", "SERVICE", "GROUP", "ASSOCIATES", "ASSOCIATION", "PHYSICIANS", "PHYSICIAN", "FAMILY", "PRACTICE", "DENTAL", "DENTISTRY",
        "PHARMACY", "HOME", "NURSING", "REHABILITATION", "REHAB", "THERAPY", "PHYSICAL", "URGENT", "OFFICE", "OFFICES", "INSTITUTE", "SYSTEM",
        "SYSTEMS", "NETWORK", "COMMUNITY", "PROFESSIONAL", "PROFESSIONALS", "SPECIALISTS", "SPECIALTY", "PARTNERS", "SOLUTIONS", "MANAGEMENT",
        "AMERICA", "AMERICAN", "NATIONAL", "UNITED", "REGIONAL", "COUNTY", "CITY", "NORTH", "SOUTH", "EAST", "WEST", "CENTRAL", "NEW", "LIVING",
        "SENIOR", "LAB", "LABS", "LABORATORY", "LABORATORIES", "IMAGING", "DIAGNOSTIC", "DIAGNOSTICS", "SURGERY", "SURGICAL", "PEDIATRICS",
        "PEDIATRIC", "CHIROPRACTIC", "OPTOMETRY", "EYE", "VISION", "WELLNESS", "BEHAVIORAL", "MENTAL", "COUNSELING", "HOSPICE", "AGENCY",
        "SUPPLY", "SUPPLIES", "EQUIPMENT", "DRUG", "DRUGS", "STORE", "DR", "DDS", "MD", "DMD", "OD", "DC", "DO", "PT", "INC", "PLLC",
    };

    /// <summary>The distinctive words of a name; empty when it has none.</summary>
    public static IReadOnlySet<string> Words(string? name) =>
        CompanyNames.Key(name) is { } key ? key.Split(' ').Where(w => !Generic.Contains(w)).ToHashSet(StringComparer.Ordinal) : new HashSet<string>();

    public static bool Same(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return false;
        }

        var shared = a.Count(b.Contains);
        return shared >= 1 && shared * 2 >= Math.Min(a.Count, b.Count);
    }

    public static bool Same(string? a, string? b) => Same(Words(a), Words(b));
}
