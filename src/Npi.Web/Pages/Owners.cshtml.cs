using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /owners: the owners of Medicare-certified facilities (docs/PRODUCT_PLAN.md step 3.6 part 2), by name or CMS associate ID, with
/// filters for organizations or people and for the owner types CMS marks (private equity, REIT …); those owning the most first.
/// </summary>
public class OwnersModel(OwnershipService ownership) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Type { get; set; }

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
        Type = string.IsNullOrWhiteSpace(Type) ? null : Type.Trim().ToLowerInvariant();
        Kind = string.IsNullOrWhiteSpace(Kind) ? null : Kind.Trim().ToLowerInvariant();
        var errors = OwnershipService.Validate(Q, Type, Kind, PageNumber, OwnershipService.DefaultPageSize);
        if (errors.Count > 0)
        {
            // A bad filter shows the error and the whole list.
            Errors = errors;
            (Q, Type, Kind, PageNumber) = (null, null, null, 1);
        }

        Result = await ownership.SearchOwnersAsync(Q, Type, Kind, PageNumber, OwnershipService.DefaultPageSize, ct);
    }

    public string PageUrl(int page) =>
        "/owners" + Query(("q", Q), ("type", Type), ("kind", Kind), ("page", page > 1 ? page.ToString(CultureInfo.InvariantCulture) : null));

    public string ApiUrl => "/api/v1/owners" + Query(("q", Q), ("type", Type), ("kind", Kind));

    private static string Query(params (string Key, string? Value)[] parts)
    {
        var set = parts.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}").ToList();
        return set.Count == 0 ? "" : "?" + string.Join("&", set);
    }
}
