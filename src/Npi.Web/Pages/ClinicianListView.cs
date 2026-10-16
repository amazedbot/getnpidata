using System.Globalization;
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
    /// <summary>
    /// An organization's list (step 3.9): a column saying where each clinician's tie comes from, sortable headers, and the summary of
    /// the sources; null on a facility's or group's list.
    /// </summary>
    public OrganizationClinicianList? Organization { get; init; }

    public string Sort => Organization?.Sort ?? OrganizationClinicians.DefaultSort;

    public string PageLink(int page) => Organization is null || Sort == OrganizationClinicians.DefaultSort
        ? $"{PagePath}?page={page.ToString(CultureInfo.InvariantCulture)}#clinicians"
        : $"{PagePath}?page={page.ToString(CultureInfo.InvariantCulture)}&sort={Sort}#clinicians";

    public string SortLink(string sort) =>
        sort == OrganizationClinicians.DefaultSort ? $"{PagePath}#clinicians" : $"{PagePath}?sort={sort}#clinicians";

    /// <summary>aria-sort for a sortable header: ascending when the list is sorted by it.</summary>
    public string? AriaSort(string sort) => Sort == sort ? "ascending" : null;
}
