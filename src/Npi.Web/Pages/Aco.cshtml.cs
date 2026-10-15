using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /aco/{id}: one Medicare Shared Savings Program ACO (docs/PRODUCT_PLAN.md step 3.6 part 2): its track, service area, website,
/// participants and skilled nursing facility affiliates, by legal business name as CMS publishes them.
/// </summary>
public class AcoModel(AcoService acos, IOptions<MapOptions> mapOptions) : PageModel
{
    public AcoDetail Detail { get; private set; } = null!;

    public StaticMap? Map { get; private set; }

    public string Attribution => mapOptions.Value.Attribution;

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        if (!AcoService.IsAcoId(id) || await acos.GetAsync(id.ToUpperInvariant(), ct) is not { } detail)
        {
            return NotFound();
        }

        Detail = detail;
        Map = mapOptions.Value.Enabled && detail.Latitude is { } lat && detail.Longitude is { } lon
            ? StaticMap.Create([("1", lat, lon, false)], mapOptions.Value.TileUrl)
            : null;
        return Page();
    }

    public AcoSummary Aco => Detail.Aco;

    /// <summary>"Medicare ACO serving Arizona and Nevada with 42 participants, in the program since 2016."</summary>
    public string Summary
    {
        get
        {
            var text = $"Medicare Shared Savings Program ACO ({Aco.ProgramYear})";
            if (!string.IsNullOrWhiteSpace(Aco.ServiceArea))
            {
                text += " serving " + NetworkFormat.States(Aco.ServiceArea);
            }

            text += Aco.Participants == 1 ? " with 1 participant" : $" with {Format.Number(Aco.Participants)} participants";
            if (Aco.InitialStartDate is { } start)
            {
                text += $", in the program since {start.Year}";
            }

            return text + ".";
        }
    }

    /// <summary>A search of the participant's name among organization NPIs (a search, not a match).</summary>
    public static string SearchUrl(string name) => "/organizations?q=" + Uri.EscapeDataString(name);
}
