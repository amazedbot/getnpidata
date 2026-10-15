namespace Npi.Client;

/// <summary>A page of health systems (/api/v1/systems).</summary>
public sealed class HealthSystemPage
{
    public IReadOnlyList<HealthSystemSummary> Items { get; set; } = [];

    public int Page { get; set; }

    public int PageSize { get; set; }

    public int TotalCount { get; set; }
}

/// <summary>A health system in AHRQ's Compendium of U.S. Health Systems, with AHRQ's counts for <see cref="DataYear"/>.</summary>
public sealed class HealthSystemSummary
{
    /// <summary>AHRQ's system ID (e.g. HSI00000010).</summary>
    public string SystemId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Headquarters city and state.</summary>
    public string? City { get; set; }

    public string? State { get; set; }

    public int DataYear { get; set; }

    public int? Physicians { get; set; }

    public int? PrimaryCarePhysicians { get; set; }

    public int? NursePractitioners { get; set; }

    public int? PhysicianAssistants { get; set; }

    public int? GroupPractices { get; set; }

    public int? Hospitals { get; set; }

    public int? NursingHomes { get; set; }

    public int? HomeHealthAgencies { get; set; }

    public int? Beds { get; set; }

    public int? Discharges { get; set; }

    public bool? MajorTeaching { get; set; }

    public bool? OffersInsurance { get; set; }

    public bool? MedicareAdvantage { get; set; }

    /// <summary>Facilities AHRQ's linkage files place in the system (by CCN).</summary>
    public int LinkedFacilities { get; set; }
}

/// <summary>A health system with its facilities and Medicare groups (/api/v1/systems/{id}).</summary>
public sealed class HealthSystemDetail
{
    public HealthSystemSummary System { get; set; } = new();

    /// <summary>The states its facilities are in.</summary>
    public IReadOnlyList<string> States { get; set; } = [];

    public IReadOnlyList<HealthSystemFacility> Facilities { get; set; } = [];

    /// <summary>Medicare groups whose PAC ID enrolls an NPI that bills for one of the facilities.</summary>
    public IReadOnlyList<HealthSystemGroup> Groups { get; set; } = [];
}

/// <summary>A facility of a health system, by CCN.</summary>
public sealed class HealthSystemFacility
{
    public string Ccn { get; set; } = "";

    /// <summary>hospital, nursing_home, home_health … as in <see cref="FacilitySummary.Type"/>.</summary>
    public string Type { get; set; } = "";

    public string Name { get; set; } = "";

    public string? City { get; set; }

    public string? State { get; set; }

    /// <summary>Certified now (null when the facility list doesn't have the CCN).</summary>
    public bool? Active { get; set; }

    public double? Rating { get; set; }
}

/// <summary>A Medicare group of a health system.</summary>
public sealed class HealthSystemGroup
{
    public string PacId { get; set; } = "";

    public string Name { get; set; } = "";

    public string? State { get; set; }

    public int Clinicians { get; set; }
}

/// <summary>A page of Medicare Shared Savings Program ACOs (/api/v1/acos).</summary>
public sealed class AcoPage
{
    public IReadOnlyList<AcoSummary> Items { get; set; } = [];

    public int Page { get; set; }

    public int PageSize { get; set; }

    public int TotalCount { get; set; }
}

/// <summary>A Medicare Shared Savings Program ACO in <see cref="ProgramYear"/>.</summary>
public sealed class AcoSummary
{
    /// <summary>CMS's ACO ID (e.g. A1234).</summary>
    public string AcoId { get; set; } = "";

    public string Name { get; set; } = "";

    public int ProgramYear { get; set; }

    /// <summary>The states it serves, as CMS writes them ("AZ, NV").</summary>
    public string? ServiceArea { get; set; }

    /// <summary>"BASIC level E", "ENHANCED".</summary>
    public string? Track { get; set; }

    public int? AgreementPeriod { get; set; }

    public DateTime? InitialStartDate { get; set; }

    public DateTime? CurrentStartDate { get; set; }

    public int Participants { get; set; }

    public int SnfAffiliates { get; set; }

    /// <summary>Its public reporting website.</summary>
    public string? Website { get; set; }
}

/// <summary>An ACO with its participants and SNF affiliates by legal business name (/api/v1/acos/{id}).</summary>
public sealed class AcoDetail
{
    public AcoSummary Aco { get; set; } = new();

    public string? Address { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public IReadOnlyList<AcoParticipant> Participants { get; set; } = [];

    public IReadOnlyList<string> SnfAffiliates { get; set; } = [];
}

/// <summary>A health system a facility belongs to, and the facility (CCN) that links it.</summary>
public sealed class HealthSystemLink
{
    public string SystemId { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Ccn { get; set; }

    public string? FacilityName { get; set; }
}

/// <summary>
/// An ACO participant by legal business name, as CMS lists it, with the active organization NPIs whose NPPES legal business name is
/// the same name (case, spacing and punctuation aside): one is a link, several are candidates; a name shared by more than 5 lists none,
/// unless they are all hospitals (then up to 50).
/// </summary>
public sealed class AcoParticipant
{
    public string Name { get; set; } = "";

    public IReadOnlyList<AcoParticipantMatch> Organizations { get; set; } = [];

    /// <summary>"name" when <see cref="Organizations"/> isn't empty: a name match, not an identifier.</summary>
    public string? MatchedBy { get; set; }

    /// <summary>Every NPI of the name is a hospital (a hospital and its subparts; up to 50 are listed instead of 5).</summary>
    public bool HospitalGroup { get; set; }

    /// <summary>Why a hospital group's NPIs belong together: "ccn" (a shared CCN, <see cref="GroupedValue"/>), "parent" (the same parent organization, <see cref="GroupedValue"/> = its legal name), "parent_tin" (the same parent tax ID), or null.</summary>
    public string? GroupedBy { get; set; }

    public string? GroupedValue { get; set; }
}

/// <summary>An organization NPI with an ACO participant's legal name, and its primary practice city.</summary>
public sealed class AcoParticipantMatch
{
    public string Npi { get; set; } = "";

    public string Name { get; set; } = "";

    public string? City { get; set; }

    public string? State { get; set; }
}

/// <summary>An ACO an organization NPI takes part in, matched by name (/api/v1/providers/{npi}/acos, /api/v1/groups/{pacId}/acos).</summary>
public sealed class AcoMembership
{
    public string AcoId { get; set; } = "";

    public string AcoName { get; set; } = "";

    public int ProgramYear { get; set; }

    /// <summary>The participant's name as CMS lists it.</summary>
    public string ParticipantName { get; set; } = "";

    public string Npi { get; set; } = "";

    /// <summary>How many active organizations have that legal name (1 = only this one).</summary>
    public int Candidates { get; set; }

    /// <summary>Always "name".</summary>
    public string MatchedBy { get; set; } = "name";

    /// <summary>Every NPI of the name is a hospital (a hospital and its subparts; up to 50 are listed instead of 5).</summary>
    public bool HospitalGroup { get; set; }

    /// <summary>Why a hospital group's NPIs belong together: "ccn" (a shared CCN, <see cref="GroupedValue"/>), "parent" (the same parent organization, <see cref="GroupedValue"/> = its legal name), "parent_tin" (the same parent tax ID), or null.</summary>
    public string? GroupedBy { get; set; }

    public string? GroupedValue { get; set; }
}

/// <summary>A 340B Drug Pricing Program registration (HRSA OPAIS) of a facility or health center site.</summary>
public sealed class Covered340BEntity
{
    public string Id340B { get; set; } = "";

    /// <summary>DSH, CAH, CH, FQHCLA, RWIII …</summary>
    public string EntityType { get; set; } = "";

    public string EntityTypeName { get; set; } = "";

    public bool Participating { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? TerminationDate { get; set; }

    public string Name { get; set; } = "";

    public string? SubName { get; set; }

    /// <summary>"ccn" (the Medicare provider number), "npi" or "site" (the HRSA site ID).</summary>
    public string MatchedBy { get; set; } = "";
}

/// <summary>A site of an HRSA-funded health center (FQHC) or look-alike.</summary>
public sealed class HealthCenterSite
{
    /// <summary>HRSA's site number (BPS-…).</summary>
    public string SiteId { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Address { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Zip { get; set; }

    /// <summary>10 digits.</summary>
    public string? Phone { get; set; }

    public string? Website { get; set; }

    public double? HoursPerWeek { get; set; }

    /// <summary>School, hospital, nursing home … (null: an ordinary clinic, or not known).</summary>
    public string? Setting { get; set; }

    /// <summary>Permanent, Seasonal or Mobile Van.</summary>
    public string? LocationType { get; set; }

    public string? SiteType { get; set; }

    public bool LookAlike { get; set; }

    /// <summary>The health center's grant or look-alike number; its sites share it.</summary>
    public string? GrantNumber { get; set; }

    public string? HealthCenter { get; set; }

    public string? HealthCenterCity { get; set; }

    public string? HealthCenterState { get; set; }

    /// <summary>The site's Medicare number (CCN), when HRSA lists one.</summary>
    public string? Ccn { get; set; }

    public string? Npi { get; set; }

    public DateTime? AddedDate { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }
}

/// <summary>A health center site with the health center's other sites (/api/v1/health-center-sites/{id}).</summary>
public sealed class HealthCenterSiteDetail
{
    public HealthCenterSite Site { get; set; } = new();

    /// <summary>The site's CCN has a facility page.</summary>
    public bool CcnListed { get; set; }

    /// <summary>The active provider the site's NPI is, when it is one.</summary>
    public string? NpiName { get; set; }

    /// <summary>The first 100 of <see cref="OtherSiteCount"/>, by name.</summary>
    public IReadOnlyList<HealthCenterSite> OtherSites { get; set; } = [];

    public int OtherSiteCount { get; set; }

    /// <summary>Its 340B registrations, by its HRSA site ID or NPI; participating first.</summary>
    public IReadOnlyList<Covered340BEntity> Covered340B { get; set; } = [];
}
