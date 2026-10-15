using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;
using Npi.Web.Api;

namespace Npi.Web.Pages;

/// <summary>/acos: the Medicare Shared Savings Program's accountable care organizations (docs/PRODUCT_PLAN.md step 3.6 part 2).</summary>
public class AcosModel(AcoService acos) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    /// <summary>Read from the query string: a property bound by the name "page" gets Razor Pages' route value of that name.</summary>
    public int PageNumber { get; private set; } = 1;

    public AcoPage Result { get; private set; } = null!;

    public IReadOnlyDictionary<string, string[]> Errors { get; private set; } = new Dictionary<string, string[]>();

    public int PageCount => Math.Max(1, (Result.TotalCount + AcoFilter.DefaultPageSize - 1) / AcoFilter.DefaultPageSize);

    public async Task OnGetAsync(CancellationToken ct)
    {
        PageNumber = int.TryParse(Request.Query["page"], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : 1;
        State = string.IsNullOrWhiteSpace(State) ? null : State.Trim().ToUpperInvariant();
        var filter = SystemEndpoints.Acos(Q, State, PageNumber);
        if (filter.Validate() is { Count: > 0 } errors)
        {
            Errors = errors;
            Q = State = null;
            PageNumber = 1;
            filter = SystemEndpoints.Acos(null, null);
        }

        Result = await acos.SearchAsync(filter, ct);
    }

    public string PageUrl(int page)
    {
        var values = new (string Key, string? Value)[] { ("q", Q), ("state", State), ("page", page > 1 ? page.ToString(CultureInfo.InvariantCulture) : null) };
        var query = string.Join("&", values.Where(v => !string.IsNullOrWhiteSpace(v.Value)).Select(v => $"{v.Key}={Uri.EscapeDataString(v.Value!.Trim())}"));
        return query.Length == 0 ? "/acos" : "/acos?" + query;
    }
}
