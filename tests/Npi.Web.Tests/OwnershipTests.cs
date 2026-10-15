using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npi.Core.Search;
using Npi.Web.Pages;

namespace Npi.Web.Tests;

/// <summary>
/// Facility owners, ownership changes and nursing home inspections (docs/PRODUCT_PLAN.md step 3.6 part 2): the API's checks that need
/// no database, its documentation, and how the pages group owners and count deficiencies.
/// </summary>
public class OwnershipTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:RemoteMySql", "Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;Connection Timeout=1");
        }
    }

    [Theory]
    [InlineData("/api/v1/facilities/not-a-ccn/ownership")]
    [InlineData("/api/v1/facilities/1/inspections")]
    [InlineData("/api/v1/owners/abc")]
    [InlineData("/api/v1/owners/12345678901")]
    public async Task Ids_that_cant_be_one_are_not_found_problems(string url)
    {
        await using var factory = new Factory();
        var response = await factory.CreateClient().GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Pages_and_downloads_of_an_owner_that_cant_exist_are_not_found()
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/owner/abc", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/owner/abc/raw.json", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/owners?type=company", "type")]
    [InlineData("/api/v1/owners?kind=pirate", "kind")]
    [InlineData("/api/v1/owners?page=0", "page")]
    [InlineData("/api/v1/owners?pageSize=500", "pageSize")]
    public async Task Bad_owner_filters_are_problems_keyed_by_parameter(string url, string parameter)
    {
        await using var factory = new Factory();
        var response = await factory.CreateClient().GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(parameter, out _), doc.RootElement.ToString());
    }

    [Fact]
    public async Task The_api_documents_owners_and_inspections()
    {
        await using var factory = new Factory();
        var openApi = await factory.CreateClient().GetStringAsync("/openapi/v1.json", Ct);
        foreach (var path in new[] { "/api/v1/facilities/{ccn}/ownership", "/api/v1/facilities/{ccn}/inspections", "/api/v1/owners", "/api/v1/owners/{id}" })
        {
            Assert.Contains($"\"{path}\"", openApi, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Owners_are_grouped_once_per_group_with_their_roles_joined()
    {
        FacilityOwner Row(string id, bool org, string name, string code, double? pct) =>
            new(id, org, name, null, null, code, OwnerRoles.Label(code, null), OwnerRoles.Group(code), pct, new DateOnly(2020, 1, 1), [], "BRENTWOOD", "TN");

        var groups = OwnershipView.Groups(
        [
            Row("0000000003", false, "JANE Q EXAMPLE", "41", null), Row("0000000001", true, "SAMPLE HOLDINGS LLC", "35", 80),
            Row("0000000001", true, "SAMPLE HOLDINGS LLC", "43", null), Row("0000000002", true, "SAMPLE PARTNERS LP", "34", 100),
            Row("0000000003", false, "JANE Q EXAMPLE", "40", null),
        ]);
        Assert.Equal([OwnerRoles.Owner, OwnerRoles.Control, OwnerRoles.Officer], groups.Select(g => g.Group));
        Assert.Equal([("Sample Partners LP", "100%"), ("Sample Holdings LLC", "80%")], groups[0].Lines.Select(l => (l.Name, OwnershipView.Share(l.Percentage))));
        Assert.Equal("Brentwood, TN", groups[0].Lines[0].Place);
        Assert.Equal("Corporate director; Corporate officer", Assert.Single(groups[2].Lines).Roles);
    }

    [Fact]
    public void Deficiencies_fill_cms_scope_and_severity_grid()
    {
        var inspections = new NursingHomeInspections("335001", null,
            [new InspectionCount("health", 1, "J", 1, 1), new InspectionCount("health", 1, "D", 3, 0), new InspectionCount("health", 2, "D", 2, 0),
             new InspectionCount("health", 3, "G", 1, 1), new InspectionCount("fire", 1, "F", 4, 0)],
            [], [], []);
        var grid = OwnershipView.Grid(inspections, InspectionSeverity.Health);
        Assert.Equal((1, 1, 5), (grid[0, 0], grid[1, 0], grid[2, 0])); // J, G, D (both cycles)
        Assert.Equal(4, OwnershipView.Grid(inspections, InspectionSeverity.FireSafety)[2, 2]); // F: more than minimal, widespread
        Assert.Equal(("J", "G", "D", "A", "L"), (OwnershipView.Letter(0, 0), OwnershipView.Letter(1, 0), OwnershipView.Letter(2, 0), OwnershipView.Letter(3, 0),
            OwnershipView.Letter(0, 2)));
        Assert.Equal((4, 1, 1), OwnershipView.Cycle(inspections, InspectionSeverity.Health, 1));
        Assert.Equal("33.5%", OwnershipView.Share(33.5));
        Assert.Equal("", OwnershipView.Share(0));
    }

    [Fact]
    public void Changes_are_published_for_hospitals_and_nursing_homes_only()
    {
        Assert.True(new FacilityOwnershipSection(null, FacilityTypes.Hospital).ChangesPublished);
        Assert.True(new FacilityOwnershipSection(null, FacilityTypes.NursingHome).ChangesPublished);
        Assert.False(new FacilityOwnershipSection(null, FacilityTypes.Hospice).ChangesPublished);
        Assert.True(FacilityModel.IsNursingHome(new CertifiedFacility("33E001", FacilityTypes.Other, "X", null, null, null, null, null, null, null, null, null,
            null, null, 0)));
    }
}
