using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>/company/{id}: a company that reports to Open Payments, by its CMS ID (CLAUDE.md §7 Stage 5.5 item 17).</summary>
public class CompanyModel(CompanyService companies) : PageModel
{
    public CompanyDetail Company { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        var company = await companies.GetAsync(id, ct);
        if (company is null)
        {
            return NotFound();
        }

        Company = company;
        return Page();
    }
}
