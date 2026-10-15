using System.Net;
using System.Net.Http.Json;
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
    public async Task A_bad_search_is_a_validation_problem()
    {
        await using var factory = new ApiFactory();
        var problem = await ProblemAsync(await factory.CreateClient().GetAsync("/api/v1/providers?npi=123", Ct), HttpStatusCode.BadRequest);
        Assert.True(problem.GetProperty("errors").TryGetProperty("npi", out _));
    }

    [Fact]
    public async Task An_export_with_no_filter_is_the_ready_made_file()
    {
        var folder = Directory.CreateTempSubdirectory("npi-web-export-").FullName;
        try
        {
            var csv = '﻿' + "NPI,Name" + "\r\n" + "1000000004,\"SMITH, ANN\"" + "\r\n";
            await using (var gz = new System.IO.Compression.GZipStream(File.Create(Path.Combine(folder, Npi.Core.Search.FullExport.FileName)),
                             System.IO.Compression.CompressionLevel.Fastest))
            {
                await gz.WriteAsync(System.Text.Encoding.UTF8.GetBytes(csv), Ct);
            }

            await File.WriteAllTextAsync(Path.Combine(folder, Npi.Core.Search.FullExport.InfoFileName),
                """{"asOf":"2026-10-11","rows":1,"builtAt":"2026-10-14T20:00:00Z","bytes":40}""", Ct);
            await using var factory = new ApiFactory(new Dictionary<string, string> { ["FullExport:Folder"] = folder });
            using var client = factory.CreateClient();

            // Without Accept-Encoding: unpacked on the way out.
            using var plain = new HttpRequestMessage(HttpMethod.Get, "/export.csv");
            var response = await client.SendAsync(plain, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("npi_all_providers_20261011.csv", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
            Assert.Equal(csv, System.Text.Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(Ct)));

            // With it: sent as stored. The API's CSV is the same file; a filter or another column set streams the search instead.
            using var gzipped = new HttpRequestMessage(HttpMethod.Get, "/api/v1/providers.csv");
            gzipped.Headers.AcceptEncoding.ParseAdd("gzip");
            var api = await client.SendAsync(gzipped, Ct);
            Assert.Equal(["gzip"], api.Content.Headers.ContentEncoding);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/export/all.csv.gz", Ct)).StatusCode);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_sites_exports_send_a_bad_search_back_to_the_search_page()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var csv = await client.GetAsync("/export.csv?npi=123", Ct);
        Assert.Equal(HttpStatusCode.Redirect, csv.StatusCode);
        Assert.Equal("/search?npi=123", csv.Headers.Location!.OriginalString);
        var panel = await client.GetAsync("/export?npi=123&columns=contact&format=xlsx", Ct);
        Assert.Equal("/search?npi=123", panel.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/providers.csv?npi=123", Ct)).StatusCode); // the API keeps its problem
    }

    [Theory]
    [InlineData("/api/v1/providers?radius=abc&zip=11701", "radius")]
    [InlineData("/api/v1/providers?zip=117", "zip")]
    [InlineData("/api/v1/providers?state=NY&entityType=3", "entityType")]
    [InlineData("/api/v1/providers?state=NY&pageSize=500", "pageSize")]
    [InlineData("/api/v1/providers.csv?npi=12", "npi")]
    [InlineData("/api/v1/companies?pageSize=500", "pageSize")]
    [InlineData("/api/v1/companies?page=0", "page")]
    [InlineData("/api/v1/products?kind=Pill", "kind")]
    [InlineData("/api/v1/products?pageSize=0", "pageSize")]
    [InlineData("/api/v1/products/eliquis/prescribers?sort=random", "sort")]
    [InlineData("/api/v1/products/eliquis/prescribers?pageSize=500", "pageSize")]
    public async Task Errors_are_keyed_by_query_parameter_name(string url, string parameter)
    {
        await using var factory = new ApiFactory();
        var problem = await ProblemAsync(await factory.CreateClient().GetAsync(url, Ct), HttpStatusCode.BadRequest);
        Assert.True(problem.GetProperty("errors").TryGetProperty(parameter, out _), problem.ToString());
    }

    [Fact]
    public async Task Lookup_validates_the_request_and_reports_invalid_npis()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var empty = await ProblemAsync(await client.PostAsync("/api/v1/providers/lookup", Json("""{"npis":[]}"""), Ct), HttpStatusCode.BadRequest);
        Assert.True(empty.GetProperty("errors").TryGetProperty("npis", out _));
        var many = "{\"npis\":[" + string.Join(",", Enumerable.Range(0, 1001).Select(_ => "\"1234567893\"")) + "]}";
        await ProblemAsync(await client.PostAsync("/api/v1/providers/lookup", Json(many), Ct), HttpStatusCode.BadRequest);

        // Invalid NPIs are answered without touching the database (the test server has none).
        var response = await client.PostAsync("/api/v1/providers/lookup", Json("""{"npis":["1234567890","12","1234567890"]}"""), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal([("1234567890", "invalid"), ("12", "invalid")],
            doc.RootElement.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("npi").GetString(), i.GetProperty("status").GetString())).ToList());
    }

    private static StringContent Json(string body) => new(body, System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task Malformed_npi_is_not_found()
    {
        await using var factory = new ApiFactory();
        var problem = await ProblemAsync(await factory.CreateClient().GetAsync("/api/v1/providers/123", Ct), HttpStatusCode.NotFound);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Malformed_company_id_is_not_found()
    {
        await using var factory = new ApiFactory();
        var problem = await ProblemAsync(await factory.CreateClient().GetAsync("/api/v1/companies/" + new string('9', 21), Ct), HttpStatusCode.NotFound);
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
    public async Task Specialty_categories_need_no_database()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var categories = await client.GetFromJsonAsync<string[]>("/api/v1/taxonomy/categories", Ct);
        Assert.Equal("Doctors (MD, DO)", categories![0]);
        var subcategories = await client.GetFromJsonAsync<string[]>("/api/v1/taxonomy/categories/dentists/subcategories", Ct);
        Assert.Contains("Orthodontists (braces)", subcategories!);
        Assert.Equal("Other dental specialists", subcategories![^1]);   // "Other …" last

        var unknown = await client.GetAsync("/api/v1/taxonomy/categories/nope/subcategories", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/problem+json", unknown.Content.Headers.ContentType?.MediaType);

        var dropdown = await client.GetFromJsonAsync<string[]>("/lookup/subcategories?category=" + Uri.EscapeDataString("Doctors (MD, DO)"), Ct);
        Assert.Contains("Heart (cardiology)", dropdown!);
        Assert.Empty((await client.GetFromJsonAsync<string[]>("/lookup/subcategories?category=nope", Ct))!);
    }

    [Theory]
    [InlineData("/api/v1/providers?subcategory=Medical&state=NY", "subcategory")]
    public async Task A_subcategory_needs_its_category(string url, string parameter)
    {
        await using var factory = new ApiFactory();
        var response = await factory.CreateClient().GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(parameter, out _));
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
            "/api/v1/providers", "/api/v1/providers.csv", "/api/v1/providers/lookup", "/api/v1/providers/lookup.csv", "/api/v1/providers/{npi}", "/api/v1/taxonomy/categories",
            "/api/v1/taxonomy/categories/{category}/subcategories", "/api/v1/taxonomy/classifications",
            "/api/v1/taxonomy/classifications/{classification}/specializations", "/api/v1/states", "/api/v1/states/{state}/counties", "/api/v1/states/{state}/cities",
            "/api/v1/counties/{fips}", "/api/v1/meta", "/api/v1/credentials", "/api/v1/companies", "/api/v1/companies.csv", "/api/v1/companies/{id}", "/api/v1/products", "/api/v1/products.csv", "/api/v1/products/{slug}", "/api/v1/products/{slug}/prescribers", "/api/v1/products/{slug}/prescribers.csv",
            "/api/v1/facilities", "/api/v1/facilities.csv", "/api/v1/groups", "/api/v1/groups.csv",
            "/api/v1/areas/us/history", "/api/v1/areas/{state}/history", "/api/v1/areas/{state}/{county}/history", "/api/v1/areas/map",
        ];
        Assert.Equal(expected.Order(), paths.EnumerateObject().Select(p => p.Name).Order());

        var parameters = paths.GetProperty("/api/v1/providers").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(Search.SearchQueryString.Parameters.Select(p => p.Name), parameters);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/index.html", Ct)).StatusCode);
    }
}
