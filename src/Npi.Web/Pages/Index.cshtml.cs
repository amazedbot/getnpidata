using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Npi.Core.Search;
using Npi.Web.Search;

namespace Npi.Web.Pages;

/// <summary>What the search page shows (docs/PRODUCT_PLAN.md step 1.3).</summary>
public enum SearchPageMode
{
    /// <summary>"/" with nothing asked: the home page.</summary>
    Home,

    /// <summary>"/search" with nothing asked: the list builder's filters, no results yet.</summary>
    Builder,

    /// <summary>A list: filters (or a typed specialty/place) and its results.</summary>
    Results,

    /// <summary>A typed name: matching people and organizations.</summary>
    Names,
}

/// <summary>
/// The home page, the search box's answers and the list builder (CLAUDE.md §7 Stage 4; docs/PRODUCT_PLAN.md step 1.3). A GET form, so every
/// search has a shareable URL. <c>q</c> is read by <see cref="QueryInterpreter"/>; the structured parameters are the API's.
/// </summary>
public class IndexModel(SearchService search, TaxonomyCatalog taxonomy, GeographyCatalog geography, CityCatalog cities, AreaService areas,
    CredentialCatalog credentials, SearchVocabularyCatalog vocabularies) : PageModel
{
    public static readonly int[] RadiusChoices = [5, 10, 25, 50, 100];

    public static readonly int[] YearChoices = [5, 10, 20, 30];

    /// <summary>People and organizations shown per block for a typed name.</summary>
    public const int NameBlockSize = 10;

    private static (DateTime At, long Count)? s_newProviders;

    public SearchPageMode Mode { get; private set; }

    /// <summary>What was typed in the search box.</summary>
    public string? Query { get; private set; }

    public QueryInterpretation? Interpretation { get; private set; }

    public SearchFilter Filter { get; private set; } = new();

    public SearchResult? Result { get; private set; }

    public SearchResult? People { get; private set; }

    public SearchResult? Organizations { get; private set; }

    public SearchFilter PeopleFilter { get; private set; } = new();

    public SearchFilter OrganizationsFilter { get; private set; } = new();

    public IReadOnlyDictionary<string, string[]> Errors { get; private set; } = new Dictionary<string, string[]>();

    /// <summary>A message about an export that came back to the page (e.g. Excel's row limit).</summary>
    public string? Notice { get; private set; }

    /// <summary>The pages a visitor can browse: paging stops at <see cref="SearchFilter.MaxResultWindow"/> rows.</summary>
    public long BrowsablePages => Result is null ? 0 : Math.Min(PageCount, Math.Max(1, SearchFilter.MaxResultWindow / Result.PageSize));

    public IReadOnlyList<string> Classifications { get; private set; } = [];

    public IReadOnlyList<string> Specializations { get; private set; } = [];

    public IReadOnlyList<StateInfo> States { get; private set; } = [];

    public IReadOnlyList<CountyInfo> Counties { get; private set; } = [];

    /// <summary>The City dropdown: the state's (or county's) cities, plus the searched city if it isn't among them.</summary>
    public IReadOnlyList<string> Cities { get; private set; } = [];

    public SearchSortOrder Sort { get; private set; } = SearchSortOrder.Default;

    /// <summary>Population and shortage facts for the searched county (Stage 5.5 item 7).</summary>
    public CountyFacts? County { get; private set; }

    /// <summary>The home page's "this week in the data" panel.</summary>
    public DataVersion? Version { get; private set; }

    /// <summary>NPIs issued in the last 30 days (home page), cached half an hour.</summary>
    public long? NewProviders { get; private set; }

    /// <summary>The specialty and provider fields, shared with the map page.</summary>
    public FilterFields Fields => new(Filter, Classifications, Specializations, Errors) { Credentials = Credentials, SelectedCredential = SelectedCredential };

    private string? SelectedCredential { get; set; }

    public IReadOnlyList<CredentialInfo> Credentials { get; private set; } = [];

    public long PageCount => Result is null ? 0 : Math.Max(1, (Result.TotalCount + Result.PageSize - 1) / Result.PageSize);

    /// <summary>True when any filter other than specialty and location is set: the "More filters" drawer opens.</summary>
    public bool MoreFiltersSet => Filter is not { LastName: null, FirstName: null, OrgName: null, Npi: null, EntityType: null, Gender: null, Credential: null,
        Excluded: null, OptedOut: null, OrderRefer: null, AcceptsAssignment: null, Telehealth: null, MinYears: null, MedicareActive: null, Shortage: null,
        AddressType: null, Quality: null, NewWithinDays: null, UpdatedWithinDays: null };

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var (filter, errors) = SearchQueryString.Parse(Request.Query);
        Query = Request.Query["q"].ToString().Trim() is { Length: > 0 } q ? q : null;
        ViewData["Query"] = Query;

        if (Query is not null && QuickSearch.Target(Query) is { } target && target.StartsWith("/provider/", StringComparison.Ordinal))
        {
            return Redirect(target); // an NPI: no need to read the words
        }

        if (Query is not null)
        {
            Interpretation = QueryInterpreter.Interpret(Query, await vocabularies.GetAsync(ct));
            switch (Interpretation.Kind)
            {
                case QueryKind.Npi:
                    return Redirect("/provider/" + Interpretation.Npi);
                case QueryKind.Names:
                    await LoadNamesAsync(Interpretation.NameText!, ct);
                    Mode = SearchPageMode.Names;
                    await LoadFormAsync(ct);
                    return Page();
                case QueryKind.List:
                    filter = Interpretation.Filter with { Sort = filter.Sort, Page = filter.Page, PageSize = filter.PageSize };
                    errors = [];
                    break;
                default:
                    Mode = SearchPageMode.Builder;
                    await LoadFormAsync(ct);
                    return Page();
            }
        }

        Filter = filter;
        await LoadFormAsync(ct);
        Sort = SearchSortOrder.TryParse(filter.Sort, out var sort) ? sort : SearchSortOrder.Default;

        // "dentists near me" with no place: don't list the whole country; offer the filters and the map, which can ask where the visitor is.
        if (Interpretation is { NearMe: true } && filter is { State: null, CountyFips: null, City: null, Zip5: null })
        {
            Mode = SearchPageMode.Builder;
            return Page();
        }

        // The home page; /search with no filter lists every active provider (owner, 2026-10-14), with the builder beside it.
        if (Query is null && !SearchQueryString.HasAnyFilter(Request.Query) && !Request.Path.StartsWithSegments("/search", StringComparison.OrdinalIgnoreCase))
        {
            Mode = SearchPageMode.Home;
            ViewData["HideTopSearch"] = true;
            Version = await geography.GetDataVersionAsync(ct);
            NewProviders = await NewProvidersAsync(ct);
            return Page();
        }

        // An export that couldn't be made (Excel past its row limit) comes back here to say so.
        if (Request.Query["notice"] == "excel-limit")
        {
            Notice = $"Excel holds at most {ProviderXlsx.MaxRows:N0} rows, and this list is larger. Download it as CSV, which has no row limit, or narrow the list.";
        }

        Mode = SearchPageMode.Results;
        if (errors.Count > 0)
        {
            Errors = errors;
            return Page();
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

        return Page();
    }

    private async Task LoadFormAsync(CancellationToken ct)
    {
        Classifications = await taxonomy.GetClassificationsAsync(ct);
        States = await geography.GetStatesAsync(ct);
        Credentials = await credentials.GetAllAsync(ct);
        SelectedCredential = Filter.Credential is null ? null : await credentials.ResolveAsync(Filter.Credential, ct);
        if (Filter.Classification is not null)
        {
            Specializations = await taxonomy.GetSpecializationsAsync(Filter.Classification, ct);
        }

        if (Filter.State is not null)
        {
            Counties = await geography.GetCountiesAsync(Filter.State, ct);
            Cities = await cities.GetCitiesAsync(Filter.State, Counties.Any(c => c.Fips == Filter.CountyFips) ? Filter.CountyFips : null, ct);
        }

        if (Filter.City is not null && !Cities.Contains(Filter.City, StringComparer.OrdinalIgnoreCase))
        {
            Cities = [.. Cities.Append(Filter.City).Order(StringComparer.OrdinalIgnoreCase)]; // a shared link or an API-style city keeps working
        }
    }

    // A typed name: the closest people and organizations, each with a link to all of them.
    private async Task LoadNamesAsync(string name, CancellationToken ct)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        PeopleFilter = words.Length == 1
            ? new SearchFilter { LastName = words[0], EntityType = 1, NameMatch = NameSearch.Similar }
            : new SearchFilter { FirstName = string.Join(' ', words[..^1]), LastName = words[^1], EntityType = 1, NameMatch = NameSearch.Similar };
        OrganizationsFilter = new SearchFilter { OrgName = name, NameMatch = NameSearch.Similar };
        var errors = new Dictionary<string, string[]>();
        People = await TrySearchAsync(PeopleFilter with { PageSize = NameBlockSize }, errors, ct);
        Organizations = await TrySearchAsync(OrganizationsFilter with { PageSize = NameBlockSize }, errors, ct);
        Errors = errors;
    }

    private async Task<SearchResult?> TrySearchAsync(SearchFilter filter, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        try
        {
            return await search.SearchAsync(filter, ct);
        }
        catch (SearchValidationException ex)
        {
            foreach (var (key, value) in ex.Errors)
            {
                errors[key] = value;
            }

            return null;
        }
    }

    private async Task<long?> NewProvidersAsync(CancellationToken ct)
    {
        if (s_newProviders is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(30))
        {
            return cached.Count;
        }

        var result = await TrySearchAsync(new SearchFilter { NewWithinDays = 30, PageSize = 1 }, [], ct);
        if (result is null)
        {
            return null;
        }

        s_newProviders = (DateTime.UtcNow, result.TotalCount);
        return result.TotalCount;
    }

    /// <summary>"Chiropractors in Suffolk County, NY", "Dentists within 10 miles of 11701", "Providers in Texas".</summary>
    public string ListTitle
    {
        get
        {
            var f = Filter;
            var category = SpecialtyCategories.Default.Find(f.Category);
            var who = category?.Find(f.Subcategory)?.Title ?? category?.Title
                      ?? ((f.Specialization ?? f.Classification) is { } specialty ? Plural(specialty) : f.EntityType == 2 ? "Organizations" : "Providers");
            if (!SearchQueryString.HasAnyFilter(Request.Query) && Interpretation is null)
            {
                return "Every active US provider";
            }

            var state = f.State is null ? null : GeographyCatalog.StateName(f.State);
            var place = f.Zip5 is not null
                ? f.RadiusMiles is { } r ? $"within {r} miles of {f.Zip5}" : $"in ZIP {f.Zip5}"
                : f.City is not null ? $"in {f.City}, {f.State}"
                : f.CountyFips is not null && Counties.FirstOrDefault(c => c.Fips == f.CountyFips) is { } county ? $"in {county.Name}, {f.State}"
                : state is not null ? $"in {state}" : null;
            return place is null ? who : $"{who} {place}";
        }
    }

    /// <summary>"Chiropractor" → "Chiropractors", "Pharmacy" → "Pharmacies", "Dermatology" → "Dermatology providers".</summary>
    public static string Plural(string specialty)
    {
        var last = specialty.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1].ToLowerInvariant();
        if (last is "pharmacy" or "facility" or "agency" or "company")
        {
            return specialty[..^1] + "ies";
        }

        string[] people = ["ist", "or", "er", "ian", "ant", "ent", "ath", "hospital", "home", "center", "unit", "aide", "nurse", "coach", "worker", "assistant"];
        return people.Any(e => last.EndsWith(e, StringComparison.Ordinal)) ? specialty + "s" : specialty + " providers";
    }

    public string Error(string field) => Errors.TryGetValue(field, out var messages) ? string.Join(" ", messages) : "";

    public static string ListLink(SearchFilter filter) =>
        "/search" + SearchQueryString.ToQueryString(filter with { Page = 1, PageSize = SearchFilter.DefaultPageSize }, includePaging: false);

    public string PageLink(int page) => "/search" + SearchQueryString.ToQueryString(Filter, page: page);

    /// <summary>The map search page with the same filters; it opens on the area of the location filters (Stage 5.5 item 10).</summary>
    public string MapLink => "/map" + SearchQueryString.ToQueryString(Filter, includePaging: false);

    /// <summary>The filter as name/value pairs for the export panel's hidden fields.</summary>
    public IEnumerable<KeyValuePair<string, string>> FilterPairs =>
        QueryHelpers.ParseQuery(SearchQueryString.ToQueryString(Filter, includePaging: false))
            .SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string>(kv.Key, v ?? "")));

    /// <summary>The list without one part of what the search box understood.</summary>
    public string RemoveLink(QueryPart part)
    {
        var f = Filter;
        foreach (var p in part.Parameters)
        {
            f = p switch
            {
                "category" or "subcategory" => f with { Category = null, Subcategory = null },
                "classification" or "specialization" => f with { Classification = null, Specialization = null },
                "state" => f with { State = null, CountyFips = null, City = null },
                "county" => f with { CountyFips = null },
                "city" => f with { City = null },
                "zip" or "radius" => f with { Zip5 = null, RadiusMiles = null },
                _ => f with { LastName = null, FirstName = null, OrgName = null, NameMatch = null },
            };
        }

        return ListLink(f);
    }

    /// <summary>Link for a column header: ascending, or descending when already sorted ascending by it.</summary>
    public string SortLink(SearchSort key)
    {
        var descending = Sort.Key == key && !Sort.Descending;
        return "/search" + SearchQueryString.ToQueryString(Filter, page: 1, sort: new SearchSortOrder(key, descending).ToString());
    }

    public string AriaSort(SearchSort key) => Sort.Key != key ? "none" : Sort.Descending ? "descending" : "ascending";
}
