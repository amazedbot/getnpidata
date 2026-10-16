using System.Globalization;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>One owner in a group of the facility page's "Who owns it": every role it holds in that group in one line.</summary>
public sealed record OwnerLine(string? OwnerId, bool IsOrganization, string Name, string? DoingBusinessAs, string? Title, string Roles, double? Percentage,
    DateOnly? Since, IReadOnlyList<string> Kinds, string? Place)
{
    /// <summary>On an organization's page (docs/PRODUCT_PLAN.md step 3.9): the facilities it holds a role of this group in, with the role where it differs.</summary>
    public IReadOnlyList<OrganizationOwnerFacility> Facilities { get; init; } = [];
}

/// <summary>
/// The facility page's ownership sections: what CMS lists (null: nothing) and the facility's type (changes exist for hospitals and
/// nursing homes only). On an organization's page (step 3.9) <see cref="Combined"/> holds the owners of all its facilities, each once.
/// </summary>
public sealed record FacilityOwnershipSection(FacilityOwnership? Ownership, string FacilityType)
{
    /// <summary>The owners of all an organization's facilities, each once with its facilities; null on a facility page.</summary>
    public OrganizationOwnership? Combined { get; init; }

    /// <summary>The CCNs whose cards are on the organization's page (#fac-{ccn}); the others link to their own page.</summary>
    public IReadOnlySet<string> Anchored { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the owners table names each owner's facilities (an organization with owners at more than one facility).</summary>
    public bool ShowFacilities => Combined is { } c && c.Owners.SelectMany(o => o.Facilities).Select(f => f.Ccn).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

    /// <summary>CMS publishes change-of-ownership files for hospitals and skilled nursing facilities only.</summary>
    public bool ChangesPublished => FacilityType is FacilityTypes.Hospital or FacilityTypes.NursingHome;

    /// <summary>CMS publishes All Owners files for these six types only (not dialysis facilities, surgery centers …).</summary>
    public bool OwnersPublished => ChangesPublished || FacilityType is FacilityTypes.HomeHealth or FacilityTypes.Hospice or FacilityTypes.Fqhc or FacilityTypes.Rhc;
}

/// <summary>The ownership and inspection sections' wording, shared by the facility and owner pages (docs/PRODUCT_PLAN.md step 3.6 part 2).</summary>
public static class OwnershipView
{
    /// <summary>The owners by group (owners, control, lenders, officers), each owner once per group with its roles joined.</summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<OwnerLine> Lines)> Groups(IEnumerable<FacilityOwner> owners) =>
        [.. owners
            .GroupBy(o => o.RoleGroup)
            .OrderBy(g => OwnerRoles.Order(g.Key))
            .Select(g => (g.Key, (IReadOnlyList<OwnerLine>)[.. g
                // A person has no ID here (owner, 2026-10-15): their roles in a group are joined by name.
                .GroupBy(o => o.OwnerId ?? "person:" + o.Name, StringComparer.Ordinal)
                .Select(o =>
                {
                    var first = o.First();
                    return new OwnerLine(first.OwnerId, first.IsOrganization, DisplayName(first.Name), first.DoingBusinessAs is { } dba ? DisplayName(dba) : null,
                        o.Select(x => x.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) is { } title ? CityCatalog.TitleCase(title) : null,
                        string.Join("; ", o.Select(x => x.Role).Distinct(StringComparer.Ordinal)), o.Max(x => x.Percentage is > 0 ? x.Percentage : null),
                        o.Min(x => x.Since), [.. first.Kinds.Select(k => OwnerKinds.Label(k) ?? k)], Place(first.City, first.State));
                })
                .OrderByDescending(l => l.Percentage ?? -1).ThenByDescending(l => l.IsOrganization).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)]))];

    /// <summary>An organization's owners (step 3.9) by group, each once with the facilities it holds a role of that group in.</summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<OwnerLine> Lines)> Groups(OrganizationOwnership ownership) =>
        [.. ownership.Owners
            .GroupBy(o => o.RoleGroup)
            .OrderBy(g => OwnerRoles.Order(g.Key))
            .Select(g => (g.Key, (IReadOnlyList<OwnerLine>)[.. g.Select(o => new OwnerLine(o.OwnerId, o.IsOrganization, DisplayName(o.Name),
                o.DoingBusinessAs is { } dba ? DisplayName(dba) : null, o.Title is { } title ? CityCatalog.TitleCase(title) : null,
                string.Join("; ", o.Roles), o.Percentage, o.Since, [.. o.Kinds.Select(k => OwnerKinds.Label(k) ?? k)], Place(o.City, o.State))
            {
                Facilities = o.Facilities,
            })]))];

    // Kept in capitals when a name is title-cased: legal forms, abbreviations and roman numerals.
    private static readonly HashSet<string> Capitals = new(StringComparer.OrdinalIgnoreCase)
    {
        "LLC", "LP", "LLP", "PLLC", "PC", "PA", "NA", "USA", "US", "REIT", "II", "III", "IV", "VI", "VII", "VIII", "IX", "XI", "HCA", "CEO", "CFO", "COO",
        "CMO", "CNO", "RN", "MD", "DDS", "NP", "LPN", "SNF", "HHA", "QA", "HR",
    };

    /// <summary>"Hickory Creek Healthcare Foundation Inc", "Sample Holdings LLC" from CMS's capitals.</summary>
    public static string DisplayName(string name) =>
        string.Join(' ', CityCatalog.TitleCase(name).Split(' ').Select(w => Capitals.Contains(w.TrimEnd(',', '.', ')')) ? w.ToUpperInvariant() : w));

    public static string? Place(string? city, string? state) =>
        city is null ? state : state is null ? CityCatalog.TitleCase(city) : $"{CityCatalog.TitleCase(city)}, {state}";

    public static string Share(double? percentage) =>
        percentage is { } p and > 0 ? p.ToString(p % 1 == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + "%" : "";

    public static string Date(DateOnly? date, string format = "MMM d, yyyy") => date?.ToString(format, CultureInfo.InvariantCulture) ?? "";

    /// <summary>The kinds an owner list shows in the type filter.</summary>
    public static IEnumerable<(string Code, string Label)> KindOptions => OwnerKinds.All;

    public static string TypeName(string type) => FacilityTypes.Find(type)?.Singular ?? "Facility";

    /// <summary>The scope and severity grid of one kind (health or fire safety), every cycle: [level 4..1][isolated, pattern, widespread].</summary>
    public static int[,] Grid(NursingHomeInspections inspections, string kind)
    {
        var grid = new int[4, 3];
        foreach (var c in inspections.Counts.Where(c => c.Kind == kind))
        {
            var level = InspectionSeverity.Level(c.ScopeSeverity);
            if (level > 0)
            {
                grid[4 - level, InspectionSeverity.Scope(c.ScopeSeverity)] += c.Deficiencies;
            }
        }

        return grid;
    }

    /// <summary>The letter of a grid cell: level 4..1 (row 0..3) and scope 0..2.</summary>
    public static string Letter(int row, int scope) => ((char)('A' + (3 - row) * 3 + scope)).ToString();

    /// <summary>Deficiencies of a kind in a cycle, and how many of them were actual harm or immediate jeopardy (G–L).</summary>
    public static (int Total, int Harm, int Complaint) Cycle(NursingHomeInspections inspections, string kind, int cycle)
    {
        var rows = inspections.Counts.Where(c => c.Kind == kind && c.Cycle == cycle).ToList();
        return (rows.Sum(c => c.Deficiencies), rows.Where(c => InspectionSeverity.IsHarm(c.ScopeSeverity)).Sum(c => c.Deficiencies), rows.Sum(c => c.Complaint));
    }
}
