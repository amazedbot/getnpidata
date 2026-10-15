using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Npi.Core.Search;
using Npi.Web.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /area, /area/{state}, /area/{state}/{county FIPS} (docs/PRODUCT_PLAN.md step 1.6): how many providers an area has, per
/// 100,000 people against the state and the US, by specialty, with sub-areas, shortage areas, cities, hospitals and recorded
/// changes; every number links to the list behind it. <c>?category=&amp;subcategory=</c> narrows it to a plain specialty.
/// At the top (step 3.3): a map of the states or counties coloured by a statistic, and the area's figures month by month.
/// </summary>
public class AreaModel(AreaProfileService areas, AreaHistoryService history, IOptions<MapOptions> mapOptions) : PageModel
{
    public AreaProfile Area { get; private set; } = null!;

    /// <summary>The area's monthly figures for the chosen category (step 3.3); null before the loader first built them.</summary>
    public AreaHistory? History { get; private set; }

    public StaticMap? Map { get; private set; }

    public string Attribution => mapOptions.Value.Attribution;

    [FromQuery(Name = "category")]
    public string? Category { get; set; }

    [FromQuery(Name = "subcategory")]
    public string? Subcategory { get; set; }

    public async Task<IActionResult> OnGetAsync(string? state, string? county, CancellationToken ct)
    {
        if (await areas.GetAsync(state, county, Category, Subcategory, ct) is not { } area)
        {
            return NotFound();
        }

        Area = area;
        History = await history.GetAsync(area.Type == "us" ? null : area.State, area.Type == "county" ? area.Id : null, area.Selected.Category, ct);

        Map = mapOptions.Value.Enabled && area.Cities.Count > 0
            ? StaticMap.Create(area.Cities.Select((c, i) => (Label: (i + 1).ToString(CultureInfo.InvariantCulture), c.Latitude, c.Longitude))
                .Where(c => c.Latitude is not null && c.Longitude is not null).Select(c => (c.Label, c.Latitude!.Value, c.Longitude!.Value, false)),
                mapOptions.Value.TileUrl)
            : null;
        return Page();
    }

    public SpecialtyCategory? SelectedCategory => SpecialtyCategories.Default.Find(Area.Selected.Category);

    public SpecialtySubcategory? SelectedSubcategory => SelectedCategory?.Find(Area.Selected.Subcategory);

    /// <summary>"Chiropractors", "Doctors", or "Providers".</summary>
    public string Who => SelectedSubcategory?.Title ?? SelectedCategory?.Title ?? "Providers";

    /// <summary>"Suffolk County, NY", "New York", "the United States".</summary>
    public string Place => Area.Type switch { "county" => $"{Area.Name}, {Area.State}", "us" => "the United States", _ => Area.Name };

    public string Title => $"{Who} in {Place}";

    /// <summary>The search filter for this area and specialty: the list behind every number.</summary>
    public SearchFilter Filter => new()
    {
        State = Area.Type == "us" ? null : Area.State,
        CountyFips = Area.Type == "county" ? Area.Id : null,
        Category = Area.Selected.Category.Length == 0 ? null : Area.Selected.Category,
        Subcategory = Area.Selected.Subcategory.Length == 0 ? null : Area.Selected.Subcategory,
    };

    public string ListLink(SearchFilter? filter = null) => "/search" + SearchQueryString.ToQueryString(filter ?? Filter, includePaging: false);

    public string ExportLink => "/export.csv" + SearchQueryString.ToQueryString(Filter, includePaging: false);

    public string MapLink => "/map" + SearchQueryString.ToQueryString(Filter, includePaging: false);

    /// <summary>This page for another area or specialty (keeping the other one).</summary>
    public string AreaLink(string? state, string? county, string? category, string? subcategory)
    {
        var path = state is null ? "/area" : county is null ? $"/area/{state}" : $"/area/{state}/{county}";
        var query = new List<string>();
        if (!string.IsNullOrEmpty(category))
        {
            query.Add("category=" + Uri.EscapeDataString(category));
        }

        if (!string.IsNullOrEmpty(subcategory))
        {
            query.Add("subcategory=" + Uri.EscapeDataString(subcategory));
        }

        return query.Count == 0 ? path : path + "?" + string.Join("&", query);
    }

    public string SpecialtyLink(AreaCount c) =>
        AreaLink(Area.Type == "us" ? null : Area.State, Area.Type == "county" ? Area.Id : null, c.Category, c.Subcategory);

    public string ChildLink(AreaChild child) => Area.Type == "us"
        ? AreaLink(child.Id, null, Area.Selected.Category, Area.Selected.Subcategory)
        : AreaLink(Area.State, child.Id, Area.Selected.Category, Area.Selected.Subcategory);

    /// <summary>"961 chiropractors serve 1,525,465 people: 63 per 100,000, more than New York overall (52)."</summary>
    public string Summary
    {
        get
        {
            var count = Area.Selected.Providers.ToString("N0", CultureInfo.InvariantCulture);
            var who = Who.ToLowerInvariant();
            if (Area.Population is not { } people || Area.Selected.Per100k is not { } rate)
            {
                return $"{count} {who} practise here.";
            }

            var text = $"{count} {who} serve {people.ToString("N0", CultureInfo.InvariantCulture)} people: {Rate(rate)} per 100,000";
            if (Area.ParentPer100k is { } parent && Area.ParentName is { } parentName)
            {
                text += $", {Compare(rate, parent)} {parentName}{(Area.Type == "county" ? " overall" : "")} ({Rate(parent)})";
            }

            return text + ".";
        }
    }

    /// <summary>"more than", "fewer than", "about the same as".</summary>
    public static string Compare(double value, double reference) =>
        reference <= 0 ? "compared with" : value / reference >= 1.15 ? "more than" : value / reference <= 0.87 ? "fewer than" : "about the same as";

    /// <summary>"1.3× the state", "about the same".</summary>
    public static string Ratio(double? value, double? reference) =>
        value is not { } v || reference is not > 0 ? "—"
        : Math.Abs(v / reference.Value - 1) < 0.13 ? "about the same"
        : (v / reference.Value).ToString("0.0", CultureInfo.InvariantCulture) + "×";

    public static string Rate(double? value) => value is not { } v ? "—" : v >= 10 ? v.ToString("N0", CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>The title of a specialty row: "Chiropractors", or the category's title.</summary>
    public static string SpecialtyTitle(AreaCount c)
    {
        var category = SpecialtyCategories.Default.Find(c.Category);
        return category?.Find(c.Subcategory)?.Title ?? category?.Title ?? c.Category;
    }

    public double? ParentRate(AreaCount c) =>
        Area.ParentSpecialties.FirstOrDefault(p => p.Category == c.Category && p.Subcategory == c.Subcategory)?.Per100k;

    /// <summary>The list behind a monthly count: /area[/{state}[/{county}]]/changes?kind=&amp;month= (new NPIs keep the category).</summary>
    public string ChangesLink(string kind, DateOnly month)
    {
        var path = Area.Type switch { "county" => $"/area/{Area.State}/{Area.Id}/changes", "state" => $"/area/{Area.State}/changes", _ => "/area/changes" };
        var link = $"{path}?kind={kind}&month={AreaHistoryMath.MonthKey(month)}";
        return kind == AreaChangeKinds.New && Area.Selected.Category.Length > 0 ? link + "&category=" + Uri.EscapeDataString(Area.Selected.Category) : link;
    }

    /// <summary>
    /// The month the tiles show: the latest complete month with the recorded counts (moves; deactivations for the US), else the
    /// as-of month so far.
    /// </summary>
    public AreaMonth? TileMonth => History?.Months is { Count: > 0 } months
        ? months.LastOrDefault(m => !m.Partial && (Area.Type == "us" ? m.Deactivated : m.MovedIn) is not null) ?? months[^1]
        : null;

    /// <summary>"In September 2026 in Suffolk County, NY: 87 new NPIs, 3 deactivated, 12 moved in and 9 moved out."</summary>
    public string HistorySentence
    {
        get
        {
            if (TileMonth is not { } m)
            {
                return "";
            }

            static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
            var parts = new List<string>();
            if (m.NewProviders is { } issued)
            {
                parts.Add($"{N(issued)} new NPI{(issued == 1 ? "" : "s")}{(Area.Selected.Category.Length > 0 ? $" ({Who.ToLowerInvariant()})" : "")}");
            }

            if (m.Deactivated is { } deactivated)
            {
                parts.Add($"{N(deactivated)} deactivated");
            }

            if (m.MovedIn is { } movedIn)
            {
                parts.Add($"{N(movedIn)} moved in");
            }

            if (m.MovedOut is { } movedOut)
            {
                parts.Add($"{N(movedOut)} moved out");
            }

            var list = parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts[..^1]) + " and " + parts[^1];
            return parts.Count == 0 ? "" : $"In {AreaHistoryMath.MonthName(m.Month)}{(m.Partial ? " (so far)" : "")} in {Place}: {list}.";
        }
    }

    /// <summary>The series the chart offers (with the property the script reads), in order; moves only below the US.</summary>
    public IEnumerable<(string Key, string Label)> ChartSeries =>
        new[]
        {
            ("newProviders", "New NPIs"), ("deactivated", "Deactivated"), ("movedIn", "Moved in"), ("movedOut", "Moved out"),
            ("netChange", "Net change"), ("providers", "Providers"), ("quality", "Record quality"),
        }.Where(s => Area.Type != "us" || s.Item1 is not ("movedIn" or "movedOut"));

    /// <summary>
    /// The archive's months without a file (no provider count for the area), as "December 2007, January 2008 – April 2008, …";
    /// empty when none or no archive.
    /// </summary>
    public string ArchiveGaps
    {
        get
        {
            if (History is not { Coverage: { ArchiveFrom: { } from, ArchiveTo: { } to } } h)
            {
                return "";
            }

            var counted = h.Months.Where(m => m.Providers is not null).Select(m => m.Month).ToHashSet();
            var runs = new List<(DateOnly First, DateOnly Last)>();
            for (var m = from; m <= to; m = m.AddMonths(1))
            {
                if (counted.Contains(m))
                {
                    continue;
                }

                if (runs.Count > 0 && runs[^1].Last.AddMonths(1) == m)
                {
                    runs[^1] = (runs[^1].First, m);
                }
                else
                {
                    runs.Add((m, m));
                }
            }

            return string.Join(", ", runs.Select(r => r.First == r.Last
                ? AreaHistoryMath.MonthName(r.First)
                : $"{AreaHistoryMath.MonthName(r.First)} – {AreaHistoryMath.MonthName(r.Last)}"));
        }
    }

    /// <summary>"since October 2026"; "" when the series hasn't started.</summary>
    public static string Since(DateOnly? month) => month is { } m ? "since " + AreaHistoryMath.MonthName(m) : "not yet";

    /// <summary>The map's view: the US page draws states (or every county), a state or county page its state's counties.</summary>
    public string MapLevel => Area.Type == "us" ? "state" : "county";

    /// <summary>"1,234", "—" for unknown, with a sign for a change ("+12", "−3").</summary>
    public static string Count(int? value, bool withSign = false) =>
        value is not { } v ? "—" : withSign && v > 0 ? "+" + v.ToString("N0", CultureInfo.InvariantCulture)
        : withSign && v < 0 ? "−" + (-v).ToString("N0", CultureInfo.InvariantCulture) : v.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"62% current · 18% check · 20% stale", or "—".</summary>
    public static string QualityMix(AreaMonth m)
    {
        if (AreaHistoryMath.QualityShare(m.QualityCurrent, m.QualityCurrent, m.QualityCheck, m.QualityStale) is not { } current)
        {
            return "—";
        }

        static string P(double? v) => (v ?? 0).ToString("0", CultureInfo.InvariantCulture) + "%";
        return $"{P(current)} current · {P(AreaHistoryMath.QualityShare(m.QualityCheck, m.QualityCurrent, m.QualityCheck, m.QualityStale))} check · "
               + $"{P(AreaHistoryMath.QualityShare(m.QualityStale, m.QualityCurrent, m.QualityCheck, m.QualityStale))} stale";
    }

    /// <summary>The monthly figures for the chart script (camelCase JSON, months as yyyy-MM).</summary>
    public object ChartData => new
    {
        type = Area.Type,
        category = Area.Selected.Category,
        months = (History?.Months ?? []).Select(m => new
        {
            month = AreaHistoryMath.MonthKey(m.Month), m.Partial, m.Basis, m.Providers, m.NewProviders, m.Deactivated, m.MovedIn, m.MovedOut, m.NetChange,
            m.QualityCurrent, m.QualityCheck, m.QualityStale,
        }),
        derivedFrom = History?.Coverage.DerivedFrom is { } d ? AreaHistoryMath.MonthKey(d) : null,
        archiveFrom = History?.Coverage.ArchiveFrom is { } af ? AreaHistoryMath.MonthKey(af) : null,
        archiveTo = History?.Coverage.ArchiveTo is { } at ? AreaHistoryMath.MonthKey(at) : null,
        anyLocationFrom = History?.Coverage.DefinitionStep() is { } step ? AreaHistoryMath.MonthKey(step) : null,
        trackedFrom = History?.Coverage.TrackedFrom is { } t ? AreaHistoryMath.MonthKey(t) : null,
    };
}
