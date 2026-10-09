using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Npi.Web.Tests;

/// <summary>Map search input checks and settings (Stage 5.5 item 10). No database: the connection string points at a closed port.</summary>
public class MapTests
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
    [InlineData("/map/search?classification=Dentist", "bbox")]                        // no area
    [InlineData("/map/search?bbox=1,2,3", "bbox")]                                    // not four numbers
    [InlineData("/map/search?bbox=-100,30,-70,45", "bbox")]                           // too large: zoom in
    [InlineData("/map/search?bbox=-73.3,40.6,-73.5,40.8", "bbox")]                    // west > east
    [InlineData("/map/search?bbox=-73.5,40.6,-73.3,40.8&minYears=99", "minYears")]    // a bad filter
    public async Task Map_search_rejects_bad_requests_without_touching_the_database(string url, string parameter)
    {
        await using var factory = new Factory();
        var response = await factory.CreateClient().GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(parameter, out _), doc.RootElement.ToString());
    }

    [Fact]
    public async Task Map_defaults_to_openstreetmap_with_attribution()
    {
        await using var factory = new Factory();
        var options = factory.Services.GetRequiredService<IOptions<MapOptions>>().Value;
        Assert.Equal("https://tile.openstreetmap.org/{z}/{x}/{y}.png", options.TileUrl);
        Assert.Contains("OpenStreetMap", options.Attribution, StringComparison.Ordinal);
        Assert.True(options.Enabled);
    }
}
