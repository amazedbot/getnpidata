using System.Runtime.CompilerServices;
using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>
/// Prescribing overlap of a drug (CLAUDE.md §7 Stage 5.5 item 19, part 4): Medicare Part D prescribing of the brands that
/// match the product's name in <see cref="Year"/>, and how much of it the active providers paid in payments naming the
/// product (in <see cref="PaymentYear"/>) wrote. The two years differ: CMS publishes Part D a year later than Open Payments.
/// </summary>
public sealed record ProductPrescribing(int Year, int PaymentYear, IReadOnlyList<string> Brands, int Prescribers, long Claims, double? DrugCost,
    int PaidProviders, int PaidPrescribers, long PaidClaims, double? PaidDrugCost)
{
    /// <summary>The share of the paid providers who prescribed it.</summary>
    public double? PaidPrescriberShare => PaidProviders > 0 ? (double)PaidPrescribers / PaidProviders : null;

    /// <summary>The share of all its Medicare claims written by paid providers.</summary>
    public double? PaidClaimShare => Claims > 0 ? (double)PaidClaims / Claims : null;
}

/// <summary>An active provider paid in payments naming the product who prescribed it to Medicare patients.</summary>
public sealed record ProductPrescriber(string Npi, string Name, string? Credential, string? Specialty, string? City, string? State, double Paid,
    int Payments, int Claims, double? DrugCost, int? Beneficiaries);

/// <summary>A page of <see cref="ProductPrescriber"/>s.</summary>
public sealed record ProductPrescriberPage(string Slug, IReadOnlyList<ProductPrescriber> Items, int Page, int PageSize, int TotalCount, string Sort);

/// <summary>
/// Reads the prescribing overlap the loader precomputes (<c>product_prescribing</c>, <c>product_prescriber</c>; rebuilt after
/// Open Payments or Part D reloads, because joining them when a page is read took seconds).
/// </summary>
internal static class ProductPrescribingData
{
    /// <summary>The sort keys of the prescriber list: by payments (default), Medicare claims, drug cost or name.</summary>
    public static readonly IReadOnlyDictionary<string, string> Sorts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["paid"] = "x.amount DESC, x.npi",
        ["claims"] = "x.claims DESC, x.npi",
        ["cost"] = "x.drug_cost DESC, x.npi",
        ["name"] = "pr.sort_name, x.npi",
    };

    private sealed record SummaryRow(short DataYear, short PaymentYear, string Brands, int Prescribers, long Claims, double? DrugCost, int PaidProviders,
        int PaidPrescribers, long PaidClaims, double? PaidDrugCost);

    private sealed record Row(string Npi, string? SortName, string? Specialty, string? City, string? State, double Amount, int Records, int Claims,
        double? DrugCost, int? Beneficiaries);

    public static async Task<ProductPrescribing?> SummaryAsync(MySqlConnection connection, string slug, CancellationToken ct)
    {
        var r = await connection.QuerySingleOrDefaultAsync<SummaryRow>(new CommandDefinition(
            """
            SELECT data_year AS DataYear, payment_year AS PaymentYear, brands AS Brands, prescribers AS Prescribers, claims AS Claims, drug_cost AS DrugCost,
                   paid_providers AS PaidProviders, paid_prescribers AS PaidPrescribers, paid_claims AS PaidClaims, paid_drug_cost AS PaidDrugCost
            FROM product_prescribing WHERE slug = @slug
            """, new { slug }, cancellationToken: ct));
        return r is null ? null : new ProductPrescribing(r.DataYear, r.PaymentYear, r.Brands.Split(" | ", StringSplitOptions.RemoveEmptyEntries), r.Prescribers,
            r.Claims, r.DrugCost, r.PaidProviders, r.PaidPrescribers, r.PaidClaims, r.PaidDrugCost);
    }

    private static string ListSql(string orderBy, string limit) =>
        $"""
        SELECT x.npi AS Npi, pr.sort_name AS SortName, t.Classification AS Specialty, l.city AS City, l.state AS State, x.amount AS Amount,
               x.records AS Records, x.claims AS Claims, x.drug_cost AS DrugCost, x.beneficiaries AS Beneficiaries
        FROM product_prescriber x
        JOIN provider pr ON pr.npi = x.npi
        LEFT JOIN taxonomy_codes t ON t.Taxonomy_Code = pr.primary_taxonomy_code
        LEFT JOIN provider_location l ON l.id = (SELECT MIN(l2.id) FROM provider_location l2 WHERE l2.npi = x.npi AND l2.is_primary = 1)
        WHERE x.slug = @slug
        ORDER BY {orderBy}
        {limit}
        """;

    private static async Task<Dictionary<string, string>> CredentialsAsync(MySqlConnection connection, List<string> npis, CancellationToken ct) =>
        npis.Count == 0 ? [] : (await connection.QueryAsync<(string Npi, string Credential)>(new CommandDefinition(
                "SELECT npi, credential FROM provider_credential WHERE npi IN @npis AND ord < 100 ORDER BY npi, ord", new { npis = npis.ToArray() },
                cancellationToken: ct)))
            .GroupBy(c => c.Npi).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(c => c.Credential)));

    private static ProductPrescriber Map(Row r, IReadOnlyDictionary<string, string> credentials) =>
        new(r.Npi, r.SortName ?? r.Npi, credentials.GetValueOrDefault(r.Npi), r.Specialty, r.City, r.State, r.Amount, r.Records, r.Claims, r.DrugCost,
            r.Beneficiaries);

    public static async Task<ProductPrescriberPage?> PageAsync(MySqlConnection connection, string slug, string sort, int page, int pageSize, CancellationToken ct)
    {
        var summary = await SummaryAsync(connection, slug, ct);
        if (summary is null)
        {
            return null;
        }

        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(ListSql(Sorts[sort], "LIMIT @offset, @pageSize"),
            new { slug, offset = (page - 1) * pageSize, pageSize }, cancellationToken: ct))).ToList();
        var credentials = await CredentialsAsync(connection, rows.Select(r => r.Npi).ToList(), ct);
        return new ProductPrescriberPage(slug, rows.Select(r => Map(r, credentials)).ToList(), page, pageSize, summary.PaidPrescribers, sort.ToLowerInvariant());
    }

    /// <summary>Every row, largest payments first, for the CSV (no cap); empty when the product has no Part D brand.</summary>
    public static async IAsyncEnumerable<ProductPrescriber> AllAsync(string connectionString, string slug, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(ListSql(Sorts["paid"], ""), new { slug }, cancellationToken: ct));
        foreach (var chunk in rows.Chunk(1000))
        {
            var credentials = await CredentialsAsync(connection, chunk.Select(r => r.Npi).ToList(), ct);
            foreach (var r in chunk)
            {
                yield return Map(r, credentials);
            }
        }
    }

    /// <summary>A provider's Medicare Part D claims of each of the given products that have Part D brands (0 when none), by slug.</summary>
    public static async Task<Dictionary<string, int>> ClaimsOfProviderAsync(MySqlConnection connection, string npi, IEnumerable<string> slugs, CancellationToken ct)
    {
        var list = slugs.ToArray();
        if (list.Length == 0)
        {
            return [];
        }

        return (await connection.QueryAsync<(string Slug, int Claims)>(new CommandDefinition(
                """
                SELECT s.slug, COALESCE(x.claims, 0) FROM product_prescribing s
                LEFT JOIN product_prescriber x ON x.slug = s.slug AND x.npi = @npi
                WHERE s.slug IN @slugs
                """, new { npi, slugs = list }, cancellationToken: ct)))
            .ToDictionary(r => r.Slug, r => r.Claims, StringComparer.Ordinal);
    }
}
