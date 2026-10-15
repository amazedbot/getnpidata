using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core;
using Npi.Core.Search;
using Npi.Web.Api;

namespace Npi.Web.Pages;

/// <summary>
/// /organizations, the "Groups &amp; organizations" page (docs/PRODUCT_PLAN.md step 3.6): Medicare group practices by size, the
/// largest groups, organization NPIs by plain category and record-quality label (each count opens the search list behind it), the
/// organizations Medicare pays most, and a search by name, NPI or PAC ID. /groups (or view=groups) is the paged list of groups
/// with its filters and CSV.
/// </summary>
public class OrganizationsModel(OrganizationDirectory directory, SearchService search) : PageModel
{
    public const int Matches = 5;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? View { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? MinClinicians { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? MaxClinicians { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    /// <summary>
    /// The list's page, read from the query string in <c>OnGetAsync</c>: a property bound by the name "page" would get Razor Pages'
    /// own route value of that name (the page's path) instead.
    /// </summary>
    public int PageNumber { get; set; } = 1;

    public OrganizationOverview Overview { get; private set; } = null!;

    /// <summary>The overview's organization NPIs by category, in <see cref="State"/> or everywhere.</summary>
    public IReadOnlyList<OrganizationCategoryCount> Categories { get; private set; } = [];

    /// <summary>The groups list (view=groups), or the groups matching the search on the overview.</summary>
    public GroupPage? Groups { get; private set; }

    /// <summary>Organization NPIs whose name matches the search (overview only).</summary>
    public SearchResult? OrganizationMatches { get; private set; }

    public IReadOnlyDictionary<string, string[]> Errors { get; private set; } = new Dictionary<string, string[]>();

    /// <summary>True before the loader has built the directory tables (the first load after they were added): the page says so.</summary>
    public bool NotReady { get; private set; }

    public bool GroupsView => View == "groups";

    public bool Searched => !string.IsNullOrWhiteSpace(Q);

    /// <summary>The search text when it is an NPI (with a valid check digit), for a link to its profile.</summary>
    public string? SearchedNpi => Q?.Trim() is { } q && InputFormats.IsNpi(q) && InputFormats.HasValidCheckDigit(q) ? q : null;

    public int PageCount => Groups is null ? 1 : Math.Max(1, (Groups.TotalCount + GroupFilter.DefaultPageSize - 1) / GroupFilter.DefaultPageSize);

    public async Task OnGetAsync(CancellationToken ct)
    {
        PageNumber = int.TryParse(Request.Query["page"], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : 1;
        if (View is null && Request.Path.StartsWithSegments("/groups", StringComparison.OrdinalIgnoreCase))
        {
            View = "groups";
        }

        View = View?.Trim().ToLowerInvariant() == "groups" ? "groups" : null;
        State = string.IsNullOrWhiteSpace(State) ? null : State.Trim().ToUpperInvariant();
        var filter = Filter(GroupsView ? PageNumber : 1);
        var errors = filter.Validate();
        // A size that isn't a number (the page number is left to Razor Pages, whose route values also carry a "page").
        foreach (var key in new[] { nameof(MinClinicians), nameof(MaxClinicians) })
        {
            if (ModelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0)
            {
                errors[char.ToLowerInvariant(key[0]) + key[1..]] = ["Must be a whole number."];
            }
        }

        if (errors.Count > 0)
        {
            Errors = errors;
            Q = State = Sort = null;
            MinClinicians = MaxClinicians = null;
            PageNumber = 1;
            filter = Filter(1);
        }

        if (!await directory.IsReadyAsync(ct))
        {
            NotReady = true;
            return;
        }

        Overview = await directory.GetOverviewAsync(ct);
        if (GroupsView)
        {
            Groups = await directory.SearchGroupsAsync(filter, ct);
            if (Groups.Items.Count == 0 && PageNumber > 1)
            {
                PageNumber = PageCount;
                Groups = await directory.SearchGroupsAsync(Filter(PageNumber), ct);
            }

            return;
        }

        Categories = await directory.GetCategoriesAsync(State, ct);
        if (Searched)
        {
            Groups = await directory.SearchGroupsAsync(new GroupFilter { Q = Q, PageSize = Matches }, ct);
            var q = Q!.Trim();
            if (!InputFormats.IsNpi(q))
            {
                // Organization names, any word anywhere (as the header search does for an organization).
                try
                {
                    OrganizationMatches = await search.SearchAsync(new SearchFilter
                    {
                        OrgName = q, NameMatch = "similar", EntityType = 2, PageSize = Matches,
                    }, ct);
                }
                catch (SearchValidationException)
                {
                    OrganizationMatches = null;
                }
            }
        }
    }

    private GroupFilter Filter(int page) => DirectoryEndpoints.Groups(GroupsView ? Q : null, State, MinClinicians, MaxClinicians, Sort, page);

    /// <summary>The groups list's query string with some values replaced (null removes one); page 1 is left out.</summary>
    public string GroupsUrl(string path = "/groups", string? state = "", string? minClinicians = "", string? maxClinicians = "", string? sort = "",
        int page = 1, string? q = "")
    {
        var values = new (string Key, string? Value)[]
        {
            ("q", q == "" ? (GroupsView ? Q : null) : q), ("state", state == "" ? State : state),
            ("minClinicians", minClinicians == "" ? MinClinicians?.ToString(CultureInfo.InvariantCulture) : minClinicians),
            ("maxClinicians", maxClinicians == "" ? MaxClinicians?.ToString(CultureInfo.InvariantCulture) : maxClinicians),
            ("sort", sort == "" ? Sort : sort), ("page", page > 1 ? page.ToString(CultureInfo.InvariantCulture) : null),
        };
        var query = string.Join("&", values.Where(v => !string.IsNullOrWhiteSpace(v.Value)).Select(v => $"{v.Key}={Uri.EscapeDataString(v.Value!.Trim())}"));
        return query.Length == 0 ? path : $"{path}?{query}";
    }

    /// <summary>A size band's list: its bounds, largest first.</summary>
    public string BandUrl(GroupSizeBand band) =>
        GroupsUrl(state: null, minClinicians: band.Min.ToString(CultureInfo.InvariantCulture),
            maxClinicians: band.Max?.ToString(CultureInfo.InvariantCulture), sort: null, q: null);

    /// <summary>A column header's sort link. Numbers start largest first, words A to Z.</summary>
    public string SortUrl(string key)
    {
        var current = Sort ?? GroupFilter.DefaultSort;
        var numeric = key is "clinicians" or "mips" or "organizations";
        var first = numeric ? "-" + key : key;
        var next = current == first ? (numeric ? key : "-" + key) : first;
        return GroupsUrl(sort: next == GroupFilter.DefaultSort ? null : next);
    }

    public string? AriaSort(string key)
    {
        var current = Sort ?? GroupFilter.DefaultSort;
        return current == key ? "ascending" : current == "-" + key ? "descending" : null;
    }

    /// <summary>The search list of organization NPIs in a category (and the chosen state), optionally one record-quality label.</summary>
    public string OrganizationsUrl(string path, string category, string? quality = null)
    {
        var parts = new List<string> { "entityType=2", "category=" + Uri.EscapeDataString(category) };
        if (State is not null)
        {
            parts.Add("state=" + Uri.EscapeDataString(State));
        }

        if (quality is not null)
        {
            parts.Add("quality=" + quality);
        }

        return path + "?" + string.Join("&", parts);
    }

    /// <summary>"81.5" or "—".</summary>
    public static string Score(double? score) => score is { } s ? s.ToString("0.#", CultureInfo.InvariantCulture) : "—";

    public static string Name(string name) => CityCatalog.TitleCase(name);
}
