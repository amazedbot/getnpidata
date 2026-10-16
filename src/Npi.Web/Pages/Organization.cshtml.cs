using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Npi.Core;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /organization/{npi}: one page per organization (docs/PRODUCT_PLAN.md step 3.9, owner 2026-10-16), the full profile of an
/// organization NPI with a certified facility, a Medicare group or other NPIs of its organization (step 3.8): an overview, each
/// facility's quality, Medicare volume and activity, who owns it (each owner once), one clinician list from every source, every NPI,
/// the locations, specialties and licenses, the history and the registration of the main NPI. One scrolling page with a section menu;
/// it works without JavaScript and prints as one document. Any member's NPI opens it: the main NPI's address is canonical, the others
/// redirect there (301); an NPI without an organization page goes to its provider page.
/// </summary>
public class OrganizationModel(OrganizationGroupService groups, OrganizationPageService pages, IOptions<MapOptions> mapOptions) : PageModel
{
    /// <summary>Rows of a long list shown before the rest is folded (NPIs, locations, changes).</summary>
    public const int Shown = 25;

    /// <summary>The profile shown (set by <see cref="OnGetAsync"/>; tests set it directly).</summary>
    public OrganizationProfile Profile { get; set; } = null!;

    /// <summary>One page of the clinicians of all its facilities and groups together, with where each tie comes from.</summary>
    public OrganizationClinicianList Clinicians { get; set; } =
        new(new OrganizationClinicianSummary(0, 0, 0, 0, 0, []), new(0, [], [], 1, OrganizationClinicians.PageSize), OrganizationClinicians.DefaultSort);

    public StaticMap? Map { get; private set; }

    public string Attribution => mapOptions.Value.Attribution;

    public async Task<IActionResult> OnGetAsync(string npi, [FromQuery] int page, [FromQuery] string? sort, CancellationToken ct)
    {
        if (!InputFormats.IsNpi(npi))
        {
            return NotFound();
        }

        var main = await groups.FindPageNpiAsync(npi, ct);
        if (main is null)
        {
            // No facility, group or other NPIs: its provider page is the whole story (it answers 404 for an unknown NPI).
            return Redirect($"/provider/{npi}");
        }

        if (main != npi)
        {
            return RedirectPermanent($"/organization/{main}{Request.QueryString}");
        }

        if (await pages.GetAsync(main, ct) is not { } profile)
        {
            return NotFound();
        }

        Profile = profile;
        Clinicians = await pages.GetCliniciansAsync(profile, Math.Max(page, 1), sort, ct);
        var points = profile.Locations.Where(l => l.Latitude is not null && l.Longitude is not null).Take(30).ToList();
        Map = mapOptions.Value.Enabled && points.Count > 0
            ? StaticMap.Create(points.Select((l, i) => ((i + 1).ToString(CultureInfo.InvariantCulture), l.Latitude!.Value, l.Longitude!.Value, l.Approximate)),
                mapOptions.Value.TileUrl)
            : null;
        return Page();
    }

    public string Name => CityCatalog.TitleCase(Profile.Name);

    public OrganizationMember MainMember => Profile.Npis.FirstOrDefault(m => m.IsMain) ?? Profile.Npis[0];

    /// <summary>The main facility (the main NPI's CCN, else the first), whose kind, phone and ratings the overview shows.</summary>
    public FacilityView? MainFacility => Profile.FacilityProfiles.Count > 0 ? new FacilityView(Profile.FacilityProfiles[0]) : null;

    /// <summary>"Hospital", "Nursing home", "Clinic": the main facility's kind in words, else the main NPI's plain specialty.</summary>
    public string KindName => MainFacility?.KindName ?? MainMember.Specialty ?? "Health care organization";

    /// <summary>The main NPI's primary practice location.</summary>
    public ProviderLocation? PrimaryLocation => Profile.Main?.Locations.FirstOrDefault(l => l.IsPrimary) ?? (Profile.Locations.Count > 0 ? Profile.Locations[0] : null);

    public string? Phone => MainFacility?.Facility.Phone ?? PrimaryLocation?.Phone;

    /// <summary>"Hospital in Jamaica, NY with an emergency department; 12 NPIs, 4 certified facilities, 1 Medicare group and 872 clinicians."</summary>
    public string Summary
    {
        get
        {
            var text = KindName;
            if (PrimaryLocation?.City is { } city)
            {
                text += $" in {CityCatalog.TitleCase(city)}{(PrimaryLocation.State is null ? "" : ", " + PrimaryLocation.State)}";
            }

            if (MainFacility?.Facility.EmergencyServices == true)
            {
                text += " with an emergency department";
            }

            var parts = new List<string> { Count(Profile.Npis.Count, "NPI", "NPIs") };
            if (Profile.Ccns.Count > 0)
            {
                parts.Add(Count(Profile.Ccns.Count, "certified facility", "certified facilities"));
            }

            if (Profile.Groups.Count > 0)
            {
                parts.Add(Count(Profile.Groups.Count, "Medicare group", "Medicare groups"));
            }

            if (Clinicians.Summary.Total > 0)
            {
                parts.Add(Count(Clinicians.Summary.Total, "clinician", "clinicians"));
            }

            return $"{text}; {Join(parts)}.";
        }
    }

    /// <summary>The facilities whose cards are on the page.</summary>
    public IReadOnlySet<string> Anchored => Profile.FacilityProfiles.Select(f => f.Facility.Ccn).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The CCNs without a card here (over <see cref="OrganizationPageService.MaxFacilityDetails"/>), each with its own page.</summary>
    public IReadOnlyList<string> MoreCcns => [.. Profile.Ccns.Where(c => !Anchored.Contains(c))];

    /// <summary>The facility type the ownership section names its CMS files by (the main facility's).</summary>
    public string OwnershipType => MainFacility is { } f ? (FacilityTypes.IsNursingHome(f.Facility) ? FacilityTypes.NursingHome : f.Facility.Kind) : FacilityTypes.Other;

    public FacilityOwnershipSection Ownership => new(null, OwnershipType) { Combined = Profile.Ownership, Anchored = Anchored };

    public ClinicianListView ClinicianView => new("Clinicians", Clinicians.List, $"/organization/{Profile.MainNpi}",
        $"/organization/{Profile.MainNpi}/clinicians.csv", ClinicianSources.Note)
    {
        Organization = Clinicians,
    };

    /// <summary>The facilities with Medicare volume (hospitals), for the Medicare volume section.</summary>
    public IReadOnlyList<FacilityView> WithVolume => [.. Profile.FacilityProfiles.Where(f => f.MedicareVolume is not null).Select(f => new FacilityView(f))];

    /// <summary>The NPIs by what tied them, for the note: "same name and address: 9; shared Medicare-certified facility (CCN): 4".</summary>
    public IEnumerable<(string Tie, int Npis)> TieCounts =>
        OrganizationGroups.TieTexts.Select(t => (t.Text, Profile.Npis.Count(m => m.JoinedBy.Contains(t.Text, StringComparison.Ordinal)))).Where(t => t.Item2 > 0);

    /// <summary>The page's sections that have content, for the section menu.</summary>
    public IEnumerable<(string Id, string Title)> Sections
    {
        get
        {
            if (Profile.FacilityProfiles.Count > 0)
            {
                yield return ("quality", "Quality");
            }

            if (WithVolume.Count > 0)
            {
                yield return ("volume", "Medicare volume");
            }

            if (Profile.MedicareActivity.Count > 0 || Profile.Main?.MedicareHistory is not null)
            {
                yield return ("medicare", "Medicare activity");
            }

            if (Profile.Ownership is not null || Ownership.OwnersPublished)
            {
                yield return ("owners", "Who owns it");
            }

            yield return ("clinicians", "Clinicians");
            yield return ("npis", Profile.Npis.Count == 1 ? "Its NPI" : "Its NPIs");
            yield return ("locations", "Locations");
            if (Profile.Main is not null)
            {
                yield return ("specialties", "Specialties & licenses");
            }

            if (Profile.Main?.History is { Versions.Count: > 0 } || Profile.Changes.Count > 0)
            {
                yield return ("history", "History");
            }

            if (Profile.Main is not null)
            {
                yield return ("record", "Registration");
            }
        }
    }

    /// <summary>Directions to the main location on OpenStreetMap.</summary>
    public string? Directions => PrimaryLocation is { } l ? ProviderModel.Directions(l) : null;

    /// <summary>The full map page around the locations, showing the main NPI.</summary>
    public string MapLink
    {
        get
        {
            var points = Profile.Locations.Where(l => l.Latitude is not null && l.Longitude is not null).ToList();
            if (points.Count == 0)
            {
                return "/map?npi=" + Profile.MainNpi;
            }

            static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
            const double pad = 0.02;
            return $"/map?npi={Profile.MainNpi}&bbox={F(points.Min(l => l.Longitude!.Value) - pad)},{F(points.Min(l => l.Latitude!.Value) - pad)},"
                   + $"{F(points.Max(l => l.Longitude!.Value) + pad)},{F(points.Max(l => l.Latitude!.Value) + pad)}";
        }
    }

    public static string QualityClass(string? label) => label switch
    {
        "Stale" => "badge warn",
        "Check" => "badge",
        "Current" => "badge good",
        _ => "badge",
    };

    /// <summary>"$1.2M"-style money for the Medicare table; "—" when CMS suppressed it or there is none.</summary>
    public static string Money(double? value) => value is { } v ? Format.ShortMoney(v) : "—";

    public static string Number(double? value) => value is { } v ? v.ToString("N0", CultureInfo.InvariantCulture) : "—";

    private static string Count(int n, string one, string many) => n == 1 ? "1 " + one : $"{n.ToString("N0", CultureInfo.InvariantCulture)} {many}";

    private static string Join(List<string> parts) =>
        parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
}
