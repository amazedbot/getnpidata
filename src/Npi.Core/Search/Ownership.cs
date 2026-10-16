using System.Globalization;
using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>
/// One owner, officer or managing party of a facility's Medicare enrollment (CMS "All Owners" files, docs/PRODUCT_PLAN.md step 3.6):
/// <see cref="OwnerId"/> is CMS's associate ID (the PECOS associate control ID, the PAC ID Care Compare uses), the same for that
/// owner across every facility, and its /owner/{id} page; null for a person, who is shown by name and role only (no ID, place or
/// page that would link one person's roles across facilities; owner, 2026-10-15). <see cref="RoleGroup"/> is one of
/// <see cref="OwnerRoles"/>' groups; <see cref="Percentage"/> is the stake CMS lists (often none for officers and managers).
/// <see cref="Kinds"/> are the owner types CMS marks (organizations only): codes of <see cref="OwnerKinds"/>, e.g. private_equity, reit.
/// </summary>
public sealed record FacilityOwner(string? OwnerId, bool IsOrganization, string Name, string? DoingBusinessAs, string? Title, string RoleCode, string Role,
    string RoleGroup, double? Percentage, DateOnly? Since, IReadOnlyList<string> Kinds, string? City, string? State);

/// <summary>
/// An owner of the seller before an ownership change (CMS "Change of Ownership - Owner Information"); <see cref="OwnerId"/> is null
/// for a person, as in <see cref="FacilityOwner"/>.
/// </summary>
public sealed record PreviousOwner(string? OwnerId, bool IsOrganization, string Name, string RoleCode, string Role, string RoleGroup, double? Percentage);

/// <summary>
/// A change of ownership CMS recorded for a facility (CMS "Change of Ownership" files, 2016 on): the buyer took over the seller's
/// Medicare enrollment on <see cref="EffectiveDate"/>. <see cref="Type"/>: "Change of ownership" or "Acquisition or merger".
/// <see cref="BuyerId"/>/<see cref="SellerId"/> are the parties' associate IDs (as owners elsewhere, <see cref="FacilityOwner.OwnerId"/>).
/// </summary>
public sealed record OwnershipChange(DateOnly? EffectiveDate, string Type, string Kind, string? BuyerName, string? BuyerDoingBusinessAs, string? BuyerId,
    string? BuyerCcn, string? BuyerNpi, string? SellerName, string? SellerDoingBusinessAs, string? SellerId, string? SellerCcn, string? SellerNpi,
    IReadOnlyList<PreviousOwner> PreviousOwners);

/// <summary>
/// Who owns and runs a facility: its Medicare enrollment (<see cref="EnrollmentId"/>, legal name and associate ID), the owners and
/// managing parties CMS lists for it (as of <see cref="OwnersAsOf"/>, the end of the release's period) and its ownership changes,
/// newest first.
/// </summary>
public sealed record FacilityOwnership(string Ccn, string? EnrollmentId, string? LegalName, string? AssociateId, DateOnly? OwnersAsOf,
    IReadOnlyList<FacilityOwner> Owners, DateOnly? ChangesAsOf, IReadOnlyList<OwnershipChange> Changes);

/// <summary>
/// An organization in the owner list: <see cref="Facilities"/> = the facility enrollments it is listed for in any role,
/// <see cref="Owned"/> = those it holds an ownership interest in. People (officers, managing employees, individual owners) have no
/// entry: they are shown only by name and role on the facilities' own pages (owner, 2026-10-15).
/// </summary>
public sealed record OwnerSummary(string Id, string Name, string? DoingBusinessAs, string? City, string? State,
    IReadOnlyList<string> Kinds, int Facilities, int Owned);

/// <summary>A page of owners.</summary>
public sealed record OwnerPage(IReadOnlyList<OwnerSummary> Items, int Page, int PageSize, int TotalCount);

/// <summary>An owner's role in one facility.</summary>
public sealed record OwnerRole(string RoleCode, string Role, string RoleGroup, double? Percentage, DateOnly? Since);

/// <summary>
/// A facility on an owner's page: <see cref="Ccn"/> from the facility's enrollment (null when CMS's enrollment files don't list it),
/// its name and place from the facility list when known, else its legal name from the owners file.
/// </summary>
public sealed record OwnerFacility(string? Ccn, string EnrollmentId, string Type, string Name, string? City, string? State, bool? Active, IReadOnlyList<OwnerRole> Roles);

/// <summary>
/// An organization owner's page (/owner/{id}): the facilities it owns or runs, and, matched by its associate ID only (never by name),
/// the NPIs it is enrolled in Medicare under (<see cref="Npis"/>), the facilities it is itself (<see cref="EnrolledAs"/>),
/// its Medicare group page (<see cref="GroupPacId"/>) and the ownership changes it was the buyer or seller in.
/// </summary>
public sealed record OwnerDetail(OwnerSummary Owner, DateOnly? AsOf, IReadOnlyList<OwnerFacility> Facilities, IReadOnlyList<OrganizationNpi> Npis,
    IReadOnlyList<OwnerFacility> EnrolledAs, string? GroupPacId, IReadOnlyList<OwnershipChange> Changes);

/// <summary>Nursing home deficiencies of one kind (health or fire safety) in one inspection cycle at one scope and severity (A–L).</summary>
public sealed record InspectionCount(string Kind, int Cycle, string ScopeSeverity, int Deficiencies, int Complaint);

/// <summary>One nursing home inspection (a survey date), its deficiencies and the most serious one's scope and severity.</summary>
public sealed record InspectionSurvey(string Kind, DateOnly Date, int Cycle, int Deficiencies, int Complaint, string? MostSevere);

/// <summary>A cited deficiency: the tag (F0689, K0345 …) with CMS's wording, its scope and severity and whether it was corrected.</summary>
public sealed record InspectionCitation(string Kind, DateOnly SurveyDate, int Cycle, string Tag, string ScopeSeverity, string? Category, string? Description,
    string? Status, DateOnly? CorrectionDate, bool Complaint, bool InfectionControl);

/// <summary>A fine (<see cref="Fine"/> in dollars) or a denial of Medicare payment for new admissions (from <see cref="PaymentDenialStart"/>).</summary>
public sealed record NursingHomePenalty(DateOnly? Date, string Type, double? Fine, DateOnly? PaymentDenialStart, int? PaymentDenialDays);

/// <summary>
/// A nursing home's inspections and penalties (Care Compare "Health Deficiencies", "Fire Safety Deficiencies" and "Penalties";
/// docs/PRODUCT_PLAN.md step 3.6): the deficiency counts by kind, cycle (1 = the newest standard inspection cycle) and scope and
/// severity, every inspection, the most serious and the newest citations (ten of each), and every fine and payment denial of the
/// last three years.
/// </summary>
public sealed record NursingHomeInspections(string Ccn, DateOnly? UpdatedOn, IReadOnlyList<InspectionCount> Counts, IReadOnlyList<InspectionSurvey> Surveys,
    IReadOnlyList<InspectionCitation> Citations, IReadOnlyList<NursingHomePenalty> Penalties)
{
    public double FinesTotal => Math.Round(Penalties.Sum(p => p.Fine ?? 0), 2);

    public int PaymentDenials => Penalties.Count(p => p.PaymentDenialStart is not null || p.Type.Contains("denial", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// CMS's owner role codes (PECOS) in plain words, and the groups the facility page shows them in: owners (an ownership interest),
/// control (operational or managerial control, a governing body, a trustee, a contracted manager, a nursing home's additional
/// disclosable party), lenders (a mortgage or security interest) and officers (corporate officers, directors, managing employees).
/// </summary>
public static class OwnerRoles
{
    public const string Owner = "owner";
    public const string Control = "control";
    public const string Lender = "lender";
    public const string Officer = "officer";

    /// <summary>The groups in the order the page lists them.</summary>
    public static readonly IReadOnlyList<string> Groups = [Owner, Control, Lender, Officer];

    /// <summary>Ownership interests: 5% or more direct (34) or indirect (35), general (38) or limited (39) partner, a nursing home's direct (85) or indirect (86) interest.</summary>
    public static readonly IReadOnlyList<string> OwnershipCodes = ["34", "35", "38", "39", "85", "86"];

    /// <summary>Control and management: operational or managerial control (43), contracted managing employee (25), governing body (63), trustee (71), additional disclosable party (72).</summary>
    public static readonly IReadOnlyList<string> ControlCodes = ["43", "25", "63", "71", "72"];

    /// <summary>A group's place in <see cref="Groups"/>.</summary>
    public static int Order(string group) => group switch { Owner => 0, Control => 1, Lender => 2, _ => 3 };

    public static string Group(string? code) => code?.Trim() switch
    {
        "34" or "35" or "38" or "39" or "85" or "86" => Owner,
        "43" or "25" or "63" or "71" or "72" => Control,
        "36" or "37" => Lender,
        _ => Officer,
    };

    /// <summary>"Owner", "Control and management", "Lenders", "Officers, directors and managers".</summary>
    public static string GroupName(string group) => group switch
    {
        Owner => "Owners",
        Control => "Control and management",
        Lender => "Lenders",
        _ => "Officers, directors and managers",
    };

    /// <summary>A role code in plain words; an unknown code falls back to CMS's text.</summary>
    public static string Label(string? code, string? text) => code?.Trim() switch
    {
        "34" => "Direct owner (5% or more)",
        "35" => "Indirect owner (5% or more)",
        "36" => "Mortgage holder (5% or more)",
        "37" => "Security interest (5% or more)",
        "38" => "General partner",
        "39" => "Limited partner",
        "40" => "Corporate officer",
        "41" => "Corporate director",
        "42" => "Managing employee",
        "43" => "Operational or managerial control",
        "44" => "Other",
        "25" => "Contracted managing employee",
        "63" => "Managing control (governing body)",
        "71" => "Trustee",
        "72" => "Additional disclosable party",
        "85" => "Direct owner",
        "86" => "Indirect owner",
        _ => string.IsNullOrWhiteSpace(text) ? "Other" : Sentence(text),
    };

    // "OTHER ROLE TEXT" → "Other role text".
    private static string Sentence(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return char.ToUpperInvariant(t[0]) + t[1..];
    }
}

/// <summary>The owner types CMS marks for an organization owner (Y in the All Owners files), as stored codes and in plain words.</summary>
public static class OwnerKinds
{
    /// <summary>Code → label, in the order the page lists them (the most telling first).</summary>
    public static readonly IReadOnlyList<(string Code, string Label)> All =
    [
        ("private_equity", "Private equity company"),
        ("reit", "Real estate investment trust (REIT)"),
        ("investment_firm", "Investment firm"),
        ("holding_company", "Holding company"),
        ("chain_home_office", "Chain home office"),
        ("management_company", "Management services company"),
        ("staffing_company", "Medical staffing company"),
        ("financial_institution", "Financial institution"),
        ("consulting_firm", "Consulting firm"),
        ("medical_provider", "Medical provider or supplier"),
        ("created_for_acquisition", "Created to acquire the facility"),
        ("parent_company", "Parent company"),
        ("trust", "Trust or trustee"),
        ("for_profit", "For-profit"),
        ("non_profit", "Non-profit"),
        ("corporation", "Corporation"),
        ("llc", "LLC"),
    ];

    public static string? Label(string code) => All.FirstOrDefault(k => k.Code == code).Label;

    public static bool IsKnown(string? code) => code is not null && All.Any(k => k.Code == code);

    /// <summary>The stored comma list ("private_equity,llc") as codes in <see cref="All"/>'s order.</summary>
    public static IReadOnlyList<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        var codes = stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        return [.. All.Where(k => codes.Contains(k.Code)).Select(k => k.Code)];
    }
}

/// <summary>
/// CMS's nursing home scope and severity codes (A–L): the severity is the row of CMS's grid (1 = potential for minimal harm,
/// 2 = potential for more than minimal harm, 3 = actual harm, 4 = immediate jeopardy), the scope its column (isolated, a pattern,
/// widespread). A later letter is more serious.
/// </summary>
public static class InspectionSeverity
{
    public const string Health = "health";
    public const string FireSafety = "fire";

    public static readonly IReadOnlyList<string> Levels =
        ["Potential for minimal harm", "Potential for more than minimal harm", "Actual harm", "Immediate jeopardy to health or safety"];

    public static readonly IReadOnlyList<string> Scopes = ["Isolated", "Pattern", "Widespread"];

    /// <summary>1–4, or 0 for anything that isn't A–L.</summary>
    public static int Level(string? code) => code is { Length: 1 } c && c[0] is >= 'A' and <= 'L' ? (c[0] - 'A') / 3 + 1 : 0;

    /// <summary>0 isolated, 1 pattern, 2 widespread; -1 for anything that isn't A–L.</summary>
    public static int Scope(string? code) => code is { Length: 1 } c && c[0] is >= 'A' and <= 'L' ? (c[0] - 'A') % 3 : -1;

    /// <summary>"Actual harm, isolated (G)".</summary>
    public static string Describe(string? code) =>
        Level(code) is var level and > 0 ? $"{Levels[level - 1]}, {Scopes[Scope(code)].ToLowerInvariant()} ({code})" : code ?? "";

    /// <summary>Actual harm or immediate jeopardy (G–L).</summary>
    public static bool IsHarm(string? code) => Level(code) >= 3;

    public static string KindName(string kind) => kind == FireSafety ? "Fire safety" : "Health";
}

/// <summary>
/// Reads facility owners, ownership changes and nursing home inspections (docs/PRODUCT_PLAN.md step 3.6, part 2). Owners join to
/// facilities by the enrollment ID in CMS's facility enrollment files (cms_facility_enrollment), and to NPIs, facilities and
/// groups by their associate ID only.
/// </summary>
public sealed class OwnershipService(string connectionString)
{
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 50;
    public const int MaxQueryLength = 100;

    /// <summary>The <c>reference_data</c> source of the nursing home inspections (its version starts with CMS's update date).</summary>
    public const string InspectionsSource = "cc_nursing_home_inspections";


    private sealed record EnrollmentRow(string EnrollmentId, string? OrgName, string? AssociateId, string? Ccn);

    private sealed record OwnerRow(string OwnerId, string OwnerType, string? Name, string? DbaName, string? Title, string RoleCode, string? RoleText,
        double? Percentage, DateTime? AssociationDate, string? OwnerKinds, string? City, string? State, DateTime? PeriodEnd, string EnrollmentId);

    private sealed record ChangeRow(long Id, string Kind, string? ChowTypeCode, string? ChowType, DateTime? EffectiveDate, string? BuyerEnrollmentId,
        string? BuyerCcn, string? BuyerNpi, string? BuyerAssociateId, string? BuyerName, string? BuyerDbaName, string? SellerEnrollmentId, string? SellerCcn,
        string? SellerNpi, string? SellerAssociateId, string? SellerName, string? SellerDbaName, DateTime? PeriodEnd);

    private sealed record PreviousRow(string EnrollmentId, string OwnerId, string OwnerType, string? Name, string RoleCode, string? RoleText, double? Percentage);

    private sealed record EntityRow(string OwnerId, string OwnerType, string Name, string? DbaName, string? City, string? State, string? OwnerKinds,
        int Facilities, int Owned);

    private sealed record OwnedRow(string EnrollmentId, string Kind, string? FacilityName, string RoleCode, string? RoleText, double? Percentage,
        DateTime? AssociationDate, string? Ccn, string? DirName, string? DirCity, string? DirState, string? DirType, long? DirActive, string? EnrollmentState,
        DateTime? PeriodEnd);

    private sealed record SelfRow(string EnrollmentId, string Kind, string? Ccn, string? OrgName, string? State, string? DirName, string? DirCity,
        string? DirState, string? DirType, long? DirActive);

    private sealed record NpiRow(string Npi, sbyte EntityType, string? LastName, string? FirstName, string? MiddleName, string? NameSuffix,
        string? OrgName, string? PrimaryTaxonomyCode, string? City, string? State);

    private sealed record CountRow(string Kind, int Cycle, string ScopeSeverity, int Deficiencies, int Complaint);

    private sealed record SurveyRow(string Kind, DateTime SurveyDate, int Cycle, int Deficiencies, int Complaint, string? MostSevere);

    private sealed record CitationRow(string Kind, DateTime SurveyDate, int Cycle, string Prefix, string Tag, string ScopeSeverity, string? Category,
        string? Description, string? Corrected, DateTime? CorrectionDate, long Complaint, long InfectionControl);

    private sealed record PenaltyRow(DateTime? PenaltyDate, string? PenaltyType, double? FineAmount, DateTime? DenialStart, int? DenialDays);

    /// <summary>A CMS associate ID: up to ten digits (the files drop leading zeros; stored padded to ten).</summary>
    public static string? NormalizeOwnerId(string? id)
    {
        var text = id?.Trim();
        return text is { Length: >= 1 and <= 10 } && text.All(char.IsAsciiDigit) ? text.PadLeft(10, '0') : null;
    }

    /// <summary>The checks /owners and /api/v1/owners make; empty when the filter is fine.</summary>
    public static Dictionary<string, string[]> Validate(string? q, string? kind, int page, int pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (q?.Trim().Length > MaxQueryLength)
        {
            errors["q"] = [$"At most {MaxQueryLength} characters."];
        }


        if (!string.IsNullOrWhiteSpace(kind) && !OwnerKinds.IsKnown(kind.Trim().ToLowerInvariant()))
        {
            errors["kind"] = [$"One of {string.Join(", ", OwnerKinds.All.Select(k => k.Code))}."];
        }

        if (page < 1)
        {
            errors["page"] = ["Must be 1 or more."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"Must be 1–{MaxPageSize}."];
        }

        return errors;
    }

    /// <summary>
    /// Organizations whose name or doing-business-as name contains <paramref name="q"/> (any part, case- and accent-insensitive) or
    /// whose associate ID it is; every owning organization when it's empty. Those owning the most facilities first. People are never
    /// listed (owner, 2026-10-15: facility_owner_entity holds organizations only).
    /// </summary>
    public async Task<OwnerPage> SearchOwnersAsync(string? q, string? kind, int page, int pageSize, CancellationToken ct)
    {
        if (Validate(q, kind, page, pageSize) is { Count: > 0 } errors)
        {
            throw new ArgumentException(string.Join(" ", errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}")));
        }

        var text = q?.Trim() ?? "";
        var conditions = new List<string> { "e.owner_type = 'O'" };
        if (text.Length > 0)
        {
            conditions.Add(NormalizeOwnerId(text) is null ? "(e.name LIKE @like OR e.dba_name LIKE @like)" : "(e.owner_id = @id OR e.name LIKE @like)");
        }


        if (!string.IsNullOrWhiteSpace(kind))
        {
            conditions.Add("FIND_IN_SET(@kind, e.owner_kinds) > 0");
        }

        var where = "WHERE " + string.Join(" AND ", conditions);
        var parameters = new
        {
            like = "%" + SearchQuery.Prefix(text),
            id = NormalizeOwnerId(text),

            kind = kind?.Trim().ToLowerInvariant(),
            offset = (page - 1) * pageSize,
            pageSize,
        };
        await using var connection = await OpenAsync(ct);
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT COUNT(*) FROM facility_owner_entity e {where}", parameters, cancellationToken: ct));
        var rows = await connection.QueryAsync<EntityRow>(new CommandDefinition(
            $"""
            SELECT e.owner_id AS OwnerId, e.owner_type AS OwnerType, e.name AS Name, e.dba_name AS DbaName, e.city AS City, e.state AS State,
                   e.owner_kinds AS OwnerKinds, e.facilities AS Facilities, e.owned AS Owned
            FROM facility_owner_entity e {where}
            ORDER BY e.owned DESC, e.facilities DESC, e.name, e.owner_id
            LIMIT @offset, @pageSize
            """, parameters, cancellationToken: ct));
        return new OwnerPage([.. rows.Select(Summary)], page, pageSize, total);
    }

    /// <summary>
    /// One organization owner by its associate ID, or null when no facility lists it or CMS lists it as a person anywhere: people
    /// are shown only by name and role on the facility pages, never with a page of their own (owner, 2026-10-15).
    /// </summary>
    public async Task<OwnerDetail?> GetOwnerAsync(string id, CancellationToken ct)
    {
        if (NormalizeOwnerId(id) is not { } ownerId)
        {
            return null;
        }

        await using var connection = await OpenAsync(ct);
        var entity = await connection.QueryFirstOrDefaultAsync<EntityRow>(new CommandDefinition(
            """
            SELECT owner_id AS OwnerId, owner_type AS OwnerType, name AS Name, dba_name AS DbaName, city AS City, state AS State,
                   owner_kinds AS OwnerKinds, facilities AS Facilities, owned AS Owned
            FROM facility_owner_entity WHERE owner_id = @id AND owner_type = 'O'
            """, new { id = ownerId }, cancellationToken: ct));
        // An owner only of a facility it sold (a previous owner in a change of ownership): its page lists those changes.
        if (entity is null && await connection.QueryFirstOrDefaultAsync<PreviousRow>(new CommandDefinition(
                """
                SELECT enrollment_id AS EnrollmentId, owner_id AS OwnerId, owner_type AS OwnerType, name AS Name, role_code AS RoleCode,
                       role_text AS RoleText, percentage AS Percentage
                FROM facility_ownership_change_owner WHERE owner_id = @id AND owner_type = 'O' ORDER BY name IS NULL, enrollment_id LIMIT 1
                """, new { id = ownerId }, cancellationToken: ct)) is { } former)
        {
            entity = new EntityRow(former.OwnerId, former.OwnerType, OwnerName(former.Name, former.OwnerId), null, null, null, null, 0, 0);
        }
        // No page for an associate ID CMS lists as a person anywhere, whatever another row says.
        if (entity is null || await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                SELECT EXISTS (SELECT 1 FROM facility_owner WHERE owner_id = @id AND owner_type = 'I')
                    OR EXISTS (SELECT 1 FROM facility_ownership_change_owner WHERE owner_id = @id AND owner_type = 'I')
                """, new { id = ownerId }, cancellationToken: ct)) != 0)
        {
            return null;
        }

        var owned = (await connection.QueryAsync<OwnedRow>(new CommandDefinition(
            """
            SELECT o.enrollment_id AS EnrollmentId, o.kind AS Kind, o.facility_name AS FacilityName, o.role_code AS RoleCode, o.role_text AS RoleText,
                   o.percentage AS Percentage, o.association_date AS AssociationDate, e.ccn AS Ccn, d.name AS DirName, d.city AS DirCity,
                   d.state AS DirState, d.type AS DirType, CAST(d.active AS SIGNED) AS DirActive, e.state AS EnrollmentState, o.period_end AS PeriodEnd
            FROM facility_owner o
            LEFT JOIN cms_facility_enrollment e ON e.enrollment_id = o.enrollment_id
            LEFT JOIN directory_facility d ON d.ccn = e.ccn
            WHERE o.owner_id = @id
            """, new { id = ownerId }, cancellationToken: ct))).ToList();
        var facilities = owned
            .GroupBy(r => r.EnrollmentId, StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First();
                var roles = g.Select(r => new OwnerRole(r.RoleCode, OwnerRoles.Label(r.RoleCode, r.RoleText), OwnerRoles.Group(r.RoleCode), r.Percentage,
                        Date(r.AssociationDate)))
                    .OrderBy(r => OwnerRoles.Order(r.RoleGroup)).ThenBy(r => r.RoleCode, StringComparer.Ordinal)
                    .ToList();
                return new OwnerFacility(first.Ccn, first.EnrollmentId, first.DirType ?? first.Kind, first.DirName ?? first.FacilityName ?? first.EnrollmentId,
                    first.DirCity, first.DirState ?? first.EnrollmentState, first.DirActive is null ? null : first.DirActive != 0, roles);
            })
            // Ownership first, the largest stake first, then by name.
            .OrderBy(f => f.Roles.Any(r => r.RoleGroup == OwnerRoles.Owner) ? 0 : f.Roles.Any(r => r.RoleGroup == OwnerRoles.Control) ? 1 : 2)
            .ThenByDescending(f => f.Roles.Max(r => r.Percentage ?? 0))
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var asOf = owned.Max(r => r.PeriodEnd);

        // By the associate ID only: the NPIs enrolled under it (Medicare enrollment), the facilities it is, its group page.
        var npis = (await connection.QueryAsync<NpiRow>(new CommandDefinition(
                """
                SELECT p.npi AS Npi, p.entity_type AS EntityType, p.last_name AS LastName, p.first_name AS FirstName, p.middle_name AS MiddleName,
                       p.name_suffix AS NameSuffix, p.org_name AS OrgName, p.primary_taxonomy_code AS PrimaryTaxonomyCode, l.city AS City, l.state AS State
                FROM (SELECT DISTINCT npi FROM medicare_enrollment WHERE pac_id = @id) m
                JOIN provider p ON p.npi = m.npi AND p.entity_type = 2
                LEFT JOIN provider_location l ON l.npi = p.npi AND l.is_primary = 1
                ORDER BY p.sort_name LIMIT 100
                """, new { id = ownerId }, cancellationToken: ct)))
            .Select(r => new OrganizationNpi(r.Npi, ProviderNames.Display(r.EntityType, r.LastName, r.FirstName, r.MiddleName, r.NameSuffix, r.OrgName),
                PlainSpecialty(r.PrimaryTaxonomyCode), r.City, r.State))
            .ToList();
        var enrolledAs = (await connection.QueryAsync<SelfRow>(new CommandDefinition(
                """
                SELECT e.enrollment_id AS EnrollmentId, e.kind AS Kind, e.ccn AS Ccn, e.org_name AS OrgName, e.state AS State, d.name AS DirName,
                       d.city AS DirCity, d.state AS DirState, d.type AS DirType, CAST(d.active AS SIGNED) AS DirActive
                FROM cms_facility_enrollment e LEFT JOIN directory_facility d ON d.ccn = e.ccn
                WHERE e.associate_id = @id ORDER BY e.ccn
                """, new { id = ownerId }, cancellationToken: ct)))
            .Select(r => new OwnerFacility(r.Ccn, r.EnrollmentId, r.DirType ?? r.Kind, r.DirName ?? r.OrgName ?? r.EnrollmentId, r.DirCity, r.DirState ?? r.State,
                r.DirActive is null ? null : r.DirActive != 0, []))
            .ToList();
        var group = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT pac_id FROM directory_group WHERE pac_id = @id", new { id = ownerId }, cancellationToken: ct));

        // The changes it was the buyer or the seller in, or an owner of the seller before.
        var changes = await ChangesAsync(connection,
            "c.buyer_associate_id = @id OR c.seller_associate_id = @id OR c.seller_enrollment_id IN (SELECT enrollment_id FROM facility_ownership_change_owner WHERE owner_id = @id)",
            new { id = ownerId }, ct);
        return new OwnerDetail(Summary(entity), Date(asOf), facilities, npis, enrolledAs, group, changes);
    }

    /// <summary>A facility's owners and ownership changes, or null when CMS lists neither for the CCN.</summary>
    public async Task<FacilityOwnership?> GetFacilityOwnershipAsync(string ccn, CancellationToken ct)
    {
        if (!OrganizationService.IsCcn(ccn))
        {
            return null;
        }

        await using var connection = await OpenAsync(ct);
        return (await ForCcnsAsync(connection, [ccn.ToUpperInvariant()], ct)).GetValueOrDefault(ccn.ToUpperInvariant());
    }

    /// <summary>
    /// The owners and ownership changes of several facilities at once (an organization's page, docs/PRODUCT_PLAN.md step 3.9), by
    /// CCN; a CCN for which CMS lists neither is left out.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, FacilityOwnership>> GetFacilitiesOwnershipAsync(IReadOnlyCollection<string> ccns, CancellationToken ct)
    {
        var valid = ccns.Where(OrganizationService.IsCcn).Select(c => c.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (valid.Count == 0)
        {
            return new Dictionary<string, FacilityOwnership>();
        }

        await using var connection = await OpenAsync(ct);
        return await ForCcnsAsync(connection, valid, ct);
    }

    private static async Task<Dictionary<string, FacilityOwnership>> ForCcnsAsync(MySqlConnection connection, IReadOnlyList<string> ccns, CancellationToken ct)
    {
        var enrollments = (await connection.QueryAsync<EnrollmentRow>(new CommandDefinition(
            """
            SELECT enrollment_id AS EnrollmentId, org_name AS OrgName, associate_id AS AssociateId, ccn AS Ccn
            FROM cms_facility_enrollment WHERE ccn IN @ccns ORDER BY enrollment_id
            """, new { ccns }, cancellationToken: ct))).ToList();
        var owners = enrollments.Count == 0
            ? []
            : (await connection.QueryAsync<OwnerRow>(new CommandDefinition(
                """
                SELECT owner_id AS OwnerId, owner_type AS OwnerType, name AS Name, dba_name AS DbaName, title AS Title, role_code AS RoleCode,
                       role_text AS RoleText, percentage AS Percentage, association_date AS AssociationDate, owner_kinds AS OwnerKinds, city AS City,
                       state AS State, period_end AS PeriodEnd, enrollment_id AS EnrollmentId
                FROM facility_owner WHERE enrollment_id IN @ids
                """, new { ids = enrollments.Select(e => e.EnrollmentId).ToList() }, cancellationToken: ct))).ToList();
        var changes = await ChangesAsync(connection, "c.buyer_ccn IN @ccns OR c.seller_ccn IN @ccns", new { ccns }, ct);
        var changesAsOf = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(period_end) FROM facility_ownership_change", cancellationToken: ct));
        var ownersOf = owners.ToLookup(o => o.EnrollmentId, StringComparer.Ordinal);
        var result = new Dictionary<string, FacilityOwnership>(StringComparer.Ordinal);
        foreach (var ccn in ccns)
        {
            var mine = enrollments.Where(e => string.Equals(e.Ccn, ccn, StringComparison.OrdinalIgnoreCase)).ToList();
            var itsChanges = changes.Where(c => string.Equals(c.BuyerCcn, ccn, StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(c.SellerCcn, ccn, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count == 0 && itsChanges.Count == 0)
            {
                continue;
            }

            var rows = mine.SelectMany(e => ownersOf[e.EnrollmentId]).ToList();
            var list = rows
                .Select(o => o.OwnerType == "O"
                    ? new FacilityOwner(o.OwnerId, true, OwnerName(o.Name, o.OwnerId), o.DbaName, o.Title, o.RoleCode, OwnerRoles.Label(o.RoleCode, o.RoleText),
                        OwnerRoles.Group(o.RoleCode), o.Percentage, Date(o.AssociationDate), OwnerKinds.Parse(o.OwnerKinds), o.City, o.State)
                    : new FacilityOwner(null, false, PersonName(o.Name), null, o.Title, o.RoleCode, OwnerRoles.Label(o.RoleCode, o.RoleText),
                        OwnerRoles.Group(o.RoleCode), o.Percentage, Date(o.AssociationDate), [], null, null))
                // Each group in the page's order; the largest stake, organizations and then names first within it.
                .OrderBy(o => OwnerRoles.Order(o.RoleGroup))
                .ThenByDescending(o => o.Percentage ?? -1)
                .ThenByDescending(o => o.IsOrganization)
                .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.RoleCode, StringComparer.Ordinal)
                .ToList();
            var first = mine.FirstOrDefault();
            result[ccn] = new FacilityOwnership(ccn, first?.EnrollmentId, first?.OrgName, first?.AssociateId, Date(rows.Max(o => o.PeriodEnd)), list,
                Date(changesAsOf), itsChanges);
        }

        return result;
    }

    /// <summary>A nursing home's inspections and penalties, or null when Care Compare lists none for the CCN.</summary>
    public async Task<NursingHomeInspections?> GetInspectionsAsync(string ccn, CancellationToken ct)
    {
        if (!OrganizationService.IsCcn(ccn))
        {
            return null;
        }

        ccn = ccn.ToUpperInvariant();
        await using var connection = await OpenAsync(ct);
        var parameters = new { ccn };
        var counts = (await connection.QueryAsync<CountRow>(new CommandDefinition(
            """
            SELECT kind AS Kind, cycle AS Cycle, scope_severity AS ScopeSeverity, deficiencies AS Deficiencies, complaint AS Complaint
            FROM nh_deficiency_count WHERE ccn = @ccn ORDER BY kind DESC, cycle, scope_severity
            """, parameters, cancellationToken: ct))).ToList();
        var surveys = (await connection.QueryAsync<SurveyRow>(new CommandDefinition(
            """
            SELECT kind AS Kind, survey_date AS SurveyDate, cycle AS Cycle, deficiencies AS Deficiencies, complaint AS Complaint, most_severe AS MostSevere
            FROM nh_survey WHERE ccn = @ccn ORDER BY survey_date DESC, kind DESC
            """, parameters, cancellationToken: ct))).ToList();
        var citations = (await connection.QueryAsync<CitationRow>(new CommandDefinition(
            """
            SELECT c.kind AS Kind, c.survey_date AS SurveyDate, c.cycle AS Cycle, c.prefix AS Prefix, c.tag AS Tag, c.scope_severity AS ScopeSeverity,
                   t.category AS Category, t.description AS Description, c.corrected AS Corrected, c.correction_date AS CorrectionDate,
                   CAST(c.complaint AS SIGNED) AS Complaint, CAST(c.infection_control AS SIGNED) AS InfectionControl
            FROM nh_citation c LEFT JOIN nh_deficiency_tag t ON t.prefix = c.prefix AND t.tag = c.tag
            WHERE c.ccn = @ccn ORDER BY c.scope_severity DESC, c.survey_date DESC, c.prefix, c.tag
            """, parameters, cancellationToken: ct))).ToList();
        var penalties = (await connection.QueryAsync<PenaltyRow>(new CommandDefinition(
            """
            SELECT penalty_date AS PenaltyDate, penalty_type AS PenaltyType, fine_amount AS FineAmount, denial_start AS DenialStart, denial_days AS DenialDays
            FROM nh_penalty WHERE ccn = @ccn ORDER BY penalty_date DESC, id
            """, parameters, cancellationToken: ct))).ToList();
        if (counts.Count == 0 && surveys.Count == 0 && penalties.Count == 0)
        {
            return null;
        }

        var version = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT version FROM reference_data WHERE source = @source", new { source = InspectionsSource }, cancellationToken: ct));
        return new NursingHomeInspections(ccn, UpdatedOn(version),
            [.. counts.Select(c => new InspectionCount(c.Kind, c.Cycle, c.ScopeSeverity, c.Deficiencies, c.Complaint))],
            [.. surveys.Select(s => new InspectionSurvey(s.Kind, DateOnly.FromDateTime(s.SurveyDate), s.Cycle, s.Deficiencies, s.Complaint, s.MostSevere))],
            [.. citations.Select(c => new InspectionCitation(c.Kind, DateOnly.FromDateTime(c.SurveyDate), c.Cycle, c.Prefix + c.Tag, c.ScopeSeverity, c.Category,
                c.Description, c.Corrected, Date(c.CorrectionDate), c.Complaint != 0, c.InfectionControl != 0))],
            [.. penalties.Select(p => new NursingHomePenalty(Date(p.PenaltyDate), p.PenaltyType ?? "Penalty", p.FineAmount, Date(p.DenialStart), p.DenialDays))]);
    }

    /// <summary>CMS's update date at the start of the stored version ("2026-09-30 NH_HealthCitations_Sep2026_2.csv | …").</summary>
    public static DateOnly? UpdatedOn(string? version) =>
        version is { Length: >= 10 } && DateOnly.TryParseExact(version[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>"Change of ownership", "Acquisition or merger", or CMS's text.</summary>
    public static string ChangeType(string? code, string? text) => code?.Trim() switch
    {
        "CH" => "Change of ownership",
        "AM" => "Acquisition or merger",
        _ => string.IsNullOrWhiteSpace(text) ? "Change of ownership" : char.ToUpperInvariant(text.Trim()[0]) + text.Trim()[1..].ToLowerInvariant(),
    };

    private static async Task<IReadOnlyList<OwnershipChange>> ChangesAsync(MySqlConnection connection, string where, object parameters, CancellationToken ct)
    {
        var rows = (await connection.QueryAsync<ChangeRow>(new CommandDefinition(
            $"""
            SELECT CAST(c.id AS SIGNED) AS Id, c.kind AS Kind, c.chow_type_code AS ChowTypeCode, c.chow_type AS ChowType, c.effective_date AS EffectiveDate,
                   c.buyer_enrollment_id AS BuyerEnrollmentId, c.buyer_ccn AS BuyerCcn, c.buyer_npi AS BuyerNpi, c.buyer_associate_id AS BuyerAssociateId,
                   c.buyer_name AS BuyerName, c.buyer_dba_name AS BuyerDbaName, c.seller_enrollment_id AS SellerEnrollmentId, c.seller_ccn AS SellerCcn,
                   c.seller_npi AS SellerNpi, c.seller_associate_id AS SellerAssociateId, c.seller_name AS SellerName, c.seller_dba_name AS SellerDbaName,
                   c.period_end AS PeriodEnd
            FROM facility_ownership_change c WHERE {where}
            ORDER BY c.effective_date DESC, c.id LIMIT 200
            """, parameters, cancellationToken: ct))).ToList();
        var sellers = rows.Select(r => r.SellerEnrollmentId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var previous = sellers.Count == 0
            ? []
            : (await connection.QueryAsync<PreviousRow>(new CommandDefinition(
                """
                SELECT enrollment_id AS EnrollmentId, owner_id AS OwnerId, owner_type AS OwnerType, name AS Name, role_code AS RoleCode,
                       role_text AS RoleText, percentage AS Percentage
                FROM facility_ownership_change_owner WHERE enrollment_id IN @sellers
                """, new { sellers }, cancellationToken: ct))).ToList();
        var bySeller = previous.ToLookup(p => p.EnrollmentId, StringComparer.Ordinal);
        return rows.Select(r => new OwnershipChange(Date(r.EffectiveDate), ChangeType(r.ChowTypeCode, r.ChowType), r.Kind, r.BuyerName, r.BuyerDbaName,
                r.BuyerAssociateId, r.BuyerCcn, r.BuyerNpi, r.SellerName, r.SellerDbaName, r.SellerAssociateId, r.SellerCcn, r.SellerNpi,
                r.SellerEnrollmentId is null
                    ? []
                    : bySeller[r.SellerEnrollmentId]
                        .Select(p => new PreviousOwner(p.OwnerType == "O" ? p.OwnerId : null, p.OwnerType == "O",
                            p.OwnerType == "O" ? OwnerName(p.Name, p.OwnerId) : PersonName(p.Name), p.RoleCode, OwnerRoles.Label(p.RoleCode, p.RoleText),
                            OwnerRoles.Group(p.RoleCode), p.Percentage))
                        .OrderBy(p => OwnerRoles.Order(p.RoleGroup)).ThenByDescending(p => p.Percentage ?? -1)
                        .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList()))
            .ToList();
    }

    private static OwnerSummary Summary(EntityRow e) =>
        new(e.OwnerId, OwnerName(e.Name, e.OwnerId), e.DbaName, e.City, e.State, OwnerKinds.Parse(e.OwnerKinds), e.Facilities, e.Owned);

    private static string OwnerName(string? name, string id) => string.IsNullOrWhiteSpace(name) ? "Owner " + id : name.Trim();

    private static string PersonName(string? name) => string.IsNullOrWhiteSpace(name) ? "Name not listed" : name.Trim();

    private static DateOnly? Date(DateTime? value) => value is { } d ? DateOnly.FromDateTime(d) : null;

    private static string? PlainSpecialty(string? code) =>
        SpecialtyCategories.Default.Place(code) is { } p && p.Category != SpecialtyCategories.FallbackCategory ? p.Singular : null;

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
