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
/// The Facilities &amp; hospitals and Groups &amp; organizations lists (docs/PRODUCT_PLAN.md step 3.6) through /api/v1 and the CSV links,
/// with the directories built from rows (no database), and the pages' links.
/// </summary>
public class DirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static FacilityDirectory Facilities() => new(FacilityDirectory.Merge(
        [
            new("330045", "GOOD SAMARITAN HOSPITAL", "1000 MONTAUK HWY", "WEST ISLIP", "NY", "11795", true, "00", null),
            new("331850", "HEALTHY COMMUNITY CENTER", "5 MAIN ST", "BROOKLYN", "NY", "11201", true, null, null),
            new("330888", "OLD NAME HOSPITAL", "10 COURT ST", "BROOKLYN", "NY", "11201", false, "07", new DateOnly(2020, 1, 1)),
        ],
        [new(FacilityTypes.Hospital, "330045", "GOOD SAMARITAN HOSPITAL", null, "WEST ISLIP", "NY", "11795", null, "Acute Care Hospitals", null, null, 3)],
        [], new Dictionary<string, (double Lat, double Lon)> { ["11795"] = (40.7, -73.3) }));

    internal static OrganizationDirectory Organizations() => new(
        [new("1000000001", "BIG HEALTH GROUP", "NY", 1200, 3, true, 91.25, 2024), new("1000000002", "SMALL PRACTICE LLC", "NY", 4, 1, null, null, null)],
        [("207Q00000X", "NY", 1, 10)], [], null,
        new Dictionary<string, IReadOnlyList<string>> { ["1234567893"] = ["1000000002"] });

    internal sealed class DirectoryFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:RemoteMySql", "Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;Connection Timeout=1");
            builder.ConfigureServices(s =>
            {
                s.AddSingleton(Facilities());
                s.AddSingleton(Organizations());
            });
        }
    }

    [Fact]
    public async Task Facilities_api_filters_and_pages()
    {
        await using var factory = new DirectoryFactory();
        using var client = factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/v1/facilities?type=hospital&status=all&sort=-closed&pageSize=1", Ct));
        var root = doc.RootElement;
        Assert.Equal((2, 1), (root.GetProperty("totalCount").GetInt32(), root.GetProperty("pageSize").GetInt32()));
        var first = root.GetProperty("items")[0];
        Assert.Equal(("330888", "hospital", false, "2020-01-01"),
            (first.GetProperty("ccn").GetString(), first.GetProperty("type").GetString(), first.GetProperty("active").GetBoolean(),
                first.GetProperty("terminationDate").GetString()));

        using var search = JsonDocument.Parse(await client.GetStringAsync("/api/v1/facilities?q=samaritan", Ct));
        Assert.Equal(3.0, search.RootElement.GetProperty("items")[0].GetProperty("rating").GetDouble());
    }

    [Theory]
    [InlineData("/api/v1/facilities?type=spa", "type")]
    [InlineData("/api/v1/facilities?minRating=9", "minRating")]
    [InlineData("/api/v1/facilities?status=open", "status")]
    [InlineData("/api/v1/facilities?sort=beds", "sort")]
    [InlineData("/api/v1/facilities?pageSize=500", "pageSize")]
    [InlineData("/api/v1/facilities.csv?state=New%20York", "state")]
    [InlineData("/api/v1/groups?minClinicians=10&maxClinicians=2", "maxClinicians")]
    [InlineData("/api/v1/groups?sort=size", "sort")]
    [InlineData("/api/v1/groups?page=0", "page")]
    [InlineData("/api/v1/groups.csv?state=N", "state")]
    public async Task Bad_filters_are_problems_keyed_by_parameter(string url, string parameter)
    {
        await using var factory = new DirectoryFactory();
        var response = await factory.CreateClient().GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(parameter, out _), doc.RootElement.ToString());
    }

    [Fact]
    public async Task A_number_that_isnt_one_is_a_bad_request()
    {
        await using var factory = new DirectoryFactory();
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().GetAsync("/api/v1/facilities?minRating=abc", Ct)).StatusCode);
    }

    [Fact]
    public async Task Csv_exports_follow_the_filters()
    {
        await using var factory = new DirectoryFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var api = await client.GetAsync("/api/v1/facilities.csv?type=fqhc", Ct);
        Assert.Equal("text/csv", api.Content.Headers.ContentType?.MediaType);
        var lines = (await api.Content.ReadAsStringAsync(Ct)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("331850,HEALTHY COMMUNITY CENTER,Federally qualified health center", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("/facility/331850", lines[1], StringComparison.Ordinal);

        var groups = await client.GetStringAsync("/groups.csv?q=1234567893", Ct);
        Assert.Contains("1000000002,SMALL PRACTICE LLC,NY,4,1,", groups, StringComparison.Ordinal);
        Assert.DoesNotContain("BIG HEALTH", groups, StringComparison.Ordinal);

        // The site's CSV links send a bad filter back to the page, which shows the error.
        var bad = await client.GetAsync("/facilities.csv?type=spa", Ct);
        Assert.Equal(HttpStatusCode.Redirect, bad.StatusCode);
        Assert.Equal("/facilities?type=spa", bad.Headers.Location!.OriginalString);
        Assert.Equal("/groups?sort=size", (await client.GetAsync("/groups.csv?sort=size", Ct)).Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Groups_api_finds_a_group_by_an_npi_that_bills_through_it()
    {
        await using var factory = new DirectoryFactory();
        using var doc = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/api/v1/groups?q=1234567893", Ct));
        var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("1000000002", item.GetProperty("pacId").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("mipsScore").ValueKind);
    }

    [Fact]
    public async Task The_api_documents_both_lists()
    {
        await using var factory = new DirectoryFactory();
        var openApi = await factory.CreateClient().GetStringAsync("/openapi/v1.json", Ct);
        foreach (var path in new[] { "/api/v1/facilities", "/api/v1/facilities.csv", "/api/v1/groups", "/api/v1/groups.csv" })
        {
            Assert.Contains($"\"{path}\"", openApi, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Facility_list_links_keep_the_filters()
    {
        var model = new FacilitiesModel(null!, Options.Create(new MapOptions())) { Type = "hospital", State = "NY", MinRating = 4, Sort = "-rating", PageNumber = 3 };
        Assert.Equal("/facilities?type=hospital&state=NY&minRating=4&sort=-rating", model.ListUrl());
        Assert.Equal("/facilities?type=hospital&state=NY&minRating=4&sort=-rating&page=2", model.ListUrl(page: 2));
        Assert.Equal("/facilities.csv?type=hospital&state=NY&minRating=4&sort=-rating", model.ListUrl("/facilities.csv"));
        Assert.Equal("/facilities?state=NY&minRating=4&sort=-rating", model.ListUrl(type: null));
        Assert.Equal("/facilities?type=hospital&state=NY&minRating=4&sort=rating", model.SortUrl("rating"));   // best first → worst first
        Assert.Equal("/facilities?type=hospital&state=NY&minRating=4&sort=city", model.SortUrl("city"));
        Assert.Equal("descending", model.AriaSort("rating"));
        Assert.Equal("/map?category=Hospitals&state=NY", model.MapUrl);
        Assert.Equal("Hospitals in New York", model.ListTitle);

        var name = new FacilitiesModel(null!, Options.Create(new MapOptions())) { Sort = "name" };
        Assert.Equal("/facilities?sort=-name", name.SortUrl("name"));
        Assert.Equal("/facilities", new FacilitiesModel(null!, Options.Create(new MapOptions())) { Sort = "-name" }.SortUrl("name"));
        Assert.Equal(("4.5 ★", "—"), (FacilitiesModel.Stars(4.5), FacilitiesModel.Stars(null)));
    }

    [Fact]
    public void Group_list_links_keep_the_filters()
    {
        var model = new OrganizationsModel(null!, null!) { View = "groups", Q = "health", State = "NY", MinClinicians = 50 };
        Assert.Equal("/groups?q=health&state=NY&minClinicians=50", model.GroupsUrl());
        Assert.Equal("/groups.csv?q=health&state=NY&minClinicians=50&sort=name", model.GroupsUrl("/groups.csv", sort: "name"));
        Assert.Equal("/groups?q=health&state=NY&minClinicians=50&sort=clinicians", model.SortUrl("clinicians")); // largest first → smallest first
        Assert.Equal("/groups?q=health&state=NY&minClinicians=50&sort=-mips", model.SortUrl("mips"));
        Assert.Equal("/groups?q=health&state=NY&minClinicians=50&sort=name", model.SortUrl("name"));
        Assert.Equal("descending", model.AriaSort("clinicians"));
        Assert.Equal("/groups?minClinicians=1000", model.BandUrl(new GroupSizeBand("1,000 or more", 1000, null, 3, 5000)));
        Assert.Equal("/groups?minClinicians=2&maxClinicians=9", model.BandUrl(new GroupSizeBand("2–9", 2, 9, 3, 15)));
        Assert.Equal("/search?entityType=2&category=Clinics%20%26%20centers&state=NY&quality=stale",
            model.OrganizationsUrl("/search", "Clinics & centers", "stale"));
        Assert.Equal("81.5", OrganizationsModel.Score(81.5));

        // On the overview the search box searches groups and organizations at once; the list links leave it out.
        var overview = new OrganizationsModel(null!, null!) { Q = "health" };
        Assert.Equal("/groups", overview.GroupsUrl());
    }
}
