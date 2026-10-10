using Microsoft.AspNetCore.Mvc;
using Npi.Core.Search;
using Npi.Web;
using Npi.Web.Api;
using Npi.Web.Search;

var builder = WebApplication.CreateBuilder(args);

// Git-ignored local overrides (connection strings for dev); production uses App Service configuration.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// The site reads the search projection. Production: App Service connection string "RemoteMySql"
// (CLAUDE.md §7 Stage 6.2). Locally it points at workplace via user-secrets (read-only login, §8).
var connectionString = builder.Configuration.GetConnectionString("RemoteMySql");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'RemoteMySql' is not set. Locally: dotnet user-secrets --project src/Npi.Web set \"ConnectionStrings:RemoteMySql\" \"server=…;database=…;user=…;password=…\"");
}

builder.Services.AddSingleton(_ => new TaxonomyCatalog(connectionString));
builder.Services.AddSingleton(_ => new CredentialCatalog(connectionString));
builder.Services.AddSingleton(sp => new SearchService(connectionString, sp.GetRequiredService<TaxonomyCatalog>(), sp.GetRequiredService<CredentialCatalog>()));
builder.Services.AddSingleton(_ => new ProviderDetailService(connectionString));
builder.Services.AddSingleton(_ => new GeographyCatalog(connectionString));
builder.Services.AddSingleton(_ => new AreaService(connectionString));
builder.Services.AddSingleton(_ => new MapService(connectionString));
builder.Services.AddSingleton(_ => new CompanyService(connectionString));
builder.Services.Configure<MapOptions>(builder.Configuration.GetSection(MapOptions.Section));
builder.Services.AddRazorPages();
builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
builder.AddApi();

var app = builder.Build();

static bool IsApi(HttpContext http) => http.Request.Path.StartsWithSegments("/api");

// The API answers errors with RFC 7807 problem details, including empty 404/405 responses; the pages keep their HTML error page.
app.UseWhen(IsApi, api =>
{
    api.UseExceptionHandler();
    api.UseStatusCodePages();
});
if (!app.Environment.IsDevelopment())
{
    app.UseWhen(http => !IsApi(http), pages => pages.UseExceptionHandler("/Error"));
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.UseOutputCache();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapHealthChecks("/health");

// REST API (CLAUDE.md §7 Stage 5) and its docs: OpenAPI at /openapi/v1.json, Swagger UI at /swagger.
app.MapApi();
app.MapOpenApi();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint(ApiOpenApi.DocumentUrl, "getnpidata API v1");
    o.RoutePrefix = "swagger";
    o.DocumentTitle = "getnpidata API";
});

// Streamed CSV of every match, no row cap (CLAUDE.md §7 Stage 4). Same query string as the search page.
app.MapGet("/export.csv", CsvExport.HandleAsync);

// Dependent dropdowns on the search page.
app.MapGet("/lookup/specializations", async ([FromQuery] string? classification, TaxonomyCatalog taxonomy, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(classification) ? Results.Ok(Array.Empty<string>()) : Results.Ok(await taxonomy.GetSpecializationsAsync(classification, ct)));

app.MapGet("/lookup/counties", async ([FromQuery] string? state, GeographyCatalog geography, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(state) ? Results.Ok(Array.Empty<CountyInfo>()) : Results.Ok(await geography.GetCountiesAsync(state, ct)));

// Map search (CLAUDE.md §7 Stage 5.5 item 10): the providers inside bbox=west,south,east,north that match the search
// filters (location filters are replaced by the area), nearest the centre first, at most SearchService.MaxAreaResults.
app.MapGet("/map/search", async (HttpContext http, SearchService search, CancellationToken ct) =>
{
    var (filter, errors) = SearchQueryString.Parse(http.Request.Query);
    var bounds = MapBounds.Parse(http.Request.Query["bbox"]);
    if (bounds is null)
    {
        errors["bbox"] = ["Send bbox=west,south,east,north in degrees."];
    }

    if (errors.Count > 0)
    {
        return Results.ValidationProblem(SearchQueryString.ByParameterName(errors));
    }

    try
    {
        return Results.Ok(await search.SearchAreaAsync(filter, bounds!, ct));
    }
    catch (SearchValidationException ex)
    {
        return Results.ValidationProblem(SearchQueryString.ByParameterName(ex.Errors));
    }
});

app.Run();

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;
