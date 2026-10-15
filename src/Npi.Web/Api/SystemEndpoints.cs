using Microsoft.AspNetCore.Http.HttpResults;
using Npi.Core.Search;

namespace Npi.Web.Api;

/// <summary>
/// Health systems (AHRQ Compendium), Medicare Shared Savings Program ACOs and HRSA health center sites (docs/PRODUCT_PLAN.md step
/// 3.6 part 2) under /api/v1, with the same parameters as the /systems and /acos pages.
/// </summary>
public static class SystemEndpoints
{
    public static HealthSystemFilter Systems(string? q, string? state, string? sort, int? page = null, int? pageSize = null) => new()
    {
        Q = Clean(q), State = Clean(state), Sort = Clean(sort), Page = page ?? 1, PageSize = pageSize ?? HealthSystemFilter.DefaultPageSize,
    };

    public static AcoFilter Acos(string? q, string? state, int? page = null, int? pageSize = null) => new()
    {
        Q = Clean(q), State = Clean(state), Page = page ?? 1, PageSize = pageSize ?? AcoFilter.DefaultPageSize,
    };

    public static void MapSystemApi(this RouteGroupBuilder api)
    {
        api.MapGet("/systems", SearchSystemsAsync)
            .WithName("SearchHealthSystems").WithTags("Health systems")
            .WithSummary("Health systems (AHRQ Compendium of U.S. Health Systems)")
            .WithDescription($"Systems of at least one hospital and one group of physicians under common ownership or management, with AHRQ's counts. q = an AHRQ system ID or words of the name or headquarters city; state = headquartered there; sort = {string.Join(" | ", HealthSystemFilter.Sorts)}, \"-\" first for descending (default {HealthSystemFilter.DefaultSort}); pageSize 1–{HealthSystemFilter.MaxPageSize} (default {HealthSystemFilter.DefaultPageSize}).");

        api.MapGet("/systems/{id}", GetSystemAsync)
            .WithName("GetHealthSystem").WithTags("Health systems")
            .WithSummary("One health system: its facilities (by CCN) and Medicare groups")
            .WithDescription("Facilities from AHRQ's linkage files by CMS Certification Number; Medicare groups whose PAC ID enrolls an NPI that bills for one of them.");

        api.MapGet("/acos", SearchAcosAsync)
            .WithName("SearchAcos").WithTags("ACOs")
            .WithSummary("Medicare Shared Savings Program ACOs")
            .WithDescription($"Accountable care organizations in the newest performance year. q = an ACO ID, words of its name, or part of a participant's name; state = in its service area; pageSize 1–{AcoFilter.MaxPageSize} (default {AcoFilter.DefaultPageSize}).");

        api.MapGet("/acos/{id}", GetAcoAsync)
            .WithName("GetAco").WithTags("ACOs")
            .WithSummary("One ACO with its participants and SNF affiliates")
            .WithDescription($"Participants and skilled nursing facility affiliates by legal business name, as CMS publishes them (the files carry no TIN, NPI or CCN). Each participant lists the active organization NPIs whose NPPES legal business name is the same name, case, spacing and punctuation aside (matchedBy = \"name\"); a name shared by more than {AcoNames.MaxCandidates} organizations lists none, unless they are a hospital and its own NPIs: every other one shares the hospital's CCN or names the hospital, or its parent organization, as parent organization in NPPES (hospitalGroup = true, up to {AcoNames.MaxHospitalCandidates}), with groupedBy = \"ccn\" (a shared CCN, groupedValue), \"parent\" (the same parent organization, groupedValue = its legal name), \"parent_tin\" (the same parent tax ID) or \"hospital\" (some share its CCN, others name it; groupedValue = the hospital's name).");

        api.MapGet("/providers/{npi}/acos", GetProviderAcosAsync)
            .WithName("GetProviderAcos").WithTags("ACOs")
            .WithSummary("The Medicare ACOs an organization NPI takes part in (matched by name)")
            .WithDescription($"ACO participants whose legal business name is this NPI's NPPES legal business name (case, spacing and punctuation aside). matchedBy is always \"name\": CMS publishes no identifier for participants. candidates = how many active organizations share the name (at most {AcoNames.MaxCandidates}, or {AcoNames.MaxHospitalCandidates} for a hospital and its own NPIs: hospitalGroup, with groupedBy and groupedValue as on the ACO). An empty list when there is none; 404 for a value that isn't an NPI.");

        api.MapGet("/groups/{pacId}/acos", GetGroupAcosAsync)
            .WithName("GetGroupAcos").WithTags("ACOs")
            .WithSummary("The Medicare ACOs of a group's organization NPIs (matched by name)")
            .WithDescription("As /providers/{npi}/acos, for the organization NPIs enrolled in Medicare under the group's PAC ID. 404 for a value that isn't a PAC ID.");

        api.MapGet("/health-center-sites/{id}", GetHealthCenterSiteAsync)
            .WithName("GetHealthCenterSite").WithTags("Facilities")
            .WithSummary("One HRSA health center site and the health center's other sites")
            .WithDescription("A site of a federally funded health center (FQHC) or look-alike, by HRSA's site number (BPS-…), with its Medicare number and NPI where HRSA lists them.");
    }

    public static async Task<Results<Ok<HealthSystemPage>, ValidationProblem>> SearchSystemsAsync(string? q, string? state, string? sort, int? page, int? pageSize,
        HealthSystemService systems, CancellationToken ct)
    {
        var filter = Systems(q, state, sort, page, pageSize);
        return filter.Validate() is { Count: > 0 } errors ? TypedResults.ValidationProblem(errors) : TypedResults.Ok(await systems.SearchAsync(filter, ct));
    }

    public static async Task<Results<Ok<AcoPage>, ValidationProblem>> SearchAcosAsync(string? q, string? state, int? page, int? pageSize, AcoService acos,
        CancellationToken ct)
    {
        var filter = Acos(q, state, page, pageSize);
        return filter.Validate() is { Count: > 0 } errors ? TypedResults.ValidationProblem(errors) : TypedResults.Ok(await acos.SearchAsync(filter, ct));
    }

    private static async Task<Results<Ok<HealthSystemDetail>, ProblemHttpResult>> GetSystemAsync(string id, HealthSystemService systems, CancellationToken ct) =>
        HealthSystemService.IsSystemId(id) && await systems.GetAsync(id, ct) is { } system ? TypedResults.Ok(system) : NotFound($"No health system with ID '{id}'.");

    private static async Task<Results<Ok<AcoDetail>, ProblemHttpResult>> GetAcoAsync(string id, AcoService acos, CancellationToken ct) =>
        AcoService.IsAcoId(id) && await acos.GetAsync(id, ct) is { } aco ? TypedResults.Ok(aco) : NotFound($"No ACO with ID '{id}'.");

    private static async Task<Results<Ok<IReadOnlyList<AcoMembership>>, ProblemHttpResult>> GetProviderAcosAsync(string npi, AcoService acos, CancellationToken ct) =>
        Npi.Core.InputFormats.IsNpi(npi) ? TypedResults.Ok(await acos.ForNpiAsync(npi, ct)) : NotFound($"'{npi}' is not an NPI.");

    private static async Task<Results<Ok<IReadOnlyList<AcoMembership>>, ProblemHttpResult>> GetGroupAcosAsync(string pacId, AcoService acos, CancellationToken ct) =>
        OrganizationService.IsPacId(pacId) ? TypedResults.Ok(await acos.ForPacAsync(pacId, ct)) : NotFound($"'{pacId}' is not a PAC ID.");

    private static async Task<Results<Ok<HealthCenterSiteDetail>, ProblemHttpResult>> GetHealthCenterSiteAsync(string id, HealthCenterService sites,
        CancellationToken ct) =>
        HealthCenterService.IsSiteId(id) && await sites.GetSiteAsync(id, ct) is { } site ? TypedResults.Ok(site) : NotFound($"No health center site '{id}'.");

    private static ProblemHttpResult NotFound(string detail) => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: detail);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
