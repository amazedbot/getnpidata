using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;
using Npi.Web.Search;

namespace Npi.Web.Pages;

/// <summary>The search form and results grid (CLAUDE.md §7 Stage 4). A GET form, so every search has a shareable URL.</summary>
public class IndexModel(SearchService search, TaxonomyCatalog taxonomy, GeographyCatalog geography, AreaService areas) : PageModel
{
    public static readonly int[] RadiusChoices = [5, 10, 25, 50, 100];

    public static readonly int[] YearChoices = [5, 10, 20, 30];

    public SearchFilter Filter { get; private set; } = new();

    public SearchResult? Result { get; private set; }

    public IReadOnlyDictionary<string, string[]> Errors { get; private set; } = new Dictionary<string, string[]>();

    public IReadOnlyList<string> Classifications { get; private set; } = [];

    public IReadOnlyList<string> Specializations { get; private set; } = [];

    public IReadOnlyList<StateInfo> States { get; private set; } = [];

    public IReadOnlyList<CountyInfo> Counties { get; private set; } = [];

    public SearchSortOrder Sort { get; private set; } = SearchSortOrder.Default;

    /// <summary>Population and shortage facts for the searched county (Stage 5.5 item 7).</summary>
    public CountyFacts? County { get; private set; }

    public long PageCount => Result is null ? 0 : Math.Max(1, (Result.TotalCount + Result.PageSize - 1) / Result.PageSize);

    public async Task OnGetAsync(CancellationToken ct)
    {
        var (filter, errors) = SearchQueryString.Parse(Request.Query);
        Filter = filter;
        Classifications = await taxonomy.GetClassificationsAsync(ct);
        States = await geography.GetStatesAsync(ct);
        if (filter.Classification is not null)
        {
            Specializations = await taxonomy.GetSpecializationsAsync(filter.Classification, ct);
        }

        if (filter.State is not null)
        {
            Counties = await geography.GetCountiesAsync(filter.State, ct);
        }

        Sort = SearchSortOrder.TryParse(filter.Sort, out var sort) ? sort : SearchSortOrder.Default;

        if (!SearchQueryString.HasAnyFilter(Request.Query))
        {
            return; // first visit: just the form
        }

        if (errors.Count > 0)
        {
            Errors = errors;
            return;
        }

        try
        {
            Result = await search.SearchAsync(filter, ct);
            if (filter.CountyFips is not null)
            {
                County = await areas.GetCountyAsync(filter.CountyFips, ct);
            }
        }
        catch (SearchValidationException ex)
        {
            Errors = ex.Errors;
        }
    }

    public string Error(string field) => Errors.TryGetValue(field, out var messages) ? string.Join(" ", messages) : "";

    public string PageLink(int page) => "/" + SearchQueryString.ToQueryString(Filter, page: page);

    public string CsvLink => "/export.csv" + SearchQueryString.ToQueryString(Filter, includePaging: false);

    /// <summary>Link for a column header: ascending, or descending when already sorted ascending by it.</summary>
    public string SortLink(SearchSort key)
    {
        var descending = Sort.Key == key && !Sort.Descending;
        return "/" + SearchQueryString.ToQueryString(Filter, page: 1, sort: new SearchSortOrder(key, descending).ToString());
    }

    public string AriaSort(SearchSort key) => Sort.Key != key ? "none" : Sort.Descending ? "descending" : "ascending";
}
