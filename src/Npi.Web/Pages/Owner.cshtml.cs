using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /owner/{id}: an organization owning Medicare-certified facilities, by its CMS associate ID (docs/PRODUCT_PLAN.md step 3.6 part 2):
/// the facilities it owns, controls or manages, and, matched by that ID only, the NPIs, facility and group it is itself. A person's
/// ID answers 404: people are named on the facility pages only (owner, 2026-10-15).
/// </summary>
public class OwnerModel(OwnershipService ownership) : PageModel
{
    public OwnerDetail Detail { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        if (await ownership.GetOwnerAsync(id, ct) is not { } detail)
        {
            return NotFound();
        }

        Detail = detail;
        return Page();
    }

    public OwnerSummary Owner => Detail.Owner;

    public string Name => OwnershipView.DisplayName(Owner.Name);

    /// <summary>"Organization listed for 12 Medicare-certified facilities, owning 10, in Brentwood, TN."</summary>
    public string Summary
    {
        get
        {
            var text = "Organization";
            if (Owner.Facilities == 0)
            {
                return text + " listed as an owner of a facility before a change of ownership.";
            }

            text += Owner.Facilities == 1 ? " listed for 1 Medicare-certified facility" : $" listed for {Format.Number(Owner.Facilities)} Medicare-certified facilities";
            if (Owner.Owned > 0)
            {
                text += Owner.Owned == Owner.Facilities ? (Owner.Facilities == 1 ? ", as an owner" : ", as an owner of each") : $", owning {Format.Number(Owner.Owned)}";
            }
            else
            {
                text += ", as a manager, officer or director";
            }

            if (OwnershipView.Place(Owner.City, Owner.State) is { } place)
            {
                text += $"; based in {place}";
            }

            return text + ".";
        }
    }

    /// <summary>Its facilities by type, most first: "Nursing homes 8 · Hospices 2".</summary>
    public IReadOnlyList<(string Type, int Count)> ByType =>
        [.. Detail.Facilities.GroupBy(f => f.Type).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.Ordinal)];
}
