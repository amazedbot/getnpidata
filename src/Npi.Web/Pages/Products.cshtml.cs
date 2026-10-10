using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>/products: products named in Open Payments, by name and type, largest payments first (CLAUDE.md §7 Stage 5.5 item 19).</summary>
public class ProductsModel(ProductService products) : PageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)]
    public string? Name { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Kind { get; set; }

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public ProductPage Result { get; private set; } = null!;

    public string? Error { get; private set; }

    public int PageCount => Math.Max(1, (Result.TotalCount + PageSize - 1) / PageSize);

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (Name?.Trim().Length > ProductService.MaxNameLength)
        {
            Error = $"Enter at most {ProductService.MaxNameLength} characters.";
            Name = null;
        }

        if (Kind is not null && !ProductService.Kinds.Contains(Kind))
        {
            Kind = null;
        }

        PageNumber = Math.Max(1, PageNumber);
        Result = await products.SearchAsync(Name, Kind, PageNumber, PageSize, ct);
    }

    public string PageUrl(int page) =>
        "/products?" + string.Join("&", new[]
        {
            string.IsNullOrWhiteSpace(Name) ? null : "name=" + Uri.EscapeDataString(Name.Trim()),
            Kind is null ? null : "kind=" + Uri.EscapeDataString(Kind),
            page > 1 ? $"page={page}" : null,
        }.Where(p => p is not null));
}
