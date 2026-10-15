using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;
using Npi.Web.Api;

namespace Npi.Web.Pages;

/// <summary>
/// /systems: the health systems in AHRQ's Compendium of U.S. Health Systems (docs/PRODUCT_PLAN.md step 3.6 part 2), by name or
/// city, with a state filter and sorts; each leads to /system/{id}.
/// </summary>
public class SystemsModel(HealthSystemService systems) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    /// <summary>Read from the query string: a property bound by the name "page" gets Razor Pages' route value of that name.</summary>
    public int PageNumber { get; private set; } = 1;

    public HealthSystemPage Result { get; private set; } = null!;

    public IReadOnlyDictionary<string, string[]> Errors { get; private set; } = new Dictionary<string, string[]>();

    public int PageCount => Math.Max(1, (Result.TotalCount + HealthSystemFilter.DefaultPageSize - 1) / HealthSystemFilter.DefaultPageSize);

    public async Task OnGetAsync(CancellationToken ct)
    {
        PageNumber = int.TryParse(Request.Query["page"], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : 1;
        State = string.IsNullOrWhiteSpace(State) ? null : State.Trim().ToUpperInvariant();
        var filter = SystemEndpoints.Systems(Q, State, Sort, PageNumber);
        if (filter.Validate() is { Count: > 0 } errors)
        {
            Errors = errors;
            Q = State = Sort = null;
            PageNumber = 1;
            filter = SystemEndpoints.Systems(null, null, null);
        }

        Result = await systems.SearchAsync(filter, ct);
    }

    /// <summary>The page's query string with some values replaced (null removes one); page 1 is left out.</summary>
    public string ListUrl(string? sort = "", int page = 1)
    {
        var values = new (string Key, string? Value)[]
        {
            ("q", Q), ("state", State), ("sort", sort == "" ? Sort : sort), ("page", page > 1 ? page.ToString(CultureInfo.InvariantCulture) : null),
        };
        var query = string.Join("&", values.Where(v => !string.IsNullOrWhiteSpace(v.Value)).Select(v => $"{v.Key}={Uri.EscapeDataString(v.Value!.Trim())}"));
        return query.Length == 0 ? "/systems" : "/systems?" + query;
    }

    /// <summary>A column's sort link: name ascending first, the counts largest first.</summary>
    public string SortUrl(string key)
    {
        var current = Sort ?? HealthSystemFilter.DefaultSort;
        var next = key == "name" ? (current == "name" ? "-name" : "name") : (current == "-" + key ? key : "-" + key);
        return ListUrl(sort: next == HealthSystemFilter.DefaultSort ? null : next);
    }

    public string? AriaSort(string key)
    {
        var current = Sort ?? HealthSystemFilter.DefaultSort;
        return current == key ? "ascending" : current == "-" + key ? "descending" : null;
    }

    public static string Count(int? n) => n is { } v ? Format.Number(v) : "—";
}
