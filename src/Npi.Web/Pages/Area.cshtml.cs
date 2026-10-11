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
/// </summary>
public class AreaModel(AreaProfileService areas, IOptions<MapOptions> mapOptions) : PageModel
{
    public AreaProfile Area { get; private set; } = null!;

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
}
