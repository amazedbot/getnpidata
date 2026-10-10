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

/// <summary>Reads the prescribing overlap from <c>op_product_npi</c>, <c>part_d_brand</c> and <c>part_d_brand_prescriber</c>.</summary>
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

    internal sealed record BrandTotal(string Brand, short DataYear, int Prescribers, long Claims, double? DrugCost);

    private sealed record Candidate(string Name, string? Brand, short Year);

    private sealed record PaidTotal(long Prescribers, decimal? Claims, double? DrugCost);

    private sealed record Row(string Npi, string? SortName, string? Specialty, string? City, string? State, double Amount, int Records, decimal Claims,
        double? DrugCost, decimal? Beneficiaries);

    // The candidate names of a product: its name and FDA brand name (drugs and biologicals only).
    private const string Candidates =
        """
        SELECT p.name AS Name, (SELECT MIN(n.brand_name) FROM fda_ndc_product n WHERE n.ndc_key = p.ndc_key) AS Brand, p.program_year AS Year
        FROM op_product p WHERE p.slug = @slug AND p.kind IN ('Drug', 'Biological')
        """;

    /// <summary>
    /// The Part D brands of a product: brand names equal to its name or FDA brand, or one of them plus more words (CMS lists
    /// some drugs by form: "Dupixent Pen").
    /// </summary>
    public static async Task<(IReadOnlyList<BrandTotal> Brands, int PaymentYear)> BrandsAsync(MySqlConnection connection, string slug, CancellationToken ct)
    {
        var candidates = await connection.QuerySingleOrDefaultAsync<Candidate>(new CommandDefinition(Candidates, new { slug }, cancellationToken: ct));
        if (candidates is null)
        {
            return ([], 0);
        }

        var names = new[] { candidates.Name, candidates.Brand }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var parameters = new DynamicParameters();
        var conditions = names.Select((n, i) =>
        {
            parameters.Add($"b{i}", n);
            parameters.Add($"p{i}", SearchQuery.Prefix(n + " "));
            return $"(brand = @b{i} OR brand LIKE @p{i})";
        });
        var brands = await connection.QueryAsync<BrandTotal>(new CommandDefinition(
            $"""
            SELECT brand AS Brand, data_year AS DataYear, prescribers AS Prescribers, claims AS Claims, drug_cost AS DrugCost
            FROM part_d_brand WHERE {string.Join(" OR ", conditions)} ORDER BY claims DESC LIMIT 20
            """, parameters, cancellationToken: ct));
        return (brands.ToList(), candidates.Year);
    }

    public static async Task<ProductPrescribing?> SummaryAsync(MySqlConnection connection, string slug, CancellationToken ct)
    {
        var (brands, paymentYear) = await BrandsAsync(connection, slug, ct);
        if (brands.Count == 0)
        {
            return null;
        }

        var names = brands.Select(b => b.Brand).ToArray();
        // A provider prescribing two forms of one drug counts once.
        var prescribers = brands.Count == 1 ? brands[0].Prescribers : await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT npi) FROM part_d_brand_prescriber WHERE brand IN @names", new { names }, cancellationToken: ct));
        var paidProviders = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM op_product_npi pn JOIN provider pr ON pr.npi = pn.npi WHERE pn.slug = @slug", new { slug }, cancellationToken: ct));
        var paid = await connection.QuerySingleAsync<PaidTotal>(new CommandDefinition(
            """
            SELECT COUNT(DISTINCT pn.npi) AS Prescribers, SUM(b.claims) AS Claims, ROUND(SUM(b.drug_cost), 2) AS DrugCost
            FROM op_product_npi pn
            JOIN provider pr ON pr.npi = pn.npi
            JOIN part_d_brand_prescriber b ON b.npi = pn.npi AND b.brand IN @names
            WHERE pn.slug = @slug
            """, new { slug, names }, cancellationToken: ct));
        return new ProductPrescribing(brands.Max(b => b.DataYear), paymentYear, names, prescribers, brands.Sum(b => b.Claims),
            brands.Any(b => b.DrugCost is not null) ? Math.Round(brands.Sum(b => b.DrugCost ?? 0), 2) : null,
            paidProviders, (int)paid.Prescribers, (long)(paid.Claims ?? 0), paid.DrugCost);
    }

    private static string ListSql(string orderBy, string limit) =>
        $"""
        SELECT x.npi AS Npi, pr.sort_name AS SortName, t.Classification AS Specialty, l.city AS City, l.state AS State, x.amount AS Amount,
               x.records AS Records, x.claims AS Claims, x.drug_cost AS DrugCost, x.beneficiaries AS Beneficiaries
        FROM (
          SELECT pn.npi, pn.amount, pn.records, SUM(b.claims) AS claims, ROUND(SUM(b.drug_cost), 2) AS drug_cost, SUM(b.beneficiaries) AS beneficiaries
          FROM op_product_npi pn
          JOIN part_d_brand_prescriber b ON b.npi = pn.npi AND b.brand IN @names
          WHERE pn.slug = @slug
          GROUP BY pn.npi, pn.amount, pn.records
        ) x
        JOIN provider pr ON pr.npi = x.npi
        LEFT JOIN taxonomy_codes t ON t.Taxonomy_Code = pr.primary_taxonomy_code
        LEFT JOIN provider_location l ON l.id = (SELECT MIN(l2.id) FROM provider_location l2 WHERE l2.npi = x.npi AND l2.is_primary = 1)
        ORDER BY {orderBy}
        {limit}
        """;

    private static async Task<Dictionary<string, string>> CredentialsAsync(MySqlConnection connection, List<string> npis, CancellationToken ct) =>
        npis.Count == 0 ? [] : (await connection.QueryAsync<(string Npi, string Credential)>(new CommandDefinition(
                "SELECT npi, credential FROM provider_credential WHERE npi IN @npis AND ord < 100 ORDER BY npi, ord", new { npis = npis.ToArray() },
                cancellationToken: ct)))
            .GroupBy(c => c.Npi).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(c => c.Credential)));

    private static ProductPrescriber Map(Row r, IReadOnlyDictionary<string, string> credentials) =>
        new(r.Npi, r.SortName ?? r.Npi, credentials.GetValueOrDefault(r.Npi), r.Specialty, r.City, r.State, r.Amount, r.Records, (int)r.Claims, r.DrugCost,
            r.Beneficiaries is { } b ? (int)b : null);

    public static async Task<ProductPrescriberPage?> PageAsync(MySqlConnection connection, string slug, string sort, int page, int pageSize, CancellationToken ct)
    {
        var (brands, _) = await BrandsAsync(connection, slug, ct);
        if (brands.Count == 0)
        {
            return null;
        }

        var names = brands.Select(b => b.Brand).ToArray();
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(DISTINCT pn.npi) FROM op_product_npi pn JOIN provider pr ON pr.npi = pn.npi
            JOIN part_d_brand_prescriber b ON b.npi = pn.npi AND b.brand IN @names WHERE pn.slug = @slug
            """, new { slug, names }, cancellationToken: ct));
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(ListSql(Sorts[sort], "LIMIT @offset, @pageSize"),
            new { slug, names, offset = (page - 1) * pageSize, pageSize }, cancellationToken: ct))).ToList();
        var credentials = await CredentialsAsync(connection, rows.Select(r => r.Npi).ToList(), ct);
        return new ProductPrescriberPage(slug, rows.Select(r => Map(r, credentials)).ToList(), page, pageSize, total, sort.ToLowerInvariant());
    }

    /// <summary>Every row, largest payments first, for the CSV (no cap); empty when the product has no Part D brand.</summary>
    public static async IAsyncEnumerable<ProductPrescriber> AllAsync(string connectionString, string slug, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var (brands, _) = await BrandsAsync(connection, slug, ct);
        if (brands.Count == 0)
        {
            yield break;
        }

        var names = brands.Select(b => b.Brand).ToArray();
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(ListSql(Sorts["paid"], ""), new { slug, names }, cancellationToken: ct));
        foreach (var chunk in rows.Chunk(1000))
        {
            var credentials = await CredentialsAsync(connection, chunk.Select(r => r.Npi).ToList(), ct);
            foreach (var r in chunk)
            {
                yield return Map(r, credentials);
            }
        }
    }

    /// <summary>A provider's Medicare Part D claims of each of the given products' brands (0 when none), by slug.</summary>
    public static async Task<Dictionary<string, int>> ClaimsOfProviderAsync(MySqlConnection connection, string npi, IEnumerable<string> slugs, CancellationToken ct)
    {
        var claims = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var slug in slugs)
        {
            var (brands, _) = await BrandsAsync(connection, slug, ct);
            if (brands.Count == 0)
            {
                continue;
            }

            claims[slug] = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COALESCE(SUM(claims), 0) FROM part_d_brand_prescriber WHERE npi = @npi AND brand IN @names",
                new { npi, names = brands.Select(b => b.Brand).ToArray() }, cancellationToken: ct));
        }

        return claims;
    }
}
