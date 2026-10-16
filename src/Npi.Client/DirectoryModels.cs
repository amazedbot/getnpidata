namespace Npi.Client;

/// <summary>A page of Medicare-certified facilities (/api/v1/facilities).</summary>
public sealed class FacilityPage
{
    public IReadOnlyList<FacilitySummary> Items { get; set; } = [];

    public int Page { get; set; }

    public int PageSize { get; set; }

    public int TotalCount { get; set; }
}

/// <summary>A Medicare-certified facility: from CMS's Provider of Services file and Medicare Care Compare, by its CCN.</summary>
public sealed class FacilitySummary
{
    /// <summary>CMS Certification Number (6 characters; 10 for surgery centers).</summary>
    public string Ccn { get; set; } = "";

    /// <summary>hospital, nursing_home, home_health, hospice, fqhc, rhc, dialysis, asc, rehab (inpatient rehabilitation facility), ltch (long-term care hospital) or other.</summary>
    public string Type { get; set; } = "";

    /// <summary>"Hospital", "Rural health clinic" ….</summary>
    public string TypeName { get; set; } = "";

    /// <summary>A finer kind when known: "Critical access hospital", "Community mental health center" ….</summary>
    public string? Kind { get; set; }

    public string Name { get; set; } = "";

    public string? Address { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Zip { get; set; }

    public string? Phone { get; set; }

    public string? Ownership { get; set; }

    /// <summary>Certified beds (nursing homes), beds (long-term care hospitals).</summary>
    public int? Beds { get; set; }

    /// <summary>The type's CMS star rating, 1–5 (home health in half stars; dialysis facilities: the five-star rating); null when not rated.</summary>
    public double? Rating { get; set; }

    /// <summary>False: its Medicare certification ended on <see cref="TerminationDate"/>.</summary>
    public bool Active { get; set; }

    public DateTime? TerminationDate { get; set; }

    public string? TerminationReason { get; set; }

    /// <summary>Active NPIs CMS's enrollment files, the dialysis facility report or the surgery center file link to the facility.</summary>
    public int BillingNpis { get; set; }

    /// <summary>The centre of the facility's ZIP code (approximate); for an HRSA site, HRSA's point of it.</summary>
    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    /// <summary>The newest change of ownership CMS recorded for the facility as buyer or seller (hospitals and nursing homes, since 2016).</summary>
    public DateTime? LastOwnershipChange { get; set; }

    /// <summary>A nursing home's fines and payment denials in Care Compare's penalty file (the last three years).</summary>
    public int Penalties { get; set; }

    /// <summary>The total of those fines, in dollars.</summary>
    public double? Fines { get; set; }

    public DateTime? LastPenalty { get; set; }
    /// <summary>
    /// "cms": a facility in CMS's files, by its CCN. "hrsa": a health center site only HRSA lists (type health_center); <see cref="Ccn"/>
    /// then holds HRSA's site number (see <see cref="NpiClient.GetHealthCenterSiteAsync"/>).
    /// </summary>
    public string Source { get; set; } = "cms";

    /// <summary>The health system it belongs to (AHRQ Compendium, by CCN), when any.</summary>
    public string? SystemId { get; set; }

    public string? SystemName { get; set; }

    /// <summary>The HRSA-funded health center (or look-alike) it is a site of.</summary>
    public string? HealthCenter { get; set; }

    /// <summary>Its website as HRSA lists it.</summary>
    public string? Website { get; set; }

    /// <summary>A participating 340B covered entity's type code (DSH, CAH, CH …) when the facility is one (HRSA OPAIS); null otherwise.</summary>
    public string? Program340B { get; set; }

    /// <summary>When that 340B participation started.</summary>
    public DateTime? Program340BSince { get; set; }
}

/// <summary>A page of Medicare group practices (/api/v1/groups).</summary>
public sealed class GroupPage
{
    public IReadOnlyList<GroupSummary> Items { get; set; } = [];

    public int Page { get; set; }

    public int PageSize { get; set; }

    public int TotalCount { get; set; }
}

/// <summary>A Medicare group practice, by its PAC ID.</summary>
public sealed class GroupSummary
{
    /// <summary>PECOS associate control ID (10 digits).</summary>
    public string PacId { get; set; } = "";

    public string Name { get; set; } = "";

    public string? State { get; set; }

    /// <summary>Active clinicians reassigned to the group in Medicare enrollment or listed with it in Care Compare.</summary>
    public int Clinicians { get; set; }

    /// <summary>Active organization NPIs enrolled in Medicare under the group's ID.</summary>
    public int OrganizationNpis { get; set; }

    public bool? AcceptsMedicare { get; set; }

    /// <summary>The group's MIPS final score (0–100) in <see cref="MipsYear"/>, when it reported as a group.</summary>
    public double? MipsScore { get; set; }

    public int? MipsYear { get; set; }
}

/// <summary>
/// One Medicare-certified facility (/api/v1/facilities/{ccn}): its Care Compare listing and ratings, how its main measures compare with the
/// nation, a hospital's Medicare volume, its certification, the NPIs it bills under and a page of its affiliated clinicians.
/// </summary>
public sealed class FacilityProfile
{
    public CertifiedFacility Facility { get; set; } = new();

    public string? Address { get; set; }

    public string? Zip { get; set; }

    /// <summary>A billing NPI's mapped practice address, else the centre of the ZIP code.</summary>
    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public IReadOnlyList<OrganizationNpi> BillingNpis { get; set; } = [];

    /// <summary>Clinicians who list the facility in Care Compare, a page of them (<see cref="ClinicianList.PageNumber"/>).</summary>
    public ClinicianList Clinicians { get; set; } = new();

    /// <summary>Its certification in CMS's Provider of Services file; null when the file doesn't list the CCN.</summary>
    public FacilityCertification? Certification { get; set; }

    /// <summary>True when only the Provider of Services file lists it (no Care Compare listing, ratings or measures).</summary>
    public bool CertificationOnly { get; set; }

    /// <summary>The type's main Care Compare measures, by group (deaths, complications, infections, readmissions, outcomes, emergency care, spending).</summary>
    public IReadOnlyList<FacilityMeasure> Measures { get; set; } = [];

    /// <summary>A hospital's Medicare inpatient and outpatient volume; null when CMS lists none for the CCN.</summary>
    public HospitalVolume? MedicareVolume { get; set; }

    /// <summary>A dialysis facility's stations, services, chain and star ratings.</summary>
    public DialysisFacts? Dialysis { get; set; }

    /// <summary>When Medicare first certified it (rehabilitation facilities and long-term care hospitals).</summary>
    public DateTime? CertifiedSince { get; set; }

    /// <summary>A dialysis facility's ESRD Quality Incentive Program score and payment reduction (newest payment year).</summary>
    public DialysisQualityIncentive? QualityIncentive { get; set; }

    /// <summary>Who owns and runs it and its ownership changes, when the server includes them (they also have their own endpoint).</summary>
    public FacilityOwnership? Ownership { get; set; }

    /// <summary>A nursing home's inspections and penalties, when the server includes them (they also have their own endpoint).</summary>
    public NursingHomeInspections? Inspections { get; set; }

    /// <summary>The health systems it belongs to (AHRQ Compendium of U.S. Health Systems, by CCN).</summary>
    public IReadOnlyList<HealthSystemLink> Systems { get; set; } = [];

    /// <summary>The HRSA health center sites it is (by the Medicare number HRSA lists, or an NPI CMS links to the CCN).</summary>
    public IReadOnlyList<HealthCenterSite> HealthCenterSites { get; set; } = [];

    /// <summary>Its 340B registrations (HRSA OPAIS), participating first, each with how it matched (ccn, npi or site).</summary>
    public IReadOnlyList<Covered340BEntity> Covered340B { get; set; } = [];
}

/// <summary>A facility's certification in CMS's Provider of Services file: active, or ended on <see cref="TerminationDate"/>.</summary>
public sealed class FacilityCertification
{
    public bool Active { get; set; }

    public DateTime? TerminationDate { get; set; }

    public string? TerminationReason { get; set; }
}

/// <summary>One page of a facility's or group's clinicians, with the specialty breakdown of the whole list.</summary>
public sealed class ClinicianList
{
    public int Total { get; set; }

    public IReadOnlyList<SpecialtyCount> BySpecialty { get; set; } = [];

    public IReadOnlyList<ClinicianRow> Page { get; set; } = [];

    public int PageNumber { get; set; }

    public int PageSize { get; set; }

    public int Pages { get; set; }
}

/// <summary>How many clinicians of a list have this plain specialty.</summary>
public sealed class SpecialtyCount
{
    public string Specialty { get; set; } = "";

    public int Clinicians { get; set; }
}

/// <summary>A clinician in a facility's or group's list.</summary>
public sealed class ClinicianRow
{
    public string Npi { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Credential { get; set; }

    public string? Gender { get; set; }

    public string? Specialty { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    /// <summary>
    /// On an organization's list only: how the clinician is tied to it (facilityAffiliation, reassignment, careCompareGroup); null elsewhere.
    /// </summary>
    public IReadOnlyList<string>? Sources { get; set; }
}

/// <summary>
/// One of a facility's measures: its value and the nation's (<see cref="Unit"/>: percent, per100, per1000, ratio or minutes), CMS's
/// comparison (better, same, worse, too_few; null when CMS doesn't compare it) and the period it covers.
/// </summary>
public sealed class FacilityMeasure
{
    /// <summary>CMS's measure ID (e.g. MORT_30_AMI, HAI_1_SIR, I_017_01), or ours for surgery center and dialysis measures (ASC_12, survival).</summary>
    public string Id { get; set; } = "";

    public string Group { get; set; } = "";

    public string Name { get; set; } = "";

    public string Unit { get; set; } = "";

    public bool HigherIsBetter { get; set; }

    public double? Value { get; set; }

    public double? National { get; set; }

    public string? Comparison { get; set; }

    /// <summary>The patients, stays or procedures measured, when CMS gives them.</summary>
    public int? Cases { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }
}

/// <summary>A dialysis facility's Care Compare listing details.</summary>
public sealed class DialysisFacts
{
    public int? Stations { get; set; }

    /// <summary>"Profit" or "Non-profit".</summary>
    public string? ProfitStatus { get; set; }

    public string? Chain { get; set; }

    public bool? InCenterHemodialysis { get; set; }

    public bool? PeritonealDialysis { get; set; }

    public bool? HomeHemodialysisTraining { get; set; }

    /// <summary>CMS's dialysis facility five-star rating (1–5) over <see cref="FiveStarFrom"/>–<see cref="FiveStarTo"/>.</summary>
    public double? FiveStar { get; set; }

    public DateTime? FiveStarFrom { get; set; }

    public DateTime? FiveStarTo { get; set; }

    /// <summary>The ICH CAHPS patient survey star rating, 1–5.</summary>
    public int? PatientSurveyRating { get; set; }
}

/// <summary>
/// A dialysis facility's ESRD Quality Incentive Program result for <see cref="PaymentYear"/>: its total performance score (0–100; null for
/// "No Score"), the state and national averages, and the cut to its Medicare payments in percent.
/// </summary>
public sealed class DialysisQualityIncentive
{
    public int PaymentYear { get; set; }

    public int? TotalPerformanceScore { get; set; }

    public int? StateAverage { get; set; }

    public int? NationalAverage { get; set; }

    /// <summary>Percent, e.g. 1.5; 0 = no reduction.</summary>
    public double? PaymentReduction { get; set; }
}

/// <summary>
/// A hospital's Medicare fee-for-service volume: inpatient discharges, patients, covered days and payments in <see cref="InpatientYear"/>;
/// outpatient services and payments in <see cref="OutpatientYear"/> (over the service groups CMS lists); the most frequent DRGs and APCs.
/// </summary>
public sealed class HospitalVolume
{
    public int? InpatientYear { get; set; }

    public int? Discharges { get; set; }

    public int? Beneficiaries { get; set; }

    public int? CoveredDays { get; set; }

    public double? MedicarePayments { get; set; }

    /// <summary>Medicare's payments with the patients' and other payers' shares.</summary>
    public double? TotalPayments { get; set; }

    public int? OutpatientYear { get; set; }

    public int? OutpatientServices { get; set; }

    public double? OutpatientPayments { get; set; }

    public IReadOnlyList<HospitalService> TopInpatient { get; set; } = [];

    public IReadOnlyList<HospitalService> TopOutpatient { get; set; } = [];
}

/// <summary>A DRG (inpatient: <see cref="Cases"/> = discharges) or APC (outpatient: services) of a hospital.</summary>
public sealed class HospitalService
{
    public string Code { get; set; } = "";

    public string? Description { get; set; }

    public int Cases { get; set; }

    public int? Beneficiaries { get; set; }

    public double? AverageMedicarePayment { get; set; }
}
