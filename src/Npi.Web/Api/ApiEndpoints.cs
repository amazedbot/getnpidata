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

        api.MapPost("/providers/lookup", LookupAsync)
            .WithName("LookupProviders").WithTags("Providers")
            .WithSummary("Look up many NPIs at once")
            .WithDescription($"Up to {SearchService.MaxLookupBatch} NPIs per request. Every requested NPI comes back once, in order, with status found, notFound (unknown or deactivated) or invalid (format or check digit).");

        api.MapPost("/providers/lookup.csv", LookupCsvAsync)
            .WithName("LookupProvidersCsv").WithTags("Providers")
            .WithSummary("Look up many NPIs at once, as CSV")
            .WithDescription("Same request as /providers/lookup; the summary columns plus Requested NPI and Lookup Status.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv").ProducesValidationProblem();

        api.MapGet("/providers/{npi}", GetProviderAsync)
            .WithName("GetProvider").WithTags("Providers")
            .WithSummary("One provider")
            .WithDescription("All taxonomies with licenses, all practice locations and other names. 404 for unknown or deactivated NPIs.");

        api.MapGet("/companies", SearchCompaniesAsync)
            .WithName("SearchCompanies").WithTags("Companies")
            .WithSummary("Companies that report to Open Payments")
            .WithDescription($"Drug and device makers and group purchasing organizations, largest payments (general + research, every published year) first. name matches any part of the company's name or other names, or its Open Payments ID; empty lists every company. pageSize 1–{CompanyService.MaxPageSize} (default 50).");

        api.MapGet("/companies/{id}", GetCompanyAsync)
            .WithName("GetCompany").WithTags("Companies")
            .WithSummary("One company")
            .WithDescription("Payments per program year, what the newest year's general payments were for and which products they named, the specialties and the active providers it paid the most. 404 for an unknown ID.");

        api.MapGet("/products", SearchProductsAsync)
            .WithName("SearchProducts").WithTags("Products")
            .WithSummary("Products named in Open Payments")
            .WithDescription($"Drugs, biologicals, devices and supplies named in the newest program year's general payments, largest first. name matches any part of the name (or the product's slug); kind is one of {string.Join(", ", ProductService.Kinds)}. pageSize 1–{ProductService.MaxPageSize} (default 50).");

        api.MapGet("/products/{slug}", GetProductAsync)
            .WithName("GetProduct").WithTags("Products")
            .WithSummary("One product")
            .WithDescription("The companies whose payments named it, the kinds of payment, the specialties and active providers paid most, and research naming it. 404 for an unknown slug.");

        api.MapGet("/products/{slug}/prescribers", GetPrescribersAsync)
            .WithName("GetProductPrescribers").WithTags("Products")
            .WithSummary("Paid providers who prescribe the product")
            .WithDescription($"The active providers paid in payments naming the drug who wrote Medicare Part D claims for it, with the amount paid and their claims, drug cost and patients. sort = {string.Join(" | ", ProductService.PrescriberSorts)} (default paid); pageSize 1–{ProductService.MaxPrescriberPageSize} (default {ProductService.PrescriberPageSize}). 404 for an unknown product or one without Part D prescribing.");

        api.MapGet("/products/{slug}/prescribers.csv", PrescribersCsvAsync)
            .WithName("ExportProductPrescribersCsv").WithTags("Products")
            .WithSummary("Paid providers who prescribe the product, as CSV")
            .WithDescription("Every row of /products/{slug}/prescribers, largest payments first, streamed. UTF-8 with BOM.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv");

        api.MapGet("/taxonomy/classifications", async (TaxonomyCatalog taxonomy, CancellationToken ct) =>
                TypedResults.Ok(await taxonomy.GetClassificationsAsync(ct)))
            .WithName("ListClassifications").WithTags("Lookups").WithSummary("NUCC classifications").CacheOutput(CachePolicy);

        api.MapGet("/taxonomy/classifications/{classification}/specializations", GetSpecializationsAsync)
            .WithName("ListSpecializations").WithTags("Lookups").WithSummary("Specializations of one classification").CacheOutput(CachePolicy);

        api.MapGet("/credentials", async (CredentialCatalog credentials, CancellationToken ct) =>
                TypedResults.Ok(await credentials.GetAllAsync(ct)))
            .WithName("ListCredentials").WithTags("Lookups")
            .WithSummary("Standardized credentials with provider counts")
            .WithDescription($"Credentials as standardized from NPPES's free text (\"M.D.\" and \"MD\" are both MD; \"MD, PhD\" counts for both), held by at least {CredentialCatalog.MinProviders} active providers, most common first. Pass one as the credential search parameter.")
            .CacheOutput(CachePolicy);

        api.MapGet("/states", async (GeographyCatalog geography, CancellationToken ct) =>
                TypedResults.Ok(await geography.GetStatesAsync(ct)))
            .WithName("ListStates").WithTags("Lookups").WithSummary("States and territories with counties").CacheOutput(CachePolicy);

        api.MapGet("/states/{state}/counties", GetCountiesAsync)
            .WithName("ListCounties").WithTags("Lookups").WithSummary("Counties of one state, with FIPS codes").CacheOutput(CachePolicy);

        api.MapGet("/counties/{fips}", GetCountyAsync)
            .WithName("GetCounty").WithTags("Lookups").WithSummary("County population and shortage areas")
            .WithDescription("Census population estimate and HRSA Health Professional Shortage Areas (primary care, dental, mental health) in force.")
            .CacheOutput(CachePolicy);

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

    private static Dictionary<string, string[]>? ValidateLookup(LookupRequest? request) =>
        request?.Npis is not { Count: > 0 } npis
            ? new() { ["npis"] = ["Send {\"npis\": [\"1234567893\", …]} with at least one NPI."] }
            : npis.Count > SearchService.MaxLookupBatch
                ? new() { ["npis"] = [$"At most {SearchService.MaxLookupBatch} NPIs per request."] }
                : null;

    private static async Task<Results<Ok<LookupResponse>, ValidationProblem>> LookupAsync(LookupRequest? request, SearchService search, CancellationToken ct) =>
        ValidateLookup(request) is { } errors
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(new LookupResponse(await search.LookupAsync(request!.Npis!, ct)));

    private static async Task<IResult> LookupCsvAsync(LookupRequest? request, HttpContext http, SearchService search, CancellationToken ct)
    {
        if (ValidateLookup(request) is { } errors)
        {
            return Results.ValidationProblem(errors);
        }

        var rows = await search.LookupAsync(request!.Npis!, ct);
        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"npi_lookup_{DateTime.UtcNow:yyyyMMdd}.csv\"";
        await ProviderCsv.WriteLookupAsync(rows.ToAsyncEnumerable(), http.Response.Body, ct);
        return Results.Empty;
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

    private static async Task<Results<Ok<CountyFacts>, ProblemHttpResult>> GetCountyAsync(string fips, AreaService areas, CancellationToken ct) =>
        await areas.GetCountyAsync(fips, ct) is { } county ? TypedResults.Ok(county) : NotFound($"Unknown county FIPS '{fips}'.");

    private static async Task<Results<Ok<ApiMeta>, ProblemHttpResult>> GetMetaAsync(GeographyCatalog geography, CancellationToken ct) =>
        await geography.GetDataVersionAsync(ct) is { } version
            ? TypedResults.Ok(ApiMeta.From(version))
            : TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No data loaded yet");

    private static async Task<Results<Ok<CompanyPage>, ValidationProblem>> SearchCompaniesAsync(
        string? name, int? page, int? pageSize, CompanyService companies, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (name?.Trim().Length > CompanyService.MaxNameLength)
        {
            errors["name"] = [$"At most {CompanyService.MaxNameLength} characters."];
        }

        if (page is < 1)
        {
            errors["page"] = ["Must be 1 or more."];
        }

        if (pageSize is < 1 or > CompanyService.MaxPageSize)
        {
            errors["pageSize"] = [$"Must be 1–{CompanyService.MaxPageSize}."];
        }

        return errors.Count > 0
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await companies.SearchAsync(name, page ?? 1, pageSize ?? 50, ct));
    }

    private static async Task<Results<Ok<CompanyDetail>, ProblemHttpResult>> GetCompanyAsync(string id, CompanyService companies, CancellationToken ct) =>
        await companies.GetAsync(id, ct) is { } company ? TypedResults.Ok(company) : NotFound($"No company with Open Payments ID '{id}'.");

    private static async Task<Results<Ok<ProductPage>, ValidationProblem>> SearchProductsAsync(
        string? name, string? kind, int? page, int? pageSize, ProductService products, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (name?.Trim().Length > ProductService.MaxNameLength)
        {
            errors["name"] = [$"At most {ProductService.MaxNameLength} characters."];
        }

        if (!string.IsNullOrWhiteSpace(kind) && !ProductService.Kinds.Contains(kind.Trim()))
        {
            errors["kind"] = [$"One of {string.Join(", ", ProductService.Kinds)}."];
        }

        if (page is < 1)
        {
            errors["page"] = ["Must be 1 or more."];
        }

        if (pageSize is < 1 or > ProductService.MaxPageSize)
        {
            errors["pageSize"] = [$"Must be 1–{ProductService.MaxPageSize}."];
        }

        return errors.Count > 0
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await products.SearchAsync(name, kind?.Trim(), page ?? 1, pageSize ?? 50, ct));
    }

    private static async Task<Results<Ok<ProductDetail>, ProblemHttpResult>> GetProductAsync(string slug, ProductService products, CancellationToken ct) =>
        await products.GetAsync(slug, ct) is { } product ? TypedResults.Ok(product) : NotFound($"No product '{slug}'.");

    private static async Task<Results<Ok<ProductPrescriberPage>, ValidationProblem, ProblemHttpResult>> GetPrescribersAsync(
        string slug, string? sort, int? page, int? pageSize, ProductService products, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (sort is not null && !ProductService.PrescriberSorts.Contains(sort, StringComparer.OrdinalIgnoreCase))
        {
            errors["sort"] = [$"One of {string.Join(", ", ProductService.PrescriberSorts)}."];
        }

        if (page is < 1)
        {
            errors["page"] = ["Must be 1 or more."];
        }

        if (pageSize is < 1 or > ProductService.MaxPrescriberPageSize)
        {
            errors["pageSize"] = [$"Must be 1–{ProductService.MaxPrescriberPageSize}."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        return await products.PrescribersAsync(slug, sort, page ?? 1, pageSize ?? ProductService.PrescriberPageSize, ct) is { } result
            ? TypedResults.Ok(result)
            : NotFound($"No product '{slug}' with Medicare Part D prescribing.");
    }

    /// <summary>The CSV of a product's paid prescribers (the page's download and the API's).</summary>
    public static async Task<IResult> PrescribersCsvAsync(string slug, HttpContext http, ProductService products, CancellationToken ct)
    {
        if (await products.PrescribersAsync(slug, "paid", 1, 1, ct) is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"No product '{slug}' with Medicare Part D prescribing.");
        }

        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{slug}_paid_prescribers_{DateTime.UtcNow:yyyyMMdd}.csv\"";
        await ProductPrescriberCsv.WriteAsync(products.AllPrescribersAsync(slug, ct), http.Response.Body, ct);
        return Results.Empty;
    }

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

/// <summary>POST /api/v1/providers/lookup body.</summary>
public sealed record LookupRequest(IReadOnlyList<string>? Npis);

/// <summary>POST /api/v1/providers/lookup response: one row per requested NPI, in request order.</summary>
public sealed record LookupResponse(IReadOnlyList<LookupRow> Items);
