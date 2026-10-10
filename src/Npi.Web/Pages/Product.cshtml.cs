using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>/product/{slug}: a product named in Open Payments (CLAUDE.md §7 Stage 5.5 item 19).</summary>
public class ProductModel(ProductService products) : PageModel
{
    public ProductDetail Product { get; private set; } = null!;

    /// <summary>The ten paid prescribers paid the most (part 4); null without Part D prescribing.</summary>
    public ProductPrescriberPage? TopPrescribers { get; private set; }

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        var product = await products.GetAsync(slug, ct);
        if (product is null)
        {
            return NotFound();
        }

        Product = product;
        TopPrescribers = product.Prescribing is null ? null : await products.PrescribersAsync(slug, "paid", 1, 10, ct);
        return Page();
    }
}
