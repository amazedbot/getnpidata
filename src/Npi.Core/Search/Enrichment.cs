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

/// <summary>A company among the recipient's top payers in the Open Payments program year; <see cref="CompanyId"/> is its CMS ID (company page).</summary>
public sealed record IndustryPayer(string Name, double Amount, int Records, string? CompanyId = null);

/// <summary>Open Payments (Sunshine Act) general payments from drug and device makers to this NPI (Stage 5.5 item 6).</summary>
public sealed record IndustryPayments(int Year, double TotalAmount, int Records, int Payers, IReadOnlyList<IndustryPaymentKind> ByNature,
    IReadOnlyList<IndustryPayer> TopPayers)
{
    /// <summary>The products named most in these payments (item 19), linked to the product pages.</summary>
    public IReadOnlyList<IndustryProduct> TopProducts { get; init; } = [];
}

/// <summary>A product named in general payments to the provider: its product page key, name, type and the amount of payments naming it.</summary>
public sealed record IndustryProduct(string Slug, string Name, string? Kind, double Amount, int Records)
{
    /// <summary>The provider's Medicare Part D claims of the drug (part 4); null when the product has no Part D brand.</summary>
    public int? MedicareClaims { get; init; }
}

/// <summary>
/// Open Payments in one program year (Stage 5.5 item 13): general payments; research payments made to the provider;
/// research funding where the provider was a principal investigator (paid to an institution); and ownership or
/// investment interests (amount invested, value of the interest).
/// </summary>
public sealed record IndustryPaymentYear(int Year, double General, int GeneralRecords, double Research, int ResearchRecords,
    double AssociatedResearch, int AssociatedResearchRecords, double OwnershipInvested, double OwnershipValue, int OwnershipRecords);

/// <summary>A company among the recipient's top payers over every published program year, by payment type.</summary>
public sealed record IndustryPaymentCompany(string Name, double Total, double General, double Research, double AssociatedResearch,
    double Ownership, int Records, string? CompanyId = null);

/// <summary>Open Payments over every published program year (2019 on): totals per year and the top companies.</summary>
public sealed record IndustryPaymentHistory(IReadOnlyList<IndustryPaymentYear> Years, IReadOnlyList<IndustryPaymentCompany> TopCompanies);

/// <summary>Medicare Part D prescribing in one data year (Stage 5.5 item 5b).</summary>
public sealed record MedicarePrescribing(int Year, string? PrescriberType, int? Claims, double? DrugCost, int? Beneficiaries, int? BrandClaims,
    int? GenericClaims, int? OpioidClaims, double? OpioidRate, int? AntibioticClaims);

/// <summary>Medicare activity in one data year: Part B billing with its patients, Part D prescribing with its claims (null: suppressed by CMS).</summary>
public sealed record MedicareHistoryYear(int Year, bool PartB, bool PartD, int? Beneficiaries, int? Claims);

/// <summary>
/// Medicare Part B billing and Part D prescribing per data year, from <see cref="FirstDataYear"/> (2013) to the newest year CMS has
/// published (docs/PRODUCT_PLAN.md Phase 3 "Older Medicare years"; the newest year is items 5a/5b). Only years with activity are
/// listed, oldest first.
/// </summary>
public sealed record MedicareHistory(int FirstDataYear, int NewestDataYear, IReadOnlyList<MedicareHistoryYear> Years)
{
    public int? FirstYear => Years.Count == 0 ? null : Years.Min(y => y.Year);

    public int? LastYear => Years.Count == 0 ? null : Years.Max(y => y.Year);

    /// <summary>Billed Medicare or prescribed under Part D in an earlier year, but not in the newest one.</summary>
    public bool Stopped => LastYear is { } last && last < NewestDataYear;

    [System.Text.Json.Serialization.JsonIgnore]
    public int? LastPartBYear => Years.Where(y => y.PartB).Select(y => (int?)y.Year).Max();

    [System.Text.Json.Serialization.JsonIgnore]
    public int? LastPartDYear => Years.Where(y => y.PartD).Select(y => (int?)y.Year).Max();

    /// <summary>The years with activity as ranges, e.g. "2013–2016, 2019, 2021–2024".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Spans
    {
        get
        {
            var years = Years.Select(y => y.Year).Distinct().Order().ToList();
            var spans = new List<string>();
            for (var i = 0; i < years.Count;)
            {
                var j = i;
                while (j + 1 < years.Count && years[j + 1] == years[j] + 1)
                {
                    j++;
                }

                spans.Add(j == i ? years[i].ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{years[i]}–{years[j]}");
                i = j + 1;
            }

            return string.Join(", ", spans);
        }
    }

    /// <summary>
    /// When the provider last billed Medicare, e.g. "Last billed Medicare in 2016 (Part B)", "Last billed Medicare in 2018 (Part B and
    /// Part D)", "Last prescribed under Medicare Part D in 2019; last billed Part B in 2015"; null without any year.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LastBilledText
    {
        get
        {
            if (LastYear is not { } last)
            {
                return null;
            }

            var year = Years.Single(y => y.Year == last);
            return (year.PartB, year.PartD) switch
            {
                (true, true) => $"Last billed Medicare in {last} (Part B and Part D)",
                (true, false) => $"Last billed Medicare in {last} (Part B)" + (LastPartDYear is { } d ? $"; last Part D prescription in {d}" : ""),
                _ => $"Last prescribed under Medicare Part D in {last}" + (LastPartBYear is { } b ? $"; last billed Part B in {b}" : ""),
            };
        }
    }
}

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
    bool? EmergencyServices, int? CertifiedBeds, int? OverallRating, int? InspectionRating, int? StaffingRating, int? QualityRating, int AffiliatedClinicians)
{
    /// <summary>Patient experience star rating 1-5 (Stage 5.5 item 14): HCAHPS for a hospital, the family caregiver survey for a hospice.</summary>
    public int? PatientSurveyRating { get; init; }

    /// <summary>A home health agency's quality of patient care star rating, 1-5 in half stars (item 14).</summary>
    public double? QualityOfCareRating { get; init; }

    /// <summary>A hospital's outcome measures compared with the national rate (item 14); null for other kinds.</summary>
    public HospitalOutcomes? Outcomes { get; init; }
}

/// <summary>One action a state board took on a license (Stage 5.5 item 15).</summary>
public sealed record StateBoardAction(DateOnly? Date, string? Action, string? Description);

/// <summary>
/// A state license record matched to this NPI (Stage 5.5 item 15) by license state, number and last name, from the
/// state's open data (<see cref="SourceName"/>, <see cref="SourceUrl"/>). NY publishes only board actions, so its
/// records have no status. <see cref="Discipline"/> is the state's own wording (e.g. "Yes", "NONE", "Y").
/// </summary>
public sealed record StateLicenseRecord(string State, string Source, string? LicenseNumber, string? LicenseType, string? Status, DateOnly? ExpirationDate,
    string? Discipline, string? VerifyUrl, IReadOnlyList<StateBoardAction> Actions)
{
    public string SourceName => StateLicenseSources.Name(Source);

    public string SourceUrl => StateLicenseSources.Url(Source);
}

/// <summary>The state datasets behind <see cref="StateLicenseRecord"/> (the loader's StateLicenseSource reads the same ones).</summary>
public static class StateLicenseSources
{
    private static readonly Dictionary<string, (string Name, string Url)> Sources = new(StringComparer.Ordinal)
    {
        ["ny_bpmc"] = ("NY Department of Health, Board for Professional Medical Conduct actions", "https://health.data.ny.gov/d/ebmi-8ctw"),
        ["tx_tmb"] = ("Texas Medical Board licenses", "https://data.texas.gov/d/tm3v-pfq9"),
        ["wa_doh"] = ("Washington State Department of Health credentials", "https://data.wa.gov/d/qxh8-f4bd"),
        ["il_idfpr"] = ("Illinois Department of Financial and Professional Regulation licenses", "https://illinois-edp.data.socrata.com/d/pzzh-kp68"),
        ["co_dora"] = ("Colorado Department of Regulatory Agencies licenses", "https://data.colorado.gov/d/7s5z-vewr"),
        ["de_dpr"] = ("Delaware Division of Professional Regulation licenses and actions", "https://data.delaware.gov/d/pjnv-eaih"),
        ["ct_elicense"] = ("Connecticut eLicense credentials", "https://data.ct.gov/d/ngch-56tr"),
        ["md_bop"] = ("Maryland Board of Physicians practitioner lists", "https://www.mbp.state.md.us/"),
        ["fl_doh"] = ("Florida Department of Health, MQA licenses and administrative complaints", "https://mqa-internet.doh.state.fl.us/MQASearchServices/"),
        ["ri_doh"] = ("Rhode Island Department of Health licensee lists", "https://health.ri.gov/licensing/licensee-lists"),
        ["vt_bmp"] = ("Vermont Board of Medical Practice roster", "https://roster.medicallicensing.vermont.gov/"),
        ["wv_bom"] = ("West Virginia Board of Medicine rosters", "https://wvbom.wv.gov/Rosters.asp"),
        ["al_bme"] = ("Alabama Board of Medical Examiners roster", "https://dashboard.albme.gov/Verification/roster.aspx"),
        ["mi_lara"] = ("Michigan LARA Bureau of Professional Licensing license lists", "https://www.michigan.gov/lara/bureau-list/bpl/License-Lists-and-Reports"),
        ["ca_mbc"] = ("Medical Board of California physician and surgeon database", "https://www.mbc.ca.gov/"),
    };

    /// <summary>
    /// The states covered: NY (board actions only), CT and MD (status and a discipline flag), TX, WA, IL, CO, DE, FL, CA (with
    /// board actions), RI, VT, and AL, MI and WV (active licenses only).
    /// </summary>
    public static readonly IReadOnlyList<string> States = ["AL", "CA", "CO", "CT", "DE", "FL", "IL", "MD", "MI", "NY", "RI", "TX", "VT", "WA", "WV"];

    public static string Name(string source) => Sources.TryGetValue(source, out var s) ? s.Name : source;

    public static string Url(string source) => Sources.TryGetValue(source, out var s) ? s.Url : "";
}

/// <summary>A count of a hospital's measures in one group and how many CMS rates better / worse than the national rate.</summary>
public sealed record OutcomeCounts(int? Measures, int? Better, int? Worse);

/// <summary>Hospital outcomes from Care Compare (Stage 5.5 item 14): mortality, safety (infections, complications), readmissions.</summary>
public sealed record HospitalOutcomes(OutcomeCounts Mortality, OutcomeCounts Safety, OutcomeCounts Readmissions);

/// <summary>
/// One MIPS score (Stage 5.5 item 14), from Care Compare's Overall MIPS Performance: the final score and the category
/// scores (quality, promoting interoperability, improvement activities, cost), 0-100, null when not scored.
/// <see cref="Source"/> says whose score it is: individual, group, apm (an APM entity), subgroup or virtual group.
/// </summary>
public sealed record MipsScore(int Year, string? Source, string? Organization, double? FinalScore, double? Quality, double? PromotingInteroperability,
    double? ImprovementActivities, double? Cost);

/// <summary>Compliance facts for the detail page and API.</summary>
public sealed record ProviderCompliance(IReadOnlyList<OigExclusion> Exclusions, MedicareOptOut? OptOut, MedicareOrderReferring? OrderReferring)
{
    /// <summary>State Medicaid programs listing the provider as enrolled (New York and California publish their lists).</summary>
    public IReadOnlyList<StateMedicaidEnrollment> Medicaid { get; init; } = [];
}

/// <summary>
/// An enrollment in a state's Medicaid program (docs/PRODUCT_PLAN.md Phase 3): the state, the earliest enrollment date listed and
/// the kind of provider in the state's wording.
/// </summary>
public sealed record StateMedicaidEnrollment(string State, DateOnly? EnrolledSince, string? ProviderType);

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

    /// <summary>The flags of the NPIs in @npis, with the record-quality label (0 when not labelled yet).</summary>
    internal static readonly string FlagsSql =
        "SELECT p.npi AS Npi, " +
        "CAST(" + Excluded.Replace("{0}", "p", StringComparison.Ordinal) + " AS SIGNED) AS Excluded, " +
        "CAST(" + OptedOut.Replace("{0}", "p", StringComparison.Ordinal) + " AS SIGNED) AS OptedOut, " +
        "CAST(" + OrderRefer.Replace("{0}", "p", StringComparison.Ordinal) + " AS SIGNED) AS OrderRefer, " +
        "CAST(COALESCE(cc.accepts_assignment, 0) AS SIGNED) AS AcceptsAssignment, CAST(COALESCE(cc.telehealth, 0) AS SIGNED) AS Telehealth, " +
        "CAST(" + BilledMedicare.Replace("{0}", "p", StringComparison.Ordinal) + " AS SIGNED) AS BilledMedicare, " +
        "CAST(COALESCE(pq.label, 0) AS SIGNED) AS QualityLabelCode, CAST(COALESCE(pq.reasons, 0) AS SIGNED) AS QualityReasonBits, " +
        "CAST(COALESCE(pq.activity, 0) AS SIGNED) AS QualityActivityBits, pq.other_npi AS QualityOtherNpi, " +
        "CAST(COALESCE(og.members, 0) AS SIGNED) AS OrganizationNpis " +
        "FROM provider p LEFT JOIN cc_clinician cc ON cc.npi = p.npi LEFT JOIN provider_quality pq ON pq.npi = p.npi " +
        "LEFT JOIN org_group_member og ON og.npi = p.npi WHERE p.npi IN @npis";
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
