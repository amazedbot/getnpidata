namespace Npi.Client;

/// <summary>Who owns and runs a facility, and its ownership changes (/api/v1/facilities/{ccn}/ownership).</summary>
public sealed class FacilityOwnership
{
    public string Ccn { get; set; } = "";

    /// <summary>The facility's Medicare enrollment the owners are listed for.</summary>
    public string? EnrollmentId { get; set; }

    /// <summary>The enrolled organization's legal name.</summary>
    public string? LegalName { get; set; }

    /// <summary>The enrolled organization's CMS associate ID (PAC ID).</summary>
    public string? AssociateId { get; set; }

    /// <summary>The end of the All Owners release's period.</summary>
    public DateTime? OwnersAsOf { get; set; }

    /// <summary>Owners first (largest stake first), then control and management, lenders, officers and directors.</summary>
    public IReadOnlyList<FacilityOwner> Owners { get; set; } = [];

    public DateTime? ChangesAsOf { get; set; }

    /// <summary>Changes of ownership since 2016 (hospitals and nursing homes), newest first.</summary>
    public IReadOnlyList<OwnershipChange> Changes { get; set; } = [];
}

/// <summary>One owner, managing party, officer or director of a facility, in one role.</summary>
public sealed class FacilityOwner
{
    /// <summary>The owner's CMS associate ID (ten digits), the same across every facility; pass it to <see cref="NpiClient.GetOwnerAsync"/>.</summary>
    public string OwnerId { get; set; } = "";

    public bool IsOrganization { get; set; }

    public string Name { get; set; } = "";

    public string? DoingBusinessAs { get; set; }

    /// <summary>A person's title ("Administrator", "Vice President").</summary>
    public string? Title { get; set; }

    /// <summary>CMS's role code (34 = 5% or more direct ownership, 43 = operational or managerial control …).</summary>
    public string RoleCode { get; set; } = "";

    public string Role { get; set; } = "";

    /// <summary>owner, control, lender or officer.</summary>
    public string RoleGroup { get; set; } = "";

    public double? Percentage { get; set; }

    public DateTime? Since { get; set; }

    /// <summary>Owner types CMS marks: private_equity, reit, holding_company, investment_firm, chain_home_office ….</summary>
    public IReadOnlyList<string> Kinds { get; set; } = [];

    public string? City { get; set; }

    public string? State { get; set; }
}

/// <summary>A change of ownership: the buyer took over the seller's Medicare enrollment.</summary>
public sealed class OwnershipChange
{
    public DateTime? EffectiveDate { get; set; }

    /// <summary>"Change of ownership" or "Acquisition or merger".</summary>
    public string Type { get; set; } = "";

    /// <summary>hospital or nursing_home.</summary>
    public string Kind { get; set; } = "";

    public string? BuyerName { get; set; }

    public string? BuyerDoingBusinessAs { get; set; }

    public string? BuyerId { get; set; }

    public string? BuyerCcn { get; set; }

    public string? BuyerNpi { get; set; }

    public string? SellerName { get; set; }

    public string? SellerDoingBusinessAs { get; set; }

    public string? SellerId { get; set; }

    public string? SellerCcn { get; set; }

    public string? SellerNpi { get; set; }

    /// <summary>The seller's owners and managing parties before the change.</summary>
    public IReadOnlyList<PreviousOwner> PreviousOwners { get; set; } = [];
}

/// <summary>An owner of the seller before a change of ownership.</summary>
public sealed class PreviousOwner
{
    public string OwnerId { get; set; } = "";

    public bool IsOrganization { get; set; }

    public string Name { get; set; } = "";

    public string RoleCode { get; set; } = "";

    public string Role { get; set; } = "";

    public string RoleGroup { get; set; } = "";

    public double? Percentage { get; set; }
}

/// <summary>A page of facility owners (/api/v1/owners).</summary>
public sealed class OwnerPage
{
    public IReadOnlyList<OwnerSummary> Items { get; set; } = [];

    public int Page { get; set; }

    public int PageSize { get; set; }

    public int TotalCount { get; set; }
}

/// <summary>A facility owner: <see cref="Facilities"/> = facilities listed for in any role, <see cref="Owned"/> = with an ownership interest.</summary>
public sealed class OwnerSummary
{
    /// <summary>CMS associate ID (ten digits).</summary>
    public string Id { get; set; } = "";

    public bool IsOrganization { get; set; }

    public string Name { get; set; } = "";

    public string? DoingBusinessAs { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public IReadOnlyList<string> Kinds { get; set; } = [];

    public int Facilities { get; set; }

    public int Owned { get; set; }
}

/// <summary>One owner (/api/v1/owners/{id}).</summary>
public sealed class OwnerDetail
{
    public OwnerSummary Owner { get; set; } = new();

    public DateTime? AsOf { get; set; }

    public IReadOnlyList<OwnerFacility> Facilities { get; set; } = [];

    /// <summary>The NPIs an organization owner is enrolled in Medicare under (by its associate ID).</summary>
    public IReadOnlyList<OrganizationNpi> Npis { get; set; } = [];

    /// <summary>The facilities an organization owner is itself (by its associate ID).</summary>
    public IReadOnlyList<OwnerFacility> EnrolledAs { get; set; } = [];

    /// <summary>Its Medicare group (PAC ID) when it has a group page.</summary>
    public string? GroupPacId { get; set; }

    public IReadOnlyList<OwnershipChange> Changes { get; set; } = [];
}

/// <summary>A facility on an owner's page, with the owner's roles in it.</summary>
public sealed class OwnerFacility
{
    public string? Ccn { get; set; }

    public string EnrollmentId { get; set; } = "";

    /// <summary>hospital, nursing_home, home_health, hospice, fqhc, rhc ….</summary>
    public string Type { get; set; } = "";

    public string Name { get; set; } = "";

    public string? City { get; set; }

    public string? State { get; set; }

    /// <summary>False when its Medicare certification ended; null when not in the facility list.</summary>
    public bool? Active { get; set; }

    public IReadOnlyList<OwnerRole> Roles { get; set; } = [];
}

/// <summary>An owner's role in one facility.</summary>
public sealed class OwnerRole
{
    public string RoleCode { get; set; } = "";

    public string Role { get; set; } = "";

    public string RoleGroup { get; set; } = "";

    public double? Percentage { get; set; }

    public DateTime? Since { get; set; }
}

/// <summary>An organization NPI (name in the registry, plain specialty, primary practice city).</summary>
public sealed class OrganizationNpi
{
    public string Npi { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Specialty { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }
}

/// <summary>A nursing home's inspections and penalties (/api/v1/facilities/{ccn}/inspections).</summary>
public sealed class NursingHomeInspections
{
    public string Ccn { get; set; } = "";

    /// <summary>When CMS last updated the files.</summary>
    public DateTime? UpdatedOn { get; set; }

    /// <summary>Deficiencies by kind (health, fire), cycle (1 = latest) and scope and severity (A–L).</summary>
    public IReadOnlyList<InspectionCount> Counts { get; set; } = [];

    public IReadOnlyList<InspectionSurvey> Surveys { get; set; } = [];

    /// <summary>The ten most serious and the ten newest citations.</summary>
    public IReadOnlyList<InspectionCitation> Citations { get; set; } = [];

    public IReadOnlyList<NursingHomePenalty> Penalties { get; set; } = [];

    public double FinesTotal { get; set; }

    public int PaymentDenials { get; set; }
}

public sealed class InspectionCount
{
    /// <summary>health or fire.</summary>
    public string Kind { get; set; } = "";

    public int Cycle { get; set; }

    /// <summary>A–L: A–C potential for minimal harm, D–F more than minimal, G–I actual harm, J–L immediate jeopardy.</summary>
    public string ScopeSeverity { get; set; } = "";

    public int Deficiencies { get; set; }

    public int Complaint { get; set; }
}

public sealed class InspectionSurvey
{
    public string Kind { get; set; } = "";

    public DateTime Date { get; set; }

    public int Cycle { get; set; }

    public int Deficiencies { get; set; }

    public int Complaint { get; set; }

    public string? MostSevere { get; set; }
}

public sealed class InspectionCitation
{
    public string Kind { get; set; } = "";

    public DateTime SurveyDate { get; set; }

    public int Cycle { get; set; }

    /// <summary>F0689, K0345 ….</summary>
    public string Tag { get; set; } = "";

    public string ScopeSeverity { get; set; } = "";

    public string? Category { get; set; }

    public string? Description { get; set; }

    public string? Status { get; set; }

    public DateTime? CorrectionDate { get; set; }

    public bool Complaint { get; set; }

    public bool InfectionControl { get; set; }
}

/// <summary>A fine or a denial of Medicare payment for new admissions.</summary>
public sealed class NursingHomePenalty
{
    public DateTime? Date { get; set; }

    /// <summary>"Fine" or "Payment Denial".</summary>
    public string Type { get; set; } = "";

    public double? Fine { get; set; }

    public DateTime? PaymentDenialStart { get; set; }

    public int? PaymentDenialDays { get; set; }
}
