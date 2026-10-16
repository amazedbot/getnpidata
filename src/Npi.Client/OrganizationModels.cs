namespace Npi.Client;

/// <summary>The organization an NPI is one of: its main NPI, its name and how many NPIs it has.</summary>
public sealed class ProviderOrganization
{
    public string MainNpi { get; set; } = "";

    public string Name { get; set; } = "";

    public int Npis { get; set; }
}

/// <summary>
/// An organization with several NPIs (GET /api/v1/organizations/{npi}): every NPI with its role, the main NPI's record and what the
/// NPIs hold together.
/// </summary>
public sealed class OrganizationProfile
{
    public string MainNpi { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Every NPI, the main one first.</summary>
    public IReadOnlyList<OrganizationMember> Npis { get; set; } = [];

    /// <summary>The main NPI's own record.</summary>
    public ProviderDetail? Main { get; set; }

    /// <summary>The certified facilities CMS ties any of the NPIs to.</summary>
    public IReadOnlyList<CertifiedFacility> Facilities { get; set; } = [];

    /// <summary>The Medicare groups any of the NPIs is enrolled under, largest first.</summary>
    public IReadOnlyList<OrganizationGroup> Groups { get; set; } = [];

    /// <summary>The distinct practice locations of all the NPIs.</summary>
    public IReadOnlyList<ProviderLocation> Locations { get; set; } = [];

    /// <summary>How the NPIs were grouped and how the main NPI was chosen.</summary>
    public string Grouping { get; set; } = "";
}

/// <summary>One NPI of an organization.</summary>
public sealed class OrganizationMember
{
    public string Npi { get; set; } = "";

    /// <summary>The legal business name as registered.</summary>
    public string Name { get; set; } = "";

    /// <summary>"Main NPI", "Psychiatric unit (CCN 33S014)", "Possible duplicate registration", "Pharmacy" ….</summary>
    public string Role { get; set; } = "";

    public string? Specialty { get; set; }

    public string? Address { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Zip { get; set; }

    public DateTime? LastUpdateDate { get; set; }

    public bool IsMain { get; set; }

    /// <summary>The CCN CMS ties it to, when there is one.</summary>
    public string? Ccn { get; set; }

    /// <summary>The record-quality label: Current, Check or Stale; null when not labelled.</summary>
    public string? Quality { get; set; }

    /// <summary>What tied it to the others, e.g. "same name and address, same Medicare enrollment (PAC ID)".</summary>
    public string JoinedBy { get; set; } = "";
}

/// <summary>A Medicare group (PAC ID) an organization is enrolled under, with its active clinicians.</summary>
public sealed class OrganizationGroup
{
    public string PacId { get; set; } = "";

    public string Name { get; set; } = "";

    public int Clinicians { get; set; }
}
