using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>/product/{slug}/prescribers: the paid providers who prescribed the drug to Medicare patients (CLAUDE.md §7 Stage 5.5 item 19, part 4).</summary>
public class ProductPrescribersModel(ProductService products) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public ProductDetail Product { get; private set; } = null!;

    public ProductPrescriberPage Result { get; private set; } = null!;

    public int PageCount => Math.Max(1, (Result.TotalCount + Result.PageSize - 1) / Result.PageSize);

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        if (Sort is not null && !ProductService.PrescriberSorts.Contains(Sort, StringComparer.OrdinalIgnoreCase))
        {
            Sort = null;
        }

        var product = await products.GetAsync(slug, ct);
        var page = product is null ? null : await products.PrescribersAsync(slug, Sort, Math.Max(1, PageNumber), ProductService.PrescriberPageSize, ct);
        if (product?.Prescribing is null || page is null)
        {
            return NotFound();
        }

        Product = product;
        Result = page;
        return Page();
    }

    public string PageUrl(string? sort, int page) =>
        $"/product/{Product.Slug}/prescribers?" + string.Join("&", new[] { sort is null or "paid" ? null : $"sort={sort}", page > 1 ? $"page={page}" : null }.Where(x => x is not null));
}
