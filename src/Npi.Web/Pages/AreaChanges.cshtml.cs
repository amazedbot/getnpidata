using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /area[/{state}[/{county}]]/changes?kind=&amp;month= (docs/PRODUCT_PLAN.md step 3.3): the providers behind one of an area's
/// monthly counts (new NPIs, deactivated, moved in or out, specialty, license and exclusion changes), 50 a page, and every one
/// as CSV (<c>?handler=csv</c>). Unknown kinds, months and areas are 404s, before any database work.
/// </summary>
public class AreaChangesModel(AreaHistoryService history, SearchService search) : PageModel
{
    [FromQuery(Name = "kind")]
    public string? Kind { get; set; }

    [FromQuery(Name = "month")]
    public string? MonthText { get; set; }

    [FromQuery(Name = "category")]
    public string? Category { get; set; }

    [FromQuery(Name = "p")]
    public int PageNumber { get; set; } = 1;

    public AreaHistory Area { get; private set; } = null!;

    public AreaChangePage Changes { get; private set; } = null!;

    public DateOnly Month { get; private set; }

    /// <summary>The page's active providers by NPI (summaries for the rows); deactivated NPIs have none.</summary>
    public IReadOnlyDictionary<string, ProviderSummary> Providers { get; private set; } = new Dictionary<string, ProviderSummary>();

    // The request, checked without the database: a known kind and month, a known category, a well-formed area.
    private bool Valid(string? state, string? county) =>
        AreaChangeKinds.IsKnown(Kind) && AreaHistoryMath.ParseMonth(MonthText) is not null
        && (string.IsNullOrWhiteSpace(Category) || SpecialtyCategories.Default.Find(Category) is not null)
        && AreaHistoryService.Area(state, county) is (string type, _) && AreaHistoryService.HasKind(type, Kind!);

    public async Task<IActionResult> OnGetAsync(string? state, string? county, CancellationToken ct)
    {
        if (!Valid(state, county) || await history.GetAsync(state, county, null, ct) is not { } area)
        {
            return NotFound();
        }

        (Area, Month) = (area, AreaHistoryMath.ParseMonth(MonthText)!.Value);
        Changes = (await history.GetChangesAsync(state, county, Kind!, Month, Category, PageNumber, ct))!;
        if (Kind != AreaChangeKinds.Deactivated && Changes.Entries.Count > 0)
        {
            Providers = (await search.LookupAsync(Changes.Entries.Select(e => e.Npi).ToList(), ct))
                .Where(r => r.Provider is not null).ToDictionary(r => r.Npi, r => r.Provider!);
        }

        return Page();
    }

    /// <summary>Every provider behind the count as CSV: the summary columns, or for deactivations the lookup columns (with the date).</summary>
    public async Task<IActionResult> OnGetCsvAsync(string? state, string? county, CancellationToken ct)
    {
        if (!Valid(state, county) || await history.GetAsync(state, county, null, ct) is not { } area)
        {
            return NotFound();
        }

        var month = AreaHistoryMath.ParseMonth(MonthText)!.Value;
        var npis = await history.GetChangeNpisAsync(state, county, Kind!, month, Category, ct);
        var name = $"{(area.Type == "us" ? "us" : area.Type == "state" ? area.Id.ToLowerInvariant() : area.Id)}_{Kind}_{AreaHistoryMath.MonthKey(month)}.csv";
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";

        async IAsyncEnumerable<LookupRow> Rows()
        {
            foreach (var batch in npis.Chunk(SearchService.MaxLookupBatch))
            {
                foreach (var row in await search.LookupAsync(batch, ct))
                {
                    yield return row;
                }
            }
        }

        if (Kind == AreaChangeKinds.Deactivated)
        {
            await ProviderCsv.WriteLookupAsync(Rows(), Response.Body, ct);
        }
        else
        {
            await ProviderCsv.WriteAsync(Rows().Where(r => r.Provider is not null).Select(r => r.Provider!), Response.Body, ct);
        }

        return new EmptyResult();
    }

    /// <summary>"Suffolk County, NY", "New York", "the United States".</summary>
    public string Place => Area.Type switch { "county" => $"{Area.Name}, {Area.State}", "us" => "the United States", _ => Area.Name };

    public string? CategoryName => Kind == AreaChangeKinds.New ? SpecialtyCategories.Default.Find(Category)?.Name : null;

    /// <summary>"Moved into Suffolk County, NY in October 2026".</summary>
    public string Title
    {
        get
        {
            var when = AreaHistoryMath.MonthName(Month);
            var who = CategoryName is { } c ? SpecialtyCategories.Default.Find(c)!.Title.ToLowerInvariant() : null;
            return Kind switch
            {
                AreaChangeKinds.New => $"New NPIs{(who is null ? "" : $" ({who})")} in {Place}, {when}",
                AreaChangeKinds.Deactivated => $"NPIs deactivated in {Place}, {when}",
                AreaChangeKinds.MovedIn => $"Moved into {Place} in {when}",
                AreaChangeKinds.MovedOut => $"Moved out of {Place} in {when}",
                AreaChangeKinds.Specialty => $"Specialty changes in {Place}, {when}",
                AreaChangeKinds.License => $"License changes in {Place}, {when}",
                _ => $"Exclusions and opt-outs in {Place}, {when}",
            };
        }
    }

    /// <summary>What the list counts, in a sentence.</summary>
    public string Description => Kind switch
    {
        AreaChangeKinds.New => "Providers whose NPI was issued in the month and who practise here now, by NPI.",
        AreaChangeKinds.Deactivated when Area.Type == "us" => "NPIs CMS deactivated in the month (by its deactivation date), with the last name on file.",
        AreaChangeKinds.Deactivated => "Providers who practised here when their NPI was deactivated, with the last name on file and the date.",
        AreaChangeKinds.MovedIn => "Providers who added a practice location here in the month and had none here before. Where they practise now:",
        AreaChangeKinds.MovedOut => "Providers who dropped their last practice location here in the month. Where they practise now:",
        AreaChangeKinds.Specialty => "Providers practising here whose primary specialty changed or who added or dropped a specialty (NUCC taxonomy).",
        AreaChangeKinds.License => "Providers practising here whose state license was listed, dropped, renewed or changed status, or with a new board action (states that publish them).",
        _ => "Providers practising here who were added to or left the OIG exclusion list, or opted out of Medicare, renewed or ended an opt-out.",
    };

    /// <summary>This list's area page.</summary>
    public string AreaLink => Area.Type switch { "county" => $"/area/{Area.State}/{Area.Id}", "state" => $"/area/{Area.State}", _ => "/area" }
        + (CategoryName is { } c ? "?category=" + Uri.EscapeDataString(c) : "");

    /// <summary>This list (another page, or the CSV).</summary>
    public string Link(int? page = null, bool csv = false)
    {
        var path = Area.Type switch { "county" => $"/area/{Area.State}/{Area.Id}/changes", "state" => $"/area/{Area.State}/changes", _ => "/area/changes" };
        var query = $"?kind={Kind}&month={AreaHistoryMath.MonthKey(Month)}";
        if (CategoryName is { } c)
        {
            query += "&category=" + Uri.EscapeDataString(c);
        }

        if (page is > 1)
        {
            query += "&p=" + page.Value.ToString(CultureInfo.InvariantCulture);
        }

        return path + query + (csv ? "&handler=csv" : "");
    }

    public int PageCount => Math.Max(1, (Changes.Total + Changes.PageSize - 1) / Changes.PageSize);
}
