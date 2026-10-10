using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>A company's Open Payments in one program year: general payments, research payments, ownership invested and its value.</summary>
public sealed record CompanyYear(int Year, double General, int GeneralRecords, double Research, int ResearchRecords, double OwnershipInvested,
    double OwnershipValue, int OwnershipRecords);

/// <summary>General payments of one kind (nature of payment) in <see cref="CompanyDetail.DetailYear"/>.</summary>
public sealed record CompanyNature(string Nature, double Amount, int Records);

/// <summary>A drug, device or supply named first on the company's general payments in <see cref="CompanyDetail.DetailYear"/>.</summary>
/// <remarks><see cref="Slug"/> is the product page's key (null in data loaded before product pages).</remarks>
public sealed record CompanyProduct(string Name, string? Kind, string? Category, double Amount, int Records, string? Slug = null);

/// <summary>A specialty (primary NUCC classification of active providers) the company paid, over every published year.</summary>
public sealed record CompanySpecialty(string Specialty, int Providers, double Amount);

/// <summary>An active provider the company paid, over every published year, by payment type.</summary>
public sealed record CompanyRecipient(string Npi, string Name, string? Credential, string? Specialty, string? City, string? State, double Total,
    double General, double Research, double AssociatedResearch, double Ownership, int Records);

/// <summary>
/// A company that reports to Open Payments (CLAUDE.md §7 Stage 5.5 item 17), keyed by its CMS ID: who it is, what it paid each
/// year, what for and with which products, and whom. <see cref="Providers"/> counts every NPI it paid, active or not.
/// <see cref="SimilarNames"/>: other companies sharing the name's distinctive word (a manufacturer often reports under
/// several entities, e.g. ELI LILLY AND COMPANY and LILLY USA, LLC); a name match only, not a verified relation.
/// </summary>
public sealed record CompanyDetail(string Id, string Name, IReadOnlyList<string> OtherNames, string? State, string? Country, double General,
    double Research, double OwnershipInvested, double OwnershipValue, int? FirstYear, int? LastYear, int? Providers, IReadOnlyList<CompanyYear> Years,
    int? DetailYear, IReadOnlyList<CompanyNature> ByNature, IReadOnlyList<CompanyProduct> TopProducts, IReadOnlyList<CompanySpecialty> TopSpecialties,
    IReadOnlyList<CompanyRecipient> TopProviders, IReadOnlyList<CompanySummary> SimilarNames)
{
    /// <summary>FDA recalls by a firm of the same name; null when none matched. A name match, not a verified identity.</summary>
    public CompanyRecalls? Recalls { get; init; }

    /// <summary>SEC registrants of the same name (public companies). A name match, not a verified identity.</summary>
    public IReadOnlyList<CompanySecListing> SecListings { get; init; } = [];

    /// <summary>OIG integrity agreements naming an entity of the same name, newest status first. A name match, not a verified identity.</summary>
    public IReadOnlyList<CompanyIntegrityAgreement> IntegrityAgreements { get; init; } = [];

    /// <summary>The company's page on CMS's Open Payments site.</summary>
    public string OpenPaymentsUrl => $"https://openpaymentsdata.cms.gov/company/{Uri.EscapeDataString(Id)}";
}

/// <summary>An FDA recall (enforcement report) by a firm whose name matches the company.</summary>
public sealed record CompanyRecall(string RecallNumber, string ProductType, string? Firm, string? Classification, string? Status, DateOnly? Initiated,
    string? Product, string? Reason);

/// <summary>
/// FDA recalls by firms whose name matches the company's (openFDA drug and device enforcement reports, since 2004):
/// counts by class (Class I is the most serious), how many are ongoing, the newest <see cref="CompanyService.MaxRecalls"/> and the
/// firm names that matched.
/// </summary>
public sealed record CompanyRecalls(int Total, int ClassI, int ClassII, int ClassIII, int Ongoing, IReadOnlyList<string> Firms,
    IReadOnlyList<CompanyRecall> Latest);

/// <summary>
/// A public company registered with the SEC (EDGAR CIK, ticker, exchange): of the same name, or, with <see cref="IsParent"/>,
/// the parent company from a hand-made list of subsidiaries (<see cref="Note"/>: "U.S. subsidiary", "acquired 2021" …).
/// </summary>
public sealed record CompanySecListing(int Cik, string Ticker, string Name, string? Exchange, bool IsParent = false, string? Note = null)
{
    public string EdgarUrl => $"https://www.sec.gov/cgi-bin/browse-edgar?action=getcompany&CIK={Cik:D10}";
}

/// <summary>An HHS-OIG Corporate Integrity Agreement (or other integrity agreement) naming an entity whose name matches.</summary>
public sealed record CompanyIntegrityAgreement(string Name, string? Location, string? Type, string? Status, DateOnly? StatusDate, string Url);

/// <summary>One company in a list: payments (general + research) over every published year, and how many NPIs it paid.</summary>
public sealed record CompanySummary(string Id, string Name, string? State, string? Country, double Payments, double OwnershipValue, int? Providers,
    int? FirstYear, int? LastYear);

/// <summary>A page of companies, largest payments first.</summary>
public sealed record CompanyPage(IReadOnlyList<CompanySummary> Items, int Page, int PageSize, int TotalCount);

/// <summary>Company lists and pages (Stage 5.5 item 17), read from the op_company* tables.</summary>
public sealed partial class CompanyService(string connectionString)
{
    public const int MaxPageSize = 100;

    public const int MaxSimilarNames = 10;

    public const int MaxRecalls = 20;

    // Words that don't identify a company.
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "THE", "AND", "COMPANY", "CORPORATION", "CORP", "INC", "LLC", "LTD", "LIMITED", "GROUP", "HOLDINGS", "INTERNATIONAL", "GLOBAL", "AMERICA",
        "AMERICAN", "AMERICAS", "NATIONAL", "UNITED", "MEDICAL", "HEALTH", "HEALTHCARE", "PHARMA", "PHARMACEUTICAL", "PHARMACEUTICALS", "LABORATORIES",
        "LABS", "SCIENCES", "SCIENTIFIC", "TECHNOLOGIES", "TECHNOLOGY", "SYSTEMS", "SOLUTIONS", "PRODUCTS", "SERVICES", "DEVICES", "SURGICAL",
        "THERAPEUTICS", "BIOSCIENCES", "BIOLOGICS", "NORTH", "SOUTH", "EAST", "WEST", "NEW", "FIRST",
    };

    [GeneratedRegex("[A-Za-z]+")]
    private static partial Regex Words();

    /// <summary>The first word of a company name that identifies it (4+ letters, not a common word), or null.</summary>
    public static string? DistinctiveWord(string name) =>
        Words().Matches(name).Select(m => m.Value).FirstOrDefault(w => w.Length >= 4 && !CommonWords.Contains(w))?.ToUpperInvariant();

    public const int MaxNameLength = 100;

    private sealed record CompanyRow(string Id, string Name, string? OtherNames, string? State, string? Country, double General, double Research,
        double Invested, double Interest, short? FirstYear, short? LastYear, int? Providers);

    private sealed record YearRow(short Year, double General, int GeneralRecords, double Research, int ResearchRecords, double Invested, double Interest,
        int OwnershipRecords);

    private sealed record NatureRow(short Year, string Nature, double Amount, int Records);

    private sealed record ProductRow(string Product, string? Kind, string? Category, double Amount, int Records, string? Slug);

    private sealed record SpecialtyRow(string Specialty, int Providers, double Amount);

    private sealed record RecallCountRow(long Total, long ClassI, long ClassII, long ClassIII, long Ongoing);

    private sealed record RecallRow(string RecallNumber, string ProductType, string? Firm, string? Classification, string? Status, DateTime? Initiated,
        string? Product, string? Reason);

    private sealed record SecRow(int Cik, string Ticker, string Name, string? Exchange, long IsParent, string? Note);

    private sealed record AgreementRow(string Name, string? Location, string? Type, string? Status, DateTime? StatusDate, string Url);

    private sealed record RecipientRow(string Npi, string? SortName, string? Specialty, string? City, string? State, double Total, double General,
        double Research, double Associated, double Ownership, int Records);

    /// <summary>
    /// Companies whose name or another name contains <paramref name="name"/> (any part, case- and accent-insensitive), or
    /// every company when it's empty; largest payments first.
    /// </summary>
    public async Task<CompanyPage> SearchAsync(string? name, int page, int pageSize, CancellationToken ct)
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

        var where = text.Length == 0 ? "" : "WHERE c.name LIKE @like OR c.other_names LIKE @like OR c.company_id = @text";
        var parameters = new { like = "%" + SearchQuery.Prefix(text), text, offset = (page - 1) * pageSize, pageSize };
        await using var connection = new MySqlConnection(connectionString);
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT COUNT(*) FROM op_company c {where}", parameters, cancellationToken: ct));
        var rows = await connection.QueryAsync<CompanyRow>(new CommandDefinition(
            $"""
            SELECT c.company_id AS Id, c.name AS Name, c.other_names AS OtherNames, c.state AS State, c.country AS Country,
                   c.general_amount AS General, c.research_amount AS Research, c.invested_amount AS Invested, c.interest_value AS Interest,
                   c.first_year AS FirstYear, c.last_year AS LastYear, r.providers AS Providers
            FROM op_company c LEFT JOIN op_company_reach r ON r.company_id = c.company_id
            {where}
            ORDER BY c.general_amount + c.research_amount DESC, c.name, c.company_id
            LIMIT @offset, @pageSize
            """, parameters, cancellationToken: ct));
        return new CompanyPage(
            rows.Select(r => new CompanySummary(r.Id, r.Name, r.State, r.Country, Math.Round(r.General + r.Research, 2), r.Interest, r.Providers,
                r.FirstYear, r.LastYear)).ToList(),
            page, pageSize, total);
    }

    /// <summary>One company by its CMS ID, or null.</summary>
    public async Task<CompanyDetail?> GetAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 20)
        {
            return null;
        }

        await using var connection = new MySqlConnection(connectionString);
        var company = await connection.QuerySingleOrDefaultAsync<CompanyRow>(new CommandDefinition(
            """
            SELECT c.company_id AS Id, c.name AS Name, c.other_names AS OtherNames, c.state AS State, c.country AS Country,
                   c.general_amount AS General, c.research_amount AS Research, c.invested_amount AS Invested, c.interest_value AS Interest,
                   c.first_year AS FirstYear, c.last_year AS LastYear, r.providers AS Providers
            FROM op_company c LEFT JOIN op_company_reach r ON r.company_id = c.company_id
            WHERE c.company_id = @id
            """, new { id }, cancellationToken: ct));
        if (company is null)
        {
            return null;
        }

        var years = await connection.QueryAsync<YearRow>(new CommandDefinition(
            """
            SELECT program_year AS Year, general_amount AS General, general_records AS GeneralRecords, research_amount AS Research,
                   research_records AS ResearchRecords, invested_amount AS Invested, interest_value AS Interest, ownership_records AS OwnershipRecords
            FROM op_company_year WHERE company_id = @id ORDER BY program_year DESC
            """, new { id }, cancellationToken: ct));
        var natures = (await connection.QueryAsync<NatureRow>(new CommandDefinition(
            "SELECT program_year AS Year, nature AS Nature, amount AS Amount, records AS Records FROM op_company_nature WHERE company_id = @id ORDER BY amount DESC, nature",
            new { id }, cancellationToken: ct))).ToList();
        var products = await connection.QueryAsync<ProductRow>(new CommandDefinition(
            "SELECT product AS Product, kind AS Kind, category AS Category, amount AS Amount, records AS Records, slug AS Slug FROM op_company_product WHERE company_id = @id ORDER BY product_rank",
            new { id }, cancellationToken: ct));
        var specialties = await connection.QueryAsync<SpecialtyRow>(new CommandDefinition(
            "SELECT specialty AS Specialty, providers AS Providers, amount AS Amount FROM op_company_specialty WHERE company_id = @id ORDER BY specialty_rank",
            new { id }, cancellationToken: ct));
        var recipients = (await connection.QueryAsync<RecipientRow>(new CommandDefinition(
            """
            SELECT r.npi AS Npi, p.sort_name AS SortName, t.Classification AS Specialty, l.city AS City, l.state AS State, r.total_amount AS Total,
                   r.general_amount AS General, r.research_amount AS Research, r.associated_research_amount AS Associated,
                   r.ownership_amount AS Ownership, r.records AS Records
            FROM op_company_recipient r
            JOIN provider p ON p.npi = r.npi
            LEFT JOIN taxonomy_codes t ON t.Taxonomy_Code = p.primary_taxonomy_code
            LEFT JOIN provider_location l ON l.id = (SELECT MIN(l2.id) FROM provider_location l2 WHERE l2.npi = r.npi AND l2.is_primary = 1)
            WHERE r.company_id = @id
            ORDER BY r.recipient_rank
            """, new { id }, cancellationToken: ct))).ToList();
        var credentials = recipients.Count == 0 ? [] : (await connection.QueryAsync<(string Npi, string Credential)>(new CommandDefinition(
                "SELECT npi, credential FROM provider_credential WHERE npi IN @npis AND ord < 100 ORDER BY npi, ord",
                new { npis = recipients.Select(r => r.Npi).ToArray() }, cancellationToken: ct)))
            .GroupBy(c => c.Npi).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(c => c.Credential)));

        IReadOnlyList<CompanySummary> similar = [];
        if (DistinctiveWord(company.Name) is { } word)
        {
            similar = (await connection.QueryAsync<CompanyRow>(new CommandDefinition(
                $"""
                SELECT c.company_id AS Id, c.name AS Name, c.other_names AS OtherNames, c.state AS State, c.country AS Country,
                       c.general_amount AS General, c.research_amount AS Research, c.invested_amount AS Invested, c.interest_value AS Interest,
                       c.first_year AS FirstYear, c.last_year AS LastYear, r.providers AS Providers
                FROM op_company c LEFT JOIN op_company_reach r ON r.company_id = c.company_id
                WHERE c.company_id <> @id AND c.name REGEXP @pattern
                ORDER BY c.general_amount + c.research_amount DESC, c.name
                LIMIT {MaxSimilarNames}
                """, new { id, pattern = $"\\b{word}\\b" }, cancellationToken: ct)))
                .Select(r => new CompanySummary(r.Id, r.Name, r.State, r.Country, Math.Round(r.General + r.Research, 2), r.Interest, r.Providers,
                    r.FirstYear, r.LastYear)).ToList();
        }

        // Public records matched by name key (op_company_key ↔ each source's key).
        var recallCounts = await connection.QuerySingleAsync<RecallCountRow>(new CommandDefinition(
            """
            SELECT COUNT(*) AS Total, CAST(COALESCE(SUM(f.classification = 'Class I'), 0) AS SIGNED) AS ClassI,
                   CAST(COALESCE(SUM(f.classification = 'Class II'), 0) AS SIGNED) AS ClassII,
                   CAST(COALESCE(SUM(f.classification = 'Class III'), 0) AS SIGNED) AS ClassIII, CAST(COALESCE(SUM(f.status = 'Ongoing'), 0) AS SIGNED) AS Ongoing
            FROM fda_enforcement f WHERE f.firm_key IN (SELECT k.name_key FROM op_company_key k WHERE k.company_id = @id)
            """, new { id }, cancellationToken: ct));
        CompanyRecalls? recalls = null;
        if (recallCounts.Total > 0)
        {
            var latest = await connection.QueryAsync<RecallRow>(new CommandDefinition(
                $"""
                SELECT recall_number AS RecallNumber, product_type AS ProductType, firm AS Firm, classification AS Classification, status AS Status,
                       initiation_date AS Initiated, product_description AS Product, reason AS Reason
                FROM fda_enforcement WHERE firm_key IN (SELECT k.name_key FROM op_company_key k WHERE k.company_id = @id)
                ORDER BY initiation_date DESC, recall_number DESC LIMIT {MaxRecalls}
                """, new { id }, cancellationToken: ct));
            var firms = await connection.QueryAsync<string>(new CommandDefinition(
                """
                SELECT firm FROM fda_enforcement WHERE firm_key IN (SELECT k.name_key FROM op_company_key k WHERE k.company_id = @id) AND firm IS NOT NULL
                GROUP BY firm ORDER BY COUNT(*) DESC, firm LIMIT 5
                """, new { id }, cancellationToken: ct));
            recalls = new CompanyRecalls((int)recallCounts.Total, (int)recallCounts.ClassI, (int)recallCounts.ClassII, (int)recallCounts.ClassIII,
                (int)recallCounts.Ongoing,
                firms.ToList(),
                latest.Select(r => new CompanyRecall(r.RecallNumber, r.ProductType, r.Firm, r.Classification, r.Status,
                    r.Initiated is { } d ? DateOnly.FromDateTime(d) : null, r.Product, r.Reason)).ToList());
        }

        var sec = (await connection.QueryAsync<SecRow>(new CommandDefinition(
            """
            SELECT s.cik AS Cik, s.ticker AS Ticker, s.name AS Name, s.exchange AS Exchange, CAST(0 AS SIGNED) AS IsParent, NULL AS Note
            FROM sec_company s WHERE s.name_key IN (SELECT k.name_key FROM op_company_key k WHERE k.company_id = @id)
            UNION ALL
            SELECT s.cik, s.ticker, s.name, s.exchange, CAST(1 AS SIGNED), p.note
            FROM company_parent p JOIN sec_company s ON s.cik = p.parent_cik
            WHERE p.company_id = @id AND p.parent_cik NOT IN (SELECT s2.cik FROM sec_company s2 JOIN op_company_key k ON k.name_key = s2.name_key WHERE k.company_id = @id)
            ORDER BY IsParent, Cik, Ticker
            """, new { id }, cancellationToken: ct)))
            .Select(r => new CompanySecListing(r.Cik, r.Ticker, r.Name, r.Exchange, r.IsParent != 0, r.Note)).ToList();
        var agreements = await connection.QueryAsync<AgreementRow>(new CommandDefinition(
            """
            SELECT a.name AS Name, a.location AS Location, a.agreement_type AS Type, a.status AS Status, a.status_date AS StatusDate, a.url AS Url
            FROM oig_cia a
            WHERE a.slug IN (SELECT e.slug FROM oig_cia_entity e JOIN op_company_key k ON k.name_key = e.name_key WHERE k.company_id = @id)
            ORDER BY a.status_date DESC, a.name
            """, new { id }, cancellationToken: ct));

        return new CompanyDetail(company.Id, company.Name,
            (company.OtherNames ?? "").Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(n => !string.Equals(n, company.Name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            company.State, company.Country, company.General, company.Research, company.Invested, company.Interest, company.FirstYear, company.LastYear,
            company.Providers,
            years.Select(y => new CompanyYear(y.Year, y.General, y.GeneralRecords, y.Research, y.ResearchRecords, y.Invested, y.Interest, y.OwnershipRecords)).ToList(),
            natures.Count > 0 ? natures[0].Year : null,
            natures.Select(n => new CompanyNature(n.Nature, n.Amount, n.Records)).ToList(),
            products.Select(p => new CompanyProduct(p.Product, p.Kind, p.Category, p.Amount, p.Records, p.Slug)).ToList(),
            specialties.Select(s => new CompanySpecialty(s.Specialty, s.Providers, s.Amount)).ToList(),
            recipients.Select(r => new CompanyRecipient(r.Npi, r.SortName ?? r.Npi, credentials.GetValueOrDefault(r.Npi), r.Specialty, r.City, r.State,
                r.Total, r.General, r.Research, r.Associated, r.Ownership, r.Records)).ToList(),
            similar)
        {
            Recalls = recalls,
            SecListings = sec,
            IntegrityAgreements = agreements.Select(a => new CompanyIntegrityAgreement(a.Name, a.Location, a.Type, a.Status,
                a.StatusDate is { } d ? DateOnly.FromDateTime(d) : null, a.Url)).ToList(),
        };
    }
}
