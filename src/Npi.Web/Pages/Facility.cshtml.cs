using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>One rating tile: a label, stars (1–5, halves allowed) or a text value, and a note.</summary>
public sealed record RatingTile(string Label, double? Stars, string? Value, string Note, bool Bad = false);

/// <summary>
/// /facility/{ccn}: a Medicare-certified hospital, nursing home, home health agency or hospice (docs/PRODUCT_PLAN.md step 1.5):
/// how CMS rates it, the clinicians affiliated with it, where it is and the NPIs it bills under.
/// </summary>
public class FacilityModel(OrganizationService organizations, IOptions<MapOptions> mapOptions) : PageModel
{
    public FacilityProfile Profile { get; private set; } = null!;

    public StaticMap? Map { get; private set; }

    public string Attribution => mapOptions.Value.Attribution;

    public async Task<IActionResult> OnGetAsync(string ccn, [FromQuery] int page, CancellationToken ct)
    {
        if (await organizations.GetFacilityAsync(ccn, Math.Max(page, 1), ct) is not { } profile)
        {
            return NotFound();
        }

        Profile = profile;
        Map = mapOptions.Value.Enabled && profile.Latitude is { } lat && profile.Longitude is { } lon
            ? StaticMap.Create([("1", lat, lon, false)], mapOptions.Value.TileUrl)
            : null;
        return Page();
    }

    public CertifiedFacility Facility => Profile.Facility;

    public string Name => CityCatalog.TitleCase(Facility.Name);

    /// <summary>"Acute care hospital", "Nursing home", "Home health agency", "Hospice", "Rural health clinic", "Dialysis facility" ….</summary>
    public string KindName => Facility.Kind switch
    {
        "hospital" => Facility.Type is { } type ? (Profile.CertificationOnly ? type : Singular(type)) : "Hospital",
        "nursing_home" => "Nursing home",
        "home_health" => "Home health agency",
        "hospice" => "Hospice",
        // Only in the Provider of Services file: the kind its CMS number stands for, else its type.
        _ => Facility.Type ?? FacilityTypes.Get(Facility.Kind).Singular,
    };

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

    public IEnumerable<RatingTile> Ratings
    {
        get
        {
            var f = Facility;
            switch (f.Kind)
            {
                case "hospital":
                    yield return new RatingTile("Overall", f.OverallRating, f.OverallRating is null ? "Not rated" : null, "CMS overall hospital rating");
                    yield return new RatingTile("Patient survey", f.PatientSurveyRating, f.PatientSurveyRating is null ? "Not rated" : null, "HCAHPS: what patients said");
                    if (f.Outcomes is { } o)
                    {
                        yield return Outcome("Deaths", o.Mortality);
                        yield return Outcome("Safety", o.Safety);
                        yield return Outcome("Readmissions", o.Readmissions);
                    }

                    break;
                case "nursing_home":
                    yield return new RatingTile("Overall", f.OverallRating, f.OverallRating is null ? "Not rated" : null, "CMS overall nursing home rating");
                    yield return new RatingTile("Health inspections", f.InspectionRating, f.InspectionRating is null ? "Not rated" : null, "State inspections");
                    yield return new RatingTile("Staffing", f.StaffingRating, f.StaffingRating is null ? "Not rated" : null, "Nurse staffing hours");
                    yield return new RatingTile("Quality measures", f.QualityRating, f.QualityRating is null ? "Not rated" : null, "Resident care measures");
                    break;
                case "home_health":
                    yield return new RatingTile("Quality of patient care", f.QualityOfCareRating, f.QualityOfCareRating is null ? "Not rated" : null, "CMS star rating");
                    break;
                case "hospice":
                    yield return new RatingTile("Family caregiver survey", f.PatientSurveyRating, f.PatientSurveyRating is null ? "Not rated" : null, "CAHPS Hospice: what families said");
                    break;
            }
        }
    }

    public ClinicianListView Clinicians => new("Clinicians who work here", Profile.Clinicians, $"/facility/{Facility.Ccn}",
        $"/facility/{Facility.Ccn}/clinicians.csv", "Clinicians who list this facility in Medicare Care Compare (Facility Affiliation Data).");

    /// <summary>"0 better, 0 worse of 7" → "Same as the US"; "1 worse" in red.</summary>
    private static RatingTile Outcome(string label, OutcomeCounts c)
    {
        if (c.Measures is not > 0)
        {
            return new RatingTile(label, null, "Not available", "Not enough cases to compare");
        }

        var better = c.Better ?? 0;
        var worse = c.Worse ?? 0;
        var value = (better, worse) switch
        {
            (0, 0) => "Same as the US",
            (_, 0) => $"{better} better than the US",
            (0, _) => $"{worse} worse than the US",
            _ => $"{better} better, {worse} worse",
        };
        return new RatingTile(label, null, value, $"of {c.Measures} measures", worse > 0);
    }

    // "Acute Care Hospitals" → "Acute care hospital".
    private static string Singular(string type)
    {
        var text = type.Trim();
        if (text.EndsWith("Hospitals", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^1];
        }

        return text.Length == 0 ? "Hospital" : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();
    }
}
