using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>/companies: companies that report to Open Payments, by name, largest payments first (CLAUDE.md §7 Stage 5.5 item 17).</summary>
public class CompaniesModel(CompanyService companies) : PageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)]
    public string? Name { get; set; }

    /// <summary>
    /// The list's page, read from the query string in <c>OnGetAsync</c>: a property bound by the name "page" got Razor Pages'
    /// own route value of that name (the page's path) instead, so every page showed page 1.
    /// </summary>
    public int PageNumber { get; private set; } = 1;

    public CompanyPage Result { get; private set; } = null!;

    public string? Error { get; private set; }

    public int PageCount => Math.Max(1, (Result.TotalCount + PageSize - 1) / PageSize);

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (Name?.Trim().Length > CompanyService.MaxNameLength)
        {
            Error = $"Enter at most {CompanyService.MaxNameLength} characters.";
            Name = null;
        }

        PageNumber = int.TryParse(Request.Query["page"], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : 1;
        Result = await companies.SearchAsync(Name, PageNumber, PageSize, ct);
    }

    public string PageUrl(int page) =>
        "/companies?" + string.Join("&", new[] { string.IsNullOrWhiteSpace(Name) ? null : "name=" + Uri.EscapeDataString(Name.Trim()), page > 1 ? $"page={page}" : null }
            .Where(p => p is not null));
}
