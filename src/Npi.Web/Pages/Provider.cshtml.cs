using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>/provider/{npi}: all taxonomies, practice locations and other names (CLAUDE.md §7 Stage 4).</summary>
public class ProviderModel(ProviderDetailService details) : PageModel
{
    public ProviderDetail Provider { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(string npi, CancellationToken ct)
    {
        var provider = await details.GetAsync(npi, ct);
        if (provider is null)
        {
            return NotFound(); // unknown, malformed, or deactivated (never shown)
        }

        Provider = provider;
        return Page();
    }
}
