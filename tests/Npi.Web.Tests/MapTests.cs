using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Npi.Web.Tests;

/// <summary>"Near me" input checks and the map settings (Stage 5.5 item 10). No database: the connection string points at a closed port.</summary>
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
    [InlineData("""{"lat":95,"lon":-73.4}""")]
    [InlineData("""{"lat":40.7,"lon":-200}""")]
    [InlineData("""{"lat":40.7}""")]
    [InlineData("""{}""")]
    public async Task Nearest_zip_rejects_bad_coordinates_without_touching_the_database(string body)
    {
        await using var factory = new Factory();
        var response = await factory.CreateClient().PostAsync("/lookup/nearest-zip", new StringContent(body, Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("valid coordinates", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nearest_zip_is_post_only_so_coordinates_never_appear_in_urls()
    {
        await using var factory = new Factory();
        var response = await factory.CreateClient().GetAsync("/lookup/nearest-zip?lat=40.7&lon=-73.4", Ct);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
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
