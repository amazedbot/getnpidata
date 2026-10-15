using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using CsvHelper;
using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>All companies' Open Payments in one program year: general and research payments and how many there were.</summary>
public sealed record IndustryYear(int Year, double General, double Research, long Payments);

/// <summary>How many products of one Open Payments type (Drug, Biological, Device, Medical Supply) the newest year names.</summary>
public sealed record ProductKindCount(string Kind, int Products);

/// <summary>
/// The Companies &amp; products page (docs/PRODUCT_PLAN.md Phase 9, owner 2026-10-12): every company reporting to Open Payments
/// and what they paid per year, the products named in the newest year, how many providers were paid then, and the largest
/// companies and products.
/// </summary>
public sealed record IndustryOverview(int Companies, double Paid, int? FirstYear, int? LastYear, IReadOnlyList<IndustryYear> Years, int? ProductYear,
    int Products, IReadOnlyList<ProductKindCount> ProductKinds, int ProvidersPaid, IReadOnlyList<CompanySummary> TopCompanies,
    IReadOnlyList<ProductSummary> TopProducts);

/// <summary>One company in the all-companies export.</summary>
public sealed record CompanyExportRow(string Id, string Name, string? OtherNames, string? State, string? Country, double General, double Research,
    double OwnershipInvested, double OwnershipValue, int? Providers, int? FirstYear, int? LastYear);

/// <summary>One product in the all-products export.</summary>
public sealed record ProductExportRow(string Slug, string Name, string? Kind, string? Category, string? Ndc, string? DeviceId, int Year, double Amount,
    int Records, int Companies, int Providers);

/// <summary>The overview, cached an hour (the tables change only when the loader reloads Open Payments), and the all-rows exports.</summary>
public sealed class IndustryService(string connectionString, CompanyService companies, ProductService products) : IDisposable
{
    public const int TopCount = 10;

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _lock = new(1, 1);

    private (DateTime At, IndustryOverview Overview)? _cache;

    public void Dispose() => _lock.Dispose();

    private sealed record YearRow(short Year, double General, double Research, decimal Payments);

    // Types as MySqlConnector returns them (Dapper maps records by constructor): SMALLINT → short.
    private sealed record CompanyRow(string Id, string Name, string? OtherNames, string? State, string? Country, double General, double Research,
        double OwnershipInvested, double OwnershipValue, int? Providers, short? FirstYear, short? LastYear);

    private sealed record ProductRow(string Slug, string Name, string? Kind, string? Category, string? Ndc, string? DeviceId, short Year, double Amount,
        int Records, int Companies, int Providers);

    public async Task<IndustryOverview> GetOverviewAsync(CancellationToken ct)
    {
        if (_cache is { } hit && DateTime.UtcNow - hit.At < CacheFor)
        {
            return hit.Overview;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } again && DateTime.UtcNow - again.At < CacheFor)
            {
                return again.Overview;
            }

            await using var connection = new MySqlConnection(connectionString);
            var (count, paid) = await connection.QuerySingleAsync<(long, double)>(new CommandDefinition(
                "SELECT COUNT(*), COALESCE(SUM(general_amount + research_amount), 0) FROM op_company", cancellationToken: ct));
            var years = (await connection.QueryAsync<YearRow>(new CommandDefinition(
                """
                SELECT program_year AS Year, SUM(general_amount) AS General, SUM(research_amount) AS Research,
                       SUM(general_records + research_records) AS Payments
                FROM op_company_year GROUP BY program_year ORDER BY program_year
                """, cancellationToken: ct)))
                .Select(y => new IndustryYear(y.Year, y.General, y.Research, (long)y.Payments)).ToList();
            var kinds = (await connection.QueryAsync<(string Kind, long Products)>(new CommandDefinition(
                "SELECT COALESCE(kind, 'Other'), COUNT(*) FROM op_product GROUP BY COALESCE(kind, 'Other') ORDER BY COUNT(*) DESC", cancellationToken: ct)))
                .Select(k => new ProductKindCount(k.Kind, (int)k.Products)).ToList();
            var productYear = await connection.ExecuteScalarAsync<int?>(new CommandDefinition("SELECT MAX(program_year) FROM op_product", cancellationToken: ct));
            var providersPaid = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM open_payments_summary", cancellationToken: ct));

            var overview = new IndustryOverview((int)count, paid, years.Count > 0 ? years[0].Year : null, years.Count > 0 ? years[^1].Year : null, years,
                productYear, kinds.Sum(k => k.Products), kinds, (int)providersPaid,
                (await companies.SearchAsync(null, 1, TopCount, ct)).Items, (await products.SearchAsync(null, null, 1, TopCount, ct)).Items);
            _cache = (DateTime.UtcNow, overview);
            return overview;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Every company, largest payments (general + research, all years) first.</summary>
    public async IAsyncEnumerable<CompanyExportRow> AllCompaniesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        var rows = await connection.QueryAsync<CompanyRow>(new CommandDefinition(
            """
            SELECT c.company_id AS Id, c.name AS Name, c.other_names AS OtherNames, c.state AS State, c.country AS Country,
                   c.general_amount AS General, c.research_amount AS Research, c.invested_amount AS OwnershipInvested, c.interest_value AS OwnershipValue,
                   r.providers AS Providers, c.first_year AS FirstYear, c.last_year AS LastYear
            FROM op_company c LEFT JOIN op_company_reach r ON r.company_id = c.company_id
            ORDER BY c.general_amount + c.research_amount DESC, c.name, c.company_id
            """, cancellationToken: ct));
        foreach (var r in rows)
        {
            yield return new CompanyExportRow(r.Id, r.Name, r.OtherNames, r.State, r.Country, r.General, r.Research, r.OwnershipInvested, r.OwnershipValue,
                r.Providers, r.FirstYear, r.LastYear);
        }
    }

    /// <summary>Every product named in the newest year's general payments, largest payments first.</summary>
    public async IAsyncEnumerable<ProductExportRow> AllProductsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        var rows = await connection.QueryAsync<ProductRow>(new CommandDefinition(
            """
            SELECT slug AS Slug, name AS Name, kind AS Kind, category AS Category, ndc AS Ndc, device_id AS DeviceId, program_year AS Year,
                   amount AS Amount, records AS Records, companies AS Companies, providers AS Providers
            FROM op_product ORDER BY amount DESC, slug
            """, cancellationToken: ct));
        foreach (var r in rows)
        {
            yield return new ProductExportRow(r.Slug, r.Name, r.Kind, r.Category, r.Ndc, r.DeviceId, r.Year, r.Amount, r.Records, r.Companies, r.Providers);
        }
    }
}

/// <summary>The all-companies and all-products CSV files (UTF-8 with BOM, like the other exports).</summary>
public static class IndustryCsv
{
    public static readonly string[] CompanyHeader =
    [
        "Open Payments ID", "Company", "Other Names", "State", "Country", "General Payments", "Research Payments", "Ownership Invested",
        "Ownership Value", "Providers Paid", "First Year", "Last Year", "Page",
    ];

    public static readonly string[] ProductHeader =
    [
        "Product", "Type", "Category", "NDC", "Device Identifier", "Program Year", "Payments Naming It", "Number of Payments", "Companies",
        "Providers Paid", "Page",
    ];

    private static string Money(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    public static Task<long> WriteCompaniesAsync(IAsyncEnumerable<CompanyExportRow> rows, Stream output, string siteBase, CancellationToken ct) =>
        WriteRowsAsync(rows, output, CompanyHeader, c =>
        [
            c.Id, c.Name, c.OtherNames?.Replace(" | ", "; ", StringComparison.Ordinal), c.State, c.Country, Money(c.General), Money(c.Research),
            Money(c.OwnershipInvested), Money(c.OwnershipValue), c.Providers?.ToString(CultureInfo.InvariantCulture),
            c.FirstYear?.ToString(CultureInfo.InvariantCulture), c.LastYear?.ToString(CultureInfo.InvariantCulture),
            $"{siteBase}/company/{Uri.EscapeDataString(c.Id)}",
        ], ct);

    public static Task<long> WriteProductsAsync(IAsyncEnumerable<ProductExportRow> rows, Stream output, string siteBase, CancellationToken ct) =>
        WriteRowsAsync(rows, output, ProductHeader, p =>
        [
            p.Name, p.Kind, p.Category, p.Ndc, p.DeviceId, p.Year.ToString(CultureInfo.InvariantCulture), Money(p.Amount),
            p.Records.ToString(CultureInfo.InvariantCulture), p.Companies.ToString(CultureInfo.InvariantCulture), p.Providers.ToString(CultureInfo.InvariantCulture),
            $"{siteBase}/product/{Uri.EscapeDataString(p.Slug)}",
        ], ct);

    /// <summary>Rows as CSV with a header, UTF-8 with BOM; the number of rows written.</summary>
    public static async Task<long> WriteRowsAsync<T>(IAsyncEnumerable<T> rows, Stream output, string[] header, Func<T, string?[]> values, CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16, leaveOpen: true);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        foreach (var h in header)
        {
            csv.WriteField(h);
        }

        await csv.NextRecordAsync();
        long count = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
            foreach (var v in values(row))
            {
                csv.WriteField(v);
            }

            await csv.NextRecordAsync();
            count++;
        }

        await csv.FlushAsync();
        return count;
    }
}
