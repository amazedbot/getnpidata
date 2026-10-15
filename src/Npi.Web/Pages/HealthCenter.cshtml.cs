using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /health-center/{id}: one site of an HRSA-funded health center or look-alike (docs/PRODUCT_PLAN.md step 3.6 part 2): where it is,
/// its hours and setting, its Medicare number and NPI where HRSA lists them, and the health center's other sites.
/// </summary>
public class HealthCenterModel(HealthCenterService sites, IOptions<MapOptions> mapOptions) : PageModel
{
    public HealthCenterSiteDetail Detail { get; private set; } = null!;

    public StaticMap? Map { get; private set; }

    public string Attribution => mapOptions.Value.Attribution;

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        if (!HealthCenterService.IsSiteId(id) || await sites.GetSiteAsync(id.ToUpperInvariant(), ct) is not { } detail)
        {
            return NotFound();
        }

        Detail = detail;
        Map = mapOptions.Value.Enabled && detail.Site.Latitude is { } lat && detail.Site.Longitude is { } lon
            ? StaticMap.Create([("1", lat, lon, false)], mapOptions.Value.TileUrl)
            : null;
        return Page();
    }

    public HealthCenterSite Site => Detail.Site;

    /// <summary>"Health center site (school) in Manning, SC, open 4 hours a week; part of HopeHealth Inc, with 38 other sites."</summary>
    public string Summary
    {
        get
        {
            var text = Site.LookAlike ? "Health center look-alike site" : "Federally funded health center site";
            if (Site.Setting is { } setting)
            {
                text += $" ({setting.ToLowerInvariant()})";
            }

            if (Site.City is { } city)
            {
                text += $" in {CityCatalog.TitleCase(city)}, {Site.State}";
            }

            if (Site.HoursPerWeek is { } hours)
            {
                text += $", open {hours.ToString("0.#", CultureInfo.InvariantCulture)} hours a week";
            }

            if (Site.HealthCenter is { } center)
            {
                text += $"; part of {CityCatalog.TitleCase(center)}";
                if (Detail.OtherSiteCount > 0)
                {
                    text += Detail.OtherSiteCount == 1 ? ", with 1 other site" : $", with {Format.Number(Detail.OtherSiteCount)} other sites";
                }
            }

            return text + ".";
        }
    }
}
