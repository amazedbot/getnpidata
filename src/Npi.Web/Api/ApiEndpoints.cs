using Microsoft.AspNetCore.Http.HttpResults;
using Npi.Core.Search;
using Npi.Web.Search;

namespace Npi.Web.Api;

/// <summary>
/// The public REST API, /api/v1 (CLAUDE.md §7 Stage 5). It uses the same Npi.Core services and query
/// parameters as the search page, so the page, the CSV and the API always return the same providers.
/// </summary>
public static class ApiEndpoints
{
    public const string RateLimitPolicy = "api";
    public const string CorsPolicy = "api";
    private const string CachePolicy = "api-lookups";

    public static void AddApi(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<ApiOptions>().BindConfiguration(ApiOptions.Section)
            .Validate(o => !o.RequireKey || o.Keys.Any(k => !string.IsNullOrWhiteSpace(k)), "Api:RequireKey is true but no Api:Keys are configured.")
            .Validate(o => o.PermitLimit > 0 && o.WindowSeconds > 0, "Api:PermitLimit and Api:WindowSeconds must be positive.")
            .ValidateOnStart();
        builder.Services.AddSingleton<ApiKeyFilter>();
        builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p.AllowAnyOrigin().WithMethods("GET").AllowAnyHeader()));
        builder.Services.AddOutputCache(o => o.AddPolicy(CachePolicy, p => p.Expire(TimeSpan.FromMinutes(10))));
        builder.Services.AddApiRateLimiting();
        builder.Services.AddApiOpenApi();
    }

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1")
            .RequireCors(CorsPolicy)
            .RequireRateLimiting(RateLimitPolicy)
            .AddEndpointFilter<ApiKeyFilter>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        api.MapGet("/providers", SearchAsync)
            .WithName("SearchProviders").WithTags("Providers").WithSearchParameters()
            .WithSummary("Search providers")
            .WithDescription("One page of matching providers with the total count. At least one filter is required; deactivated NPIs are never returned.");

        api.MapGet("/providers.csv", CsvExport.HandleAsync)
            .WithName("ExportProvidersCsv").WithTags("Providers").WithSearchParameters()
            .WithSummary("Download every match as CSV")
            .WithDescription("Same filters as /providers (paging ignored), streamed with no row cap. UTF-8 with BOM.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv").ProducesValidationProblem();

        api.MapGet("/providers/{npi}", GetProviderAsync)
            .WithName("GetProvider").WithTags("Providers")
            .WithSummary("One provider")
            .WithDescription("All taxonomies with licenses, all practice locations and other names. 404 for unknown or deactivated NPIs.");

        api.MapGet("/taxonomy/classifications", async (TaxonomyCatalog taxonomy, CancellationToken ct) =>
                TypedResults.Ok(await taxonomy.GetClassificationsAsync(ct)))
            .WithName("ListClassifications").WithTags("Lookups").WithSummary("NUCC classifications").CacheOutput(CachePolicy);

        api.MapGet("/taxonomy/classifications/{classification}/specializations", GetSpecializationsAsync)
            .WithName("ListSpecializations").WithTags("Lookups").WithSummary("Specializations of one classification").CacheOutput(CachePolicy);

        api.MapGet("/states", async (GeographyCatalog geography, CancellationToken ct) =>
                TypedResults.Ok(await geography.GetStatesAsync(ct)))
            .WithName("ListStates").WithTags("Lookups").WithSummary("States and territories with counties").CacheOutput(CachePolicy);

        api.MapGet("/states/{state}/counties", GetCountiesAsync)
            .WithName("ListCounties").WithTags("Lookups").WithSummary("Counties of one state, with FIPS codes").CacheOutput(CachePolicy);

        api.MapGet("/meta", GetMetaAsync)
            .WithName("GetMeta").WithTags("Meta").WithSummary("Data versions")
            .WithDescription("As-of date, source files and reference data versions of the data being served.").CacheOutput(CachePolicy);
    }

    private static async Task<Results<Ok<SearchResult>, ValidationProblem>> SearchAsync(HttpContext http, SearchService search, CancellationToken ct)
    {
        var (filter, errors) = SearchQueryString.Parse(http.Request.Query);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(SearchQueryString.ByParameterName(errors));
        }

        try
        {
            SearchValidation.Normalize(filter); // cheap checks first, before any database work
            return TypedResults.Ok(await search.SearchAsync(filter, ct));
        }
        catch (SearchValidationException ex)
        {
            return TypedResults.ValidationProblem(SearchQueryString.ByParameterName(ex.Errors));
        }
    }

    private static async Task<Results<Ok<ProviderDetail>, ProblemHttpResult>> GetProviderAsync(string npi, ProviderDetailService details, CancellationToken ct) =>
        await details.GetAsync(npi, ct) is { } provider
            ? TypedResults.Ok(provider)
            : NotFound($"No active provider with NPI '{npi}'.");

    private static async Task<Results<Ok<IReadOnlyList<string>>, ProblemHttpResult>> GetSpecializationsAsync(
        string classification, TaxonomyCatalog taxonomy, CancellationToken ct)
    {
        var known = (await taxonomy.GetClassificationsAsync(ct)).Contains(classification, StringComparer.OrdinalIgnoreCase);
        return known
            ? TypedResults.Ok(await taxonomy.GetSpecializationsAsync(classification, ct))
            : NotFound($"Unknown classification '{classification}'.");
    }

    private static async Task<Results<Ok<IReadOnlyList<CountyInfo>>, ProblemHttpResult>> GetCountiesAsync(
        string state, GeographyCatalog geography, CancellationToken ct)
    {
        var counties = await geography.GetCountiesAsync(state, ct);
        return counties.Count > 0 ? TypedResults.Ok(counties) : NotFound($"Unknown state '{state}'.");
    }

    private static async Task<Results<Ok<ApiMeta>, ProblemHttpResult>> GetMetaAsync(GeographyCatalog geography, CancellationToken ct) =>
        await geography.GetDataVersionAsync(ct) is { } version
            ? TypedResults.Ok(ApiMeta.From(version))
            : TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No data loaded yet");

    private static ProblemHttpResult NotFound(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: detail);
}

/// <summary>GET /api/v1/meta. The loader stores projected_at/published_at in UTC.</summary>
public sealed record ApiMeta(
    DateOnly? DataAsOf, string? MonthlyFile, string? WeeklyFile, string? DeactivationFile,
    string? NuccVersion, string? HudVersion, string? CensusVersion, int ProviderCount, DateTimeOffset ProjectedAt, DateTimeOffset? PublishedAt)
{
    public static ApiMeta From(DataVersion v) => new(
        v.AsOfDate is { } asOf ? DateOnly.FromDateTime(asOf) : null,
        v.MonthlyFile, v.WeeklyFile, v.DeactivationFile, v.NuccVersion, v.HudVersion, v.CensusVersion, v.ProviderCount,
        Utc(v.ProjectedAt), v.PublishedAt is { } published ? Utc(published) : null);

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
