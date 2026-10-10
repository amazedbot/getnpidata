namespace Npi.Core.Search;

/// <summary>
/// Yes/no facts about a provider from the Stage 5.5 datasets (CLAUDE.md §7 Stage 5.5), shown as badges in
/// results and available as search filters.
/// </summary>
public sealed record ProviderFlags(bool Excluded, bool OptedOutOfMedicare, bool CanOrderAndRefer, bool AcceptsMedicareAssignment, bool OffersTelehealth,
    bool BilledMedicare)
{
    public static readonly ProviderFlags None = new(false, false, false, false, false, false);
}

/// <summary>One of a provider's most frequent Medicare Part B services.</summary>
public sealed record MedicareService(string Hcpcs, string? Description, bool IsDrug, string? PlaceOfService, int? Beneficiaries, double? Services,
    double? AveragePayment);

/// <summary>Medicare Part B activity in one data year (Stage 5.5 item 5a). Counts under 11 are suppressed by CMS (null).</summary>
public sealed record MedicareServices(int Year, string? ProviderType, bool? Participating, int? DistinctServices, int? Beneficiaries, double? Services,
    double? AllowedAmount, double? PaymentAmount, double? AverageBeneficiaryAge, double? AverageRiskScore, IReadOnlyList<MedicareService> TopServices);

/// <summary>Industry payments of one kind (nature of payment) in the Open Payments program year.</summary>
public sealed record IndustryPaymentKind(string Nature, double Amount, int Records);

/// <summary>A company among the recipient's top payers in the Open Payments program year.</summary>
public sealed record IndustryPayer(string Name, double Amount, int Records);

/// <summary>Open Payments (Sunshine Act) general payments from drug and device makers to this NPI (Stage 5.5 item 6).</summary>
public sealed record IndustryPayments(int Year, double TotalAmount, int Records, int Payers, IReadOnlyList<IndustryPaymentKind> ByNature,
    IReadOnlyList<IndustryPayer> TopPayers);

/// <summary>
/// Open Payments in one program year (Stage 5.5 item 13): general payments; research payments made to the provider;
/// research funding where the provider was a principal investigator (paid to an institution); and ownership or
/// investment interests (amount invested, value of the interest).
/// </summary>
public sealed record IndustryPaymentYear(int Year, double General, int GeneralRecords, double Research, int ResearchRecords,
    double AssociatedResearch, int AssociatedResearchRecords, double OwnershipInvested, double OwnershipValue, int OwnershipRecords);

/// <summary>A company among the recipient's top payers over every published program year, by payment type.</summary>
public sealed record IndustryPaymentCompany(string Name, double Total, double General, double Research, double AssociatedResearch,
    double Ownership, int Records);

/// <summary>Open Payments over every published program year (2019 on): totals per year and the top companies.</summary>
public sealed record IndustryPaymentHistory(IReadOnlyList<IndustryPaymentYear> Years, IReadOnlyList<IndustryPaymentCompany> TopCompanies);

/// <summary>Medicare Part D prescribing in one data year (Stage 5.5 item 5b).</summary>
public sealed record MedicarePrescribing(int Year, string? PrescriberType, int? Claims, double? DrugCost, int? Beneficiaries, int? BrandClaims,
    int? GenericClaims, int? OpioidClaims, double? OpioidRate, int? AntibioticClaims);

/// <summary>An entry on the HHS-OIG List of Excluded Individuals/Entities, matched by NPI.</summary>
public sealed record OigExclusion(string? Type, string? TypeDescription, DateOnly? ExclusionDate, DateOnly? WaiverDate, string? WaiverState,
    string? Category, string? Specialty);

/// <summary>A Medicare opt-out affidavit. While active, the practitioner bills Medicare patients privately.</summary>
public sealed record MedicareOptOut(string? Specialty, DateOnly? EffectiveDate, DateOnly? EndDate, bool Active, bool? CanOrderAndRefer);

/// <summary>The Medicare programs in which the provider may order or refer.</summary>
public sealed record MedicareOrderReferring(bool PartB, bool DurableMedicalEquipment, bool HomeHealth, bool PowerMobilityDevices, bool Hospice);

/// <summary>A group practice the clinician bills Medicare through (Care Compare).</summary>
public sealed record GroupPractice(string OrgPacId, string? Name, int? Members, bool AcceptsMedicareAssignment, string? City, string? State);

/// <summary>A facility where the clinician works (Care Compare), with the facility's own NPI and rating when known.</summary>
public sealed record FacilityAffiliation(string FacilityType, string Ccn, string? Name, string? City, string? State, int? OverallRating, string? Npi);

/// <summary>What Medicare's Care Compare publishes about a clinician (Stage 5.5 item 3).</summary>
public sealed record ProviderCareCompare(string? MedicalSchool, int? GraduationYear, string? PrimarySpecialty, string? SecondarySpecialties,
    bool AcceptsMedicareAssignment, bool OffersTelehealth, IReadOnlyList<GroupPractice> GroupPractices, IReadOnlyList<FacilityAffiliation> Facilities);

/// <summary>A Medicare-certified facility (hospital or nursing home) held by this organization NPI, with its Care Compare ratings (Stage 5.5 item 4).</summary>
public sealed record CertifiedFacility(string Ccn, string Kind, string Name, string? Type, string? Ownership, string? City, string? State, string? Phone,
    bool? EmergencyServices, int? CertifiedBeds, int? OverallRating, int? InspectionRating, int? StaffingRating, int? QualityRating, int AffiliatedClinicians);

/// <summary>Compliance facts for the detail page and API.</summary>
public sealed record ProviderCompliance(IReadOnlyList<OigExclusion> Exclusions, MedicareOptOut? OptOut, MedicareOrderReferring? OrderReferring);

/// <summary>
/// SQL fragments for the dataset flags, shared by the search filters and the result badges so they can't
/// disagree. "{0}" is the alias of a table with an <c>npi</c> column.
/// </summary>
internal static class EnrichmentSql
{
    internal const string Excluded = "EXISTS (SELECT 1 FROM oig_exclusion x WHERE x.npi = {0}.npi)";

    internal const string ActiveOptOut = "(o.end_date IS NULL OR o.end_date >= CURRENT_DATE)";

    internal const string OptedOut = "EXISTS (SELECT 1 FROM medicare_opt_out o WHERE o.npi = {0}.npi AND " + ActiveOptOut + ")";

    internal const string AnyProgram = "(r.part_b = 1 OR r.dme = 1 OR r.hha = 1 OR r.pmd = 1 OR r.hospice = 1)";

    internal const string OrderRefer = "EXISTS (SELECT 1 FROM medicare_order_referring r WHERE r.npi = {0}.npi AND " + AnyProgram + ")";

    internal const string AcceptsAssignment = "EXISTS (SELECT 1 FROM cc_clinician cc WHERE cc.npi = {0}.npi AND cc.accepts_assignment = 1)";

    internal const string Telehealth = "EXISTS (SELECT 1 FROM cc_clinician cc WHERE cc.npi = {0}.npi AND cc.telehealth = 1)";

    internal const string BilledMedicare =
        "(EXISTS (SELECT 1 FROM medicare_utilization mu WHERE mu.npi = {0}.npi) OR EXISTS (SELECT 1 FROM medicare_part_d md WHERE md.npi = {0}.npi))";

    /// <summary>Needs the @maxGraduationYear parameter.</summary>
    internal const string MinYears = "EXISTS (SELECT 1 FROM cc_clinician cc WHERE cc.npi = {0}.npi AND cc.graduation_year <= @maxGraduationYear)";

    // Candidate lists for searches driven by a selective flag (~9k excluded, ~55k opted out).
    internal const string ExcludedNpis = "SELECT DISTINCT x.npi FROM oig_exclusion x WHERE x.npi IS NOT NULL";

    internal const string OptedOutNpis = "SELECT DISTINCT o.npi FROM medicare_opt_out o WHERE " + ActiveOptOut;

    /// <summary>The flags of the NPIs in @npis.</summary>
    internal static readonly string FlagsSql =
        "SELECT p.npi AS Npi, " +
        "CAST(" + Excluded.Replace("{0}", "p", StringComparison.Ordinal) + " AS SIGNED) AS Excluded, " +
        "CAST(" + OptedOut.Replace("{0}", "p", StringComparison.Ordinal) + " AS SIGNED) AS OptedOut, " +
        "CAST(" + OrderRefer.Replace("{0}", "p", StringComparison.Ordinal) + " AS SIGNED) AS OrderRefer, " +
        "CAST(COALESCE(cc.accepts_assignment, 0) AS SIGNED) AS AcceptsAssignment, CAST(COALESCE(cc.telehealth, 0) AS SIGNED) AS Telehealth, " +
        "CAST(" + BilledMedicare.Replace("{0}", "p", StringComparison.Ordinal) + " AS SIGNED) AS BilledMedicare " +
        "FROM provider p LEFT JOIN cc_clinician cc ON cc.npi = p.npi WHERE p.npi IN @npis";
}

/// <summary>Descriptions of the HHS-OIG exclusion authorities (LEIE EXCLTYPE codes).</summary>
public static class ComplianceCodes
{
    private static readonly Dictionary<string, string> OigTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1128a1"] = "Conviction of program-related crimes",
        ["1128a2"] = "Conviction relating to patient abuse or neglect",
        ["1128a3"] = "Felony conviction relating to health care fraud",
        ["1128a4"] = "Felony conviction relating to controlled substances",
        ["1128b1"] = "Misdemeanor conviction relating to health care fraud",
        ["1128b2"] = "Conviction relating to obstruction of an investigation or audit",
        ["1128b3"] = "Misdemeanor conviction relating to controlled substances",
        ["1128b4"] = "License revocation, suspension or surrender",
        ["1128b5"] = "Exclusion or suspension under a federal or state health care program",
        ["1128b6"] = "Claims for excessive charges or unnecessary services",
        ["1128b7"] = "Fraud, kickbacks and other prohibited activities",
        ["1128b8"] = "Entity controlled by a sanctioned individual",
        ["1128b8a"] = "Entity controlled by a family or household member of an excluded individual",
        ["1128b9"] = "Failure to disclose required information",
        ["1128b10"] = "Failure to supply requested information on subcontractors and suppliers",
        ["1128b11"] = "Failure to supply payment information",
        ["1128b12"] = "Failure to grant immediate access",
        ["1128b13"] = "Failure to take corrective action",
        ["1128b14"] = "Default on health education loan or scholarship obligations",
        ["1128b15"] = "Individual controlling a sanctioned entity",
        ["1128b16"] = "Making false statements or misrepresentations of material fact",
        ["1128Aa"] = "Civil monetary penalty",
        ["1156"] = "Failure to meet statutory obligations (QIO)",
    };

    public static string? DescribeOigType(string? code) => code is null ? null : OigTypes.GetValueOrDefault(code.Trim());
}
