using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// A group's or facility's clinicians for _ClinicianList (docs/PRODUCT_PLAN.md step 1.5): the specialty breakdown, one page of the
/// list, paging links and the CSV export.
/// </summary>
/// <param name="PagePath">The page the list is on ("/group/1234567890"); paging adds <c>?page=N#clinicians</c>.</param>
/// <param name="Limit">Show only this many rows and a "see all" link to <paramref name="AllLink"/> instead of paging.</param>
public sealed record ClinicianListView(string Title, ClinicianList List, string PagePath, string ExportLink, string Source, int? Limit = null,
    string? AllLink = null)
{
    public string PageLink(int page) => $"{PagePath}?page={page}#clinicians";
}
