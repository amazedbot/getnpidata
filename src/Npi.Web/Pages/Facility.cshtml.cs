using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /facility/{ccn}: a Medicare-certified hospital, nursing home, home health agency, hospice, dialysis facility, surgery center, inpatient
/// rehabilitation facility or long-term care hospital (docs/PRODUCT_PLAN.md steps 1.5 and 3.6): how CMS rates it, how it compares with
/// the nation, a hospital's Medicare volume, the clinicians affiliated with it, where it is and the NPIs it bills under. Since step 3.9
/// it is the detail page of one CCN: when its NPIs have an organization page, a banner links to everything on one page.
/// </summary>
public class FacilityModel(OrganizationService organizations, OwnershipService ownership, IOptions<MapOptions> mapOptions,
    OrganizationGroupService? groups = null) : PageModel
{
    /// <summary>The organization page (step 3.9) its billing NPIs are part of, for the banner; null when they have none.</summary>
    public ProviderOrganization? Organization { get; private set; }

    /// <summary>The facility shown (set by <see cref="OnGetAsync"/>; tests set it directly).</summary>
    public FacilityProfile Profile { get; set; } = null!;

    public StaticMap? Map { get; private set; }

    /// <summary>Who owns and runs it and its ownership changes (CMS All Owners and Change of Ownership files); null when CMS lists neither.</summary>
    public FacilityOwnership? Ownership { get; private set; }

    /// <summary>A nursing home's inspections and penalties (Care Compare); null for other facilities or when none are listed.</summary>
    public NursingHomeInspections? Inspections { get; private set; }

    public string Attribution => mapOptions.Value.Attribution;

    public async Task<IActionResult> OnGetAsync(string ccn, [FromQuery] int page, CancellationToken ct)
    {
        if (await organizations.GetFacilityAsync(ccn, Math.Max(page, 1), ct) is not { } profile)
        {
            return NotFound();
        }

        Profile = profile;
        Ownership = await ownership.GetFacilityOwnershipAsync(profile.Facility.Ccn, ct);
        Inspections = IsNursingHome(profile.Facility) ? await ownership.GetInspectionsAsync(profile.Facility.Ccn, ct) : null;
        if (groups is not null)
        {
            Organization = (await groups.GetPagesForCcnsAsync([profile.Facility.Ccn], ct)).GetValueOrDefault(profile.Facility.Ccn);
        }

        Map = mapOptions.Value.Enabled && profile.Latitude is { } lat && profile.Longitude is { } lon
            ? StaticMap.Create([("1", lat, lon, false)], mapOptions.Value.TileUrl)
            : null;
        return Page();
    }

    public CertifiedFacility Facility => Profile.Facility;

    /// <summary>The ratings, measures and volume in words (shared with the organization page).</summary>
    public FacilityView View => new(Profile);

    /// <summary>A nursing home by its Care Compare listing or its CMS number (also a Medicaid-only nursing facility).</summary>
    public static bool IsNursingHome(CertifiedFacility facility) => FacilityTypes.IsNursingHome(facility);

    public string Name => CityCatalog.TitleCase(Facility.Name);

    /// <summary>"Acute care hospital", "Nursing home", "Home health agency", "Hospice", "Rural health clinic", "Dialysis facility" ….</summary>
    public string KindName => View.KindName;

    /// <summary>"Acute care hospital in West Islip, NY with an emergency department; 812 clinicians list an affiliation with it."</summary>
    public string Summary
    {
        get
        {
            var text = KindName;
            if (Facility.City is { } city)
            {
                text += $" in {CityCatalog.TitleCase(city)}, {Facility.State}";
            }

            if (Facility.EmergencyServices == true)
            {
                text += " with an emergency department";
            }

            if (Facility.CertifiedBeds is { } beds)
            {
                text += $", {beds.ToString("N0", CultureInfo.InvariantCulture)} certified beds";
            }

            if (Profile.Dialysis?.Stations is { } stations)
            {
                text += $", {stations.ToString("N0", CultureInfo.InvariantCulture)} dialysis stations";
            }

            var clinicians = Profile.Clinicians.Total;
            if (clinicians > 0)
            {
                text += clinicians == 1 ? "; 1 clinician lists an affiliation with it" : $"; {clinicians.ToString("N0", CultureInfo.InvariantCulture)} clinicians list an affiliation with it";
            }

            if (Profile.Certification is { Active: false } ended)
            {
                text += ". Its Medicare certification ended" + (ended.TerminationDate is { } d ? " on " + d.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture) : "");
            }

            return text + ".";
        }
    }

    public IEnumerable<RatingTile> Ratings => View.Ratings;

    /// <summary>"How it compares", by group in the page's order.</summary>
    public IEnumerable<IGrouping<string, MeasureRow>> MeasureGroups => View.MeasureGroups;

    /// <summary>Where "How it compares" comes from, in the type's own words.</summary>
    public string MeasureSource => View.MeasureSource;

    /// <summary>The ESRD QIP tile's averages: "State average 54, US 54".</summary>
    public string QipAverages => View.QipAverages;

    /// <summary>The Medicare volume section's source line, with its data years.</summary>
    public string VolumeSource => View.VolumeSource;

    /// <summary>"Jul 2022 – Jun 2025".</summary>
    public static string Period(DateOnly from, DateOnly to) => FacilityView.Period(from, to);

    /// <summary>"2021–2024", or "2024".</summary>
    public static string Years(DateOnly from, DateOnly to) => FacilityView.Years(from, to);

    public ClinicianListView Clinicians => new("Clinicians who work here", Profile.Clinicians, $"/facility/{Facility.Ccn}",
        $"/facility/{Facility.Ccn}/clinicians.csv", "Clinicians who list this facility in Medicare Care Compare (Facility Affiliation Data).");
}
