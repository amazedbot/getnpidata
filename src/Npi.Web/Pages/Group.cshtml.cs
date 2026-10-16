using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /group/{pacId}: a Medicare group practice (docs/PRODUCT_PLAN.md step 1.5): who it is, the organization NPIs enrolled under it,
/// and its clinicians by specialty, with a list and an export. Since step 3.9 it is the detail page of one PAC ID: when its organization
/// NPIs have an organization page, a banner links to everything on one page.
/// </summary>
public class GroupModel(OrganizationService organizations, OrganizationDirectory directory, OrganizationGroupService? groups = null) : PageModel
{
    /// <summary>The organization page (step 3.9) its organization NPIs are part of, for the banner; null when they have none.</summary>
    public ProviderOrganization? Organization { get; private set; }

    public GroupProfile Group { get; private set; } = null!;

    /// <summary>The group in the Groups &amp; organizations list (its group MIPS score); null while that list is being built.</summary>
    public GroupSummary? Listing { get; private set; }

    public async Task<IActionResult> OnGetAsync(string pacId, [FromQuery] int page, CancellationToken ct)
    {
        if (await organizations.GetGroupAsync(pacId, Math.Max(page, 1), ct) is not { } group)
        {
            return NotFound();
        }

        Group = group;
        Listing = directory.PeekGroup(group.PacId);
        if (groups is not null && group.Organizations.Count > 0)
        {
            Organization = (await groups.GetPagesForPacIdsAsync([group.PacId], ct)).GetValueOrDefault(group.PacId);
        }
        return Page();
    }

    public string Name => CityCatalog.TitleCase(Group.Name);

    /// <summary>"Group practice in NY with 96 clinicians, mostly cardiologists."</summary>
    public string Summary
    {
        get
        {
            var text = "Medicare group practice";
            if (Group.State is { } state)
            {
                text += " in " + (GeographyCatalog.StateName(state) ?? state);
            }

            var total = Group.Clinicians.Total;
            text += total == 1 ? " with 1 clinician" : $" with {total.ToString("N0", CultureInfo.InvariantCulture)} clinicians";
            if (Group.Clinicians.BySpecialty is [var first, ..] && total > 1 && first.Clinicians * 2 > total && first.Specialty != "Other or not listed")
            {
                text += ", mostly " + Plural(first.Specialty).ToLowerInvariant();
            }

            return text + ".";
        }
    }

    public ClinicianListView Clinicians => new("Clinicians", Group.Clinicians, $"/group/{Group.PacId}", $"/group/{Group.PacId}/clinicians.csv",
        "Clinicians who bill Medicare through this group (Medicare enrollment, reassignments) or list it in Medicare Care Compare.");

    private static string Plural(string singular) =>
        singular.EndsWith('y') && !singular.EndsWith("ey", StringComparison.Ordinal) ? singular[..^1] + "ies"
        : singular.EndsWith('s') ? singular : singular + "s";
}
