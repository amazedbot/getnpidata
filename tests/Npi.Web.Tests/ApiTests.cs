using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Npi.Web.Api;

namespace Npi.Web.Tests;

/// <summary>
/// /api/v1 behaviour that needs no database: validation problems, 404s, the API-key switch, rate limiting,
/// CORS and the OpenAPI document. The connection string points at a closed port, so any test that reached
/// MySQL would fail instead of passing by accident.
/// </summary>
public class ApiTests
{
    private sealed class ApiFactory(IReadOnlyDictionary<string, string>? settings = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing"); // no user-secrets
            builder.UseSetting("ConnectionStrings:RemoteMySql", "Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;Connection Timeout=1");
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }
        }
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.Clone();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Search_without_filters_is_a_validation_problem()
    {
        await using var factory = new ApiFactory();
        var problem = await ProblemAsync(await factory.CreateClient().GetAsync("/api/v1/providers", Ct), HttpStatusCode.BadRequest);
        Assert.True(problem.GetProperty("errors").TryGetProperty("filter", out _));
    }

    [Theory]
    [InlineData("/api/v1/providers?radius=abc&zip=11701", "radius")]
    [InlineData("/api/v1/providers?zip=117", "zip")]
    [InlineData("/api/v1/providers?state=NY&entityType=3", "entityType")]
    [InlineData("/api/v1/providers?state=NY&pageSize=500", "pageSize")]
    [InlineData("/api/v1/providers.csv?npi=12", "npi")]
    [InlineData("/export.csv?zip=117", "zip")]
    public async Task Errors_are_keyed_by_query_parameter_name(string url, string parameter)
    {
        await using var factory = new ApiFactory();
        var problem = await ProblemAsync(await factory.CreateClient().GetAsync(url, Ct), HttpStatusCode.BadRequest);
        Assert.True(problem.GetProperty("errors").TryGetProperty(parameter, out _), problem.ToString());
    }

    [Fact]
    public async Task Malformed_npi_is_not_found()
    {
        await using var factory = new ApiFactory();
        var problem = await ProblemAsync(await factory.CreateClient().GetAsync("/api/v1/providers/123", Ct), HttpStatusCode.NotFound);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Unknown_api_route_is_a_problem_not_an_empty_404()
    {
        await using var factory = new ApiFactory();
        await ProblemAsync(await factory.CreateClient().GetAsync("/api/v1/nothing-here", Ct), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Api_key_is_required_only_when_switched_on()
    {
        await using var open = new ApiFactory();
        Assert.Equal(HttpStatusCode.NotFound, (await open.CreateClient().GetAsync("/api/v1/providers/123", Ct)).StatusCode);

        await using var locked = new ApiFactory(new Dictionary<string, string> { ["Api:RequireKey"] = "true", ["Api:Keys:0"] = "test-key-1" });
        var client = locked.CreateClient();
        await ProblemAsync(await client.GetAsync("/api/v1/providers/123", Ct), HttpStatusCode.Unauthorized);

        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/v1/providers/123") { Headers = { { ApiKeyFilter.HeaderName, "test-key-2" } } };
        await ProblemAsync(await client.SendAsync(wrong, Ct), HttpStatusCode.Unauthorized);

        using var right = new HttpRequestMessage(HttpMethod.Get, "/api/v1/providers/123") { Headers = { { ApiKeyFilter.HeaderName, "test-key-1" } } };
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(right, Ct)).StatusCode);

        // The pages never need a key.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
    }

    [Fact]
    public async Task Requiring_a_key_without_keys_fails_at_startup()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string> { ["Api:RequireKey"] = "true" });
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Rate_limit_answers_429_with_retry_after()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string> { ["Api:PermitLimit"] = "2", ["Api:WindowSeconds"] = "3600" });
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/providers/123", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/providers/123", Ct)).StatusCode);

        var third = await client.GetAsync("/api/v1/providers/123", Ct);
        await ProblemAsync(third, HttpStatusCode.TooManyRequests);
        Assert.True(third.Headers.RetryAfter?.Delta > TimeSpan.Zero);

        // The website is not rate-limited.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
    }

    [Fact]
    public async Task Cors_allows_any_origin_for_get()
    {
        await using var factory = new ApiFactory();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/providers/123") { Headers = { { "Origin", "https://example.org" } } };
        var response = await factory.CreateClient().SendAsync(request, Ct);
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task OpenApi_document_lists_the_api_and_its_search_parameters()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var paths = doc.RootElement.GetProperty("paths");

        string[] expected =
        [
            "/api/v1/providers", "/api/v1/providers.csv", "/api/v1/providers/{npi}", "/api/v1/taxonomy/classifications",
            "/api/v1/taxonomy/classifications/{classification}/specializations", "/api/v1/states", "/api/v1/states/{state}/counties",
            "/api/v1/counties/{fips}", "/api/v1/meta",
        ];
        Assert.Equal(expected.Order(), paths.EnumerateObject().Select(p => p.Name).Order());

        var parameters = paths.GetProperty("/api/v1/providers").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(Search.SearchQueryString.Parameters.Select(p => p.Name), parameters);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/index.html", Ct)).StatusCode);
    }
}
