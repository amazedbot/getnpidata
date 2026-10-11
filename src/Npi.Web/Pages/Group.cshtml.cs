using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /group/{pacId}: a Medicare group practice (docs/PRODUCT_PLAN.md step 1.5): who it is, the organization NPIs enrolled under it,
/// and its clinicians by specialty, with a list and an export.
/// </summary>
public class GroupModel(OrganizationService organizations) : PageModel
{
    public GroupProfile Group { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(string pacId, [FromQuery] int page, CancellationToken ct)
    {
        if (await organizations.GetGroupAsync(pacId, Math.Max(page, 1), ct) is not { } group)
        {
            return NotFound();
        }

        Group = group;
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
