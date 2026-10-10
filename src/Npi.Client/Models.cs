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

    /// <summary>Open Payments general payments in the newest program year; null when none.</summary>
    public IndustryPayments? IndustryPayments { get; set; }

    /// <summary>State license records and board actions (CO, IL, NY actions, TX, WA open data) matched by license state, number and last name.</summary>
    public IReadOnlyList<StateLicenseRecord> StateLicenses { get; set; } = [];

    /// <summary>MIPS scores in the newest program year, best first.</summary>
    public IReadOnlyList<MipsScore> MipsScores { get; set; } = [];

    /// <summary>Open Payments per program year (2019 on) and the top companies over all years; null when none.</summary>
    public IndustryPaymentHistory? PaymentHistory { get; set; }

    /// <summary>Changes recorded since change tracking began (October 2026), newest first.</summary>
    public IReadOnlyList<ProviderChange> Changes { get; set; } = [];
}

/// <summary>A change noticed between two weekly/monthly NPPES loads.</summary>
public sealed class ProviderChange
{
    public DateTime RecordedOn { get; set; }

    /// <summary>name, credential, specialty or address (primary practice address).</summary>
    public string Type { get; set; } = "";

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }
}

/// <summary>Open Payments (Sunshine Act) general payments from drug and device makers to one NPI.</summary>
public sealed class IndustryPayments
{
    /// <summary>Program year.</summary>
    public int Year { get; set; }

    public double TotalAmount { get; set; }

    public int Records { get; set; }

    public int Payers { get; set; }

    /// <summary>Amounts by nature of payment, largest first.</summary>
    public IReadOnlyList<IndustryPaymentKind> ByNature { get; set; } = [];

    /// <summary>The three largest payers.</summary>
    public IReadOnlyList<IndustryPayer> TopPayers { get; set; } = [];

    /// <summary>The products named most in these payments; <see cref="IndustryProduct.Slug"/> is for <see cref="NpiClient.GetProductAsync"/>.</summary>
    public IReadOnlyList<IndustryProduct> TopProducts { get; set; } = [];
}

/// <summary>A product named in general payments to a provider.</summary>
public sealed class IndustryProduct
{
    public string Slug { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Kind { get; set; }

    public double Amount { get; set; }

    public int Records { get; set; }
}

/// <summary>A page of products named in Open Payments, largest payments first.</summary>
public sealed class ProductPage
{
    public IReadOnlyList<ProductSummary> Items { get; set; } = [];

    public int Page { get; set; }

    public int PageSize { get; set; }

    public int TotalCount { get; set; }
}

/// <summary>One product in a list.</summary>
public sealed class ProductSummary
{
    public string Slug { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Drug, Biological, Device or Medical Supply.</summary>
    public string? Kind { get; set; }

    public string? Category { get; set; }

    public int Year { get; set; }

    /// <summary>General payments naming the product (a payment naming several products counts for each).</summary>
    public double Amount { get; set; }

    public int Records { get; set; }

    public int Companies { get; set; }

    public int Providers { get; set; }
}

/// <summary>A product named in Open Payments: who pays for it, what for, to whom, and research naming it.</summary>
public sealed class ProductDetail
{
    public string Slug { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Kind { get; set; }

    public string? Category { get; set; }

    /// <summary>The NDC (drug code) used most in the payments.</summary>
    public string? Ndc { get; set; }

    /// <summary>The device identifier (GUDID primary DI) used most in the payments.</summary>
    public string? DeviceId { get; set; }

    public int Year { get; set; }

    public double Amount { get; set; }

    public int Records { get; set; }

    public int CompanyCount { get; set; }

    public int Providers { get; set; }

    public IReadOnlyList<ProductCompany> Companies { get; set; } = [];

    public IReadOnlyList<ProductNature> ByNature { get; set; } = [];

    public IReadOnlyList<ProductSpecialty> TopSpecialties { get; set; } = [];

    public IReadOnlyList<ProductRecipient> TopProviders { get; set; } = [];

    public ProductResearch? Research { get; set; }

    /// <summary>FDA's facts when the product's NDC is in the NDC directory.</summary>
    public ProductDrugInfo? Drug { get; set; }

    /// <summary>FDA's GUDID facts for the device identifier named most.</summary>
    public ProductDeviceInfo? Device { get; set; }
}

/// <summary>What a drug or biological is, from FDA's NDC directory, Drugs@FDA and its label.</summary>
public sealed class ProductDrugInfo
{
    public string ProductNdc { get; set; } = "";

    public string? BrandName { get; set; }

    public string? GenericName { get; set; }

    public string? ActiveIngredients { get; set; }

    public string? DosageForm { get; set; }

    public string? Route { get; set; }

    public string? Labeler { get; set; }

    public string? MarketingCategory { get; set; }

    public string? ApplicationNumber { get; set; }

    public string? ProductType { get; set; }

    /// <summary>FDA established pharmacologic classes.</summary>
    public string? PharmClasses { get; set; }

    public string? Sponsor { get; set; }

    public DateTime? ApprovalDate { get; set; }

    public DateTime? MarketingStart { get; set; }

    /// <summary>Other labelers listing the same generic name.</summary>
    public int OtherMakers { get; set; }

    /// <summary>Of those, labelers with an approved generic (ANDA).</summary>
    public int GenericMakers { get; set; }

    public string? Indications { get; set; }

    public string? BoxedWarning { get; set; }

    public DateTime? LabelDate { get; set; }

    public string? LabelSetId { get; set; }

    public string? DailyMedUrl { get; set; }

    public string? DrugsAtFdaUrl { get; set; }
}

/// <summary>What a device is, from FDA's GUDID.</summary>
public sealed class ProductDeviceInfo
{
    public string DeviceId { get; set; } = "";

    public string? BrandName { get; set; }

    public string? Company { get; set; }

    public string? Description { get; set; }

    public string? Model { get; set; }

    public string? GmdnTerm { get; set; }

    public string? GmdnDefinition { get; set; }

    public string? ProductCode { get; set; }

    public string? ProductCodeName { get; set; }

    /// <summary>FDA class 1, 2 or 3 (highest risk).</summary>
    public string? DeviceClass { get; set; }

    public string? MedicalSpecialty { get; set; }

    public bool? IsRx { get; set; }

    public bool? IsOtc { get; set; }

    public bool? Implantable { get; set; }

    public string? DistributionStatus { get; set; }

    public IReadOnlyList<ProductPremarket> Premarket { get; set; } = [];

    public string GudidUrl { get; set; } = "";
}

/// <summary>A 510(k), PMA or De Novo decision a device cites.</summary>
public sealed class ProductPremarket
{
    public string Number { get; set; } = "";

    public string Kind { get; set; } = "";

    public string? Applicant { get; set; }

    public string? DeviceName { get; set; }

    public DateTime? DecisionDate { get; set; }

    public string? Decision { get; set; }

    public string Url { get; set; } = "";
}

/// <summary>A company whose payments named the product.</summary>
public sealed class ProductCompany
{
    public string CompanyId { get; set; } = "";

    public string Name { get; set; } = "";

    public double Amount { get; set; }

    public int Records { get; set; }
}

/// <summary>Payments naming the product, of one kind.</summary>
public sealed class ProductNature
{
    public string Nature { get; set; } = "";

    public double Amount { get; set; }

    public int Records { get; set; }
}

/// <summary>A specialty paid in payments naming the product.</summary>
public sealed class ProductSpecialty
{
    public string Specialty { get; set; } = "";

    public int Providers { get; set; }

    public double Amount { get; set; }
}

/// <summary>An active provider paid in payments naming the product.</summary>
public sealed class ProductRecipient
{
    public string Npi { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Credential { get; set; }

    public string? Specialty { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public double Amount { get; set; }

    public int Records { get; set; }
}

/// <summary>Research payments naming the product.</summary>
public sealed class ProductResearch
{
    public int Year { get; set; }

    public double Amount { get; set; }

    public int Records { get; set; }

    public int Studies { get; set; }

    public IReadOnlyList<ProductStudy> TopStudies { get; set; } = [];
}

/// <summary>A research study naming the product.</summary>
public sealed class ProductStudy
{
    public string? Study { get; set; }

    public string? NctId { get; set; }

    public string? CompanyId { get; set; }

    public string? CompanyName { get; set; }

    public double Amount { get; set; }

    public int Records { get; set; }

    public string? ClinicalTrialsUrl { get; set; }
}

/// <summary>Payments of one kind (e.g. "Food and Beverage", "Consulting Fee").</summary>
public sealed class IndustryPaymentKind
{
    public string Nature { get; set; } = "";

    public double Amount { get; set; }

    public int Records { get; set; }
}

/// <summary>A company that paid the recipient.</summary>
public sealed class IndustryPayer
{
    public string Name { get; set; } = "";

    public double Amount { get; set; }

    public int Records { get; set; }

    /// <summary>The company's Open Payments ID, for <see cref="NpiClient.GetCompanyAsync"/>.</summary>
    public string? CompanyId { get; set; }
}

/// <summary>Open Payments over every published program year.</summary>
public sealed class IndustryPaymentHistory
{
    /// <summary>Newest year first.</summary>
    public IReadOnlyList<IndustryPaymentYear> Years { get; set; } = [];

    /// <summary>The five companies that paid the most over all years.</summary>
    public IReadOnlyList<IndustryPaymentCompany> TopCompanies { get; set; } = [];
}

/// <summary>
/// One program year: general payments, research payments, research funding as a principal investigator (paid to an
/// institution), and ownership or investment interests (amount invested, value of the interest).
/// </summary>
public sealed class IndustryPaymentYear
{
    public int Year { get; set; }

    public double General { get; set; }

    public int GeneralRecords { get; set; }

    public double Research { get; set; }

    public int ResearchRecords { get; set; }

    public double AssociatedResearch { get; set; }

    public int AssociatedResearchRecords { get; set; }

    public double OwnershipInvested { get; set; }

    public double OwnershipValue { get; set; }

    public int OwnershipRecords { get; set; }
}

/// <summary>A company among the recipient's top payers over all years, by payment type.</summary>
public sealed class IndustryPaymentCompany
{
    public string Name { get; set; } = "";

    public double Total { get; set; }

    public double General { get; set; }

    public double Research { get; set; }

    public double AssociatedResearch { get; set; }

    public double Ownership { get; set; }

    public int Records { get; set; }

    /// <summary>The company's Open Payments ID, for <see cref="NpiClient.GetCompanyAsync"/>.</summary>
    public string? CompanyId { get; set; }
}

/// <summary>A page of companies that report to Open Payments, largest payments first.</summary>
public sealed class CompanyPage
{
    public IReadOnlyList<CompanySummary> Items { get; set; } = [];

    public int Page { get; set; }

    public int PageSize { get; set; }

    public int TotalCount { get; set; }
}

/// <summary>One company in a list.</summary>
public sealed class CompanySummary
{
    /// <summary>Open Payments ID.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string? State { get; set; }

    public string? Country { get; set; }

    /// <summary>General + research payments over every published program year.</summary>
    public double Payments { get; set; }

    /// <summary>Value of physicians' ownership or investment interests (not a payment).</summary>
    public double OwnershipValue { get; set; }

    /// <summary>NPIs it paid, all years.</summary>
    public int? Providers { get; set; }

    public int? FirstYear { get; set; }

    public int? LastYear { get; set; }
}

/// <summary>A company that reports to Open Payments: who it is, what it paid each year, what for, for which products and to whom.</summary>
public sealed class CompanyDetail
{
    /// <summary>Open Payments ID.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public IReadOnlyList<string> OtherNames { get; set; } = [];

    public string? State { get; set; }

    public string? Country { get; set; }

    public double General { get; set; }

    public double Research { get; set; }

    public double OwnershipInvested { get; set; }

    public double OwnershipValue { get; set; }

    public int? FirstYear { get; set; }

    public int? LastYear { get; set; }

    /// <summary>NPIs it paid, all years (active or not).</summary>
    public int? Providers { get; set; }

    /// <summary>Newest year first.</summary>
    public IReadOnlyList<CompanyYear> Years { get; set; } = [];

    /// <summary>The program year of <see cref="ByNature"/> and <see cref="TopProducts"/>.</summary>
    public int? DetailYear { get; set; }

    public IReadOnlyList<CompanyNature> ByNature { get; set; } = [];

    public IReadOnlyList<CompanyProduct> TopProducts { get; set; } = [];

    public IReadOnlyList<CompanySpecialty> TopSpecialties { get; set; } = [];

    public IReadOnlyList<CompanyRecipient> TopProviders { get; set; } = [];

    /// <summary>Other companies sharing the name's distinctive word (may be related; a name match only).</summary>
    public IReadOnlyList<CompanySummary> SimilarNames { get; set; } = [];

    /// <summary>FDA recalls by a firm of the same name; null when none matched.</summary>
    public CompanyRecalls? Recalls { get; set; }

    /// <summary>SEC registrants of the same name.</summary>
    public IReadOnlyList<CompanySecListing> SecListings { get; set; } = [];

    /// <summary>OIG integrity agreements naming an entity of the same name.</summary>
    public IReadOnlyList<CompanyIntegrityAgreement> IntegrityAgreements { get; set; } = [];

    /// <summary>The company's page on CMS's Open Payments site.</summary>
    public string OpenPaymentsUrl { get; set; } = "";
}

/// <summary>FDA recalls (drug and device enforcement reports) by firms whose name matches the company's.</summary>
public sealed class CompanyRecalls
{
    public int Total { get; set; }

    public int ClassI { get; set; }

    public int ClassII { get; set; }

    public int ClassIII { get; set; }

    public int Ongoing { get; set; }

    /// <summary>The recalling firm names that matched.</summary>
    public IReadOnlyList<string> Firms { get; set; } = [];

    /// <summary>Newest first.</summary>
    public IReadOnlyList<CompanyRecall> Latest { get; set; } = [];
}

/// <summary>One FDA recall.</summary>
public sealed class CompanyRecall
{
    public string RecallNumber { get; set; } = "";

    /// <summary>Drugs or Devices.</summary>
    public string ProductType { get; set; } = "";

    public string? Firm { get; set; }

    public string? Classification { get; set; }

    public string? Status { get; set; }

    public DateTime? Initiated { get; set; }

    public string? Product { get; set; }

    public string? Reason { get; set; }
}

/// <summary>A public company registered with the SEC.</summary>
public sealed class CompanySecListing
{
    public int Cik { get; set; }

    public string Ticker { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Exchange { get; set; }

    /// <summary>True for the parent company of a subsidiary (from a hand-made list), false for a registrant of the same name.</summary>
    public bool IsParent { get; set; }

    /// <summary>For a parent: how they're related ("U.S. subsidiary", "acquired 2021" …).</summary>
    public string? Note { get; set; }

    public string EdgarUrl { get; set; } = "";
}

/// <summary>An HHS-OIG integrity agreement.</summary>
public sealed class CompanyIntegrityAgreement
{
    public string Name { get; set; } = "";

    public string? Location { get; set; }

    public string? Type { get; set; }

    /// <summary>Effective, Closed, Suspended, …</summary>
    public string? Status { get; set; }

    public DateTime? StatusDate { get; set; }

    public string Url { get; set; } = "";
}

/// <summary>A company's payments in one program year.</summary>
public sealed class CompanyYear
{
    public int Year { get; set; }

    public double General { get; set; }

    public int GeneralRecords { get; set; }

    public double Research { get; set; }

    public int ResearchRecords { get; set; }

    public double OwnershipInvested { get; set; }

    public double OwnershipValue { get; set; }

    public int OwnershipRecords { get; set; }
}

/// <summary>General payments of one kind.</summary>
public sealed class CompanyNature
{
    public string Nature { get; set; } = "";

    public double Amount { get; set; }

    public int Records { get; set; }
}

/// <summary>A product named first on the company's general payments.</summary>
public sealed class CompanyProduct
{
    /// <summary>The product page's key, for <see cref="NpiClient.GetProductAsync"/>.</summary>
    public string? Slug { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Drug, Device, Biological or Medical Supply.</summary>
    public string? Kind { get; set; }

    public string? Category { get; set; }

    public double Amount { get; set; }

    public int Records { get; set; }
}

/// <summary>A specialty the company paid (primary classification of active providers), all years.</summary>
public sealed class CompanySpecialty
{
    public string Specialty { get; set; } = "";

    public int Providers { get; set; }

    public double Amount { get; set; }
}

/// <summary>An active provider the company paid, all years, by payment type.</summary>
public sealed class CompanyRecipient
{
    public string Npi { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Credential { get; set; }

    public string? Specialty { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public double Total { get; set; }

    public double General { get; set; }

    public double Research { get; set; }

    public double AssociatedResearch { get; set; }

    public double Ownership { get; set; }

    public int Records { get; set; }
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

    /// <summary>"hospital", "nursing_home", "home_health" or "hospice".</summary>
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

    /// <summary>Patient experience stars 1-5: HCAHPS for a hospital, the family caregiver survey for a hospice.</summary>
    public int? PatientSurveyRating { get; set; }

    /// <summary>A home health agency's quality of patient care stars, 1-5 in half stars.</summary>
    public double? QualityOfCareRating { get; set; }

    /// <summary>A hospital's outcome measures compared with the national rate; null for other kinds.</summary>
    public HospitalOutcomes? Outcomes { get; set; }
}

/// <summary>How many of a hospital's measures in one group CMS rates better / worse than the national rate.</summary>
public sealed class OutcomeCounts
{
    public int? Measures { get; set; }

    public int? Better { get; set; }

    public int? Worse { get; set; }
}

/// <summary>Hospital mortality, safety (infections, complications) and readmission measures.</summary>
public sealed class HospitalOutcomes
{
    public OutcomeCounts Mortality { get; set; } = new();

    public OutcomeCounts Safety { get; set; } = new();

    public OutcomeCounts Readmissions { get; set; } = new();
}

/// <summary>One action a state board took on a license.</summary>
public sealed class StateBoardAction
{
    public DateTime? Date { get; set; }

    public string? Action { get; set; }

    public string? Description { get; set; }
}

/// <summary>A state license record from the state's open data. New York publishes only board actions (no status).</summary>
public sealed class StateLicenseRecord
{
    public string State { get; set; } = "";

    /// <summary>ny_bpmc, tx_tmb, wa_doh, il_idfpr or co_dora.</summary>
    public string Source { get; set; } = "";

    public string? LicenseNumber { get; set; }

    public string? LicenseType { get; set; }

    public string? Status { get; set; }

    public DateTime? ExpirationDate { get; set; }

    /// <summary>The state's own wording, e.g. "Yes", "NONE", "Y".</summary>
    public string? Discipline { get; set; }

    public string? VerifyUrl { get; set; }

    public IReadOnlyList<StateBoardAction> Actions { get; set; } = [];

    public string SourceName { get; set; } = "";

    public string SourceUrl { get; set; } = "";
}

/// <summary>One MIPS score (0-100; null when a category wasn't scored).</summary>
public sealed class MipsScore
{
    public int Year { get; set; }

    /// <summary>individual, group, apm, subgroup or virtual group.</summary>
    public string? Source { get; set; }

    public string? Organization { get; set; }

    public double? FinalScore { get; set; }

    public double? Quality { get; set; }

    public double? PromotingInteroperability { get; set; }

    public double? ImprovementActivities { get; set; }

    public double? Cost { get; set; }
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
public sealed class CredentialInfo
{
    /// <summary>A standardized credential, e.g. MD, PhD, PA-C.</summary>
    public string Credential { get; set; } = "";

    /// <summary>Active providers holding it.</summary>
    public int Providers { get; set; }
}

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

/// <summary>What a bulk lookup found for one NPI.</summary>
public enum LookupStatus
{
    /// <summary>An active provider; <see cref="LookupRow.Provider"/> is set.</summary>
    Found,

    /// <summary>Unknown, or deactivated (deactivated NPIs are never shown).</summary>
    NotFound,

    /// <summary>Not 10 digits, or the check digit is wrong.</summary>
    Invalid,
}

/// <summary>One requested NPI in a bulk lookup.</summary>
public sealed class LookupRow
{
    public string Npi { get; set; } = "";

    public LookupStatus Status { get; set; }

    public ProviderSummary? Provider { get; set; }
}

/// <summary>The API's bulk lookup response.</summary>
public sealed class LookupResponse
{
    public IReadOnlyList<LookupRow> Items { get; set; } = [];
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
