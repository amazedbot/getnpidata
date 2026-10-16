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
            .WithDescription("One page of matching providers with the total count. Every filter is optional (none lists every active provider); deactivated NPIs are never returned.");

        api.MapGet("/providers.csv", CsvExport.HandleAsync)
            .WithName("ExportProvidersCsv").WithTags("Providers").WithSearchParameters()
            .WithSummary("Download every match as CSV")
            .WithDescription("Same filters as /providers (paging ignored), streamed with no row cap. UTF-8 with BOM. With no filter: every active provider, sorted by NPI, from a file rebuilt after each load (sent gzipped when the client accepts it).")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv").ProducesValidationProblem();

        api.MapPost("/providers/lookup", LookupAsync)
            .WithName("LookupProviders").WithTags("Providers")
            .WithSummary("Look up many NPIs at once")
            .WithDescription($"Up to {SearchService.MaxLookupBatch} NPIs per request. Every requested NPI comes back once, in order, with status found, deactivated (with its date), notFound or invalid (format or check digit). With \"changedSince\": \"YYYY-MM-DD\" each found or deactivated NPI also lists what changed after that date (changes: on, kind, text), from the archived monthly NPPES files (CMS NPPES; historical copies via NBER) and the changes recorded since October 2026; an empty list means nothing did.");

        api.MapPost("/providers/lookup.csv", LookupCsvAsync)
            .WithName("LookupProvidersCsv").WithTags("Providers")
            .WithSummary("Look up many NPIs at once, as CSV")
            .WithDescription("Same request as /providers/lookup; the summary columns plus Requested NPI and Lookup Status, and with changedSince the Changed Since and Changes columns.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv").ProducesValidationProblem();

        api.MapGet("/providers/{npi}", GetProviderAsync)
            .WithName("GetProvider").WithTags("Providers")
            .WithSummary("One provider")
            .WithDescription("All taxonomies with licenses, all practice locations and other names. 404 for unknown or deactivated NPIs.");

        api.MapGet("/organizations/{npi}", GetOrganizationAsync)
            .WithName("GetOrganization").WithTags("Providers")
            .WithSummary("One organization with all its NPIs, facilities and groups")
            .WithDescription($"The organization page of an NPI (any of its NPIs): every NPI with its role (main NPI, a unit or facility with its CCN, a possible duplicate registration, its specialty), the main NPI's record, the certified facilities (ccns; facilityProfiles: the first {OrganizationPageService.MaxFacilityDetails} with their ratings, how they compare with the nation, Medicare volume and a nursing home's inspections), Medicare groups and practice locations of all of them; ownership (each owner once with the facilities it holds a role in; people by name and role only); clinicians (how many, and how many each source lists: Care Compare facility affiliations, Medicare reassignments, Care Compare groups; every clinician: /organization/{{npi}}/clinicians.csv); medicareActivity of its NPIs, the changes recorded for them, health systems, ACOs (name matches) and 340B registrations; and how the NPIs were grouped (the same legal name at the same street address, or a shared CCN, the same PAC ID or the same parent organization in one ZIP code; never similar names alone). An organization NPI with a certified facility or a Medicare group and no other NPIs has a page of one NPI. 404 when the NPI has no organization page (a person, an organization NPI with none of those, or unknown).");

        api.MapGet("/companies", SearchCompaniesAsync)
            .WithName("SearchCompanies").WithTags("Companies")
            .WithSummary("Companies that report to Open Payments")
            .WithDescription($"Drug and device makers and group purchasing organizations, largest payments (general + research, every published year) first. name matches any part of the company's name or other names, or its Open Payments ID; empty lists every company. pageSize 1–{CompanyService.MaxPageSize} (default 50).");

        api.MapGet("/companies.csv", IndustryEndpoints.CompaniesCsvAsync)
            .WithName("ExportCompaniesCsv").WithTags("Companies")
            .WithSummary("Every company, as CSV")
            .WithDescription("Every company that reports to Open Payments, largest payments first: ID, names, location, general and research payments and ownership over every published year, providers paid, years, page link. UTF-8 with BOM.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv");

        api.MapGet("/companies/{id}", GetCompanyAsync)
            .WithName("GetCompany").WithTags("Companies")
            .WithSummary("One company")
            .WithDescription("Payments per program year, what the newest year's general payments were for and which products they named, the specialties and the active providers it paid the most. 404 for an unknown ID.");

        api.MapGet("/products", SearchProductsAsync)
            .WithName("SearchProducts").WithTags("Products")
            .WithSummary("Products named in Open Payments")
            .WithDescription($"Drugs, biologicals, devices and supplies named in the newest program year's general payments, largest first. name matches any part of the name (or the product's slug); kind is one of {string.Join(", ", ProductService.Kinds)}. pageSize 1–{ProductService.MaxPageSize} (default 50).");

        api.MapGet("/products.csv", IndustryEndpoints.ProductsCsvAsync)
            .WithName("ExportProductsCsv").WithTags("Products")
            .WithSummary("Every product, as CSV")
            .WithDescription("Every product named in the newest program year's general payments, largest first: name, type, category, NDC, device identifier, payments naming it, companies, providers paid, page link. UTF-8 with BOM.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv");

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

        api.MapGet("/facilities", DirectoryEndpoints.SearchFacilitiesAsync)
            .WithName("SearchFacilities").WithTags("Facilities")
            .WithSummary("Medicare-certified facilities")
            .WithDescription($"Hospitals, nursing homes, home health agencies, hospices, FQHCs, rural health clinics, dialysis facilities, surgery centers and other facilities certified by Medicare (CMS Provider of Services file and Care Compare), by CCN. q = a CCN, a ZIP, or words of the name or city; type = {string.Join(" | ", FacilityTypes.All.Select(t => t.Code))}; state = a two-letter code; minRating / maxRating = 1–5 stars (the type's CMS rating; halves count down); status = {string.Join(" | ", FacilityFilter.Statuses)} (default active); ownershipChangedWithinYears = 1–{FacilityFilter.MaxOwnershipChangeYears} (a change of ownership in that many years, from CMS's hospital and nursing home change-of-ownership files; lastOwnershipChange is the newest); hasPenalties = true (nursing homes fined or denied payment in the last {FacilityFilter.PenaltyYears} years; penalties, fines and lastPenalty from Care Compare's penalty file); system = an AHRQ health system ID (its facilities only); in340b = true | false (participating 340B covered entities, HRSA OPAIS; program340B and program340BSince); sort = {string.Join(" | ", FacilityFilter.Sorts)}, \"-\" first for descending (default name); pageSize 1–{FacilityFilter.MaxPageSize} (default {FacilityFilter.DefaultPageSize}). Latitude and longitude are the centre of the facility's ZIP code. Each facility carries its health system (AHRQ Compendium, by CCN) and the HRSA health center it is a site of; HRSA health center sites no CMS facility stands for are listed as type health_center with source hrsa (their ccn is HRSA's site number; see /health-center-sites/{{id}}).");

        api.MapGet("/facilities.csv", DirectoryEndpoints.FacilitiesCsvAsync)
            .WithName("ExportFacilitiesCsv").WithTags("Facilities")
            .WithSummary("Matching facilities, as CSV")
            .WithDescription("Same filters as /facilities (paging ignored), every row with a link to its page. UTF-8 with BOM.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv").ProducesValidationProblem();

        api.MapGet("/facilities/{ccn}", GetFacilityAsync)
            .WithName("GetFacility").WithTags("Facilities")
            .WithSummary("One Medicare-certified facility")
            .WithDescription($"By CMS Certification Number (CCN): its Care Compare listing and ratings; how its main measures compare with the nation (hospitals' deaths, complications, infections, readmissions, emergency care and spending; rehabilitation facilities' and long-term care hospitals' infections, readmissions, discharge to the community and spending; surgery centers' hospital visits after procedures; dialysis facilities' outcome categories; unit = {MeasureUnits.Percent} | {MeasureUnits.PerHundred} | {MeasureUnits.PerThousand} | {MeasureUnits.Ratio} | {MeasureUnits.Minutes}; comparison = {MeasureComparisons.Better} | {MeasureComparisons.Same} | {MeasureComparisons.Worse} | {MeasureComparisons.TooFew}, null when CMS doesn't compare it); a hospital's Medicare inpatient and outpatient volume with its top DRGs and APCs; its certification; the NPIs it bills under; and one page of the clinicians affiliated with it (page, {OrganizationService.PageSize} a page). 404 for an unknown CCN.");

        api.MapGet("/groups", DirectoryEndpoints.SearchGroupsAsync)
            .WithName("SearchGroups").WithTags("Groups")
            .WithSummary("Medicare group practices")
            .WithDescription($"Medicare groups by PAC ID with their active clinicians (reassigned to the group in Medicare enrollment or listed with it in Care Compare), the organization NPIs enrolled under them and their group MIPS score. q = a PAC ID, an NPI (the groups it is enrolled as or bills through) or words of the name; state = a two-letter code; minClinicians / maxClinicians; sort = {string.Join(" | ", GroupFilter.Sorts)}, \"-\" first for descending (default {GroupFilter.DefaultSort}); pageSize 1–{GroupFilter.MaxPageSize} (default {GroupFilter.DefaultPageSize}).");

        api.MapGet("/groups.csv", DirectoryEndpoints.GroupsCsvAsync)
            .WithName("ExportGroupsCsv").WithTags("Groups")
            .WithSummary("Matching Medicare groups, as CSV")
            .WithDescription("Same filters as /groups (paging ignored), every row with a link to its page. UTF-8 with BOM.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv").ProducesValidationProblem();

        api.MapGet("/taxonomy/categories", () => TypedResults.Ok(TaxonomyCatalog.GetCategories()))
            .WithName("ListCategories").WithTags("Lookups").WithSummary("Plain specialty categories")
            .WithDescription("The site's plain specialty categories (\"Doctors (MD, DO)\", \"Dentists\" …), in display order. Each groups NUCC taxonomy codes; pass one as the category search parameter.")
            .CacheOutput(CachePolicy);

        api.MapGet("/taxonomy/categories/{category}/subcategories", GetSubcategories)
            .WithName("ListSubcategories").WithTags("Lookups").WithSummary("Subcategories of one category").CacheOutput(CachePolicy);

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

        api.MapGet("/states/{state}/cities", GetCitiesAsync)
            .WithName("ListCities").WithTags("Lookups").WithSummary("Practice-location cities of one state, optionally of one county")
            .WithDescription("The names USPS uses for the state's ZIPs that have practice locations (optionally only the county's, by 5-digit FIPS). The city search parameter matches a location by the name as written or by a ZIP USPS gives that name.")
            .CacheOutput(CachePolicy);

        api.MapGet("/counties/{fips}", GetCountyAsync)
            .WithName("GetCounty").WithTags("Lookups").WithSummary("County population and shortage areas")
            .WithDescription("Census population estimate and HRSA Health Professional Shortage Areas (primary care, dental, mental health) in force.")
            .CacheOutput(CachePolicy);

        api.MapAreaApi();
        api.MapSystemApi();

        api.MapOwnershipApi();

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
            : TypedResults.Ok(new LookupResponse(await search.LookupAsync(request!.Npis!, request.ChangedSince, ct)));

    private static async Task<IResult> LookupCsvAsync(LookupRequest? request, HttpContext http, SearchService search, CancellationToken ct)
    {
        if (ValidateLookup(request) is { } errors)
        {
            return Results.ValidationProblem(errors);
        }

        var rows = await search.LookupAsync(request!.Npis!, request.ChangedSince, ct);
        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"npi_lookup_{DateTime.UtcNow:yyyyMMdd}.csv\"";
        await ProviderCsv.WriteLookupAsync(rows.ToAsyncEnumerable(), http.Response.Body, ct, withChanges: request.ChangedSince is not null);
        return Results.Empty;
    }

    private static async Task<Results<Ok<ProviderDetail>, ProblemHttpResult>> GetProviderAsync(string npi, ProviderDetailService details, CancellationToken ct) =>
        await details.GetAsync(npi, ct) is { } provider
            ? TypedResults.Ok(provider)
            : NotFound($"No active provider with NPI '{npi}'.");

    private static async Task<Results<Ok<OrganizationProfile>, ProblemHttpResult>> GetOrganizationAsync(string npi, OrganizationPageService pages,
        CancellationToken ct) =>
        await pages.GetAsync(npi, ct) is { } organization
            ? TypedResults.Ok(organization)
            : NotFound($"NPI '{npi}' has no organization page: it isn't an active organization NPI with a certified facility, a Medicare group or other NPIs.");

    private static Results<Ok<IReadOnlyList<string>>, ProblemHttpResult> GetSubcategories(string category) =>
        TaxonomyCatalog.GetSubcategories(category) is { } subcategories
            ? TypedResults.Ok(subcategories)
            : NotFound($"Unknown category '{category}'.");

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

    private static async Task<Results<Ok<IReadOnlyList<string>>, ProblemHttpResult>> GetCitiesAsync(
        string state, string? county, GeographyCatalog geography, CityCatalog cities, CancellationToken ct)
    {
        var counties = await geography.GetCountiesAsync(state, ct);
        if (counties.Count == 0)
        {
            return NotFound($"Unknown state '{state}'.");
        }

        if (county is not null && !counties.Any(c => c.Fips == county))
        {
            return NotFound($"County '{county}' is not in {state.ToUpperInvariant()}.");
        }

        return TypedResults.Ok(await cities.GetCitiesAsync(state, county, ct));
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

    private static async Task<Results<Ok<FacilityProfile>, ProblemHttpResult>> GetFacilityAsync(string ccn, int? page, OrganizationService organizations,
        CancellationToken ct) =>
        await organizations.GetFacilityAsync(ccn, Math.Max(page ?? 1, 1), ct) is { } facility ? TypedResults.Ok(facility) : NotFound($"No facility with CCN '{ccn}'.");

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
/// <param name="ChangedSince">Optional (docs/PRODUCT_PLAN.md step 3.4): also say what changed for each NPI after this date.</param>
public sealed record LookupRequest(IReadOnlyList<string>? Npis, DateOnly? ChangedSince = null);

/// <summary>POST /api/v1/providers/lookup response: one row per requested NPI, in request order.</summary>
public sealed record LookupResponse(IReadOnlyList<LookupRow> Items);
