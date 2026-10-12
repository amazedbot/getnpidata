using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Npi.Web.Tests;

/// <summary>Application Insights (docs/PRODUCT_PLAN.md step 1.9): off without a connection string, and nothing identifying is sent.</summary>
public class TelemetryTests
{
    private sealed class Factory(string? connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:RemoteMySql", "Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;Connection Timeout=1");
            if (connectionString is not null)
            {
                builder.UseSetting("ApplicationInsights:ConnectionString", connectionString);
            }
        }
    }

    [Fact]
    public void It_is_on_only_with_a_connection_string()
    {
        IConfiguration Config(params (string, string?)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build();

        Assert.Null(Telemetry.ConnectionString(Config()));
        Assert.Null(Telemetry.ConnectionString(Config(("ApplicationInsights:ConnectionString", " "))));
        Assert.Equal("a", Telemetry.ConnectionString(Config(("APPLICATIONINSIGHTS_CONNECTION_STRING", "a"), ("ApplicationInsights:ConnectionString", "b"))));
        Assert.Equal("b", Telemetry.ConnectionString(Config(("ApplicationInsights:ConnectionString", "b"))));
    }

    [Fact]
    public void Query_strings_and_client_details_are_removed()
    {
        using var activity = new Activity("request");
        activity.SetTag("url.path", "/search");
        activity.SetTag("url.query", "?lastName=smith&zip=11702");
        activity.SetTag("client.address", "203.0.113.7");
        activity.SetTag("user_agent.original", "Mozilla/5.0");

        Telemetry.Scrub(activity);

        Assert.Equal([("url.path", "/search")], activity.TagObjects.Select(t => (t.Key, t.Value as string)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:1/")]
    public async Task The_site_runs_with_telemetry_off_and_on(string? connectionString)
    {
        await using var factory = new Factory(connectionString);
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/providers", TestContext.Current.CancellationToken)).StatusCode);
    }
}
