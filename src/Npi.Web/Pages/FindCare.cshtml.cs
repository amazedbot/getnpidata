using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Npi.Core;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// Find care (docs/PRODUCT_PLAN.md §2.4, step 1.8): the patient path. Three plain questions (what kind of care, where,
/// optional preferences) and simple result cards nearest first, with the phone, the address, directions and a hospital
/// the clinician works with. A GET form, so results can be bookmarked and shared.
/// </summary>
public class FindCareModel(CareFinder finder, HealthCenterService healthCenters, IOptions<MapOptions> mapOptions) : PageModel
{
    public const int PerPage = 10;

    [BindProperty(SupportsGet = true)]
    public string? Need { get; set; }

    /// <summary>"Something else": a category, or "category|subcategory".</summary>
    [BindProperty(SupportsGet = true)]
    public string? Specialty { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Zip { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Medicare { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Video { get; set; }

    /// <summary>F or M; empty = either.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Gender { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public CareNeed? Chosen { get; private set; }

    public string? Error { get; private set; }

    public CareResults? Results { get; private set; }

    /// <summary>The cards of this page.</summary>
    public IReadOnlyList<CareResult> Shown { get; private set; } = [];

    public StaticMap? Map { get; private set; }

    /// <summary>HRSA health center sites near the ZIP (page 1 of the needs health centers serve; docs/PRODUCT_PLAN.md step 3.6 part 2).</summary>
    public IReadOnlyList<NearbyHealthCenter> HealthCenters { get; private set; } = [];

    /// <summary>How many health center sites are shown, nearest first.</summary>
    public const int HealthCentersShown = 3;

    public bool Asked => Need is not null || Specialty is not null || Zip is not null;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!Asked)
        {
            return Page();
        }

        Zip = Zip?.Trim();
        Chosen = CareFinder.Resolve(Need, Specialty);
        Gender = Gender?.ToUpperInvariant() is "F" or "M" ? Gender.ToUpperInvariant() : null;
        if (Chosen is null)
        {
            Error = "Choose the kind of care you're looking for.";
            return Page();
        }

        if (Zip is null || !InputFormats.IsZip5(Zip))
        {
            Error = "Enter a 5-digit ZIP code, or use your location.";
            return Page();
        }

        Results = await finder.FindAsync(new CareRequest(Chosen, Zip, Medicare, Video, Gender), ct);
        if (Results is null)
        {
            Error = $"We don't have a location for ZIP {Zip} (it may be a PO-box ZIP). Try the ZIP where you live.";
            return Page();
        }

        var pages = Math.Max(1, (Results.Items.Count + PerPage - 1) / PerPage);
        PageNumber = Math.Clamp(PageNumber, 1, pages);
        Shown = await finder.WithHospitalsAsync([.. Results.Items.Skip((PageNumber - 1) * PerPage).Take(PerPage)], ct);
        if (Chosen.HealthCenters && PageNumber == 1)
        {
            // As close as the providers found (at least 10 miles), then 25 miles when there is none that close.
            foreach (var radius in new[] { Math.Max(10, Results.RadiusMiles), 25 }.Distinct())
            {
                HealthCenters = await healthCenters.NearAsync(Results.CenterLat, Results.CenterLon, radius, HealthCentersShown, ct);
                if (HealthCenters.Count > 0)
                {
                    break;
                }
            }
        }

        Map = StaticMap.Create(
            Shown.Select((r, i) => (Label: Number(i), r.Nearest.Lat, r.Nearest.Lon, r.Nearest.Approximate)),
            mapOptions.Value.TileUrl);
        return Page();
    }

    /// <summary>The card number of the i-th card of this page (1-based across pages), shared by the cards and the map pins.</summary>
    public string Number(int i) => ((PageNumber - 1) * PerPage + i + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>"Use my location": the nearest ZIP to a point the browser gave (rounded by the page to about a kilometre).</summary>
    public async Task<IActionResult> OnGetNearestZipAsync(double lat, double lon, CancellationToken ct) =>
        lat is < -90 or > 90 || lon is < -180 or > 180 || !double.IsFinite(lat) || !double.IsFinite(lon)
            ? BadRequest()
            : new JsonResult(new { zip = await finder.NearestZipAsync(lat, lon, ct) });

    /// <summary>This search's URL with other values (null keeps the current one).</summary>
    public string Link(int? page = null)
    {
        var query = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                query.Add(key + "=" + Uri.EscapeDataString(value));
            }
        }

        Add("need", Need);
        Add("specialty", Need is null or "other" ? Specialty : null);
        Add("zip", Zip);
        Add("medicare", Medicare ? "true" : null);
        Add("video", Video ? "true" : null);
        Add("gender", Gender);
        Add("p", page is > 1 ? page.Value.ToString(CultureInfo.InvariantCulture) : null);
        return "/find-care?" + string.Join('&', query);
    }

    /// <summary>The same search on the map page: the specialties of the first kind, the preferences, around the ZIP.</summary>
    public string? MapLink()
    {
        if (Chosen is null || Results is null)
        {
            return null;
        }

        var (category, subcategory) = Chosen.Specialties[0];
        var query = new List<string>
        {
            "category=" + Uri.EscapeDataString(category),
            "zip=" + Zip,
            "radius=" + Results.RadiusMiles.ToString(CultureInfo.InvariantCulture),
        };
        if (subcategory is not null)
        {
            query.Add("subcategory=" + Uri.EscapeDataString(subcategory));
        }

        if (Chosen.EntityType is { } type)
        {
            query.Add("entityType=" + type.ToString(CultureInfo.InvariantCulture));
        }

        if (Medicare)
        {
            query.Add("acceptsAssignment=true");
        }

        if (Video)
        {
            query.Add("telehealth=true");
        }

        if (Gender is not null)
        {
            query.Add("gender=" + Gender);
        }

        return "/map?" + string.Join('&', query);
    }

    /// <summary>"Dana L Smith" from "SMITH, DANA L"; an organization keeps its name.</summary>
    public static string NaturalName(ProviderSummary p)
    {
        if (p.EntityType != 1)
        {
            return p.Name;
        }

        var parts = p.Name.Split(',', 2, StringSplitOptions.TrimEntries);
        return CityCatalog.TitleCase(parts.Length == 2 && parts[1].Length > 0 ? $"{parts[1]} {parts[0]}" : p.Name);
    }

    /// <summary>"0.8 miles", "1 mile", "12 miles".</summary>
    public static string Miles(double miles) =>
        miles < 0.95 ? miles.ToString("0.0", CultureInfo.InvariantCulture) + " miles"
        : miles < 1.5 ? "1 mile"
        : miles < 9.95 ? miles.ToString("0.#", CultureInfo.InvariantCulture) + " miles"
        : Math.Round(miles).ToString(CultureInfo.InvariantCulture) + " miles";

    /// <summary>OpenStreetMap directions to the point.</summary>
    public static string Directions(AreaProvider a) => Directions(a.Lat, a.Lon);

    /// <summary>OpenStreetMap directions to a point.</summary>
    public static string Directions(double lat, double lon) =>
        "https://www.openstreetmap.org/directions?route=%3B" + Uri.EscapeDataString(
            $"{lat.ToString("0.#####", CultureInfo.InvariantCulture)},{lon.ToString("0.#####", CultureInfo.InvariantCulture)}");
}
