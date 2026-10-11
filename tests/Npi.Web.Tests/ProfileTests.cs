using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npi.Web.Pages;

namespace Npi.Web.Tests;

/// <summary>The provider profile (docs/PRODUCT_PLAN.md step 1.4): the static map preview and the raw-data routes.</summary>
public class ProfileTests
{
    private const string Tiles = "https://tile.example.org/{z}/{x}/{y}.png";

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:RemoteMySql", "Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;Connection Timeout=1");
        }
    }

    [Fact]
    public void One_location_is_centred_at_the_closest_zoom()
    {
        var map = StaticMap.Create([("1", 40.7128, -74.0060, false)], Tiles)!;
        Assert.Equal(16, map.Zoom);
        var pin = Assert.Single(map.Pins);
        Assert.Equal(("1", 50.0, 50.0), (pin.Label, Math.Round(pin.LeftPercent, 6), Math.Round(pin.TopPercent, 6)));

        // The tiles cover the whole frame, and their URLs carry the zoom and tile numbers.
        Assert.All(map.Tiles, t => Assert.StartsWith("https://tile.example.org/16/", t.Url, StringComparison.Ordinal));
        Assert.True(map.Tiles.Min(t => t.LeftPercent) <= 0 && map.Tiles.Max(t => t.LeftPercent + t.WidthPercent) >= 100);
        Assert.True(map.Tiles.Min(t => t.TopPercent) <= 0 && map.Tiles.Max(t => t.TopPercent + t.HeightPercent) >= 100);
    }

    [Fact]
    public void Several_locations_all_fit_inside_the_frame()
    {
        var map = StaticMap.Create([("1", 40.70, -73.30, false), ("2", 40.80, -72.90, true), ("3", 40.65, -73.60, false)], Tiles)!;
        Assert.True(map.Zoom < 16);
        Assert.All(map.Pins, p => Assert.InRange(p.LeftPercent, 0, 100));
        Assert.All(map.Pins, p => Assert.InRange(p.TopPercent, 0, 100));
        Assert.True(map.Pins[1].Approximate);
    }

    [Fact]
    public void No_points_or_no_tiles_means_no_map()
    {
        Assert.Null(StaticMap.Create([], Tiles));
        Assert.Null(StaticMap.Create([("1", 40.7, -74.0, false)], ""));
        Assert.Null(StaticMap.Create([("1", 89.9, -74.0, false)], Tiles)); // outside Web Mercator
    }

    [Theory]
    [InlineData("/provider/123/raw.json")]
    [InlineData("/provider/123/raw.csv")]
    [InlineData("/provider/123")]
    [InlineData("/group/123")]
    [InlineData("/group/123/raw.json")]
    [InlineData("/group/123/clinicians.csv")]
    [InlineData("/facility/12-34")]
    [InlineData("/facility/12/raw.json")]
    [InlineData("/facility/1234567/clinicians.csv")]
    public async Task A_malformed_npi_is_not_found_without_the_database(string url)
    {
        await using var factory = new Factory();
        var response = await factory.CreateClient().GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
