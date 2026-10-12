using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /companies-and-products (docs/PRODUCT_PLAN.md Phase 9, owner 2026-10-12): the main page of "Companies &amp; products": totals,
/// payments by year, the largest companies and products, a search across both, and the all-companies and all-products CSV files.
/// </summary>
public class CompaniesAndProductsModel(IndustryService industry, CompanyService companies, ProductService products) : PageModel
{
    public const int Matches = 5;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public IndustryOverview Overview { get; private set; } = null!;

    public CompanyPage? CompanyMatches { get; private set; }

    public ProductPage? ProductMatches { get; private set; }

    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Overview = await industry.GetOverviewAsync(ct);
        var q = Q?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            return;
        }

        if (q.Length > CompanyService.MaxNameLength)
        {
            Error = $"Search for at most {CompanyService.MaxNameLength} characters.";
            return;
        }

        CompanyMatches = await companies.SearchAsync(q, 1, Matches, ct);
        ProductMatches = await products.SearchAsync(q, null, 1, Matches, ct);
    }

    public YearBars Bars => new("Payments by program year, general and research",
        [.. Overview.Years.Select(y => (y.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), y.General + y.Research))]);
}
