using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

public sealed record ProviderTaxonomy(int Slot, string Code, string? Classification, string? Specialization, bool IsPrimary, string? LicenseNumber, string? LicenseState);

public sealed record ProviderLocation(bool IsPrimary, string? Address1, string? Address2, string? City, string? State, string? Zip, string? CountryCode, string? Phone);

/// <summary>The person an organization registered as its authorized official.</summary>
public sealed record AuthorizedOfficial(string Name, string? Credential, string? Title, string? Phone);

/// <summary>NPPES business mailing address (shown, never searched).</summary>
public sealed record MailingAddress(string? Address1, string? Address2, string? City, string? State, string? PostalCode, string? CountryCode,
    string? Phone, string? Fax);

/// <summary>NPPES details that are shown but not searched (CLAUDE.md §7 Stage 5.5 item 1).</summary>
public sealed record ProviderProfile(bool? IsSoleProprietor, bool? IsOrganizationSubpart, string? ParentOrganization,
    AuthorizedOfficial? AuthorizedOfficial, MailingAddress? MailingAddress, string? PracticeFax);

/// <summary>An identifier other than the NPI (e.g. a state Medicaid number).</summary>
public sealed record ProviderIdentifier(string Identifier, string? TypeCode, string? Type, string? State, string? Issuer)
{
    /// <summary>NPPES "Other Provider Identifier Type Code" → description.</summary>
    public static string? Describe(string? code) => code switch
    {
        null => null,
        "01" => "Other",
        "02" => "Medicare UPIN",
        "04" => "Medicare ID (type unspecified)",
        "05" => "Medicaid",
        "06" => "Medicare OSCAR/Certification",
        "07" => "Medicare NSC",
        "08" => "Medicare PIN",
        _ => code,
    };
}

/// <summary>A Direct messaging address, FHIR endpoint, website or similar published in NPPES.</summary>
public sealed record ProviderEndpoint(string? Type, string? TypeDescription, string Endpoint, string? Description, string? Use, string? Content,
    string? AffiliationName, string? AffiliationCity, string? AffiliationState);

/// <summary>
/// A change the loader noticed between two projections (Stage 5.5 item 11): type is name, credential, specialty
/// (old/new shown as "Classification – Specialization") or address (the primary practice address).
/// </summary>
public sealed record ProviderChange(DateOnly RecordedOn, string Type, string? OldValue, string? NewValue);

/// <summary>Everything the detail page and GET /api/v1/providers/{npi} show for one NPI.</summary>
public sealed record ProviderDetail(
    string Npi, int EntityType, string Name, string? NamePrefix, string? Credential, string? Gender,
    DateOnly? EnumerationDate, DateOnly? LastUpdateDate,
    IReadOnlyList<ProviderTaxonomy> Taxonomies, IReadOnlyList<ProviderLocation> Locations, IReadOnlyList<string> OtherNames)
{
    public string EntityTypeName => EntityType == 2 ? "Organization" : "Individual";

    public ProviderProfile? Profile { get; init; }

    public IReadOnlyList<ProviderIdentifier> Identifiers { get; init; } = [];

    public IReadOnlyList<ProviderEndpoint> Endpoints { get; init; } = [];

    /// <summary>OIG exclusions, Medicare opt-out and order/refer eligibility (Stage 5.5 item 2).</summary>
    public ProviderCompliance Compliance { get; init; } = new([], null, null);

    /// <summary>Medicare Care Compare clinician facts; null when the NPI isn't listed there (Stage 5.5 item 3).</summary>
    public ProviderCareCompare? CareCompare { get; init; }

    /// <summary>Medicare-certified hospitals and nursing homes held by this organization NPI (Stage 5.5 item 4).</summary>
    public IReadOnlyList<CertifiedFacility> Facilities { get; init; } = [];

    /// <summary>Medicare Part B services billed in the latest data year; null when none (Stage 5.5 item 5a).</summary>
    public MedicareServices? MedicareServices { get; init; }

    /// <summary>Medicare Part D prescribing in the latest data year; null when none (Stage 5.5 item 5b).</summary>
    public MedicarePrescribing? MedicarePrescribing { get; init; }

    /// <summary>Open Payments general payments in the newest program year; null when none (Stage 5.5 item 6).</summary>
    public IndustryPayments? IndustryPayments { get; init; }

    /// <summary>MIPS scores in the newest program year, best first (Stage 5.5 item 14).</summary>
    public IReadOnlyList<MipsScore> MipsScores { get; init; } = [];

    /// <summary>Open Payments per program year and the top companies over all years; null when none (Stage 5.5 item 13).</summary>
    public IndustryPaymentHistory? PaymentHistory { get; init; }

    /// <summary>Changes recorded since change tracking began, newest first (Stage 5.5 item 11).</summary>
    public IReadOnlyList<ProviderChange> Changes { get; init; } = [];
}

/// <summary>Reads one provider from the projection. Deactivated NPIs are not in the projection, so they come back as null.</summary>
public sealed class ProviderDetailService(string connectionString)
{
    private sealed record Row(string Npi, sbyte EntityType, string? LastName, string? FirstName, string? MiddleName, string? NamePrefix,
        string? NameSuffix, string? Credential, string? OrgName, string? Gender, DateTime? EnumerationDate, DateTime? LastUpdateDate);

    private sealed record TaxRow(sbyte Slot, string Code, string? Classification, string? Specialization, sbyte IsPrimary, string? LicenseNo, string? LicenseState);

    private sealed record LocRow(sbyte IsPrimary, string? Address1, string? Address2, string? City, string? State, string? Zip5, string? Zip4,
        string? PostalCode, string? CountryCode, string? Phone);

    private sealed record ProfileRow(sbyte? IsSoleProprietor, sbyte? IsSubpart, string? ParentOrgName,
        string? OfficialPrefix, string? OfficialFirstName, string? OfficialMiddleName, string? OfficialLastName, string? OfficialSuffix,
        string? OfficialCredential, string? OfficialTitle, string? OfficialPhone,
        string? MailingAddress1, string? MailingAddress2, string? MailingCity, string? MailingState, string? MailingPostalCode,
        string? MailingCountryCode, string? MailingPhone, string? MailingFax, string? PracticeFax);

    private sealed record IdentifierRow(string Identifier, string? TypeCode, string? State, string? Issuer);

    private sealed record ExclusionRow(string? ExclusionType, DateTime? ExclusionDate, DateTime? WaiverDate, string? WaiverState,
        string? GeneralCategory, string? Specialty);

    private sealed record OptOutRow(string? Specialty, DateTime? EffectiveDate, DateTime? EndDate, sbyte? CanOrderRefer);

    private sealed record OrderReferRow(sbyte? PartB, sbyte? Dme, sbyte? Hha, sbyte? Pmd, sbyte? Hospice);

    private sealed record ClinicianRow(string? MedicalSchool, short? GraduationYear, string? PrimarySpecialty, string? SecondarySpecialties,
        sbyte AcceptsAssignment, sbyte Telehealth);

    private sealed record GroupRow(string OrgPacId, string? GroupName, int? Members, sbyte AcceptsAssignment, string? City, string? State);

    private sealed record AffiliationRow(string FacilityType, string Ccn, string? Name, string? City, string? State, long? OverallRating, string? FacilityNpi);

    private sealed record FacilityRow(string Ccn, string Kind, string Name, string? Type, string? Ownership, string? City, string? State, string? Phone,
        sbyte? EmergencyServices, int? CertifiedBeds, long? OverallRating, long? InspectionRating, long? StaffingRating, long? QualityRating, long AffiliatedClinicians,
        long? SurveyRating, double? QualityOfCare, long? MortMeasures, long? MortBetter, long? MortWorse, long? SafetyMeasures, long? SafetyBetter,
        long? SafetyWorse, long? ReadmMeasures, long? ReadmBetter, long? ReadmWorse);

    private sealed record MipsRow(short Year, string? Source, string? Organization, double? FinalScore, double? Quality, double? Pi, double? Ia, double? Cost);

    private sealed record UtilizationRow(short DataYear, string? ProviderType, sbyte? Participating, int? DistinctServices, int? Beneficiaries,
        double? Services, double? AllowedAmount, double? PaymentAmount, double? AvgBeneficiaryAge, double? AvgRiskScore);

    private sealed record TopServiceRow(string Hcpcs, string? Description, sbyte? IsDrug, string? PlaceOfService, int? Beneficiaries, double? Services,
        double? AvgPayment);

    private sealed record PartDRow(short DataYear, string? PrescriberType, int? Claims, double? DrugCost, int? Beneficiaries, int? BrandClaims,
        int? GenericClaims, int? OpioidClaims, double? OpioidRate, int? AntibioticClaims);

    private sealed record PaymentSummaryRow(short ProgramYear, double TotalAmount, int Records, int Payers);

    private sealed record PaymentKindRow(string Nature, double Amount, int Records);

    private sealed record PayerRow(string Payer, double Amount, int Records);

    private sealed record PaymentYearRow(short Year, double General, int GeneralRecords, double Research, int ResearchRecords,
        double AssociatedResearch, int AssociatedResearchRecords, double OwnershipInvested, double OwnershipValue, int OwnershipRecords);

    private sealed record PaymentCompanyRow(string Company, double Total, double General, double Research, double AssociatedResearch,
        double Ownership, int Records);

    private sealed record ChangeRow(DateTime DetectedAt, string ChangeType, string? OldValue, string? NewValue);

    private sealed record EndpointRow(string? EndpointType, string? EndpointTypeDescription, string Endpoint, string? EndpointDescription,
        string? UseDescription, string? ContentDescription, string? AffiliationName, string? AffiliationCity, string? AffiliationState);

    public async Task<ProviderDetail?> GetAsync(string npi, CancellationToken ct)
    {
        if (!InputFormats.IsNpi(npi))
        {
            return null;
        }

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var p = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT npi AS Npi, entity_type AS EntityType, last_name AS LastName, first_name AS FirstName, middle_name AS MiddleName,
                   name_prefix AS NamePrefix, name_suffix AS NameSuffix, credential AS Credential, org_name AS OrgName, gender AS Gender,
                   enumeration_date AS EnumerationDate, last_update_date AS LastUpdateDate
            FROM provider WHERE npi = @npi
            """, new { npi }, cancellationToken: ct));
        if (p is null)
        {
            return null;
        }

        var taxonomies = await connection.QueryAsync<TaxRow>(new CommandDefinition(
            """
            SELECT t.slot AS Slot, t.taxonomy_code AS Code, c.Classification, c.Specialization, t.is_primary AS IsPrimary,
                   t.license_no AS LicenseNo, t.license_state AS LicenseState
            FROM provider_taxonomy t LEFT JOIN taxonomy_codes c ON c.Taxonomy_Code = t.taxonomy_code
            WHERE t.npi = @npi ORDER BY t.is_primary DESC, t.slot
            """, new { npi }, cancellationToken: ct));
        var locations = await connection.QueryAsync<LocRow>(new CommandDefinition(
            """
            SELECT is_primary AS IsPrimary, address1 AS Address1, address2 AS Address2, city AS City, state AS State, zip5 AS Zip5, zip4 AS Zip4,
                   postal_code AS PostalCode, country_code AS CountryCode, phone AS Phone
            FROM provider_location WHERE npi = @npi ORDER BY is_primary DESC, id
            """, new { npi }, cancellationToken: ct));
        var otherNames = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM provider_other_name WHERE npi = @npi ORDER BY id", new { npi }, cancellationToken: ct));
        var profile = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition(
            """
            SELECT is_sole_proprietor AS IsSoleProprietor, is_subpart AS IsSubpart, parent_org_name AS ParentOrgName,
                   official_prefix AS OfficialPrefix, official_first_name AS OfficialFirstName, official_middle_name AS OfficialMiddleName,
                   official_last_name AS OfficialLastName, official_suffix AS OfficialSuffix, official_credential AS OfficialCredential,
                   official_title AS OfficialTitle, official_phone AS OfficialPhone,
                   mailing_address1 AS MailingAddress1, mailing_address2 AS MailingAddress2, mailing_city AS MailingCity,
                   mailing_state AS MailingState, mailing_postal_code AS MailingPostalCode, mailing_country_code AS MailingCountryCode,
                   mailing_phone AS MailingPhone, mailing_fax AS MailingFax, practice_fax AS PracticeFax
            FROM provider_profile WHERE npi = @npi
            """, new { npi }, cancellationToken: ct));
        var identifiers = await connection.QueryAsync<IdentifierRow>(new CommandDefinition(
            "SELECT identifier AS Identifier, type_code AS TypeCode, state AS State, issuer AS Issuer FROM provider_identifier WHERE npi = @npi ORDER BY slot",
            new { npi }, cancellationToken: ct));
        var endpoints = await connection.QueryAsync<EndpointRow>(new CommandDefinition(
            """
            SELECT endpoint_type AS EndpointType, endpoint_type_description AS EndpointTypeDescription, endpoint AS Endpoint,
                   endpoint_description AS EndpointDescription, use_description AS UseDescription, content_description AS ContentDescription,
                   affiliation_name AS AffiliationName, affiliation_city AS AffiliationCity, affiliation_state AS AffiliationState
            FROM provider_endpoint WHERE npi = @npi ORDER BY id
            """, new { npi }, cancellationToken: ct));

        var exclusions = await connection.QueryAsync<ExclusionRow>(new CommandDefinition(
            """
            SELECT exclusion_type AS ExclusionType, exclusion_date AS ExclusionDate, waiver_date AS WaiverDate, waiver_state AS WaiverState,
                   general_category AS GeneralCategory, specialty AS Specialty
            FROM oig_exclusion WHERE npi = @npi ORDER BY exclusion_date, id
            """, new { npi }, cancellationToken: ct));
        var optOut = await connection.QueryFirstOrDefaultAsync<OptOutRow>(new CommandDefinition(
            """
            SELECT specialty AS Specialty, effective_date AS EffectiveDate, end_date AS EndDate, can_order_refer AS CanOrderRefer
            FROM medicare_opt_out WHERE npi = @npi ORDER BY end_date DESC, id DESC LIMIT 1
            """, new { npi }, cancellationToken: ct));
        var orderRefer = await connection.QueryFirstOrDefaultAsync<OrderReferRow>(new CommandDefinition(
            """
            SELECT MAX(part_b) AS PartB, MAX(dme) AS Dme, MAX(hha) AS Hha, MAX(pmd) AS Pmd, MAX(hospice) AS Hospice
            FROM medicare_order_referring WHERE npi = @npi HAVING COUNT(*) > 0
            """, new { npi }, cancellationToken: ct));
        var today = DateOnly.FromDateTime(DateTime.Today);

        var clinician = await connection.QuerySingleOrDefaultAsync<ClinicianRow>(new CommandDefinition(
            """
            SELECT medical_school AS MedicalSchool, graduation_year AS GraduationYear, primary_specialty AS PrimarySpecialty,
                   secondary_specialties AS SecondarySpecialties, accepts_assignment AS AcceptsAssignment, telehealth AS Telehealth
            FROM cc_clinician WHERE npi = @npi
            """, new { npi }, cancellationToken: ct));
        ProviderCareCompare? careCompare = null;
        if (clinician is not null)
        {
            var groups = await connection.QueryAsync<GroupRow>(new CommandDefinition(
                """
                SELECT org_pac_id AS OrgPacId, group_name AS GroupName, members AS Members, accepts_assignment AS AcceptsAssignment, city AS City, state AS State
                FROM cc_group WHERE npi = @npi ORDER BY group_name, org_pac_id
                """, new { npi }, cancellationToken: ct));
            var affiliations = await connection.QueryAsync<AffiliationRow>(new CommandDefinition(
                """
                SELECT a.facility_type AS FacilityType, a.ccn AS Ccn, COALESCE(h.name, n.name, hh.name, hs.name) AS Name,
                       COALESCE(h.city, n.city, hh.city, hs.city) AS City, COALESCE(h.state, n.state, hh.state, hs.state) AS State,
                       CAST(COALESCE(h.overall_rating, n.overall_rating) AS SIGNED) AS OverallRating,
                       (SELECT MIN(f.npi) FROM cms_facility_npi f WHERE f.ccn = a.ccn) AS FacilityNpi
                FROM (SELECT DISTINCT facility_type, ccn FROM cc_facility_affiliation WHERE npi = @npi) a
                LEFT JOIN cms_hospital h ON h.ccn = a.ccn
                LEFT JOIN cms_nursing_home n ON n.ccn = a.ccn
                LEFT JOIN cms_home_health hh ON hh.ccn = a.ccn
                LEFT JOIN cms_hospice hs ON hs.ccn = a.ccn
                ORDER BY a.facility_type, Name, a.ccn
                """, new { npi }, cancellationToken: ct));
            careCompare = new ProviderCareCompare(
                clinician.MedicalSchool, clinician.GraduationYear, clinician.PrimarySpecialty, clinician.SecondarySpecialties,
                clinician.AcceptsAssignment != 0, clinician.Telehealth != 0,
                groups.Select(g => new GroupPractice(g.OrgPacId, g.GroupName, g.Members, g.AcceptsAssignment != 0, g.City, g.State)).ToList(),
                affiliations.Select(a => new FacilityAffiliation(a.FacilityType, a.Ccn, a.Name, a.City, a.State, (int?)a.OverallRating, a.FacilityNpi)).ToList());
        }

        var utilization = await connection.QuerySingleOrDefaultAsync<UtilizationRow>(new CommandDefinition(
            """
            SELECT data_year AS DataYear, provider_type AS ProviderType, participating AS Participating, distinct_services AS DistinctServices,
                   beneficiaries AS Beneficiaries, services AS Services, allowed_amount AS AllowedAmount, payment_amount AS PaymentAmount,
                   avg_beneficiary_age AS AvgBeneficiaryAge, avg_risk_score AS AvgRiskScore
            FROM medicare_utilization WHERE npi = @npi
            """, new { npi }, cancellationToken: ct));
        var topServices = await connection.QueryAsync<TopServiceRow>(new CommandDefinition(
            """
            SELECT hcpcs AS Hcpcs, description AS Description, is_drug AS IsDrug, place_of_service AS PlaceOfService, beneficiaries AS Beneficiaries,
                   services AS Services, avg_payment AS AvgPayment
            FROM medicare_top_service WHERE npi = @npi ORDER BY service_rank
            """, new { npi }, cancellationToken: ct));
        var partD = await connection.QuerySingleOrDefaultAsync<PartDRow>(new CommandDefinition(
            """
            SELECT data_year AS DataYear, prescriber_type AS PrescriberType, claims AS Claims, drug_cost AS DrugCost, beneficiaries AS Beneficiaries,
                   brand_claims AS BrandClaims, generic_claims AS GenericClaims, opioid_claims AS OpioidClaims, opioid_rate AS OpioidRate,
                   antibiotic_claims AS AntibioticClaims
            FROM medicare_part_d WHERE npi = @npi
            """, new { npi }, cancellationToken: ct));

        var payments = await connection.QuerySingleOrDefaultAsync<PaymentSummaryRow>(new CommandDefinition(
            "SELECT program_year AS ProgramYear, total_amount AS TotalAmount, records AS Records, payers AS Payers FROM open_payments_summary WHERE npi = @npi",
            new { npi }, cancellationToken: ct));
        IndustryPayments? industry = null;
        if (payments is not null)
        {
            var kinds = await connection.QueryAsync<PaymentKindRow>(new CommandDefinition(
                "SELECT nature AS Nature, amount AS Amount, records AS Records FROM open_payments_nature WHERE npi = @npi ORDER BY amount DESC, nature",
                new { npi }, cancellationToken: ct));
            var payers = await connection.QueryAsync<PayerRow>(new CommandDefinition(
                "SELECT payer AS Payer, amount AS Amount, records AS Records FROM open_payments_payer WHERE npi = @npi ORDER BY payer_rank",
                new { npi }, cancellationToken: ct));
            industry = new IndustryPayments(payments.ProgramYear, payments.TotalAmount, payments.Records, payments.Payers,
                kinds.Select(k => new IndustryPaymentKind(k.Nature, k.Amount, k.Records)).ToList(),
                payers.Select(p => new IndustryPayer(p.Payer, p.Amount, p.Records)).ToList());
        }

        var paymentYears = (await connection.QueryAsync<PaymentYearRow>(new CommandDefinition(
            """
            SELECT program_year AS Year, general_amount AS General, general_records AS GeneralRecords, research_amount AS Research,
                   research_records AS ResearchRecords, associated_research_amount AS AssociatedResearch,
                   associated_research_records AS AssociatedResearchRecords, invested_amount AS OwnershipInvested, interest_value AS OwnershipValue,
                   ownership_records AS OwnershipRecords
            FROM open_payments_year WHERE npi = @npi ORDER BY program_year DESC
            """, new { npi }, cancellationToken: ct))).ToList();
        var paymentCompanies = (await connection.QueryAsync<PaymentCompanyRow>(new CommandDefinition(
            """
            SELECT company AS Company, total_amount AS Total, general_amount AS General, research_amount AS Research,
                   associated_research_amount AS AssociatedResearch, ownership_amount AS Ownership, records AS Records
            FROM open_payments_company WHERE npi = @npi ORDER BY company_rank
            """, new { npi }, cancellationToken: ct))).ToList();

        var facilities = await connection.QueryAsync<FacilityRow>(new CommandDefinition(
            """
            SELECT f.ccn AS Ccn, f.kind AS Kind, COALESCE(h.name, n.name, hh.name, hs.name) AS Name, COALESCE(h.hospital_type, n.provider_type) AS Type,
                   COALESCE(h.ownership, n.ownership, hh.ownership, hs.ownership) AS Ownership, COALESCE(h.city, n.city, hh.city, hs.city) AS City,
                   COALESCE(h.state, n.state, hh.state, hs.state) AS State, COALESCE(h.phone, n.phone, hh.phone, hs.phone) AS Phone,
                   h.emergency_services AS EmergencyServices, n.certified_beds AS CertifiedBeds,
                   CAST(COALESCE(h.overall_rating, n.overall_rating) AS SIGNED) AS OverallRating, CAST(n.inspection_rating AS SIGNED) AS InspectionRating,
                   CAST(n.staffing_rating AS SIGNED) AS StaffingRating, CAST(n.quality_rating AS SIGNED) AS QualityRating,
                   (SELECT COUNT(DISTINCT a.npi) FROM cc_facility_affiliation a WHERE a.ccn = f.ccn) AS AffiliatedClinicians,
                   CAST(COALESCE(sv.star_rating, hs.family_rating) AS SIGNED) AS SurveyRating, hh.quality_rating AS QualityOfCare,
                   CAST(h.mort_measures AS SIGNED) AS MortMeasures, CAST(h.mort_better AS SIGNED) AS MortBetter, CAST(h.mort_worse AS SIGNED) AS MortWorse,
                   CAST(h.safety_measures AS SIGNED) AS SafetyMeasures, CAST(h.safety_better AS SIGNED) AS SafetyBetter,
                   CAST(h.safety_worse AS SIGNED) AS SafetyWorse, CAST(h.readm_measures AS SIGNED) AS ReadmMeasures,
                   CAST(h.readm_better AS SIGNED) AS ReadmBetter, CAST(h.readm_worse AS SIGNED) AS ReadmWorse
            FROM cms_facility_npi f
            LEFT JOIN cms_hospital h ON f.kind = 'hospital' AND h.ccn = f.ccn
            LEFT JOIN cms_hospital_survey sv ON f.kind = 'hospital' AND sv.ccn = f.ccn
            LEFT JOIN cms_nursing_home n ON f.kind = 'nursing_home' AND n.ccn = f.ccn
            LEFT JOIN cms_home_health hh ON f.kind = 'home_health' AND hh.ccn = f.ccn
            LEFT JOIN cms_hospice hs ON f.kind = 'hospice' AND hs.ccn = f.ccn
            WHERE f.npi = @npi AND COALESCE(h.name, n.name, hh.name, hs.name) IS NOT NULL
            ORDER BY f.kind, Name
            """, new { npi }, cancellationToken: ct));

        // Specialty changes are stored as taxonomy codes and shown by name. Added/removed rows are for feeds, not this page.
        var changes = await connection.QueryAsync<ChangeRow>(new CommandDefinition(
            """
            SELECT c.detected_at AS DetectedAt, c.change_type AS ChangeType,
                   IF(c.change_type = 'specialty', COALESCE(CONCAT_WS(' – ', o.Classification, o.Specialization), c.old_value), c.old_value) AS OldValue,
                   IF(c.change_type = 'specialty', COALESCE(CONCAT_WS(' – ', n.Classification, n.Specialization), c.new_value), c.new_value) AS NewValue
            FROM provider_change c
            LEFT JOIN taxonomy_codes o ON c.change_type = 'specialty' AND o.Taxonomy_Code = c.old_value
            LEFT JOIN taxonomy_codes n ON c.change_type = 'specialty' AND n.Taxonomy_Code = c.new_value
            WHERE c.npi = @npi AND c.change_type IN ('name', 'credential', 'specialty', 'address')
            ORDER BY c.detected_at DESC, c.id DESC LIMIT 100
            """, new { npi }, cancellationToken: ct));

        var mips = await connection.QueryAsync<MipsRow>(new CommandDefinition(
            """
            SELECT program_year AS Year, source AS Source, facility_name AS Organization, final_score AS FinalScore, quality_score AS Quality,
                   pi_score AS Pi, ia_score AS Ia, cost_score AS Cost
            FROM cc_mips WHERE npi = @npi ORDER BY program_year DESC, final_score DESC, id LIMIT 5
            """, new { npi }, cancellationToken: ct));

        var standardCredential = (await SearchService.StandardCredentialsAsync(connection, [npi], ct)).GetValueOrDefault(npi);
        return new ProviderDetail(
            p.Npi, p.EntityType,
            ProviderNames.Display(p.EntityType, p.LastName, p.FirstName, p.MiddleName, p.NameSuffix, p.OrgName),
            p.NamePrefix, standardCredential ?? p.Credential, p.Gender,
            p.EnumerationDate is null ? null : DateOnly.FromDateTime(p.EnumerationDate.Value),
            p.LastUpdateDate is null ? null : DateOnly.FromDateTime(p.LastUpdateDate.Value),
            taxonomies.Select(t => new ProviderTaxonomy(t.Slot, t.Code, t.Classification, t.Specialization, t.IsPrimary != 0, t.LicenseNo, t.LicenseState)).ToList(),
            locations.Select(l => new ProviderLocation(l.IsPrimary != 0, l.Address1, l.Address2, l.City, l.State,
                ProviderNames.Zip(l.Zip5, l.Zip4, l.PostalCode), l.CountryCode, l.Phone)).ToList(),
            otherNames.ToList())
        {
            Profile = profile is null ? null : ToProfile(profile),
            Identifiers = identifiers.Select(i => new ProviderIdentifier(i.Identifier, i.TypeCode, ProviderIdentifier.Describe(i.TypeCode), i.State, i.Issuer)).ToList(),
            Endpoints = endpoints.Select(e => new ProviderEndpoint(e.EndpointType, e.EndpointTypeDescription, e.Endpoint, e.EndpointDescription,
                e.UseDescription, e.ContentDescription, e.AffiliationName, e.AffiliationCity, e.AffiliationState)).ToList(),
            Compliance = new ProviderCompliance(
                exclusions.Select(x => new OigExclusion(x.ExclusionType, ComplianceCodes.DescribeOigType(x.ExclusionType), ToDate(x.ExclusionDate),
                    ToDate(x.WaiverDate), x.WaiverState, x.GeneralCategory, x.Specialty)).ToList(),
                optOut is null ? null : new MedicareOptOut(optOut.Specialty, ToDate(optOut.EffectiveDate), ToDate(optOut.EndDate),
                    ToDate(optOut.EndDate) is not { } end || end >= today, optOut.CanOrderRefer is null ? null : optOut.CanOrderRefer != 0),
                orderRefer is null ? null : new MedicareOrderReferring(orderRefer.PartB == 1, orderRefer.Dme == 1, orderRefer.Hha == 1,
                    orderRefer.Pmd == 1, orderRefer.Hospice == 1)),
            CareCompare = careCompare,
            IndustryPayments = industry,
            PaymentHistory = paymentYears.Count == 0 && paymentCompanies.Count == 0 ? null : new IndustryPaymentHistory(
                paymentYears.Select(y => new IndustryPaymentYear(y.Year, y.General, y.GeneralRecords, y.Research, y.ResearchRecords, y.AssociatedResearch,
                    y.AssociatedResearchRecords, y.OwnershipInvested, y.OwnershipValue, y.OwnershipRecords)).ToList(),
                paymentCompanies.Select(c => new IndustryPaymentCompany(c.Company, c.Total, c.General, c.Research, c.AssociatedResearch, c.Ownership,
                    c.Records)).ToList()),
            Changes = changes.Select(c => new ProviderChange(DateOnly.FromDateTime(c.DetectedAt), c.ChangeType, c.OldValue, c.NewValue)).ToList(),
            MedicareServices = utilization is null ? null : new MedicareServices(utilization.DataYear, utilization.ProviderType,
                utilization.Participating is null ? null : utilization.Participating != 0, utilization.DistinctServices, utilization.Beneficiaries,
                utilization.Services, utilization.AllowedAmount, utilization.PaymentAmount, utilization.AvgBeneficiaryAge, utilization.AvgRiskScore,
                topServices.Select(s => new MedicareService(s.Hcpcs, s.Description, s.IsDrug == 1,
                    s.PlaceOfService switch { "F" => "Facility", "O" => "Office", _ => s.PlaceOfService }, s.Beneficiaries, s.Services, s.AvgPayment)).ToList()),
            MedicarePrescribing = partD is null ? null : new MedicarePrescribing(partD.DataYear, partD.PrescriberType, partD.Claims, partD.DrugCost,
                partD.Beneficiaries, partD.BrandClaims, partD.GenericClaims, partD.OpioidClaims, partD.OpioidRate, partD.AntibioticClaims),
            Facilities = facilities.Select(f => new CertifiedFacility(f.Ccn, f.Kind, f.Name, f.Type, f.Ownership, f.City, f.State, f.Phone,
                f.EmergencyServices is null ? null : f.EmergencyServices != 0, f.CertifiedBeds, (int?)f.OverallRating, (int?)f.InspectionRating, (int?)f.StaffingRating,
                (int?)f.QualityRating, (int)f.AffiliatedClinicians)
            {
                PatientSurveyRating = (int?)f.SurveyRating,
                QualityOfCareRating = f.QualityOfCare,
                Outcomes = f.Kind != "hospital" || (f.MortMeasures is null && f.SafetyMeasures is null && f.ReadmMeasures is null) ? null : new HospitalOutcomes(
                    new OutcomeCounts((int?)f.MortMeasures, (int?)f.MortBetter, (int?)f.MortWorse),
                    new OutcomeCounts((int?)f.SafetyMeasures, (int?)f.SafetyBetter, (int?)f.SafetyWorse),
                    new OutcomeCounts((int?)f.ReadmMeasures, (int?)f.ReadmBetter, (int?)f.ReadmWorse)),
            }).ToList(),
            MipsScores = mips.Select(m => new MipsScore(m.Year, m.Source, m.Organization, m.FinalScore, m.Quality, m.Pi, m.Ia, m.Cost)).ToList(),
        };
    }

    private static DateOnly? ToDate(DateTime? value) => value is null ? null : DateOnly.FromDateTime(value.Value);

    private static ProviderProfile ToProfile(ProfileRow r)
    {
        var officialName = string.Join(" ", new[] { r.OfficialPrefix, r.OfficialFirstName, r.OfficialMiddleName, r.OfficialLastName, r.OfficialSuffix }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        var official = officialName.Length == 0 ? null : new AuthorizedOfficial(officialName, r.OfficialCredential, r.OfficialTitle, r.OfficialPhone);
        var mailing = r.MailingAddress1 is null && r.MailingCity is null
            ? null
            : new MailingAddress(r.MailingAddress1, r.MailingAddress2, r.MailingCity, r.MailingState,
                ProviderNames.PostalCode(r.MailingPostalCode, r.MailingCountryCode), r.MailingCountryCode, r.MailingPhone, r.MailingFax);
        return new ProviderProfile(
            r.IsSoleProprietor is null ? null : r.IsSoleProprietor != 0,
            r.IsSubpart is null ? null : r.IsSubpart != 0,
            r.ParentOrgName, official, mailing, r.PracticeFax);
    }
}
