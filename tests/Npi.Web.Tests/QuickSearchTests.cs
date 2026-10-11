using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npi.Web.Search;

namespace Npi.Web.Tests;

/// <summary>The header search box (docs/PRODUCT_PLAN.md step 1.2): NPIs open profiles, other text searches names.</summary>
public class QuickSearchTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("  ", "/")]
    [InlineData("1234567893", "/provider/1234567893")]
    [InlineData(" 1234 567 893 ", "/provider/1234567893")]
    [InlineData("123-456-7893", "/provider/1234567893")]
    [InlineData("12345", "/?nameMatch=similar&lastName=12345")]
    [InlineData("smith", "/?nameMatch=similar&lastName=smith")]
    [InlineData("John Smith", "/?nameMatch=similar&firstName=John&lastName=Smith")]
    [InlineData("Mary Ann O'Brien", "/?nameMatch=similar&firstName=Mary%20Ann&lastName=O%27Brien")]
    [InlineData("Stony Brook University Hospital", "/?nameMatch=similar&orgName=Stony%20Brook%20University%20Hospital")]
    [InlineData("Shore Point Chiropractic, PC", "/?nameMatch=similar&orgName=Shore%20Point%20Chiropractic,%20PC")]
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
    }
}
