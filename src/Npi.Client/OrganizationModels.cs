namespace Npi.Client;

/// <summary>
/// The organization an NPI is one of: its main NPI, its name and how many NPIs it has. An organization NPI alone with a certified
/// facility or a Medicare group has an organization page too (<see cref="Npis"/> = 1).
/// </summary>
public sealed class ProviderOrganization
{
    public string MainNpi { get; set; } = "";

    public string Name { get; set; } = "";

    public int Npis { get; set; }
}

/// <summary>
/// An organization's one page (GET /api/v1/organizations/{npi}): every NPI with its role, the main NPI's record, what the NPIs hold
/// together, and the data of all of them: each facility's record, the owners once, the clinicians in numbers, Medicare billing,
/// recorded changes, health systems, ACOs and 340B. An organization NPI alone with a certified facility or a Medicare group has one too.
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

    /// <summary>Every CCN CMS ties any of the NPIs to, the main facility first.</summary>
    public IReadOnlyList<string> Ccns { get; set; } = [];

    /// <summary>
    /// The facilities' own records (ratings, how they compare, Medicare volume, a nursing home's inspections …), in <see cref="Ccns"/>' order,
    /// at most 12; <see cref="NpiClient.GetFacilityAsync"/> gives any of them. Their clinician lists carry only the count.
    /// </summary>
    public IReadOnlyList<FacilityProfile> FacilityProfiles { get; set; } = [];

    /// <summary>The owners, managers and ownership changes of all the facilities, each owner once; null when CMS lists none.</summary>
    public OrganizationOwnership? Ownership { get; set; }

    /// <summary>How many clinicians the organization has, from which source; the list itself is the page's CSV.</summary>
    public OrganizationClinicianSummary? Clinicians { get; set; }

    /// <summary>Medicare billing of the organization's NPIs, largest first.</summary>
    public IReadOnlyList<OrganizationMedicareNpi> MedicareActivity { get; set; } = [];

    /// <summary>Medicaid billing and enrollment of its NPIs (T-MSIS): the newest data year, the last year billed, the states enrolled in.</summary>
    public IReadOnlyList<OrganizationMedicaidNpi> MedicaidActivity { get; set; } = [];

    /// <summary>The changes recorded for any of its NPIs, newest first (at most 200).</summary>
    public IReadOnlyList<OrganizationChange> Changes { get; set; } = [];

    /// <summary>The health systems (AHRQ Compendium) of its facilities and of those its Medicare groups bill for.</summary>
    public IReadOnlyList<HealthSystemLink> HealthSystems { get; set; } = [];

    /// <summary>The Medicare ACOs any of its NPIs takes part in (name matches).</summary>
    public IReadOnlyList<AcoMembership> Acos { get; set; } = [];

    /// <summary>The 340B registrations of its facilities (HRSA OPAIS), participating first.</summary>
    public IReadOnlyList<Covered340BEntity> Covered340B { get; set; } = [];
}

/// <summary>Who owns and runs an organization's facilities: each owner once per role group with its facilities, and every ownership change.</summary>
public sealed class OrganizationOwnership
{
    /// <summary>Owners first (largest stake first), then control and management, lenders, officers and directors.</summary>
    public IReadOnlyList<OrganizationOwner> Owners { get; set; } = [];

    /// <summary>Changes of ownership of any of the facilities, newest first.</summary>
    public IReadOnlyList<OwnershipChange> Changes { get; set; } = [];

    /// <summary>The facilities' Medicare enrollments the owners are listed for.</summary>
    public IReadOnlyList<FacilityEnrollment> Enrollments { get; set; } = [];

    public DateTime? OwnersAsOf { get; set; }

    public DateTime? ChangesAsOf { get; set; }
}

/// <summary>An owner, manager or officer of an organization's facilities, in one role group, with each facility it holds such a role in.</summary>
public sealed class OrganizationOwner
{
    /// <summary>An organization's CMS associate ID (see <see cref="NpiClient.GetOwnerAsync"/>); null for a person, given by name, title and role only.</summary>
    public string? OwnerId { get; set; }

    public bool IsOrganization { get; set; }

    public string Name { get; set; } = "";

    public string? DoingBusinessAs { get; set; }

    public string? Title { get; set; }

    /// <summary>owner, control, lender or officer.</summary>
    public string RoleGroup { get; set; } = "";

    public IReadOnlyList<string> Roles { get; set; } = [];

    /// <summary>The largest stake it holds in any of the facilities.</summary>
    public double? Percentage { get; set; }

    public DateTime? Since { get; set; }

    public IReadOnlyList<string> Kinds { get; set; } = [];

    public string? City { get; set; }

    public string? State { get; set; }

    public IReadOnlyList<OrganizationOwnerFacility> Facilities { get; set; } = [];
}

/// <summary>An owner's roles at one of the organization's facilities.</summary>
public sealed class OrganizationOwnerFacility
{
    public string Ccn { get; set; } = "";

    public string Facility { get; set; } = "";

    public string Roles { get; set; } = "";

    public double? Percentage { get; set; }

    public DateTime? Since { get; set; }
}

/// <summary>A facility's Medicare enrollment, as CMS's owners file lists it.</summary>
public sealed class FacilityEnrollment
{
    public string Ccn { get; set; } = "";

    public string Facility { get; set; } = "";

    public string? EnrollmentId { get; set; }

    public string? LegalName { get; set; }
}

/// <summary>An organization's clinicians in numbers: everyone once, how many each source lists, how many more than one does, by specialty.</summary>
public sealed class OrganizationClinicianSummary
{
    public int Total { get; set; }

    /// <summary>Clinicians who list one of its facilities in Care Compare.</summary>
    public int FacilityAffiliations { get; set; }

    /// <summary>Clinicians who bill Medicare through one of its Medicare groups (reassignment).</summary>
    public int Reassignments { get; set; }

    /// <summary>Clinicians who list one of its Medicare groups in Care Compare.</summary>
    public int CareCompareGroups { get; set; }

    /// <summary>Clinicians tied through more than one of the three.</summary>
    public int InSeveral { get; set; }

    public IReadOnlyList<SpecialtyCount> BySpecialty { get; set; } = [];
}

/// <summary>One organization NPI's Medicare billing: Part B and Part D in the newest data year, and the last year it billed in the CMS data.</summary>
public sealed class OrganizationMedicareNpi
{
    public string Npi { get; set; } = "";

    public string Name { get; set; } = "";

    public string Role { get; set; } = "";

    public int? PartBYear { get; set; }

    public int? Beneficiaries { get; set; }

    public double? Services { get; set; }

    /// <summary>Medicare's Part B payment in dollars.</summary>
    public double? Payment { get; set; }

    public int? PartDYear { get; set; }

    public int? PartDClaims { get; set; }

    public double? DrugCost { get; set; }

    public int? LastBilledYear { get; set; }
}

/// <summary>One of an organization's NPIs in the Medicaid data (T-MSIS).</summary>
public sealed class OrganizationMedicaidNpi
{
    public string Npi { get; set; } = "";

    public string Name { get; set; } = "";

    public string Role { get; set; } = "";

    /// <summary>The newest data year, when the NPI billed Medicaid in it (else null, as are the two counts).</summary>
    public int? Year { get; set; }

    public long? ClaimLines { get; set; }

    /// <summary>What Medicaid paid in dollars in <see cref="Year"/>.</summary>
    public double? Paid { get; set; }

    public int? LastBilledYear { get; set; }

    /// <summary>The states whose Medicaid programs it was enrolled in, 2018 on.</summary>
    public IReadOnlyList<string> EnrolledStates { get; set; } = [];
}

/// <summary>A change recorded for one of an organization's NPIs.</summary>
public sealed class OrganizationChange
{
    public string Npi { get; set; } = "";

    public DateTime RecordedOn { get; set; }

    public string Type { get; set; } = "";

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }
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
