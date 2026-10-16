using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /organization/{npi}: the combined profile of an organization with several NPIs (docs/PRODUCT_PLAN.md step 3.8): the main NPI's
/// record in brief, every NPI with its role, the facilities, Medicare groups and locations they hold together, their clinicians, and
/// how the NPIs were grouped. Any member's NPI opens it: the main NPI's address is canonical, the others redirect there (301).
/// </summary>
public class OrganizationModel(OrganizationGroupService groups, ProviderDetailService details, OrganizationService organizations) : PageModel
{
    /// <summary>The profile shown (set by <see cref="OnGetAsync"/>; tests set it directly).</summary>
    public OrganizationProfile Profile { get; set; } = null!;

    /// <summary>The clinicians of all the organization's Medicare groups together.</summary>
    public ClinicianList Clinicians { get; set; } = new(0, [], [], 1, OrganizationService.PageSize);

    public async Task<IActionResult> OnGetAsync(string npi, [FromQuery] int page, CancellationToken ct)
    {
        if (!InputFormats.IsNpi(npi))
        {
            return NotFound();
        }

        var main = await groups.GetMainNpiAsync(npi, ct);
        if (main is null)
        {
            // An NPI alone in its organization: its own profile is the whole story.
            return Redirect($"/provider/{npi}");
        }

        if (main != npi)
        {
            return RedirectPermanent($"/organization/{main}{Request.QueryString}");
        }

        if (await groups.GetCombinedAsync(main, details, ct) is not { } profile)
        {
            return NotFound();
        }

        Profile = profile;
        Clinicians = await organizations.GetCliniciansForGroupsAsync([.. profile.Groups.Select(g => g.PacId)], Math.Max(page, 1), ct);
        return Page();
    }

    public string Name => CityCatalog.TitleCase(Profile.Name);

    public OrganizationMember MainMember => Profile.Npis.FirstOrDefault(m => m.IsMain) ?? Profile.Npis[0];

    /// <summary>"Hospital in Jamaica, NY with 12 NPIs, 3 certified facilities and 2 Medicare groups."</summary>
    public string Summary
    {
        get
        {
            var main = MainMember;
            var text = main.Specialty ?? "Health care organization";
            if (main.City is { } city)
            {
                text += $" in {CityCatalog.TitleCase(city)}{(main.State is null ? "" : ", " + main.State)}";
            }

            var parts = new List<string> { $"{Profile.Npis.Count.ToString(CultureInfo.InvariantCulture)} NPIs" };
            if (Profile.Facilities.Count > 0)
            {
                parts.Add(Profile.Facilities.Count == 1 ? "1 certified facility" : $"{Profile.Facilities.Count.ToString(CultureInfo.InvariantCulture)} certified facilities");
            }

            if (Profile.Groups.Count > 0)
            {
                parts.Add(Profile.Groups.Count == 1 ? "1 Medicare group" : $"{Profile.Groups.Count.ToString(CultureInfo.InvariantCulture)} Medicare groups");
            }

            return $"{text} with {Join(parts)}.";
        }
    }

    public ClinicianListView ClinicianView => new("Clinicians of its Medicare groups", Clinicians, $"/organization/{Profile.MainNpi}",
        $"/organization/{Profile.MainNpi}/clinicians.csv",
        "Clinicians who bill Medicare through one of the organization's Medicare groups (Medicare enrollment, reassignments) or list one in Medicare Care Compare.");

    /// <summary>The NPIs by what tied them, for the note: "same name and address: 9; shared Medicare-certified facility (CCN): 4".</summary>
    public IEnumerable<(string Tie, int Npis)> TieCounts =>
        OrganizationGroups.TieTexts.Select(t => (t.Text, Profile.Npis.Count(m => m.JoinedBy.Contains(t.Text, StringComparison.Ordinal)))).Where(t => t.Item2 > 0);

    public static string QualityClass(string? label) => label switch
    {
        "Stale" => "badge warn",
        "Check" => "badge",
        "Current" => "badge good",
        _ => "badge",
    };

    private static string Join(List<string> parts) =>
        parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
}
