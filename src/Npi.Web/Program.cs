using Microsoft.AspNetCore.Mvc;
using Npi.Core.Search;
using Npi.Web;
using Npi.Web.Api;
using Npi.Web.Search;

var builder = WebApplication.CreateBuilder(args);

// Git-ignored local overrides (connection strings for dev); production uses App Service configuration.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Azure Application Insights (docs/PRODUCT_PLAN.md decision 7, step 1.9), described on /privacy. Off unless a connection
// string is configured (App Service: APPLICATIONINSIGHTS_CONNECTION_STRING). Query strings (names, ZIPs and other search
// terms) and client addresses are removed before anything is sent.
if (Telemetry.ConnectionString(builder.Configuration) is { } insights)
{
    Telemetry.Add(builder.Services, insights);
}

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
builder.Services.AddSingleton(_ => new OrganizationService(connectionString));
builder.Services.AddSingleton(_ => new GeographyCatalog(connectionString));
builder.Services.AddSingleton(_ => new CityCatalog(connectionString));
builder.Services.AddSingleton(_ => new AreaService(connectionString));
builder.Services.AddSingleton(_ => new AreaProfileService(connectionString));
builder.Services.AddSingleton(_ => new MapService(connectionString));
builder.Services.AddSingleton(sp => new CareFinder(connectionString, sp.GetRequiredService<SearchService>()));
builder.Services.AddSingleton(_ => new CompanyService(connectionString));
builder.Services.AddSingleton(sp => new IndustryService(connectionString, sp.GetRequiredService<CompanyService>(), sp.GetRequiredService<ProductService>()));
builder.Services.AddSingleton(_ => new ProductService(connectionString));
// Facilities & hospitals and Groups & organizations (docs/PRODUCT_PLAN.md step 3.6): lists kept in memory until the data changes,
// built once after the site starts (not in tests, which have no database).
builder.Services.AddSingleton(_ => new FacilityDirectory(connectionString));
builder.Services.AddSingleton(_ => new OrganizationDirectory(connectionString));
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<DirectoryWarmup>();
}

builder.Services.AddSingleton(sp => new SearchVocabularyCatalog(connectionString, sp.GetRequiredService<TaxonomyCatalog>()));
builder.Services.AddSingleton(sp => new SuggestService(connectionString, sp.GetRequiredService<SearchVocabularyCatalog>()));
builder.Services.Configure<MapOptions>(builder.Configuration.GetSection(MapOptions.Section));
// /search is the search page with the list builder open (docs/PRODUCT_PLAN.md step 1.3); / is the home page.
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AddPageRoute("/Index", "search");
    o.Conventions.AddPageRoute("/Verify", "lookup"); // the bulk lookup's first URL (docs/PRODUCT_PLAN.md §2.1)
    o.Conventions.AddPageRoute("/Facilities", "hospitals"); // the facilities list on hospitals (step 3.6)
    o.Conventions.AddPageRoute("/Organizations", "groups"); // the groups & organizations page on Medicare groups
});
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

// The search box in the site header (docs/PRODUCT_PLAN.md step 1.2): an NPI opens its profile, other text searches names.
app.MapGet("/go", ([FromQuery] string? q) => Results.Redirect(QuickSearch.Target(q))).ExcludeFromDescription();

// REST API (CLAUDE.md §7 Stage 5) and its docs: OpenAPI at /openapi/v1.json, Swagger UI at /swagger.
app.MapApi();
app.MapOpenApi();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint(ApiOpenApi.DocumentUrl, "getnpidata API v1");
    o.RoutePrefix = "swagger";
    o.DocumentTitle = "getnpidata API";
});

// A product's paid prescribers as CSV, every row (Stage 5.5 item 19, part 4).
app.MapGet("/product/{slug}/prescribers.csv", (string slug, HttpContext http, ProductService products, CancellationToken ct) =>
    Npi.Web.Api.ApiEndpoints.PrescribersCsvAsync(slug, http, products, ct));

// Streamed CSV of every match, no row cap (CLAUDE.md §7 Stage 4). Same query string as the search page; with no filter, the
// loader's ready-made file of every active provider (also as the gzipped file itself).
app.MapGet("/export.csv", CsvExport.HandleSiteAsync);
app.MapGet("/export/all.csv.gz", CsvExport.HandleFullFile);

// The search page's export panel (docs/PRODUCT_PLAN.md step 1.3): CSV or Excel, with a column set.
app.MapGet("/export", CsvExport.HandleExportAsync);
app.MapGet("/export.xlsx", CsvExport.HandleXlsxAsync);

// A profile's "Download raw data" (docs/PRODUCT_PLAN.md step 1.4): the whole record as JSON (the API's shape), or its
// summary row as CSV. Not rate-limited like /api/v1: one record at a time, from a page.
app.MapGet("/provider/{npi}/raw.json", async (string npi, HttpContext http, ProviderDetailService details, CancellationToken ct) =>
{
    if (await details.GetAsync(npi, ct) is not { } provider)
    {
        return Results.NotFound();
    }

    http.Response.Headers.ContentDisposition = $"attachment; filename=\"npi_{provider.Npi}.json\"";
    return Results.Json(provider);
});

app.MapGet("/provider/{npi}/raw.csv", async (string npi, HttpContext http, SearchService search, CancellationToken ct) =>
{
    if (!Npi.Core.InputFormats.IsNpi(npi) || (await search.LookupAsync([npi], ct)) is not [{ Provider: not null } row])
    {
        return Results.NotFound();
    }

    var provider = row.Provider!;

    http.Response.ContentType = "text/csv; charset=utf-8";
    http.Response.Headers.ContentDisposition = $"attachment; filename=\"npi_{provider.Npi}.csv\"";
    await ProviderCsv.WriteAsync(new[] { provider }.ToAsyncEnumerable(), http.Response.Body, ct);
    return Results.Empty;
});

// Group practice and facility profiles (step 1.5): the record as JSON, and every clinician as CSV (the summary columns).
// Companies & products (docs/PRODUCT_PLAN.md Phase 9, owner 2026-10-12): every company or product as CSV, and each one's full record.
app.MapGet("/companies.csv", IndustryEndpoints.CompaniesCsvAsync);
// Facilities & hospitals, Groups & organizations (step 3.6): the lists as CSV, the same filters as the pages and /api/v1.
app.MapGet("/facilities.csv", DirectoryEndpoints.FacilitiesCsvAsync);
app.MapGet("/groups.csv", DirectoryEndpoints.GroupsCsvAsync);
app.MapGet("/products.csv", IndustryEndpoints.ProductsCsvAsync);
app.MapGet("/company/{id}/raw.json", async (string id, HttpContext http, CompanyService companies, CancellationToken ct) =>
{
    if (await companies.GetAsync(id, ct) is not { } company)
    {
        return Results.NotFound();
    }

    http.Response.Headers.ContentDisposition = $"attachment; filename=\"company_{company.Id}.json\"";
    return Results.Json(company);
});
app.MapGet("/product/{slug}/raw.json", async (string slug, HttpContext http, ProductService products, CancellationToken ct) =>
{
    if (await products.GetAsync(slug, ct) is not { } product)
    {
        return Results.NotFound();
    }

    http.Response.Headers.ContentDisposition = $"attachment; filename=\"product_{product.Slug}.json\"";
    return Results.Json(product);
});

app.MapGet("/group/{pacId}/raw.json", async (string pacId, HttpContext http, OrganizationService organizations, CancellationToken ct) =>
{
    if (await organizations.GetGroupAsync(pacId, 1, ct) is not { } group)
    {
        return Results.NotFound();
    }

    http.Response.Headers.ContentDisposition = $"attachment; filename=\"group_{group.PacId}.json\"";
    return Results.Json(group);
});

app.MapGet("/facility/{ccn}/raw.json", async (string ccn, HttpContext http, OrganizationService organizations, CancellationToken ct) =>
{
    if (await organizations.GetFacilityAsync(ccn, 1, ct) is not { } facility)
    {
        return Results.NotFound();
    }

    http.Response.Headers.ContentDisposition = $"attachment; filename=\"facility_{facility.Facility.Ccn}.json\"";
    return Results.Json(facility);
});

app.MapGet("/group/{pacId}/clinicians.csv", (string pacId, HttpContext http, OrganizationService organizations, SearchService search, CancellationToken ct) =>
    CliniciansCsvAsync(pacId, facility: false, http, organizations, search, ct));
app.MapGet("/facility/{ccn}/clinicians.csv", (string ccn, HttpContext http, OrganizationService organizations, SearchService search, CancellationToken ct) =>
    CliniciansCsvAsync(ccn, facility: true, http, organizations, search, ct));

// Type-ahead for the search box (step 1.3).
app.MapGet("/suggest", async ([FromQuery] string? q, SuggestService suggestions, CancellationToken ct) =>
    Results.Ok(await suggestions.SuggestAsync(q is { Length: > 100 } ? q[..100] : q, ct)));

// Dependent dropdowns on the search page.
app.MapGet("/lookup/subcategories", ([FromQuery] string? category) =>
    Results.Ok(TaxonomyCatalog.GetSubcategories(category) ?? []));

app.MapGet("/lookup/specializations", async ([FromQuery] string? classification, TaxonomyCatalog taxonomy, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(classification) ? Results.Ok(Array.Empty<string>()) : Results.Ok(await taxonomy.GetSpecializationsAsync(classification, ct)));

app.MapGet("/lookup/counties", async ([FromQuery] string? state, GeographyCatalog geography, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(state) ? Results.Ok(Array.Empty<CountyInfo>()) : Results.Ok(await geography.GetCountiesAsync(state, ct)));

app.MapGet("/lookup/cities", async ([FromQuery] string? state, [FromQuery] string? county, CityCatalog cities, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(state) || state.Trim().Length != 2
        ? Results.Ok(Array.Empty<string>())
        : Results.Ok(await cities.GetCitiesAsync(state.Trim(), county is { Length: 5 } fips && fips.All(char.IsAsciiDigit) ? fips : null, ct)));

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

// Every active clinician of a group or facility as CSV, looked up in batches (SearchService.MaxLookupBatch) and streamed.
static async Task<IResult> CliniciansCsvAsync(string id, bool facility, HttpContext http, OrganizationService organizations, SearchService search,
    CancellationToken ct)
{
    var npis = await organizations.GetClinicianNpisAsync(id, facility, ct);
    if (npis.Count == 0)
    {
        return Results.NotFound();
    }

    async IAsyncEnumerable<ProviderSummary> Rows()
    {
        foreach (var batch in npis.Chunk(SearchService.MaxLookupBatch))
        {
            foreach (var row in await search.LookupAsync(batch, ct))
            {
                if (row.Provider is { } provider)
                {
                    yield return provider;
                }
            }
        }
    }

    http.Response.ContentType = "text/csv; charset=utf-8";
    http.Response.Headers.ContentDisposition = $"attachment; filename=\"{(facility ? "facility" : "group")}_{id.ToUpperInvariant()}_clinicians.csv\"";
    await ProviderCsv.WriteAsync(Rows(), http.Response.Body, ct);
    return Results.Empty;
}

app.Run();

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;
