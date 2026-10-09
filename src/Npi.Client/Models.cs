namespace Npi.Client;

/// <summary>One search result: the summary columns, showing the location that matched the search.</summary>
public sealed class ProviderSummary
{
    /// <summary>Ten-digit National Provider Identifier.</summary>
    public string Npi { get; set; } = "";

    /// <summary>1 = individual, 2 = organization.</summary>
    public int EntityType { get; set; }

    /// <summary>"Individual" or "Organization".</summary>
    public string EntityTypeName { get; set; } = "";

    /// <summary>"LAST, FIRST MIDDLE SUFFIX" for individuals, the legal business name for organizations.</summary>
    public string Name { get; set; } = "";

    public string? Credential { get; set; }

    /// <summary>"Classification – Specialization" of the primary taxonomy.</summary>
    public string? PrimarySpecialty { get; set; }

    public string? Address1 { get; set; }

    public string? Address2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    /// <summary>ZIP or ZIP+4 ("11702-2218"); the raw postal code outside the US.</summary>
    public string? Zip { get; set; }

    public string? County { get; set; }

    /// <summary>Digits only, as published by NPPES.</summary>
    public string? Phone { get; set; }

    /// <summary>F or M (individuals only).</summary>
    public string? Gender { get; set; }

    public DateTime? EnumerationDate { get; set; }

    public DateTime? LastUpdateDate { get; set; }

    /// <summary>Badges from the joined datasets (exclusion, Medicare opt-out, order/refer eligibility).</summary>
    public ProviderFlags Flags { get; set; } = new();
}

/// <summary>Yes/no facts from the joined datasets.</summary>
public sealed class ProviderFlags
{
    /// <summary>On the HHS-OIG List of Excluded Individuals/Entities (matched by NPI).</summary>
    public bool Excluded { get; set; }

    /// <summary>Has an active Medicare opt-out affidavit.</summary>
    public bool OptedOutOfMedicare { get; set; }

    /// <summary>Eligible to order or refer in at least one Medicare program.</summary>
    public bool CanOrderAndRefer { get; set; }

    /// <summary>Accepts Medicare's approved amount as full payment (Care Compare).</summary>
    public bool AcceptsMedicareAssignment { get; set; }

    /// <summary>Offers telehealth (Care Compare).</summary>
    public bool OffersTelehealth { get; set; }

    /// <summary>Billed Medicare Part B or Part D in the latest CMS data year.</summary>
    public bool BilledMedicare { get; set; }
}

/// <summary>One page of search results.</summary>
public sealed class ProviderPage
{
    public IReadOnlyList<ProviderSummary> Items { get; set; } = [];

    public int Page { get; set; }

    public int PageSize { get; set; }

    /// <summary>Every match, not just this page. Paging stops at the first 10,000; download the CSV for more.</summary>
    public long TotalCount { get; set; }

    /// <summary>Newest NPPES update date in the data being served.</summary>
    public DateTime? DataAsOf { get; set; }
}

/// <summary>One taxonomy (specialty) of a provider, with its license.</summary>
public sealed class ProviderTaxonomy
{
    /// <summary>NPPES slot 1–15.</summary>
    public int Slot { get; set; }

    /// <summary>NUCC taxonomy code, e.g. 111N00000X.</summary>
    public string Code { get; set; } = "";

    public string? Classification { get; set; }

    public string? Specialization { get; set; }

    public bool IsPrimary { get; set; }

    public string? LicenseNumber { get; set; }

    public string? LicenseState { get; set; }
}

/// <summary>A practice location (primary or secondary).</summary>
public sealed class ProviderLocation
{
    public bool IsPrimary { get; set; }

    public string? Address1 { get; set; }

    public string? Address2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Zip { get; set; }

    /// <summary>"US", or the country code of a foreign address.</summary>
    public string? CountryCode { get; set; }

    public string? Phone { get; set; }
}

/// <summary>Everything published for one provider.</summary>
public sealed class ProviderDetail
{
    public string Npi { get; set; } = "";

    public int EntityType { get; set; }

    public string EntityTypeName { get; set; } = "";

    public string Name { get; set; } = "";

    public string? NamePrefix { get; set; }

    public string? Credential { get; set; }

    public string? Gender { get; set; }

    public DateTime? EnumerationDate { get; set; }

    public DateTime? LastUpdateDate { get; set; }

    public IReadOnlyList<ProviderTaxonomy> Taxonomies { get; set; } = [];

    public IReadOnlyList<ProviderLocation> Locations { get; set; } = [];

    public IReadOnlyList<string> OtherNames { get; set; } = [];

    /// <summary>Registration details (authorized official, parent organization, mailing address); null when none are published.</summary>
    public ProviderProfile? Profile { get; set; }

    /// <summary>Other identifiers, e.g. state Medicaid numbers.</summary>
    public IReadOnlyList<ProviderIdentifier> Identifiers { get; set; } = [];

    /// <summary>Direct messaging addresses, FHIR endpoints and websites published in NPPES.</summary>
    public IReadOnlyList<ProviderEndpoint> Endpoints { get; set; } = [];

    /// <summary>OIG exclusions, Medicare opt-out and order/refer eligibility.</summary>
    public ProviderCompliance Compliance { get; set; } = new();

    /// <summary>Medicare Care Compare facts; null when the NPI isn't listed there.</summary>
    public ProviderCareCompare? CareCompare { get; set; }

    /// <summary>Medicare-certified hospitals and nursing homes held by this organization NPI.</summary>
    public IReadOnlyList<CertifiedFacility> Facilities { get; set; } = [];

    /// <summary>Medicare Part B services in the latest data year; null when none.</summary>
    public MedicareServices? MedicareServices { get; set; }

    /// <summary>Medicare Part D prescribing in the latest data year; null when none.</summary>
    public MedicarePrescribing? MedicarePrescribing { get; set; }
}

/// <summary>Medicare Part B activity in one data year. Counts under 11 are suppressed by CMS (null).</summary>
public sealed class MedicareServices
{
    public int Year { get; set; }

    public string? ProviderType { get; set; }

    public bool? Participating { get; set; }

    public int? DistinctServices { get; set; }

    public int? Beneficiaries { get; set; }

    public double? Services { get; set; }

    public double? AllowedAmount { get; set; }

    public double? PaymentAmount { get; set; }

    public double? AverageBeneficiaryAge { get; set; }

    /// <summary>HCC risk score of the provider's patients (1.0 = average Medicare patient).</summary>
    public double? AverageRiskScore { get; set; }

    /// <summary>The five most frequent services.</summary>
    public IReadOnlyList<MedicareService> TopServices { get; set; } = [];
}

/// <summary>One Medicare Part B service (HCPCS code).</summary>
public sealed class MedicareService
{
    public string Hcpcs { get; set; } = "";

    public string? Description { get; set; }

    public bool IsDrug { get; set; }

    /// <summary>"Facility" or "Office".</summary>
    public string? PlaceOfService { get; set; }

    public int? Beneficiaries { get; set; }

    public double? Services { get; set; }

    public double? AveragePayment { get; set; }
}

/// <summary>Medicare Part D prescribing in one data year.</summary>
public sealed class MedicarePrescribing
{
    public int Year { get; set; }

    public string? PrescriberType { get; set; }

    public int? Claims { get; set; }

    public double? DrugCost { get; set; }

    public int? Beneficiaries { get; set; }

    public int? BrandClaims { get; set; }

    public int? GenericClaims { get; set; }

    public int? OpioidClaims { get; set; }

    /// <summary>Percent of claims that are for opioids.</summary>
    public double? OpioidRate { get; set; }

    public int? AntibioticClaims { get; set; }
}

/// <summary>What Medicare Care Compare publishes about a clinician.</summary>
public sealed class ProviderCareCompare
{
    public string? MedicalSchool { get; set; }

    public int? GraduationYear { get; set; }

    public string? PrimarySpecialty { get; set; }

    public string? SecondarySpecialties { get; set; }

    public bool AcceptsMedicareAssignment { get; set; }

    public bool OffersTelehealth { get; set; }

    public IReadOnlyList<GroupPractice> GroupPractices { get; set; } = [];

    public IReadOnlyList<FacilityAffiliation> Facilities { get; set; } = [];
}

/// <summary>A group practice the clinician bills Medicare through.</summary>
public sealed class GroupPractice
{
    /// <summary>Medicare PECOS associate ID of the group.</summary>
    public string OrgPacId { get; set; } = "";

    public string? Name { get; set; }

    public int? Members { get; set; }

    public bool AcceptsMedicareAssignment { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }
}

/// <summary>A facility where the clinician works.</summary>
public sealed class FacilityAffiliation
{
    /// <summary>e.g. "Hospital", "Nursing home", "Home health agency".</summary>
    public string FacilityType { get; set; } = "";

    /// <summary>CMS Certification Number.</summary>
    public string Ccn { get; set; } = "";

    public string? Name { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    /// <summary>Care Compare overall star rating, 1–5.</summary>
    public int? OverallRating { get; set; }

    /// <summary>The facility's own organization NPI, when known.</summary>
    public string? Npi { get; set; }
}

/// <summary>A Medicare-certified hospital or nursing home with its Care Compare ratings.</summary>
public sealed class CertifiedFacility
{
    public string Ccn { get; set; } = "";

    /// <summary>"hospital" or "nursing_home".</summary>
    public string Kind { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Type { get; set; }

    public string? Ownership { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Phone { get; set; }

    public bool? EmergencyServices { get; set; }

    public int? CertifiedBeds { get; set; }

    public int? OverallRating { get; set; }

    public int? InspectionRating { get; set; }

    public int? StaffingRating { get; set; }

    public int? QualityRating { get; set; }

    /// <summary>Clinicians Care Compare lists as affiliated with this facility.</summary>
    public int AffiliatedClinicians { get; set; }
}

/// <summary>Compliance facts about a provider.</summary>
public sealed class ProviderCompliance
{
    /// <summary>HHS-OIG exclusions matched by NPI (usually none).</summary>
    public IReadOnlyList<OigExclusion> Exclusions { get; set; } = [];

    public MedicareOptOut? OptOut { get; set; }

    public MedicareOrderReferring? OrderReferring { get; set; }
}

/// <summary>An entry on the HHS-OIG List of Excluded Individuals/Entities.</summary>
public sealed class OigExclusion
{
    /// <summary>OIG authority code, e.g. 1128b4.</summary>
    public string? Type { get; set; }

    public string? TypeDescription { get; set; }

    public DateTime? ExclusionDate { get; set; }

    public DateTime? WaiverDate { get; set; }

    public string? WaiverState { get; set; }

    public string? Category { get; set; }

    public string? Specialty { get; set; }
}

/// <summary>A Medicare opt-out affidavit.</summary>
public sealed class MedicareOptOut
{
    public string? Specialty { get; set; }

    public DateTime? EffectiveDate { get; set; }

    public DateTime? EndDate { get; set; }

    /// <summary>True while the opt-out is in effect.</summary>
    public bool Active { get; set; }

    public bool? CanOrderAndRefer { get; set; }
}

/// <summary>The Medicare programs in which the provider may order or refer.</summary>
public sealed class MedicareOrderReferring
{
    public bool PartB { get; set; }

    public bool DurableMedicalEquipment { get; set; }

    public bool HomeHealth { get; set; }

    public bool PowerMobilityDevices { get; set; }

    public bool Hospice { get; set; }
}

/// <summary>NPPES registration details that are shown but not searched.</summary>
public sealed class ProviderProfile
{
    public bool? IsSoleProprietor { get; set; }

    /// <summary>True when this organization NPI is a subpart of <see cref="ParentOrganization"/>.</summary>
    public bool? IsOrganizationSubpart { get; set; }

    public string? ParentOrganization { get; set; }

    public AuthorizedOfficial? AuthorizedOfficial { get; set; }

    public MailingAddress? MailingAddress { get; set; }

    public string? PracticeFax { get; set; }
}

/// <summary>The person an organization registered as its authorized official.</summary>
public sealed class AuthorizedOfficial
{
    public string Name { get; set; } = "";

    public string? Credential { get; set; }

    public string? Title { get; set; }

    public string? Phone { get; set; }
}

/// <summary>NPPES business mailing address.</summary>
public sealed class MailingAddress
{
    public string? Address1 { get; set; }

    public string? Address2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? PostalCode { get; set; }

    public string? CountryCode { get; set; }

    public string? Phone { get; set; }

    public string? Fax { get; set; }
}

/// <summary>An identifier other than the NPI.</summary>
public sealed class ProviderIdentifier
{
    public string Identifier { get; set; } = "";

    /// <summary>NPPES type code, e.g. "05".</summary>
    public string? TypeCode { get; set; }

    /// <summary>Description of <see cref="TypeCode"/>, e.g. "Medicaid".</summary>
    public string? Type { get; set; }

    public string? State { get; set; }

    public string? Issuer { get; set; }
}

/// <summary>An electronic endpoint published in NPPES.</summary>
public sealed class ProviderEndpoint
{
    /// <summary>e.g. "DIRECT", "FHIR", "CONNECT".</summary>
    public string? Type { get; set; }

    public string? TypeDescription { get; set; }

    public string Endpoint { get; set; } = "";

    public string? Description { get; set; }

    public string? Use { get; set; }

    public string? Content { get; set; }

    public string? AffiliationName { get; set; }

    public string? AffiliationCity { get; set; }

    public string? AffiliationState { get; set; }
}

/// <summary>A state or territory.</summary>
public sealed class StateInfo
{
    /// <summary>Two-letter code, e.g. NY.</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
}

/// <summary>A county, by five-digit FIPS code (use it as <see cref="ProviderSearch.County"/>).</summary>
public sealed class CountyInfo
{
    public string Fips { get; set; } = "";

    public string Name { get; set; } = "";
}

/// <summary>Population and shortage-area facts about a county.</summary>
public sealed class CountyFacts
{
    /// <summary>Five-digit county FIPS code.</summary>
    public string Fips { get; set; } = "";

    public string Name { get; set; } = "";

    public string State { get; set; } = "";

    /// <summary>Census population estimate for <see cref="PopulationYear"/>.</summary>
    public int? Population { get; set; }

    public int? PopulationYear { get; set; }

    /// <summary>HRSA Health Professional Shortage Areas in force, per discipline.</summary>
    public IReadOnlyList<CountyShortage> Shortages { get; set; } = [];
}

/// <summary>HRSA shortage areas of one discipline in a county.</summary>
public sealed class CountyShortage
{
    /// <summary>PC (primary care), DH (dental) or MH (mental health).</summary>
    public string Discipline { get; set; } = "";

    public string DisciplineName { get; set; } = "";

    /// <summary>True when the whole county is a shortage area.</summary>
    public bool WholeCounty { get; set; }

    public int ShortageAreas { get; set; }

    /// <summary>Highest HRSA score (0-26; higher = greater need).</summary>
    public int? MaxScore { get; set; }
}

/// <summary>Where the served data comes from.</summary>
public sealed class ApiMeta
{
    /// <summary>Newest NPPES update date in the data.</summary>
    public DateTime? DataAsOf { get; set; }

    public string? MonthlyFile { get; set; }

    public string? WeeklyFile { get; set; }

    public string? DeactivationFile { get; set; }

    public string? NuccVersion { get; set; }

    public string? HudVersion { get; set; }

    public string? CensusVersion { get; set; }

    /// <summary>Active providers in the data.</summary>
    public int ProviderCount { get; set; }

    public DateTimeOffset ProjectedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
}
