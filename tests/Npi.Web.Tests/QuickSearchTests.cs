using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npi.Web.Search;

namespace Npi.Web.Tests;

/// <summary>The header search box (docs/PRODUCT_PLAN.md steps 1.2–1.3): NPIs open profiles, other text goes to the search page.</summary>
public class QuickSearchTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("  ", "/")]
    [InlineData("1234567893", "/provider/1234567893")]
    [InlineData(" 1234 567 893 ", "/provider/1234567893")]
    [InlineData("123-456-7893", "/provider/1234567893")]
    [InlineData("12345", "/search?q=12345")]
    [InlineData("John Smith", "/search?q=John%20Smith")]
    [InlineData("chiropractors in Suffolk County, NY", "/search?q=chiropractors%20in%20Suffolk%20County,%20NY")]
    public void Text_goes_to_a_profile_or_a_name_search(string? text, string target) => Assert.Equal(target, QuickSearch.Target(text));

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:RemoteMySql", "Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;Connection Timeout=1");
        }
    }

    [Fact]
    public async Task The_header_form_redirects_without_touching_the_database()
    {
        await using var factory = new Factory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/go?q=1234567893", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/provider/1234567893", response.Headers.Location?.ToString());

        // The search page sends a typed NPI to its profile before reading words (no vocabulary, so no database).
        var typed = await client.GetAsync("/search?q=1234%20567%20893", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, typed.StatusCode);
        Assert.Equal("/provider/1234567893", typed.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Exports_validate_before_the_download_starts()
    {
        await using var factory = new Factory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync("/export?format=xlsx&zip=123", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); // back to the search page, which says what's wrong
        Assert.Equal("/search?zip=123", response.Headers.Location!.OriginalString);
    }
}
