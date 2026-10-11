using System.Globalization;

namespace Npi.Core.Search;

/// <summary>One export column: its header and its value for a provider.</summary>
public sealed record ProviderColumn(string Header, Func<ProviderSummary, string?> Value);

/// <summary>
/// The export column sets (docs/PRODUCT_PLAN.md step 1.3): <c>summary</c> (the CSV's columns since Stage 4, CLAUDE.md §7.3, plus the
/// Stage 5.5 flags), <c>contact</c> (who and where, for outreach lists) and <c>compliance</c> (standing, for credentialing).
/// </summary>
public static class ProviderColumns
{
    public const string Summary = "summary";
    public const string Contact = "contact";
    public const string Compliance = "compliance";

    /// <summary>The set names with their labels, in the order the export panel lists them.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Sets =
    [
        (Summary, "Summary (22 columns)"),
        (Contact, "Contact: name, specialty, address, phone"),
        (Compliance, "Compliance: status and Medicare flags"),
    ];

    private static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Yes(bool flag) => flag ? "Yes" : "";

    private static readonly ProviderColumn Npi = new("NPI", r => r.Npi);
    private static readonly ProviderColumn EntityType = new("Entity Type", r => r.EntityTypeName);
    private static readonly ProviderColumn Name = new("Name", r => r.Name);
    private static readonly ProviderColumn Credential = new("Credential", r => r.Credential);
    private static readonly ProviderColumn Specialty = new("Primary Specialty", r => r.PrimarySpecialty);
    private static readonly ProviderColumn Address1 = new("Address 1", r => r.Address1);
    private static readonly ProviderColumn Address2 = new("Address 2", r => r.Address2);
    private static readonly ProviderColumn City = new("City", r => r.City);
    private static readonly ProviderColumn State = new("State", r => r.State);
    private static readonly ProviderColumn Zip = new("ZIP", r => r.Zip);
    private static readonly ProviderColumn County = new("County", r => r.County);
    private static readonly ProviderColumn Phone = new("Phone", r => r.Phone);
    private static readonly ProviderColumn Gender = new("Gender", r => r.Gender);
    private static readonly ProviderColumn Enumerated = new("Enumeration Date", r => Date(r.EnumerationDate));
    private static readonly ProviderColumn Updated = new("Last Update Date", r => Date(r.LastUpdateDate));
    private static readonly ProviderColumn Excluded = new("OIG Excluded", r => Yes(r.Flags.Excluded));
    private static readonly ProviderColumn OptOut = new("Medicare Opt-Out", r => Yes(r.Flags.OptedOutOfMedicare));
    private static readonly ProviderColumn OrderRefer = new("Medicare Order/Refer", r => Yes(r.Flags.CanOrderAndRefer));
    private static readonly ProviderColumn Assignment = new("Accepts Medicare Assignment", r => Yes(r.Flags.AcceptsMedicareAssignment));
    private static readonly ProviderColumn Telehealth = new("Telehealth", r => Yes(r.Flags.OffersTelehealth));
    private static readonly ProviderColumn Billed = new("Billed Medicare", r => Yes(r.Flags.BilledMedicare));
    private static readonly ProviderColumn Building = new("Practice Building", r => AddressTypes.Label(r.AddressType));

    public static readonly IReadOnlyList<ProviderColumn> SummaryColumns =
    [
        Npi, EntityType, Name, Credential, Specialty, Address1, Address2, City, State, Zip, County, Phone, Gender, Enumerated, Updated,
        Excluded, OptOut, OrderRefer, Assignment, Telehealth, Billed, Building,
    ];

    public static readonly IReadOnlyList<ProviderColumn> ContactColumns =
        [Npi, Name, Credential, Specialty, Address1, Address2, City, State, Zip, County, Phone, Building];

    public static readonly IReadOnlyList<ProviderColumn> ComplianceColumns =
        [Npi, EntityType, Name, Credential, Specialty, State, Enumerated, Updated, Excluded, OptOut, OrderRefer, Assignment, Billed];

    /// <summary>The columns of a set; unknown or missing names mean the summary.</summary>
    public static IReadOnlyList<ProviderColumn> For(string? set) => set?.ToLowerInvariant() switch
    {
        Contact => ContactColumns,
        Compliance => ComplianceColumns,
        _ => SummaryColumns,
    };
}
