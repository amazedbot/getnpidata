using Npi.Core.Search;
using Npi.Web.Pages;

namespace Npi.Web.Tests;

/// <summary>One page per organization (docs/PRODUCT_PLAN.md step 3.9): links from lists, the merged clinician list's paging and sorting.</summary>
public class OrganizationPageTests
{
    private static readonly ProviderOrganization Hospital = new("1245370717", "JAMAICA HOSPITAL", 12);

    [Fact]
    public void Lists_link_to_the_organization_page_when_there_is_one()
    {
        var links = new OrganizationLinks(
            new Dictionary<string, ProviderOrganization> { ["330014"] = Hospital },
            new Dictionary<string, ProviderOrganization> { ["9000000001"] = Hospital },
            new Dictionary<string, ProviderOrganization> { ["1588702093"] = Hospital });

        Assert.Equal("/organization/1245370717", links.Facility("330014"));
        Assert.Equal("/organization/1245370717", links.Facility("330014".ToLowerInvariant()));
        Assert.Equal("/facility/335001", links.Facility("335001"));
        Assert.Equal("/organization/1245370717", links.Group("9000000001"));
        Assert.Equal("/group/9000000002", links.Group("9000000002"));
        Assert.Equal("/organization/1245370717", links.Provider("1588702093"));
        Assert.True(links.HasPage("1588702093"));
        Assert.Equal("/provider/1003000126", links.Provider("1003000126"));
    }

    [Fact]
    public async Task Without_the_tables_lists_keep_their_detail_links()
    {
        var links = await OrganizationLinks.ForAsync(null, ["330014"], ["9000000001"], ["1588702093"], TestContext.Current.CancellationToken);
        Assert.Equal(("/facility/330014", "/group/9000000001", "/provider/1588702093"),
            (links.Facility("330014"), links.Group("9000000001"), links.Provider("1588702093")));
    }

    [Fact]
    public void The_merged_clinician_list_keeps_its_sort_while_paging()
    {
        var summary = new OrganizationClinicianSummary(60, 40, 30, 5, 15, []);
        var list = new ClinicianList(60, [], [], 1, OrganizationClinicians.PageSize);
        ClinicianListView View(string sort) => new("Clinicians", list, "/organization/1245370717", "/organization/1245370717/clinicians.csv", "")
        {
            Organization = new OrganizationClinicianList(summary, list, sort),
        };

        Assert.Equal("/organization/1245370717?page=2#clinicians", View("name").PageLink(2));
        Assert.Equal("/organization/1245370717?page=2&sort=specialty#clinicians", View("specialty").PageLink(2));
        Assert.Equal("/organization/1245370717#clinicians", View("specialty").SortLink("name"));
        Assert.Equal("/organization/1245370717?sort=source#clinicians", View("name").SortLink("source"));
        Assert.Equal(("ascending", (string?)null), (View("location").AriaSort("location"), View("location").AriaSort("name")));

        // A facility's or group's list pages as before.
        Assert.Equal("/group/1?page=3#clinicians", new ClinicianListView("Clinicians", list, "/group/1", "/group/1/clinicians.csv", "").PageLink(3));
    }
}
