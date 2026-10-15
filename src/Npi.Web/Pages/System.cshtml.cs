using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /system/{id}: one health system (docs/PRODUCT_PLAN.md step 3.6 part 2): AHRQ's counts, its facilities by type (from AHRQ's linkage
/// files, by CMS number) and its Medicare groups (whose PAC ID enrolls an NPI billing for one of those facilities).
/// </summary>
public class SystemModel(HealthSystemService systems) : PageModel
{
    public HealthSystemDetail Detail { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        if (await systems.GetAsync(id, ct) is not { } detail)
        {
            return NotFound();
        }

        Detail = detail;
        return Page();
    }

    public HealthSystemSummary System => Detail.System;

    /// <summary>The facilities grouped by type, in the list's type order.</summary>
    public IEnumerable<(FacilityType Type, IReadOnlyList<HealthSystemFacility> Facilities)> ByType =>
        Detail.Facilities.GroupBy(f => f.Type).Select(g => (FacilityTypes.Get(g.Key), (IReadOnlyList<HealthSystemFacility>)[.. g]));
}
