namespace Npi.Core.Search;

/// <summary>
/// The kinds of change in <c>provider_change</c> (CLAUDE.md §7 Stage 5.5 item 11, widened in docs/PRODUCT_PLAN.md Phase 3
/// step 3.2) and how pages name them.
/// </summary>
public static class ChangeTypes
{
    /// <summary>Kinds shown on a provider's profile, with their labels, in this order of the label list.</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["name"] = "Name",
        ["credential"] = "Credential",
        ["specialty"] = "Primary specialty",
        ["address"] = "Primary practice address",
        ["phone"] = "Phone",
        ["location_added"] = "Practice location added",
        ["location_removed"] = "Practice location dropped",
        ["taxonomy_added"] = "Specialty added",
        ["taxonomy_removed"] = "Specialty dropped",
        ["other_name_added"] = "Other name added",
        ["other_name_removed"] = "Other name dropped",
        ["license_added"] = "State license listed",
        ["license_removed"] = "State license no longer listed",
        ["license_status"] = "State license status",
        ["license_renewed"] = "State license renewed",
        ["board_action"] = "Board action",
        ["excluded"] = "Added to the OIG exclusion list",
        ["exclusion_ended"] = "Off the OIG exclusion list",
        ["opted_out"] = "Opted out of Medicare",
        ["opt_out_renewed"] = "Medicare opt-out renewed",
        ["opt_out_ended"] = "Medicare opt-out ended",
    };

    /// <summary>Kinds whose values are taxonomy codes, shown by name.</summary>
    public static readonly IReadOnlySet<string> Taxonomy = new HashSet<string>(StringComparer.Ordinal) { "specialty", "taxonomy_added", "taxonomy_removed" };

    /// <summary>The label of a kind; an unknown kind is shown as stored.</summary>
    public static string Label(string type) => Labels.TryGetValue(type, out var label) ? label : type;

    /// <summary>The profile's kinds as an SQL list ('name', 'credential', …): every kind with a label (not added/removed/deactivated, which are for feeds).</summary>
    public static string ProfileSqlList { get; } = string.Join(", ", Labels.Keys.Select(k => $"'{k}'"));

    /// <summary>The taxonomy kinds as an SQL list.</summary>
    public static string TaxonomySqlList { get; } = string.Join(", ", Taxonomy.Select(k => $"'{k}'"));
}
