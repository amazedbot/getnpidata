using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Npi.Core.Search;
using Npi.Web.Api;

namespace Npi.Web.Pages;

/// <summary>
/// /facilities, the "Facilities &amp; hospitals" page (docs/PRODUCT_PLAN.md step 3.6; /hospitals opens it on hospitals): every
/// Medicare-certified facility by type, how CMS rates them, a list with type, state, rating and status filters, the listed page on
/// a map, and the list as CSV. Each row leads to /facility/{ccn}.
/// </summary>
public class FacilitiesModel(FacilityDirectory facilities, IOptions<MapOptions> mapOptions) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Type { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? MinRating { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? MaxRating { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    /// <summary>
    /// The list's page, read from the query string in <c>OnGetAsync</c>: a property bound by the name "page" would get Razor Pages'
    /// own route value of that name (the page's path) instead.
    /// </summary>
    public int PageNumber { get; set; } = 1;

    public FacilityOverview Overview { get; private set; } = null!;

    public FacilityPage Result { get; private set; } = null!;

    public IReadOnlyList<FacilityTypeCount> TypeCounts { get; private set; } = [];

    public IReadOnlyDictionary<string, string[]> Errors { get; private set; } = new Dictionary<string, string[]>();

    public StaticMap? Map { get; private set; }

    public string Attribution => mapOptions.Value.Attribution;

    public FacilityType? SelectedType => FacilityTypes.Find(Type);

    public bool ShowClosed => Status is FacilityFilter.StatusClosed or FacilityFilter.StatusAll;

    public int PageCount => Math.Max(1, (Result.TotalCount + FacilityFilter.DefaultPageSize - 1) / FacilityFilter.DefaultPageSize);

    public async Task OnGetAsync(CancellationToken ct)
    {
        PageNumber = int.TryParse(Request.Query["page"], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : 1;
        // /hospitals is this page on hospitals.
        if (Type is null && Request.Path.StartsWithSegments("/hospitals", StringComparison.OrdinalIgnoreCase))
        {
            Type = FacilityTypes.Hospital;
        }

        Status = string.IsNullOrWhiteSpace(Status) ? null : Status.Trim().ToLowerInvariant();
        Type = FacilityTypes.Find(Type)?.Code ?? Type;
        var filter = Filter(PageNumber);
        var errors = filter.Validate();
        // A rating that isn't a number (the page number is left to Razor Pages, whose route values also carry a "page").
        foreach (var key in new[] { nameof(MinRating), nameof(MaxRating) })
        {
            if (ModelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0)
            {
                errors[char.ToLowerInvariant(key[0]) + key[1..]] = ["Must be a whole number."];
            }
        }

        if (errors.Count > 0)
        {
            Errors = errors;
            Q = Type = State = Status = Sort = null;
            MinRating = MaxRating = null;
            PageNumber = 1;
            filter = Filter(1);
        }

        Overview = await facilities.GetOverviewAsync(ct);
        Result = await facilities.SearchAsync(filter, ct);
        if (Result.Items.Count == 0 && PageNumber > 1)
        {
            PageNumber = PageCount;
            Result = await facilities.SearchAsync(Filter(PageNumber), ct);
        }

        TypeCounts = await facilities.CountByTypeAsync(filter, ct);
        Map = mapOptions.Value.Enabled
            ? StaticMap.Create(Result.Items.Select((f, i) => (f, i)).Where(x => x.f.Latitude is not null)
                .Select(x => (((PageNumber - 1) * FacilityFilter.DefaultPageSize + x.i + 1).ToString(CultureInfo.InvariantCulture), x.f.Latitude!.Value,
                    x.f.Longitude!.Value, true)), mapOptions.Value.TileUrl)
            : null;
    }

    private FacilityFilter Filter(int page) => DirectoryEndpoints.Facilities(Q, Type, State, MinRating, MaxRating, Status, Sort, page);

    /// <summary>"Hospitals in NY", "Closed nursing homes", "Facilities matching “good samaritan”".</summary>
    public string ListTitle
    {
        get
        {
            var what = SelectedType?.Plural ?? "Certified facilities";
            if (Status == FacilityFilter.StatusClosed)
            {
                what = "Closed " + (SelectedType?.Plural ?? "facilities").ToLowerInvariant();
            }

            if (State is { Length: 2 } st)
            {
                what += " in " + GeographyCatalog.StateName(st.ToUpperInvariant());
            }

            return string.IsNullOrWhiteSpace(Q) ? what : $"{what} matching “{Q.Trim()}”";
        }
    }

    /// <summary>The page's query string with some values replaced (null removes one); page 1 is left out.</summary>
    public string ListUrl(string path = "/facilities", string? type = "", string? minRating = "", string? maxRating = "", string? status = "",
        string? sort = "", int page = 1, string? state = "", string? q = "")
    {
        var values = new (string Key, string? Value)[]
        {
            ("q", q == "" ? Q : q), ("type", type == "" ? Type : type), ("state", state == "" ? State : state),
            ("minRating", minRating == "" ? MinRating?.ToString(CultureInfo.InvariantCulture) : minRating),
            ("maxRating", maxRating == "" ? MaxRating?.ToString(CultureInfo.InvariantCulture) : maxRating),
            ("status", status == "" ? Status : status), ("sort", sort == "" ? Sort : sort),
            ("page", page > 1 ? page.ToString(CultureInfo.InvariantCulture) : null),
        };
        var query = string.Join("&", values.Where(v => !string.IsNullOrWhiteSpace(v.Value)).Select(v => $"{v.Key}={Uri.EscapeDataString(v.Value!.Trim())}"));
        return query.Length == 0 ? path : $"{path}?{query}";
    }

    /// <summary>A column header's sort link: ascending first, descending when it is already ascending (rating: best first).</summary>
    public string SortUrl(string key)
    {
        var current = Sort ?? "name";
        var next = key == "rating"
            ? (current == "-rating" ? "rating" : "-rating")
            : (current == key ? "-" + key : key);
        return ListUrl(sort: next == "name" ? null : next);
    }

    public string? AriaSort(string key)
    {
        var current = Sort ?? "name";
        return current == key ? "ascending" : current == "-" + key ? "descending" : null;
    }

    /// <summary>The full map, on the selected type's plain specialty and state.</summary>
    public string MapUrl
    {
        get
        {
            var parts = new List<string>();
            if (SelectedType is { Category: { } category } t)
            {
                parts.Add("category=" + Uri.EscapeDataString(category));
                if (t.Subcategory is { } sub)
                {
                    parts.Add("subcategory=" + Uri.EscapeDataString(sub));
                }
            }

            if (State is { Length: 2 } st)
            {
                parts.Add("state=" + Uri.EscapeDataString(st.ToUpperInvariant()));
            }

            return parts.Count == 0 ? "/map" : "/map?" + string.Join("&", parts);
        }
    }

    /// <summary>"4.5 ★" or "—".</summary>
    public static string Stars(double? rating) => rating is { } r ? r.ToString("0.#", CultureInfo.InvariantCulture) + " ★" : "—";

    public static string Name(string name) => CityCatalog.TitleCase(name);
}
