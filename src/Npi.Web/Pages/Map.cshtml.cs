using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Npi.Core.Search;
using Npi.Web.Search;

namespace Npi.Web.Pages;

/// <summary>
/// Map search (CLAUDE.md §7 Stage 5.5 item 10): providers as pins at their street addresses, searched by the
/// visible map area ("Search in this area") with the same filters as the search page, and a table of the
/// pins in view. The page itself only renders the form and the map; map.js calls /map/search.
/// </summary>
public class MapModel(TaxonomyCatalog taxonomy, MapService maps, CredentialCatalog credentials, IOptions<MapOptions> mapOptions) : PageModel
{
    public MapOptions Map => mapOptions.Value;

    public FilterFields Fields { get; private set; } = new(new SearchFilter(), [], [], new Dictionary<string, string[]>());

    /// <summary>The area to open on: the bbox in the URL, else the area of the search's location filters; null = the whole country.</summary>
    public MapBounds? Start { get; private set; }

    /// <summary>Search the start area as soon as the page opens (it came from a search or a shared link, and isn't too large).</summary>
    public bool AutoSearch { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var (filter, errors) = SearchQueryString.Parse(Request.Query);
        var classifications = await taxonomy.GetClassificationsAsync(ct);
        var specializations = filter.Classification is null ? [] : await taxonomy.GetSpecializationsAsync(filter.Classification, ct);
        Fields = new FilterFields(filter, classifications, specializations, errors)
        {
            Credentials = await credentials.GetAllAsync(ct),
            SelectedCredential = filter.Credential is null ? null : await credentials.ResolveAsync(filter.Credential, ct),
        };

        Start = MapBounds.Parse(Request.Query["bbox"]) is { Problem: null } shared ? shared
            : errors.Count == 0 ? await maps.GetStartAreaAsync(filter, ct)
            : null;
        AutoSearch = Start is { Problem: null };
    }

    public string StartText => Start is null ? "" : string.Join(",", new[] { Start.West, Start.South, Start.East, Start.North }
        .Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
}
