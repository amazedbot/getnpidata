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
/// A drug, biological, device or medical supply named in Open Payments (CLAUDE.md §7 Stage 5.5 item 19), keyed by
/// <see cref="Slug"/>. Every product a payment names counts with the payment's full amount, so a payment naming two
/// products counts for both. <see cref="Name"/>, <see cref="Kind"/>, <see cref="Category"/>, <see cref="Ndc"/> and
/// <see cref="DeviceId"/> are the values used most in the payments.
/// </summary>
public sealed record ProductDetail(string Slug, string Name, string? Kind, string? Category, string? Ndc, string? DeviceId, int Year, double Amount,
    int Records, int CompanyCount, int Providers, IReadOnlyList<ProductCompany> Companies, IReadOnlyList<ProductNature> ByNature,
    IReadOnlyList<ProductSpecialty> TopSpecialties, IReadOnlyList<ProductRecipient> TopProviders, ProductResearch? Research);

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

        return new ProductDetail(p.Slug, p.Name, p.Kind, p.Category, p.Ndc, p.DeviceId, p.Year, p.Amount, p.Records, p.Companies, p.Providers,
            companies.ToList(), natures.ToList(), specialties.ToList(),
            recipients.Select(x => new ProductRecipient(x.Npi, x.SortName ?? x.Npi, credentials.GetValueOrDefault(x.Npi), x.Specialty, x.City, x.State,
                x.Amount, x.Records)).ToList(),
            research);
    }
}
