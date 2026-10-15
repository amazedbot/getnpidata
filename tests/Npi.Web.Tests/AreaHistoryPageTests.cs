using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Npi.Web.Tests;

/// <summary>
/// Areas over time (docs/PRODUCT_PLAN.md step 3.3): the map data, the change lists and the API refuse bad requests before
/// any database work (the connection string points at a closed port), and the map's shapes are the current counties.
/// </summary>
public class AreaHistoryPageTests
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:RemoteMySql", "Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;Connection Timeout=1");
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/area/map.json?stat=bogus", "stat")]
    [InlineData("/area/map.json?level=nation", "level")]
    [InlineData("/area/map.json?level=county&state=ZZ", "state")]
    [InlineData("/area/map.json?level=state&state=NY", "state")]
    [InlineData("/area/map.json?stat=new&month=2026-13", "month")]
    [InlineData("/area/map.json?category=Wizards", "category")]
    [InlineData("/api/v1/areas/map?stat=bogus", "stat")]
    [InlineData("/api/v1/areas/us/history?category=Wizards", "category")]
    [InlineData("/api/v1/areas/NY/36103/history?category=Wizards", "category")]
    public async Task Bad_requests_name_the_parameter(string url, string parameter)
    {
        await using var factory = new Factory();
        var response = await factory.CreateClient().GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(parameter, out _), doc.RootElement.ToString());
    }

    [Theory]
    [InlineData("/area/NY/36103/changes?kind=bogus&month=2026-10")]          // not a kind
    [InlineData("/area/NY/36103/changes?kind=new&month=2026-13")]            // not a month
    [InlineData("/area/NY/36103/changes?kind=new")]                          // no month
    [InlineData("/area/NY/36103/changes?kind=new&month=2026-10&category=Wizards")]
    [InlineData("/area/changes?kind=moved_in&month=2026-10")]                // the US counts no moves
    [InlineData("/area/ZZ/changes?kind=new&month=2026-10")]                  // not a state
    [InlineData("/area/NY/3610/changes?kind=new&month=2026-10")]             // not a FIPS code
    [InlineData("/area/NY/36103/changes?kind=bogus&month=2026-10&handler=csv")]
    public async Task Change_lists_refuse_unknown_kinds_months_and_areas(string url)
    {
        await using var factory = new Factory();
        var response = await factory.CreateClient().GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // A good request gets as far as the database (closed here, so the connection fails). On the area page itself these paths
    // would be a 404 before any query ("changes" is no state or county, "map.json" no state), so this shows the routes win.
    [Theory]
    [InlineData("/area/changes?kind=new&month=2026-10")]
    [InlineData("/area/NY/changes?kind=moved_in&month=2026-10")]
    [InlineData("/area/NY/36103/changes?kind=deactivated&month=2026-10&handler=csv")]
    [InlineData("/area/map.json?level=county&state=NY&stat=movedIn&month=2026-10")]
    public async Task Good_requests_reach_the_lists_and_the_map_data(string url)
    {
        await using var factory = new Factory();
        var client = factory.CreateClient();
        var ex = await Record.ExceptionAsync(() => client.GetAsync(url, Ct));
        Assert.Equal("MySqlException", ex?.GetType().Name);
    }

    [Fact]
    public void The_map_shapes_are_todays_counties_with_the_insets()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "Npi.Web", "wwwroot", "maps", "us-counties.topo.json");
        Assert.True(new FileInfo(path).Length < 1_000_000, "keep the shapes under 1 MB");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var objects = doc.RootElement.GetProperty("objects");
        var counties = objects.GetProperty("counties").GetProperty("geometries").EnumerateArray().Select(g => g.GetProperty("id").GetString()!).ToHashSet();
        var states = objects.GetProperty("states").GetProperty("geometries").EnumerateArray().Select(g => g.GetProperty("id").GetString()!).ToHashSet();

        Assert.Equal(3222, counties.Count);
        // Connecticut by its planning regions, as HUD's crosswalk and the Census Gazetteer (the county table) count it.
        Assert.Equal(["09110", "09120", "09130", "09140", "09150", "09160", "09170", "09180", "09190"], counties.Where(c => c.StartsWith("09", StringComparison.Ordinal)).Order());
        Assert.Contains("02063", counties);   // Alaska's Chugach Census Area (2019)
        Assert.Contains("15003", counties);   // Honolulu
        Assert.Contains("72127", counties);   // San Juan, PR
        Assert.Equal(52, states.Count);       // 50 states, DC and Puerto Rico
        Assert.Contains("PR", states);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "getnpidata.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("getnpidata.sln not found above " + AppContext.BaseDirectory);
    }
}
