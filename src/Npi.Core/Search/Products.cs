using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>A company whose payments named the product, in <see cref="ProductDetail.Year"/>.</summary>
public sealed record ProductCompany(string CompanyId, string Name, double Amount, int Records);

/// <summary>Payments naming the product, of one kind (nature of payment).</summary>
public sealed record ProductNature(string Nature, double Amount, int Records);

/// <summary>A specialty (primary NUCC classification of active providers) paid in payments naming the product.</summary>
public sealed record ProductSpecialty(string Specialty, int Providers, double Amount);

/// <summary>An active provider paid in general payments naming the product.</summary>
public sealed record ProductRecipient(string Npi, string Name, string? Credential, string? Specialty, string? City, string? State, double Amount, int Records);

/// <summary>A research study whose payments named the product (largest first), with its ClinicalTrials.gov ID when reported.</summary>
public sealed record ProductStudy(string? Study, string? NctId, string? CompanyId, string? CompanyName, double Amount, int Records)
{
    /// <summary>The study on ClinicalTrials.gov, when the ID looks like one (NCT + 8 digits).</summary>
    public string? ClinicalTrialsUrl => NctId is { Length: 11 } id && id.StartsWith("NCT", StringComparison.OrdinalIgnoreCase) && id[3..].All(char.IsAsciiDigit)
        ? $"https://clinicaltrials.gov/study/{id.ToUpperInvariant()}"
        : null;
}

/// <summary>Research payments naming the product in one program year: totals and the largest studies.</summary>
public sealed record ProductResearch(int Year, double Amount, int Records, int Studies, IReadOnlyList<ProductStudy> TopStudies);

/// <summary>
/// What a drug or biological is, from FDA (item 19, part 2): its NDC directory listing (the product's NDC labeler and product
/// parts), the Drugs@FDA application, and its label's indications and boxed warning. <see cref="OtherMakers"/> counts the
/// other labelers listing the same generic name (generics, repackagers, authorized generics); <see cref="GenericMakers"/>
/// those of them with an ANDA (approved generics).
/// </summary>
/// <remarks>
/// <see cref="MatchedBy"/> is "ndc" (the NDC reported in the payments is listed) or "name" (it isn't, and the brand name matched:
/// the listing chosen is the one under the application most listings of that name use). A listing without a drug class or
/// label borrows them from a listing of the same generic name / the same application.
/// </remarks>
public sealed record ProductDrugInfo(string ProductNdc, string? BrandName, string? GenericName, string? ActiveIngredients, string? DosageForm,
    string? Route, string? Labeler, string? MarketingCategory, string? ApplicationNumber, string? ProductType, string? PharmClasses, string? Sponsor,
    DateOnly? ApprovalDate, DateOnly? MarketingStart, int OtherMakers, int GenericMakers, string? Indications, string? BoxedWarning,
    DateOnly? LabelDate, string? LabelSetId, string MatchedBy = "ndc")
{
    /// <summary>The label on DailyMed (NLM), when FDA's files had one.</summary>
    public string? DailyMedUrl => LabelSetId is null ? null : $"https://dailymed.nlm.nih.gov/dailymed/lookup.cfm?setid={Uri.EscapeDataString(LabelSetId)}";

    /// <summary>The application on Drugs@FDA (NDA and ANDA; BLAs are listed by CBER/Purple Book instead).</summary>
    public string? DrugsAtFdaUrl => ApplicationNumber is { } a && (a.StartsWith("NDA", StringComparison.OrdinalIgnoreCase) || a.StartsWith("ANDA", StringComparison.OrdinalIgnoreCase))
        ? $"https://www.accessdata.fda.gov/scripts/cder/daf/index.cfm?event=overview.process&ApplNo={new string(a.Where(char.IsAsciiDigit).ToArray())}"
        : null;
}

/// <summary>A 510(k) clearance, PMA approval or De Novo grant a device cites; the details are from FDA's files when found.</summary>
public sealed record ProductPremarket(string Number, string Kind, string? Applicant, string? DeviceName, DateOnly? DecisionDate, string? Decision)
{
    public string Url => Kind switch
    {
        "PMA" => $"https://www.accessdata.fda.gov/scripts/cdrh/cfdocs/cfpma/pma.cfm?id={Uri.EscapeDataString(Number)}",
        "De Novo" => $"https://www.accessdata.fda.gov/scripts/cdrh/cfdocs/cfpmn/denovo.cfm?id={Uri.EscapeDataString(Number)}",
        _ => $"https://www.accessdata.fda.gov/scripts/cdrh/cfdocs/cfpmn/pmn.cfm?ID={Uri.EscapeDataString(Number)}",
    };

    /// <summary>The kind of an FDA premarket number: K… 510(k), P… PMA, DEN… De Novo.</summary>
    public static string KindOf(string number) =>
        number.StartsWith("DEN", StringComparison.OrdinalIgnoreCase) ? "De Novo"
        : number.StartsWith('P') || number.StartsWith('p') ? "PMA"
        : number.StartsWith('K') || number.StartsWith('k') ? "510(k)" : "Other";
}

/// <summary>
/// What a device is, from FDA's GUDID record of the device identifier used most in the payments (item 19, part 2): one
/// model of the product line, with its GMDN term, FDA product code and class, and its premarket decisions.
/// </summary>
public sealed record ProductDeviceInfo(string DeviceId, string? BrandName, string? Company, string? Description, string? Model, string? GmdnTerm,
    string? GmdnDefinition, string? ProductCode, string? ProductCodeName, string? DeviceClass, string? MedicalSpecialty, bool? IsRx, bool? IsOtc,
    bool? Implantable, string? DistributionStatus, IReadOnlyList<ProductPremarket> Premarket)
{
    public string GudidUrl => $"https://accessgudid.nlm.nih.gov/devices/{Uri.EscapeDataString(DeviceId)}";
}

/// <summary>
/// A drug, biological, device or medical supply named in Open Payments (CLAUDE.md §7 Stage 5.5 item 19), keyed by
/// <see cref="Slug"/>. Every product a payment names counts with the payment's full amount, so a payment naming two
/// products counts for both. <see cref="Name"/>, <see cref="Kind"/>, <see cref="Category"/>, <see cref="Ndc"/> and
/// <see cref="DeviceId"/> are the values used most in the payments.
/// </summary>
public sealed record ProductDetail(string Slug, string Name, string? Kind, string? Category, string? Ndc, string? DeviceId, int Year, double Amount,
    int Records, int CompanyCount, int Providers, IReadOnlyList<ProductCompany> Companies, IReadOnlyList<ProductNature> ByNature,
    IReadOnlyList<ProductSpecialty> TopSpecialties, IReadOnlyList<ProductRecipient> TopProviders, ProductResearch? Research)
{
    /// <summary>FDA's facts when the product's NDC is in the NDC directory (part 2); null otherwise.</summary>
    public ProductDrugInfo? Drug { get; init; }

    /// <summary>FDA's GUDID facts when the product's device identifier is in GUDID (part 2); null otherwise.</summary>
    public ProductDeviceInfo? Device { get; init; }

    /// <summary>Medicare Part D / Part B and Medicaid spending on the drug by year (part 3), by brand name.</summary>
    public IReadOnlyList<ProductSpending> Spending { get; init; } = [];

    /// <summary>NADAC prices of the drug's package NDCs (part 3).</summary>
    public IReadOnlyList<ProductPrice> Prices { get; init; } = [];

    /// <summary>FDA drug shortage listings (part 3).</summary>
    public IReadOnlyList<ProductShortage> Shortages { get; init; } = [];

    /// <summary>FDA recalls naming the product (part 3); null when none.</summary>
    public ProductRecalls? Recalls { get; init; }

    /// <summary>Adverse event report counts (part 3); null until asked.</summary>
    public ProductAdverseEvents? AdverseEvents { get; init; }

    /// <summary>ClinicalTrials.gov study counts (part 3); null until asked.</summary>
    public ProductTrials? Trials { get; init; }

    /// <summary>Medicare Part D prescribing and how much of it the paid providers wrote (part 4); null without a Part D brand.</summary>
    public ProductPrescribing? Prescribing { get; init; }
}

/// <summary>One product in a list.</summary>
public sealed record ProductSummary(string Slug, string Name, string? Kind, string? Category, int Year, double Amount, int Records, int Companies,
    int Providers);

/// <summary>A page of products, largest payments first.</summary>
public sealed record ProductPage(IReadOnlyList<ProductSummary> Items, int Page, int PageSize, int TotalCount);

/// <summary>Product lists and pages (Stage 5.5 item 19), read from the op_product* tables.</summary>
public sealed class ProductService(string connectionString)
{
    public const int MaxPageSize = 100;

    public const int MaxNameLength = 100;

    /// <summary>The product types Open Payments uses, for the list's filter.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["Drug", "Biological", "Device", "Medical Supply"];

    private sealed record ProductRow(string Slug, string Name, string? Kind, string? Category, string? Ndc, string? DeviceId, short Year, double Amount,
        int Records, int Companies, int Providers);

    private sealed record DrugRow(string ProductNdc, string? NdcKey, string? BrandName, string? GenericName, string? ActiveIngredients, string? DosageForm, string? Route,
        string? Labeler, string? MarketingCategory, string? ApplicationNumber, string? ProductType, string? PharmClasses, string? Sponsor, DateTime? ApprovalDate,
        DateTime? MarketingStart, long OtherMakers, long GenericMakers);

    private sealed record LabelRow(string SetId, DateTime? EffectiveDate, string? Indications, string? BoxedWarning);

    private sealed record DeviceRow(string DeviceId, string? BrandName, string? Company, string? Description, string? Model, string? GmdnTerm,
        string? GmdnDefinition, string? ProductCode, string? ProductCodeName, string? DeviceClass, string? MedicalSpecialty, sbyte? IsRx, sbyte? IsOtc,
        sbyte? Implantable, string? DistributionStatus, string? Submissions);

    private sealed record PremarketRow(string Number, string Kind, string? Applicant, string? DeviceName, DateTime? DecisionDate, string? Decision);

    private static DateOnly? ToDate(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;

    private sealed record ResearchRow(short Year, double Amount, int Records, int Studies);

    private sealed record RecipientRow(string Npi, string? SortName, string? Specialty, string? City, string? State, double Amount, int Records);

    /// <summary>
    /// Products whose name contains <paramref name="name"/> (any part, case-insensitive; or the slug itself), optionally of one
    /// <paramref name="kind"/>; every product when both are empty. Largest payments first.
    /// </summary>
    public async Task<ProductPage> SearchAsync(string? name, string? kind, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize < 1 || pageSize > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"Page must be ≥ 1 and the page size 1–{MaxPageSize}.");
        }

        var text = name?.Trim() ?? "";
        if (text.Length > MaxNameLength)
        {
            throw new ArgumentOutOfRangeException(nameof(name), $"At most {MaxNameLength} characters.");
        }

        var conditions = new List<string>();
        if (text.Length > 0)
        {
            conditions.Add("(p.name LIKE @like OR p.slug = @text)");
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            conditions.Add("p.kind = @kind");
        }

        var where = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
        var parameters = new { like = "%" + SearchQuery.Prefix(text), text, kind = kind?.Trim(), offset = (page - 1) * pageSize, pageSize };
        await using var connection = new MySqlConnection(connectionString);
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT COUNT(*) FROM op_product p {where}", parameters, cancellationToken: ct));
        var rows = await connection.QueryAsync<ProductRow>(new CommandDefinition(
            $"""
            SELECT slug AS Slug, name AS Name, kind AS Kind, category AS Category, ndc AS Ndc, device_id AS DeviceId, program_year AS Year,
                   amount AS Amount, records AS Records, companies AS Companies, providers AS Providers
            FROM op_product p {where}
            ORDER BY p.amount DESC, p.slug
            LIMIT @offset, @pageSize
            """, parameters, cancellationToken: ct));
        return new ProductPage(rows.Select(r => new ProductSummary(r.Slug, r.Name, r.Kind, r.Category, r.Year, r.Amount, r.Records, r.Companies, r.Providers))
            .ToList(), page, pageSize, total);
    }

    public const int PrescriberPageSize = 50;

    public const int MaxPrescriberPageSize = 200;

    /// <summary>The sorts of <see cref="PrescribersAsync"/>: paid (default), claims, cost, name.</summary>
    public static IEnumerable<string> PrescriberSorts => ProductPrescribingData.Sorts.Keys;

    /// <summary>
    /// The active providers paid in payments naming the product who prescribed it to Medicare patients (part 4), a page at
    /// a time; null when the product is unknown or has no Part D brand.
    /// </summary>
    public async Task<ProductPrescriberPage?> PrescribersAsync(string slug, string? sort, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize < 1 || pageSize > MaxPrescriberPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"Page must be ≥ 1 and the page size 1–{MaxPrescriberPageSize}.");
        }

        sort = string.IsNullOrWhiteSpace(sort) ? "paid" : sort.Trim();
        if (!ProductPrescribingData.Sorts.ContainsKey(sort))
        {
            throw new ArgumentOutOfRangeException(nameof(sort), $"Sort by one of {string.Join(", ", PrescriberSorts)}.");
        }

        if (string.IsNullOrWhiteSpace(slug) || slug.Length > 200)
        {
            return null;
        }

        await using var connection = new MySqlConnection(connectionString);
        return await ProductPrescribingData.PageAsync(connection, slug, sort, page, pageSize, ct);
    }

    /// <summary>Every row of <see cref="PrescribersAsync"/>, largest payments first, streamed (CSV; no cap).</summary>
    public IAsyncEnumerable<ProductPrescriber> AllPrescribersAsync(string slug, CancellationToken ct) => ProductPrescribingData.AllAsync(connectionString, slug, ct);

    /// <summary>One product by its slug, or null.</summary>
    public async Task<ProductDetail?> GetAsync(string slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > 200)
        {
            return null;
        }

        await using var connection = new MySqlConnection(connectionString);
        var p = await connection.QuerySingleOrDefaultAsync<ProductRow>(new CommandDefinition(
            """
            SELECT slug AS Slug, name AS Name, kind AS Kind, category AS Category, ndc AS Ndc, device_id AS DeviceId, program_year AS Year,
                   amount AS Amount, records AS Records, companies AS Companies, providers AS Providers
            FROM op_product WHERE slug = @slug
            """, new { slug }, cancellationToken: ct));
        if (p is null)
        {
            return null;
        }

        var companies = await connection.QueryAsync<ProductCompany>(new CommandDefinition(
            """
            SELECT pc.company_id AS CompanyId, COALESCE(c.name, pc.company_id) AS Name, pc.amount AS Amount, pc.records AS Records
            FROM op_product_company pc LEFT JOIN op_company c ON c.company_id = pc.company_id
            WHERE pc.slug = @slug ORDER BY pc.amount DESC, Name
            """, new { slug }, cancellationToken: ct));
        var natures = await connection.QueryAsync<ProductNature>(new CommandDefinition(
            "SELECT nature AS Nature, amount AS Amount, records AS Records FROM op_product_nature WHERE slug = @slug ORDER BY amount DESC, nature",
            new { slug }, cancellationToken: ct));
        var specialties = await connection.QueryAsync<ProductSpecialty>(new CommandDefinition(
            "SELECT specialty AS Specialty, providers AS Providers, amount AS Amount FROM op_product_specialty WHERE slug = @slug ORDER BY specialty_rank",
            new { slug }, cancellationToken: ct));
        var recipients = (await connection.QueryAsync<RecipientRow>(new CommandDefinition(
            """
            SELECT r.npi AS Npi, pr.sort_name AS SortName, t.Classification AS Specialty, l.city AS City, l.state AS State, r.amount AS Amount, r.records AS Records
            FROM op_product_recipient r
            JOIN provider pr ON pr.npi = r.npi
            LEFT JOIN taxonomy_codes t ON t.Taxonomy_Code = pr.primary_taxonomy_code
            LEFT JOIN provider_location l ON l.id = (SELECT MIN(l2.id) FROM provider_location l2 WHERE l2.npi = r.npi AND l2.is_primary = 1)
            WHERE r.slug = @slug
            ORDER BY r.recipient_rank
            """, new { slug }, cancellationToken: ct))).ToList();
        var credentials = recipients.Count == 0 ? [] : (await connection.QueryAsync<(string Npi, string Credential)>(new CommandDefinition(
                "SELECT npi, credential FROM provider_credential WHERE npi IN @npis AND ord < 100 ORDER BY npi, ord",
                new { npis = recipients.Select(r => r.Npi).ToArray() }, cancellationToken: ct)))
            .GroupBy(c => c.Npi).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(c => c.Credential)));

        ProductResearch? research = null;
        var r = await connection.QuerySingleOrDefaultAsync<ResearchRow>(new CommandDefinition(
            "SELECT program_year AS Year, amount AS Amount, records AS Records, studies AS Studies FROM op_product_research WHERE slug = @slug",
            new { slug }, cancellationToken: ct));
        if (r is not null)
        {
            var studies = await connection.QueryAsync<ProductStudy>(new CommandDefinition(
                """
                SELECT s.study AS Study, s.nct_id AS NctId, s.company_id AS CompanyId, c.name AS CompanyName, s.amount AS Amount, s.records AS Records
                FROM op_product_study s LEFT JOIN op_company c ON c.company_id = s.company_id
                WHERE s.slug = @slug ORDER BY s.study_rank
                """, new { slug }, cancellationToken: ct));
            research = new ProductResearch(r.Year, r.Amount, r.Records, r.Studies, studies.ToList());
        }

        var drug = await GetDrugAsync(connection, slug, ct);
        var isDrug = drug is not null || p.Kind is "Drug" or "Biological";
        var ndcKeys = new[] { ProductPublicData.NdcKey(p.Ndc), ProductPublicData.NdcKey(drug?.ProductNdc) }.OfType<string>().Distinct().ToList();
        var brands = isDrug ? new[] { p.Name, drug?.BrandName }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [];
        return new ProductDetail(p.Slug, p.Name, p.Kind, p.Category, p.Ndc, p.DeviceId, p.Year, p.Amount, p.Records, p.Companies, p.Providers,
            companies.ToList(), natures.ToList(), specialties.ToList(),
            recipients.Select(x => new ProductRecipient(x.Npi, x.SortName ?? x.Npi, credentials.GetValueOrDefault(x.Npi), x.Specialty, x.City, x.State,
                x.Amount, x.Records)).ToList(),
            research)
        {
            Drug = drug,
            Device = await GetDeviceAsync(connection, slug, ct),
            Spending = await ProductPublicData.SpendingAsync(connection, brands, ct),
            Prices = await ProductPublicData.PricesAsync(connection, ndcKeys, ct),
            Shortages = isDrug ? await ProductPublicData.ShortagesAsync(connection, ndcKeys, drug?.GenericName, ct) : [],
            Recalls = await ProductPublicData.RecallsAsync(connection, slug, isDrug,
                isDrug ? [p.Name, drug?.BrandName, drug?.ProductNdc, p.Ndc] : [p.Name], ct),
            AdverseEvents = await ProductPublicData.AdverseEventsAsync(connection, slug, ct),
            Trials = await ProductPublicData.TrialsAsync(connection, slug, ct),
            Prescribing = await ProductPrescribingData.SummaryAsync(connection, slug, ct),
        };
    }

    private static async Task<ProductDrugInfo?> GetDrugAsync(MySqlConnection connection, string slug, CancellationToken ct)
    {
        const string columns = """
            SELECT n.product_ndc AS ProductNdc, n.ndc_key AS NdcKey, n.brand_name AS BrandName, n.generic_name AS GenericName, n.active_ingredients AS ActiveIngredients,
                   n.dosage_form AS DosageForm, n.route AS Route, n.labeler AS Labeler, n.marketing_category AS MarketingCategory,
                   n.application_number AS ApplicationNumber, n.product_type AS ProductType, n.pharm_classes AS PharmClasses, a.sponsor AS Sponsor,
                   a.approval_date AS ApprovalDate, n.marketing_start AS MarketingStart,
                   (SELECT COUNT(DISTINCT o.labeler) FROM fda_ndc_product o
                     WHERE o.generic_name = n.generic_name AND o.labeler <> n.labeler AND NOT (o.application_number <=> n.application_number)) AS OtherMakers,
                   (SELECT COUNT(DISTINCT o.labeler) FROM fda_ndc_product o
                     WHERE o.generic_name = n.generic_name AND o.labeler <> n.labeler AND o.marketing_category = 'ANDA') AS GenericMakers
            FROM op_product p
            """;
        var matchedBy = "ndc";
        var d = await connection.QueryFirstOrDefaultAsync<DrugRow>(new CommandDefinition(
            columns + """

            JOIN fda_ndc_product n ON n.ndc_key = p.ndc_key
            LEFT JOIN fda_application a ON a.application_number = n.application_number
            WHERE p.slug = @slug
            ORDER BY n.product_ndc
            LIMIT 1
            """, new { slug }, cancellationToken: ct));
        if (d is null)
        {
            // The reported NDC isn't listed (or there is none): a drug or biological of the same brand name, under the
            // application most of its listings use, the maker's own listing (labeler named like the sponsor) before a repackager's.
            matchedBy = "name";
            d = await connection.QueryFirstOrDefaultAsync<DrugRow>(new CommandDefinition(
                columns + """

                JOIN fda_ndc_product n ON n.brand_name = p.name
                LEFT JOIN fda_application a ON a.application_number = n.application_number
                WHERE p.slug = @slug AND p.kind IN ('Drug', 'Biological')
                ORDER BY (SELECT COUNT(*) FROM fda_ndc_product n2 WHERE n2.brand_name = n.brand_name AND n2.application_number <=> n.application_number) DESC,
                         a.sponsor IS NULL, n.labeler NOT LIKE CONCAT(SUBSTRING_INDEX(a.sponsor, ' ', 1), '%'), n.pharm_classes IS NULL, n.product_ndc
                LIMIT 1
                """, new { slug }, cancellationToken: ct));
        }

        if (d is null)
        {
            return null;
        }

        // The listing's own label, else the newest label of a listing under the same application.
        var label = await connection.QueryFirstOrDefaultAsync<LabelRow>(new CommandDefinition(
            """
            SELECT l.set_id AS SetId, l.effective_date AS EffectiveDate, l.indications AS Indications, l.boxed_warning AS BoxedWarning
            FROM fda_drug_label_ndc ln JOIN fda_drug_label l ON l.set_id = ln.set_id
            WHERE ln.ndc_key = @key
            ORDER BY l.effective_date DESC, l.set_id
            LIMIT 1
            """, new { key = d.NdcKey }, cancellationToken: ct))
            ?? (d.ApplicationNumber is null ? null : await connection.QueryFirstOrDefaultAsync<LabelRow>(new CommandDefinition(
                """
                SELECT l.set_id AS SetId, l.effective_date AS EffectiveDate, l.indications AS Indications, l.boxed_warning AS BoxedWarning
                FROM fda_ndc_product n2 JOIN fda_drug_label_ndc ln ON ln.ndc_key = n2.ndc_key JOIN fda_drug_label l ON l.set_id = ln.set_id
                WHERE n2.application_number = @app
                ORDER BY l.effective_date DESC, l.set_id
                LIMIT 1
                """, new { app = d.ApplicationNumber }, cancellationToken: ct)));
        var classes = d.PharmClasses ?? (d.GenericName is null ? null : await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "SELECT pharm_classes FROM fda_ndc_product WHERE generic_name = @generic AND pharm_classes IS NOT NULL AND pharm_classes <> '' ORDER BY product_ndc LIMIT 1",
            new { generic = d.GenericName }, cancellationToken: ct)));
        return new ProductDrugInfo(d.ProductNdc, d.BrandName, d.GenericName, d.ActiveIngredients, d.DosageForm, d.Route, d.Labeler, d.MarketingCategory,
            d.ApplicationNumber, d.ProductType, classes, d.Sponsor, ToDate(d.ApprovalDate), ToDate(d.MarketingStart), (int)d.OtherMakers,
            (int)d.GenericMakers, label?.Indications, label?.BoxedWarning, ToDate(label?.EffectiveDate), label?.SetId, matchedBy);
    }

    private static async Task<ProductDeviceInfo?> GetDeviceAsync(MySqlConnection connection, string slug, CancellationToken ct)
    {
        var d = await connection.QueryFirstOrDefaultAsync<DeviceRow>(new CommandDefinition(
            """
            SELECT f.device_id AS DeviceId, f.brand_name AS BrandName, f.company_name AS Company, f.description AS Description, f.model AS Model,
                   f.gmdn_term AS GmdnTerm, f.gmdn_definition AS GmdnDefinition, f.product_code AS ProductCode, f.product_code_name AS ProductCodeName,
                   f.device_class AS DeviceClass, f.medical_specialty AS MedicalSpecialty, f.is_rx AS IsRx, f.is_otc AS IsOtc, f.implantable AS Implantable,
                   f.distribution_status AS DistributionStatus, f.submissions AS Submissions
            FROM op_product p JOIN fda_device f ON f.device_id = TRIM(p.device_id)
            WHERE p.slug = @slug
            """, new { slug }, cancellationToken: ct));
        if (d is null)
        {
            return null;
        }

        var numbers = (d.Submissions ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var found = numbers.Length == 0 ? [] : (await connection.QueryAsync<PremarketRow>(new CommandDefinition(
                """
                SELECT number AS Number, kind AS Kind, applicant AS Applicant, device_name AS DeviceName, decision_date AS DecisionDate, decision AS Decision
                FROM fda_premarket WHERE number IN @numbers
                """, new { numbers }, cancellationToken: ct)))
            .ToDictionary(r => r.Number, StringComparer.OrdinalIgnoreCase);
        var premarket = numbers.Select(n => found.TryGetValue(n, out var r)
                ? new ProductPremarket(r.Number, r.Kind, r.Applicant, r.DeviceName, ToDate(r.DecisionDate), r.Decision)
                : new ProductPremarket(n, ProductPremarket.KindOf(n), null, null, null, null))
            .OrderByDescending(x => x.DecisionDate).ThenBy(x => x.Number, StringComparer.Ordinal).ToList();
        return new ProductDeviceInfo(d.DeviceId, d.BrandName, d.Company, d.Description, d.Model, d.GmdnTerm, d.GmdnDefinition, d.ProductCode,
            d.ProductCodeName, d.DeviceClass, d.MedicalSpecialty, d.IsRx is null ? null : d.IsRx != 0, d.IsOtc is null ? null : d.IsOtc != 0,
            d.Implantable is null ? null : d.Implantable != 0, d.DistributionStatus, premarket);
    }
}
