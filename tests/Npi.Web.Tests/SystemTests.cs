using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npi.Core.Search;
using Npi.Web.Pages;

namespace Npi.Web.Tests;

/// <summary>
/// Health systems, ACOs and HRSA health center sites (docs/PRODUCT_PLAN.md step 3.6 part 2) through /api/v1 and the pages, without a
/// database: validation, unknown IDs, and the facility list with its systems and HRSA sites built from rows.
/// </summary>
public class SystemTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static FacilityDirectory Facilities() => new(FacilityDirectory.Merge(
        [
            new("330045", "GOOD SAMARITAN HOSPITAL", "1000 MONTAUK HWY", "WEST ISLIP", "NY", "11795", true, "00", null),
            new("331850", "HEALTHY COMMUNITY CENTER", "5 MAIN ST", "BROOKLYN", "NY", "11201", true, null, null),
        ],
        [], [], new Dictionary<string, (double Lat, double Lon)>(),
        networks: new FacilityNetworks([new("330045", "HSI90000001", "Harbor Health System")],
            [
                new("BPS-H80-900001", "Healthy Community Center Main", "5 Main St", "Brooklyn", "NY", "11201", null, null, null, "Permanent", false,
                    "HEALTHY COMMUNITY INC", "331850", null, 40.69, -73.99),
                new("BPS-LAL-900004", "Valley Mobile Clinic", "1 Farm Rd", "Albany", "NY", "12201", null, null, null, "Mobile Van", true, "VALLEY CARE", null,
                    null, 42.65, -73.75),
            ],
            new Dictionary<string, string>(),
            [new("DSH330045", "DSH", true, new DateOnly(2012, 1, 1), "330045", null, [])])));

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:RemoteMySql", "Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;Connection Timeout=1");
            builder.ConfigureServices(s => s.AddSingleton(Facilities()));
        }
    }

    [Theory]
    [InlineData("/api/v1/systems?state=New%20York", "state")]
    [InlineData("/api/v1/systems?sort=size", "sort")]
    [InlineData("/api/v1/systems?pageSize=500", "pageSize")]
    [InlineData("/api/v1/systems?q=" + "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "q")]
    [InlineData("/api/v1/acos?state=N", "state")]
    [InlineData("/api/v1/acos?page=0", "page")]
    [InlineData("/api/v1/facilities?system=HSI%201", "system")]
    public async Task Bad_filters_are_problems_keyed_by_parameter(string url, string parameter)
    {
        await using var factory = new Factory();
        var response = await factory.CreateClient().GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(parameter, out _), doc.RootElement.ToString());
    }

    [Theory]
    [InlineData("/api/v1/systems/12345")]
    [InlineData("/api/v1/acos/1234")]
    [InlineData("/api/v1/health-center-sites/x%20y")]
    [InlineData("/system/12345")]
    [InlineData("/aco/A12-34")]
    [InlineData("/health-center/x%20y")]
    [InlineData("/api/v1/providers/12345/acos")]
    [InlineData("/api/v1/groups/abc/acos")]
    public async Task Ids_that_cant_be_one_are_not_found_without_asking_the_database(string url)
    {
        await using var factory = new Factory();
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync(url, Ct)).StatusCode);
    }

    [Fact]
    public async Task The_facility_list_carries_systems_and_hrsa_sites()
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        using var harbor = JsonDocument.Parse(await client.GetStringAsync("/api/v1/facilities?system=HSI90000001", Ct));
        var hospital = Assert.Single(harbor.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(("330045", "HSI90000001", "Harbor Health System", "cms"), (hospital.GetProperty("ccn").GetString(),
            hospital.GetProperty("systemId").GetString(), hospital.GetProperty("systemName").GetString(), hospital.GetProperty("source").GetString()));
        Assert.False(hospital.TryGetProperty("pagePath", out _)); // the page link is the site's, not part of the API

        using var sites = JsonDocument.Parse(await client.GetStringAsync("/api/v1/facilities?type=health_center", Ct));
        var van = Assert.Single(sites.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(("BPS-LAL-900004", "hrsa", "VALLEY CARE"), (van.GetProperty("ccn").GetString(), van.GetProperty("source").GetString(),
            van.GetProperty("healthCenter").GetString()));

        using var fqhc = JsonDocument.Parse(await client.GetStringAsync("/api/v1/facilities?q=331850", Ct));
        Assert.Equal("HEALTHY COMMUNITY INC", fqhc.RootElement.GetProperty("items")[0].GetProperty("healthCenter").GetString());

        var csv = await client.GetStringAsync("/facilities.csv?type=health_center", Ct);
        Assert.Contains("/health-center/BPS-LAL-900004", csv, StringComparison.Ordinal);

        // 340B covered entities only (HRSA OPAIS), in the list, its CSV and the page.
        using var covered = JsonDocument.Parse(await client.GetStringAsync("/api/v1/facilities?in340b=true", Ct));
        var entity = Assert.Single(covered.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(("330045", "DSH", "2012-01-01"), (entity.GetProperty("ccn").GetString(), entity.GetProperty("program340B").GetString(),
            entity.GetProperty("program340BSince").GetString()));
        using var notCovered = JsonDocument.Parse(await client.GetStringAsync("/api/v1/facilities?in340b=false", Ct));
        Assert.DoesNotContain(notCovered.RootElement.GetProperty("items").EnumerateArray(), f => f.GetProperty("ccn").GetString() == "330045");
        Assert.Contains("Disproportionate share hospital,2012-01-01,", await client.GetStringAsync("/facilities.csv?in340b=true", Ct), StringComparison.Ordinal);

        var openApi = await client.GetStringAsync("/openapi/v1.json", Ct);
        foreach (var path in new[] { "/api/v1/systems", "/api/v1/systems/{id}", "/api/v1/acos", "/api/v1/acos/{id}", "/api/v1/health-center-sites/{id}",
                     "/api/v1/providers/{npi}/acos", "/api/v1/groups/{pacId}/acos" })
        {
            Assert.Contains($"\"{path}\"", openApi, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Facility_list_links_keep_the_system()
    {
        var model = new FacilitiesModel(null!, Options.Create(new MapOptions())) { Type = "hospital", System = "HSI90000001" };
        Assert.Equal("/facilities?type=hospital&system=HSI90000001", model.ListUrl());
        Assert.Equal("/facilities?type=hospital", model.ListUrl(system: null));
        Assert.Equal("Hospitals of health system HSI90000001", model.ListTitle); // before the list is read

        var covered = new FacilitiesModel(null!, Options.Create(new MapOptions())) { Type = "hospital", In340B = true };
        Assert.Equal("/facilities?type=hospital&in340b=true", covered.ListUrl());
        Assert.Equal("/facilities?type=hospital", covered.ListUrl(in340b: null));
        Assert.Equal("Hospitals in the 340B program", covered.ListTitle);
    }

    [Fact]
    public void System_and_aco_list_links()
    {
        var systems = new SystemsModel(null!) { Q = "harbor", State = "NY" };
        Assert.Equal("/systems?q=harbor&state=NY&sort=name", systems.SortUrl("name"));
        Assert.Equal("/systems?q=harbor&state=NY&sort=hospitals", systems.SortUrl("hospitals")); // most first → fewest first
        Assert.Equal("/systems?q=harbor&state=NY&sort=-beds", systems.SortUrl("beds"));
        Assert.Equal("descending", systems.AriaSort("hospitals"));
        Assert.Equal("/systems?q=harbor&state=NY&page=2", systems.ListUrl(page: 2));
        Assert.Equal("/acos?state=AZ&page=3", new AcosModel(null!) { State = "AZ" }.PageUrl(3));
        Assert.Equal("/organizations?q=DESERT%20FAMILY%20PRACTICE%2C%20PLLC", AcoModel.SearchUrl("DESERT FAMILY PRACTICE, PLLC"));
    }

    [Theory]
    [InlineData("www.example.org/aco", "https://www.example.org/aco")]
    [InlineData("http://example.org", "http://example.org/")]
    [InlineData("AZBestCare.org", "https://azbestcare.org/")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("call us", null)]
    [InlineData("someone@example.org", null)]
    [InlineData("", null)]
    public void Websites_become_safe_links(string site, string? link) => Assert.Equal(link, NetworkFormat.Website(site));

    [Fact]
    public void Service_areas_in_words() => Assert.Equal("Arizona, Nevada, ZZ", NetworkFormat.States("AZ, NV,zz"));
}
