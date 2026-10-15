using Microsoft.AspNetCore.Http.HttpResults;
using Npi.Core.Search;

namespace Npi.Web.Api;

/// <summary>
/// Facility owners, ownership changes and nursing home inspections (docs/PRODUCT_PLAN.md step 3.6 part 2) under /api/v1: the same
/// service and filters as /owners, /owner/{id} and the facility page's "Who owns it" and "Inspections and penalties".
/// </summary>
public static class OwnershipEndpoints
{
    public static void MapOwnershipApi(this RouteGroupBuilder api)
    {
        api.MapGet("/facilities/{ccn}/ownership", GetFacilityOwnershipAsync)
            .WithName("GetFacilityOwnership").WithTags("Facilities")
            .WithSummary("Who owns a facility, and its ownership changes")
            .WithDescription("The owners, managing parties, officers and directors CMS lists for the facility's Medicare enrollment (CMS All Owners files of hospitals, skilled nursing facilities, home health agencies, hospices, FQHCs and rural health clinics), each with its CMS associate ID, role, stake and since when; and its changes of ownership since 2016 (hospitals and nursing homes only), with the seller's owners before. roleGroup = owner | control | lender | officer. 404 when CMS lists neither for the CCN.");

        api.MapGet("/facilities/{ccn}/inspections", GetInspectionsAsync)
            .WithName("GetNursingHomeInspections").WithTags("Facilities")
            .WithSummary("A nursing home's inspections and penalties")
            .WithDescription("Medicare Care Compare health and fire safety deficiencies of the last three standard inspection cycles (cycle 1 = latest): counts by kind, cycle and scope and severity (A–L; G–I actual harm, J–L immediate jeopardy), every inspection, the ten most serious and ten newest citations with CMS's wording, and every fine and payment denial of the last three years. 404 when Care Compare lists none for the CCN.");

        api.MapGet("/owners", SearchOwnersAsync)
            .WithName("SearchOwners").WithTags("Facilities")
            .WithSummary("Owners of Medicare-certified facilities")
            .WithDescription($"Companies and people CMS lists as owners or managers of facilities, those owning the most first. q = part of the name or doing-business-as name, or a CMS associate ID; type = {OwnershipService.TypeOrganization} | {OwnershipService.TypeIndividual}; kind = {string.Join(" | ", OwnerKinds.All.Select(k => k.Code))}; pageSize 1–{OwnershipService.MaxPageSize} (default {OwnershipService.DefaultPageSize}). facilities = facilities listed for in any role, owned = with an ownership interest.")
            .ProducesValidationProblem();

        api.MapGet("/owners/{id}", GetOwnerAsync)
            .WithName("GetOwner").WithTags("Facilities")
            .WithSummary("One facility owner")
            .WithDescription("Every facility the owner is listed for with its roles and stake; for an organization, matched by its associate ID only, the NPIs it is enrolled in Medicare under, the facilities it is itself and its Medicare group; and the ownership changes it was the buyer or seller in, or owned the seller before. 404 for an unknown ID.");
    }

    public static async Task<Results<Ok<OwnerPage>, ValidationProblem>> SearchOwnersAsync(string? q, string? type, string? kind, int? page, int? pageSize,
        OwnershipService ownership, CancellationToken ct)
    {
        var errors = OwnershipService.Validate(q, type, kind, page ?? 1, pageSize ?? OwnershipService.DefaultPageSize);
        return errors.Count > 0
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await ownership.SearchOwnersAsync(Clean(q), Clean(type), Clean(kind), page ?? 1, pageSize ?? OwnershipService.DefaultPageSize, ct));
    }

    private static async Task<Results<Ok<OwnerDetail>, ProblemHttpResult>> GetOwnerAsync(string id, OwnershipService ownership, CancellationToken ct) =>
        await ownership.GetOwnerAsync(id, ct) is { } owner ? TypedResults.Ok(owner) : NotFound($"No facility owner with CMS associate ID '{id}'.");

    private static async Task<Results<Ok<FacilityOwnership>, ProblemHttpResult>> GetFacilityOwnershipAsync(string ccn, OwnershipService ownership, CancellationToken ct) =>
        await ownership.GetFacilityOwnershipAsync(ccn, ct) is { } result ? TypedResults.Ok(result) : NotFound($"CMS lists no owners or ownership changes for CCN '{ccn}'.");

    private static async Task<Results<Ok<NursingHomeInspections>, ProblemHttpResult>> GetInspectionsAsync(string ccn, OwnershipService ownership, CancellationToken ct) =>
        await ownership.GetInspectionsAsync(ccn, ct) is { } result ? TypedResults.Ok(result) : NotFound($"Care Compare lists no nursing home inspections for CCN '{ccn}'.");

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ProblemHttpResult NotFound(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: detail);
}
