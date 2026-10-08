using Microsoft.AspNetCore.Mvc;
using Npi.Core.Search;
using Npi.Web.Search;

var builder = WebApplication.CreateBuilder(args);

// Git-ignored local overrides (connection strings for dev); production uses App Service configuration.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// The site reads the search projection. Production: App Service connection string "RemoteMySql"
// (CLAUDE.md §7 Stage 6.2). Locally it can point at npi_test via user-secrets.
var connectionString = builder.Configuration.GetConnectionString("RemoteMySql");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'RemoteMySql' is not set. Locally: dotnet user-secrets --project src/Npi.Web set \"ConnectionStrings:RemoteMySql\" \"server=…;database=…;user=…;password=…\"");
}

builder.Services.AddSingleton(_ => new TaxonomyCatalog(connectionString));
builder.Services.AddSingleton(sp => new SearchService(connectionString, sp.GetRequiredService<TaxonomyCatalog>()));
builder.Services.AddSingleton(_ => new ProviderDetailService(connectionString));
builder.Services.AddSingleton(_ => new GeographyCatalog(connectionString));
builder.Services.AddRazorPages();
builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapHealthChecks("/health");

// Streamed CSV of every match, no row cap (CLAUDE.md §7 Stage 4). Same query string as the search page.
app.MapGet("/export.csv", async (HttpContext http, SearchService search, CancellationToken ct) =>
{
    var (filter, errors) = SearchQueryString.Parse(http.Request.Query);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    try
    {
        // Validate before the response starts, so a bad search is a 400, not a broken download.
        SearchValidation.Normalize(filter with { Page = 1, PageSize = SearchFilter.DefaultPageSize });
    }
    catch (SearchValidationException ex)
    {
        return Results.ValidationProblem(ex.Errors.ToDictionary(e => e.Key, e => e.Value));
    }

    http.Response.ContentType = "text/csv; charset=utf-8";
    http.Response.Headers.ContentDisposition = $"attachment; filename=\"npi_search_{DateTime.UtcNow:yyyyMMdd}.csv\"";
    await ProviderCsv.WriteAsync(search.SearchAllAsync(filter with { Page = 1, PageSize = SearchFilter.DefaultPageSize }, ct), http.Response.Body, ct);
    return Results.Empty;
});

// Dependent dropdowns on the search page.
app.MapGet("/lookup/specializations", async ([FromQuery] string? classification, TaxonomyCatalog taxonomy, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(classification) ? Results.Ok(Array.Empty<string>()) : Results.Ok(await taxonomy.GetSpecializationsAsync(classification, ct)));

app.MapGet("/lookup/counties", async ([FromQuery] string? state, GeographyCatalog geography, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(state) ? Results.Ok(Array.Empty<CountyInfo>()) : Results.Ok(await geography.GetCountiesAsync(state, ct)));

app.Run();

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;
