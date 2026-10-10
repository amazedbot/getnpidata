using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>A company's Open Payments in one program year: general payments, research payments, ownership invested and its value.</summary>
public sealed record CompanyYear(int Year, double General, int GeneralRecords, double Research, int ResearchRecords, double OwnershipInvested,
    double OwnershipValue, int OwnershipRecords);

/// <summary>General payments of one kind (nature of payment) in <see cref="CompanyDetail.DetailYear"/>.</summary>
public sealed record CompanyNature(string Nature, double Amount, int Records);

/// <summary>A drug, device or supply named first on the company's general payments in <see cref="CompanyDetail.DetailYear"/>.</summary>
public sealed record CompanyProduct(string Name, string? Kind, string? Category, double Amount, int Records);

/// <summary>A specialty (primary NUCC classification of active providers) the company paid, over every published year.</summary>
public sealed record CompanySpecialty(string Specialty, int Providers, double Amount);

/// <summary>An active provider the company paid, over every published year, by payment type.</summary>
public sealed record CompanyRecipient(string Npi, string Name, string? Credential, string? Specialty, string? City, string? State, double Total,
    double General, double Research, double AssociatedResearch, double Ownership, int Records);

/// <summary>
/// A company that reports to Open Payments (CLAUDE.md §7 Stage 5.5 item 17), keyed by its CMS ID: who it is, what it paid each
/// year, what for and with which products, and whom. <see cref="Providers"/> counts every NPI it paid, active or not.
/// </summary>
public sealed record CompanyDetail(string Id, string Name, IReadOnlyList<string> OtherNames, string? State, string? Country, double General,
    double Research, double OwnershipInvested, double OwnershipValue, int? FirstYear, int? LastYear, int? Providers, IReadOnlyList<CompanyYear> Years,
    int? DetailYear, IReadOnlyList<CompanyNature> ByNature, IReadOnlyList<CompanyProduct> TopProducts, IReadOnlyList<CompanySpecialty> TopSpecialties,
    IReadOnlyList<CompanyRecipient> TopProviders)
{
    /// <summary>The company's page on CMS's Open Payments site.</summary>
    public string OpenPaymentsUrl => $"https://openpaymentsdata.cms.gov/company/{Uri.EscapeDataString(Id)}";
}

/// <summary>One company in a list: payments (general + research) over every published year, and how many NPIs it paid.</summary>
public sealed record CompanySummary(string Id, string Name, string? State, string? Country, double Payments, double OwnershipValue, int? Providers,
    int? FirstYear, int? LastYear);

/// <summary>A page of companies, largest payments first.</summary>
public sealed record CompanyPage(IReadOnlyList<CompanySummary> Items, int Page, int PageSize, int TotalCount);

/// <summary>Company lists and pages (Stage 5.5 item 17), read from the op_company* tables.</summary>
public sealed class CompanyService(string connectionString)
{
    public const int MaxPageSize = 100;

    public const int MaxNameLength = 100;

    private sealed record CompanyRow(string Id, string Name, string? OtherNames, string? State, string? Country, double General, double Research,
        double Invested, double Interest, short? FirstYear, short? LastYear, int? Providers);

    private sealed record YearRow(short Year, double General, int GeneralRecords, double Research, int ResearchRecords, double Invested, double Interest,
        int OwnershipRecords);

    private sealed record NatureRow(short Year, string Nature, double Amount, int Records);

    private sealed record ProductRow(string Product, string? Kind, string? Category, double Amount, int Records);

    private sealed record SpecialtyRow(string Specialty, int Providers, double Amount);

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
            "SELECT product AS Product, kind AS Kind, category AS Category, amount AS Amount, records AS Records FROM op_company_product WHERE company_id = @id ORDER BY product_rank",
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

        return new CompanyDetail(company.Id, company.Name,
            (company.OtherNames ?? "").Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(n => !string.Equals(n, company.Name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            company.State, company.Country, company.General, company.Research, company.Invested, company.Interest, company.FirstYear, company.LastYear,
            company.Providers,
            years.Select(y => new CompanyYear(y.Year, y.General, y.GeneralRecords, y.Research, y.ResearchRecords, y.Invested, y.Interest, y.OwnershipRecords)).ToList(),
            natures.Count > 0 ? natures[0].Year : null,
            natures.Select(n => new CompanyNature(n.Nature, n.Amount, n.Records)).ToList(),
            products.Select(p => new CompanyProduct(p.Product, p.Kind, p.Category, p.Amount, p.Records)).ToList(),
            specialties.Select(s => new CompanySpecialty(s.Specialty, s.Providers, s.Amount)).ToList(),
            recipients.Select(r => new CompanyRecipient(r.Npi, r.SortName ?? r.Npi, credentials.GetValueOrDefault(r.Npi), r.Specialty, r.City, r.State,
                r.Total, r.General, r.Research, r.Associated, r.Ownership, r.Records)).ToList());
    }
}
