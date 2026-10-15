using Microsoft.AspNetCore.Http.HttpResults;
using Npi.Core.Search;

namespace Npi.Web.Api;

/// <summary>
/// The facility and group lists (docs/PRODUCT_PLAN.md step 3.6), shared by /api/v1 and the pages' CSV links: the same parameters as
/// /facilities and /groups, so a page's list, its CSV and the API return the same rows.
/// </summary>
public static class DirectoryEndpoints
{
    public static FacilityFilter Facilities(string? q, string? type, string? state, int? minRating, int? maxRating, string? status,
        int? ownershipChangedWithinYears, bool? hasPenalties, string? sort, int? page = null, int? pageSize = null) => new()
    {
        Q = Clean(q), Type = Clean(type), State = Clean(state), MinRating = minRating, MaxRating = maxRating, Status = Clean(status),
        OwnershipChangedWithinYears = ownershipChangedWithinYears, HasPenalties = hasPenalties, Sort = Clean(sort),
        Page = page ?? 1, PageSize = pageSize ?? FacilityFilter.DefaultPageSize,
    };

    public static GroupFilter Groups(string? q, string? state, int? minClinicians, int? maxClinicians, string? sort, int? page = null, int? pageSize = null) => new()
    {
        Q = Clean(q), State = Clean(state), MinClinicians = minClinicians, MaxClinicians = maxClinicians, Sort = Clean(sort), Page = page ?? 1,
        PageSize = pageSize ?? GroupFilter.DefaultPageSize,
    };

    public static async Task<Results<Ok<FacilityPage>, ValidationProblem, ProblemHttpResult>> SearchFacilitiesAsync(string? q, string? type, string? state,
        int? minRating, int? maxRating, string? status, int? ownershipChangedWithinYears, bool? hasPenalties, string? sort, int? page, int? pageSize,
        FacilityDirectory facilities, CancellationToken ct)
    {
        var filter = Facilities(q, type, state, minRating, maxRating, status, ownershipChangedWithinYears, hasPenalties, sort, page, pageSize);
        if (filter.Validate() is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (!await facilities.IsReadyAsync(ct))
        {
            return NotReady(new DirectoryNotReadyException("The facility list"));
        }

        return TypedResults.Ok(await facilities.SearchAsync(filter, ct));
    }

    public static async Task<Results<Ok<GroupPage>, ValidationProblem, ProblemHttpResult>> SearchGroupsAsync(string? q, string? state, int? minClinicians,
        int? maxClinicians, string? sort, int? page, int? pageSize, OrganizationDirectory organizations, CancellationToken ct)
    {
        var filter = Groups(q, state, minClinicians, maxClinicians, sort, page, pageSize);
        if (filter.Validate() is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (!await organizations.IsReadyAsync(ct))
        {
            return NotReady(new DirectoryNotReadyException("The group list"));
        }

        return TypedResults.Ok(await organizations.SearchGroupsAsync(filter, ct));
    }

    /// <summary>Every facility matching the filters as CSV, in the list's order (paging ignored).</summary>
    public static async Task<IResult> FacilitiesCsvAsync(string? q, string? type, string? state, int? minRating, int? maxRating, string? status,
        int? ownershipChangedWithinYears, bool? hasPenalties, string? sort, HttpContext http, FacilityDirectory facilities, CancellationToken ct)
    {
        var filter = Facilities(q, type, state, minRating, maxRating, status, ownershipChangedWithinYears, hasPenalties, sort);
        if (filter.Validate() is { Count: > 0 } errors)
        {
            return Invalid(http, "/facilities", errors);
        }

        if (!await facilities.IsReadyAsync(ct))
        {
            return IsApi(http) ? NotReady(new DirectoryNotReadyException("The facility list")) : TypedResults.Redirect("/facilities" + http.Request.QueryString);
        }

        return await CsvAsync(http, $"facilities_{DateTime.UtcNow:yyyyMMdd}.csv",
            (body, site) => FacilityCsv.WriteAsync(facilities.AllAsync(filter, ct), body, site, ct), ct);
    }

    /// <summary>Every group matching the filters as CSV, in the list's order (paging ignored).</summary>
    public static async Task<IResult> GroupsCsvAsync(string? q, string? state, int? minClinicians, int? maxClinicians, string? sort, HttpContext http,
        OrganizationDirectory organizations, CancellationToken ct)
    {
        var filter = Groups(q, state, minClinicians, maxClinicians, sort);
        if (filter.Validate() is { Count: > 0 } errors)
        {
            return Invalid(http, "/groups", errors);
        }

        if (!await organizations.IsReadyAsync(ct))
        {
            return IsApi(http) ? NotReady(new DirectoryNotReadyException("The group list")) : TypedResults.Redirect("/groups" + http.Request.QueryString);
        }

        return await CsvAsync(http, $"medicare_groups_{DateTime.UtcNow:yyyyMMdd}.csv",
            (body, site) => GroupCsv.WriteAsync(organizations.AllGroupsAsync(filter, ct), body, site, ct), ct);
    }

    private static bool IsApi(HttpContext http) => http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    // The API answers a bad filter with its problem; the site's CSV links go back to the page, which shows the error.
    private static IResult Invalid(HttpContext http, string page, Dictionary<string, string[]> errors) =>
        IsApi(http) ? TypedResults.ValidationProblem(errors) : TypedResults.Redirect(page + http.Request.QueryString);

    // Before the loader's first build of the directory tables (after they were added): try again after the next load.
    private static ProblemHttpResult NotReady(DirectoryNotReadyException ex) =>
        TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Not ready yet", detail: ex.Message);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<IResult> CsvAsync(HttpContext http, string fileName, Func<Stream, string, Task<long>> write, CancellationToken ct)
    {
        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        try
        {
            await write(http.Response.Body, $"{http.Request.Scheme}://{http.Request.Host}");
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // The client stopped the download.
        }

        return Results.Empty;
    }
}
