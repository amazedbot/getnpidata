using Microsoft.AspNetCore.Http.HttpResults;
using Npi.Core.Search;

namespace Npi.Web.Api;

/// <summary>
/// Areas over time (docs/PRODUCT_PLAN.md step 3.3): the monthly series per area and the values of one statistic for every
/// state or county, for the API (/api/v1/areas/…) and for the area pages' map (/area/map.json, not rate-limited, cached).
/// </summary>
public static class AreaEndpoints
{
    // Map values change only when the loader runs: a short cache serves a page's repeated redraws (and the national county map) from memory.
    private static readonly string[] MapQueryKeys = ["level", "state", "stat", "category", "subcategory", "month"];

    /// <summary>The site's map data: /area/map.json?level=&amp;state=&amp;stat=&amp;category=&amp;subcategory=&amp;month=.</summary>
    public static void MapAreaEndpoints(this WebApplication app) =>
        app.MapGet("/area/map.json", MapDataAsync)
            .CacheOutput(p => p.Expire(TimeSpan.FromMinutes(10)).SetVaryByQuery(MapQueryKeys))
            .ExcludeFromDescription();

    public static void MapAreaApi(this RouteGroupBuilder api)
    {
        const string history = "Monthly figures, oldest first: providers and new NPIs for the category (every provider when none), and for every provider the NPIs deactivated, providers who moved in and out (states and counties), specialty, license and exclusion changes and the record-quality mix. Null means not known for that month; coverage gives the first month of each series (derivedFrom: worked out from today's data; trackedFrom: recorded as the loads arrived; changesFrom: the change log; archiveFrom to archiveTo: from the archived monthly NPPES files, CMS NPPES with historical copies via NBER, basis \"archive\", by primary practice location before anyLocationFrom (the first file listing secondary practice locations, September 2018) and by every practice location from it, as the other series; no net change is given for that month). The as-of month is partial.";
        api.MapGet("/areas/us/history", (string? category, AreaHistoryService areas, CancellationToken ct) => HistoryAsync(null, null, category, areas, ct))
            .WithName("GetUsHistory").WithTags("Areas").WithSummary("The United States, month by month").WithDescription(history);
        api.MapGet("/areas/{state}/history", (string state, string? category, AreaHistoryService areas, CancellationToken ct) => HistoryAsync(state, null, category, areas, ct))
            .WithName("GetStateHistory").WithTags("Areas").WithSummary("A state, month by month").WithDescription(history);
        api.MapGet("/areas/{state}/{county}/history", (string state, string county, string? category, AreaHistoryService areas, CancellationToken ct) =>
                HistoryAsync(state, county, category, areas, ct))
            .WithName("GetCountyHistory").WithTags("Areas").WithSummary("A county (5-digit FIPS), month by month").WithDescription(history);
        api.MapGet("/areas/map", MapDataAsync)
            .WithName("GetAreaMap").WithTags("Areas").WithSummary("One statistic for every state or county")
            .WithDescription($"level = state | county (a state's counties with state=NY, else every county); stat = {string.Join(" | ", AreaMapStats.All.Select(s => s.Stat))} (default per100k); category and subcategory narrow providers, per100k and the new NPIs; month = yyyy-MM for the monthly statistics (default: the data's as-of month). Counties come with their HRSA shortage areas (PC, DH, MH).")
            .ProducesValidationProblem();
    }

    private static async Task<Results<Ok<AreaHistory>, ValidationProblem, ProblemHttpResult>> HistoryAsync(string? state, string? county, string? category,
        AreaHistoryService areas, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(category) && SpecialtyCategories.Default.Find(category) is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["category"] = ["Not a specialty category (see /api/v1/taxonomy/categories)."] });
        }

        return await areas.GetAsync(state, county, category, ct) is { } area
            ? TypedResults.Ok(area)
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found",
                detail: county is null ? $"Unknown state '{state}'." : $"No county '{county}' in '{state}'.");
    }

    /// <summary>The values of one statistic (the site's map and the API's /areas/map).</summary>
    public static async Task<IResult> MapDataAsync(HttpContext http, AreaHistoryService areas, CancellationToken ct)
    {
        var q = http.Request.Query;
        var (query, errors) = AreaMapQuery.Parse(q["level"], q["state"], q["stat"], q["category"], q["subcategory"], q["month"]);
        if (query is null)
        {
            return Results.ValidationProblem(errors);
        }

        return await areas.GetMapAsync(query.Level, query.State, query.Stat, query.Category, query.Subcategory, query.Month, ct) is { } data
            ? Results.Ok(data)
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
    }
}
