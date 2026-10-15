using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /owners: the organizations owning Medicare-certified facilities (docs/PRODUCT_PLAN.md step 3.6 part 2), by name or CMS associate
/// ID, with a filter for the owner types CMS marks (private equity, REIT …); those owning the most first. People are not listed
/// (owner, 2026-10-15): they appear by name and role on the facility pages only.
/// </summary>
public class OwnersModel(OwnershipService ownership) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }


    [BindProperty(SupportsGet = true)]
    public string? Kind { get; set; }

    /// <summary>Read from the query string: a property bound by the name "page" gets Razor Pages' own route value instead.</summary>
    public int PageNumber { get; private set; } = 1;

    public OwnerPage Result { get; private set; } = null!;

    public IReadOnlyDictionary<string, string[]> Errors { get; private set; } = new Dictionary<string, string[]>();

    public int PageCount => Math.Max(1, (Result.TotalCount + OwnershipService.DefaultPageSize - 1) / OwnershipService.DefaultPageSize);

    public async Task OnGetAsync(CancellationToken ct)
    {
        PageNumber = int.TryParse(Request.Query["page"], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : 1;
        Q = string.IsNullOrWhiteSpace(Q) ? null : Q.Trim();
        Kind = string.IsNullOrWhiteSpace(Kind) ? null : Kind.Trim().ToLowerInvariant();
        var errors = OwnershipService.Validate(Q, Kind, PageNumber, OwnershipService.DefaultPageSize);
        if (errors.Count > 0)
        {
            // A bad filter shows the error and the whole list.
            Errors = errors;
            (Q, Kind, PageNumber) = (null, null, 1);
        }

        Result = await ownership.SearchOwnersAsync(Q, Kind, PageNumber, OwnershipService.DefaultPageSize, ct);
    }

    public string PageUrl(int page) =>
        "/owners" + Query(("q", Q), ("kind", Kind), ("page", page > 1 ? page.ToString(CultureInfo.InvariantCulture) : null));

    public string ApiUrl => "/api/v1/owners" + Query(("q", Q), ("kind", Kind));

    private static string Query(params (string Key, string? Value)[] parts)
    {
        var set = parts.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}").ToList();
        return set.Count == 0 ? "" : "?" + string.Join("&", set);
    }
}
