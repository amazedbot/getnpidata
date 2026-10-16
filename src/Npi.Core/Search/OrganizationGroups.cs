using System.Globalization;
using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>
/// What tied an NPI to the other NPIs of its organization (docs/PRODUCT_PLAN.md step 3.8; <c>org_group_member.joined_by</c>); stored
/// bits, never renumber them.
/// </summary>
[Flags]
public enum OrganizationTie
{
    None = 0,

    /// <summary>The same legal business name at the same street address (house number, street and ZIP code, abbreviations aside).</summary>
    NameAddress = 1,

    /// <summary>CMS enrolls both for one Medicare-certified facility (a CCN; a hospital's psychiatric or rehabilitation unit counts as the hospital's), in one ZIP code.</summary>
    Ccn = 2,

    /// <summary>Both are enrolled in Medicare under one organization (PECOS associate ID, the PAC ID), in one ZIP code.</summary>
    MedicareEnrollment = 4,

    /// <summary>NPPES names one as the other's parent organization, or both name the same parent (legal name or tax ID), in one ZIP code.</summary>
    Parent = 8,
}

/// <summary>
/// Combined organization profiles (docs/PRODUCT_PLAN.md step 3.8): the rules shared by the loader, which groups the NPIs
/// (<c>OrgGroupBuilder</c>), and the site, which says how they were grouped and what each one is.
/// </summary>
public static class OrganizationGroups
{
    /// <summary>A group with more NPIs than this is logged as a warning by the loader (kept, but worth a look).</summary>
    public const int LargeGroup = 100;

    /// <summary>The <c>search_total</c> row with the number of members that aren't their organization's main NPI (written by the loader).</summary>
    public const string MembersTotal = "org_group:members";

    /// <summary>The ties in plain words, in this order.</summary>
    public static readonly IReadOnlyList<(OrganizationTie Tie, string Text)> TieTexts =
    [
        (OrganizationTie.NameAddress, "same name and address"),
        (OrganizationTie.Ccn, "shared Medicare-certified facility (CCN)"),
        (OrganizationTie.MedicareEnrollment, "same Medicare enrollment (PAC ID)"),
        (OrganizationTie.Parent, "same parent organization"),
    ];

    /// <summary>"same name and address, same Medicare enrollment (PAC ID)".</summary>
    public static string Ties(OrganizationTie ties) =>
        string.Join(", ", TieTexts.Where(t => (ties & t.Tie) != 0).Select(t => t.Text));

    /// <summary>How the NPIs are grouped, for the profile's note.</summary>
    public const string Rule =
        "NPIs are grouped when they have the same legal business name at the same street address, or when CMS or NPPES ties them " +
        "together in the same ZIP code: a shared Medicare-certified facility (CCN; a hospital's units count as the hospital), the same " +
        "Medicare enrollment (PAC ID), or the same parent organization. Similar names alone, or the same name in the same city, are " +
        "never enough. Two NPIs tied only by an enrollment or a parent organization are not grouped when they have the same primary " +
        "specialty at different addresses (a chain's stores, a group's offices).";

    /// <summary>How the main NPI is chosen, for the profile's note.</summary>
    public const string MainRule =
        "The main NPI is the one CMS ties to a hospital (its CCN), else to any certified facility, else one enrolled in or billing " +
        "Medicare, else one with another sign of current practice, else the most recently updated in NPPES.";

    /// <summary>
    /// The facility a CCN belongs to: a hospital unit's or swing beds' CCN (a letter in the third place: S psychiatric, T
    /// rehabilitation, U, W, Y swing beds; M, R, Z the same in a critical access hospital) stands for its hospital, "33S014" →
    /// "330014", "33Z305" → "331305" (CMS State Operations Manual §2779A); any other CCN for itself, upper-cased.
    /// </summary>
    public static string FacilityKey(string ccn)
    {
        var id = ccn.Trim().ToUpperInvariant();
        if (id.Length == 6 && char.IsAsciiDigit(id[0]) && char.IsAsciiDigit(id[1]) && id[3..].All(char.IsAsciiDigit))
        {
            return id[2] switch
            {
                'S' or 'T' or 'U' or 'W' or 'Y' => string.Concat(id.AsSpan(0, 2), "0", id.AsSpan(3)),
                'M' or 'R' or 'Z' => string.Concat(id.AsSpan(0, 2), "1", id.AsSpan(3)),
                _ => id,
            };
        }

        return id;
    }

    /// <summary>True for a hospital unit's or swing beds' CCN (<see cref="FacilityKey"/> maps it to its hospital).</summary>
    public static bool IsUnitCcn(string ccn) => !string.Equals(FacilityKey(ccn), ccn.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>"Psychiatric unit", "Hospital", "Nursing home": what a CCN of this kind (<c>cms_facility_npi.kind</c> …) is.</summary>
    public static string FacilityName(string ccn, string? kind)
    {
        var id = ccn.Trim().ToUpperInvariant();
        if (IsUnitCcn(id))
        {
            return id[2] switch
            {
                'S' or 'M' => "Psychiatric unit",
                'T' or 'R' => "Rehabilitation unit",
                _ => "Swing beds",
            };
        }

        return FacilityTypes.Find(kind)?.Singular ?? FacilityTypes.Get(FacilityTypes.FromCcn(id).Type).Singular;
    }

    /// <summary>
    /// A member's role in plain words: "Main NPI"; "Psychiatric unit (CCN 33S014)" for an NPI CMS ties to a facility; "Possible
    /// duplicate registration" for an NPI with the main NPI's primary specialty, no facility and no sign of practice; else its plain
    /// specialty ("Pharmacy", "Clinic").
    /// </summary>
    public static string Role(OrganizationRoleInputs member, OrganizationRoleInputs main)
    {
        if (member.IsMain)
        {
            return member.Ccn is { } c ? $"Main NPI: {FacilityName(c, member.FacilityKind)} (CCN {c})" : "Main NPI";
        }

        if (member.Ccn is { } ccn)
        {
            return $"{FacilityName(ccn, member.FacilityKind)} (CCN {ccn})";
        }

        if (member.Taxonomy is not null && member.Taxonomy == main.Taxonomy && member.Activity == QualityActivity.None)
        {
            return "Possible duplicate registration";
        }

        return SpecialtyCategories.Default.Place(member.Taxonomy) is { } p && p.Category != SpecialtyCategories.FallbackCategory
            ? p.Singular
            : "Other NPI";
    }
}

/// <summary>What <see cref="OrganizationGroups.Role"/> reads about a member.</summary>
public sealed record OrganizationRoleInputs(bool IsMain, string? Ccn, string? FacilityKind, string? Taxonomy, QualityActivity Activity);

/// <summary>One NPI of an organization, for the combined profile's table.</summary>
public sealed record OrganizationMember(string Npi, string Name, string Role, string? Specialty, string? Address, string? City, string? State,
    string? Zip, DateOnly? LastUpdateDate)
{
    public bool IsMain { get; init; }

    /// <summary>The CCN CMS ties it to, when there is one.</summary>
    public string? Ccn { get; init; }

    /// <summary>The record-quality label ("Current", "Check", "Stale"); null when not labelled yet.</summary>
    public string? Quality { get; init; }

    /// <summary>What tied it to the others, in plain words ("same name and address, same Medicare enrollment (PAC ID)").</summary>
    public string JoinedBy { get; init; } = "";
}

/// <summary>An NPI's organization, on its provider record: the main NPI, the organization's name and how many NPIs it has.</summary>
public sealed record ProviderOrganization(string MainNpi, string Name, int Npis);

/// <summary>
/// A combined organization profile (docs/PRODUCT_PLAN.md step 3.8): the main NPI's record, every NPI of the organization with its
/// role, and what the NPIs hold together: certified facilities (every CCN), Medicare groups (every PAC ID) and practice locations.
/// </summary>
public sealed record OrganizationProfile(string MainNpi, string Name, IReadOnlyList<OrganizationMember> Npis)
{
    /// <summary>The main NPI's own record (as <c>/api/v1/providers/{npi}</c> gives it).</summary>
    public ProviderDetail? Main { get; init; }

    /// <summary>The certified facilities CMS ties any of the NPIs to.</summary>
    public IReadOnlyList<CertifiedFacility> Facilities { get; init; } = [];

    /// <summary>The Medicare groups (PAC IDs) any of the NPIs is enrolled under, largest first.</summary>
    public IReadOnlyList<OrganizationGroup> Groups { get; init; } = [];

    /// <summary>The distinct practice locations of all the NPIs (primary first).</summary>
    public IReadOnlyList<ProviderLocation> Locations { get; init; } = [];

    /// <summary>How the NPIs were grouped and how the main NPI was chosen.</summary>
    public string Grouping { get; init; } = OrganizationGroups.Rule + " " + OrganizationGroups.MainRule;
}

/// <summary>Reads the organization groups the loader built (<c>org_group_member</c>, docs/PRODUCT_PLAN.md step 3.8).</summary>
public sealed class OrganizationGroupService(string connectionString)
{
    private sealed record MemberRow(string Npi, string MainNpi, sbyte IsMain, string? Ccn, string? FacilityKind, short JoinedBy, sbyte EntityType,
        string? OrgName, string? PrimaryTaxonomyCode, DateTime? LastUpdateDate, string? Address1, string? City, string? State, string? Zip5,
        string? Zip4, string? PostalCode, sbyte? Label, short? Activity);

    /// <summary>The organization of an NPI (main NPI, name, size); null when the NPI is the only one of its organization or not active.</summary>
    public async Task<ProviderOrganization?> GetMembershipAsync(string npi, CancellationToken ct)
    {
        if (!InputFormats.IsNpi(npi))
        {
            return null;
        }

        await using var connection = await OpenAsync(ct);
        return await MembershipAsync(connection, npi, ct);
    }

    internal static async Task<ProviderOrganization?> MembershipAsync(MySqlConnection connection, string npi, CancellationToken ct)
    {
        var row = await connection.QueryFirstOrDefaultAsync<(string MainNpi, int Members, string? Name)?>(new CommandDefinition(
            """
            SELECT g.main_npi, g.members, p.org_name FROM org_group_member g LEFT JOIN provider p ON p.npi = g.main_npi WHERE g.npi = @npi
            """, new { npi }, cancellationToken: ct));
        return row is { } r ? new ProviderOrganization(r.MainNpi, r.Name ?? "", r.Members) : null;
    }

    /// <summary>The main NPI of an NPI's organization, null when the NPI is in none.</summary>
    public async Task<string?> GetMainNpiAsync(string npi, CancellationToken ct)
    {
        if (!InputFormats.IsNpi(npi))
        {
            return null;
        }

        await using var connection = await OpenAsync(ct);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT main_npi FROM org_group_member WHERE npi = @npi", new { npi }, cancellationToken: ct));
    }

    /// <summary>The main NPIs of the organizations of these NPIs (those in one), by NPI.</summary>
    public async Task<IReadOnlyDictionary<string, ProviderOrganization>> GetMembershipsAsync(IReadOnlyCollection<string> npis, CancellationToken ct)
    {
        if (npis.Count == 0)
        {
            return new Dictionary<string, ProviderOrganization>();
        }

        await using var connection = await OpenAsync(ct);
        return (await connection.QueryAsync<(string Npi, string MainNpi, int Members, string? Name)>(new CommandDefinition(
                "SELECT g.npi, g.main_npi, g.members, p.org_name FROM org_group_member g LEFT JOIN provider p ON p.npi = g.main_npi WHERE g.npi IN @npis",
                new { npis }, cancellationToken: ct)))
            .ToDictionary(r => r.Npi, r => new ProviderOrganization(r.MainNpi, r.Name ?? "", r.Members), StringComparer.Ordinal);
    }

    /// <summary>
    /// The combined profile of the organization <paramref name="npi"/> belongs to (any member's NPI), without the main NPI's own
    /// record and the combined facts (<see cref="CombinedAsync"/> adds them); null when the NPI is in no group.
    /// </summary>
    public async Task<OrganizationProfile?> GetAsync(string npi, CancellationToken ct)
    {
        if (!InputFormats.IsNpi(npi))
        {
            return null;
        }

        await using var connection = await OpenAsync(ct);
        var main = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT main_npi FROM org_group_member WHERE npi = @npi", new { npi }, cancellationToken: ct));
        if (main is null)
        {
            return null;
        }

        var rows = (await connection.QueryAsync<MemberRow>(new CommandDefinition(
            """
            SELECT g.npi AS Npi, g.main_npi AS MainNpi, g.is_main AS IsMain, g.ccn AS Ccn, g.facility_kind AS FacilityKind, g.joined_by AS JoinedBy,
                   p.entity_type AS EntityType, p.org_name AS OrgName, p.primary_taxonomy_code AS PrimaryTaxonomyCode, p.last_update_date AS LastUpdateDate,
                   l.address1 AS Address1, l.city AS City, l.state AS State, l.zip5 AS Zip5, l.zip4 AS Zip4, l.postal_code AS PostalCode,
                   q.label AS Label, q.activity AS Activity
            FROM org_group_member g
            JOIN provider p ON p.npi = g.npi
            LEFT JOIN provider_location l ON l.npi = g.npi AND l.is_primary = 1
            LEFT JOIN provider_quality q ON q.npi = g.npi
            WHERE g.main_npi = @main
            ORDER BY g.is_main DESC, g.ccn IS NULL, p.org_name, g.npi
            """, new { main }, cancellationToken: ct))).ToList();
        if (rows.Count == 0)
        {
            return null; // replaced by a concurrent rebuild
        }

        static OrganizationRoleInputs Inputs(MemberRow r) =>
            new(r.IsMain != 0, r.Ccn, r.FacilityKind, r.PrimaryTaxonomyCode, (QualityActivity)(r.Activity ?? 0));
        var mainRow = rows.FirstOrDefault(r => r.IsMain != 0) ?? rows[0];
        var members = rows.Select(r => new OrganizationMember(r.Npi, r.OrgName ?? "", OrganizationGroups.Role(Inputs(r), Inputs(mainRow)),
                SpecialtyCategories.Default.Place(r.PrimaryTaxonomyCode) is { } p && p.Category != SpecialtyCategories.FallbackCategory ? p.Singular : r.PrimaryTaxonomyCode,
                r.Address1, r.City, r.State, ProviderNames.Zip(r.Zip5, r.Zip4, r.PostalCode),
                r.LastUpdateDate is { } d ? DateOnly.FromDateTime(d) : null)
            {
                IsMain = r.IsMain != 0,
                Ccn = r.Ccn,
                Quality = r.Label is { } label and > 0 ? RecordQuality.Name((QualityLabel)label) : null,
                JoinedBy = OrganizationGroups.Ties((OrganizationTie)r.JoinedBy),
            })
            .ToList();
        return new OrganizationProfile(mainRow.Npi, mainRow.OrgName ?? "", members);
    }

    /// <summary>
    /// The combined profile with the main NPI's own record (<paramref name="details"/>) and what all the NPIs hold together: certified
    /// facilities, Medicare groups (clinicians as the directory counts them) and distinct practice locations; null when the NPI is in no group.
    /// </summary>
    public async Task<OrganizationProfile?> GetCombinedAsync(string npi, ProviderDetailService details, CancellationToken ct)
    {
        var profile = await GetAsync(npi, ct);
        if (profile is null)
        {
            return null;
        }

        await using var connection = await OpenAsync(ct);
        var npis = profile.Npis.Select(m => m.Npi).ToList();
        var facilities = await ProviderDetailService.FacilitiesAsync(connection,
            $"FROM (SELECT DISTINCT ccn, kind FROM ({FacilityDirectory.BillingNpiSql}) b WHERE npi IN @npis) f", "1 = 1", new { npis }, ct);
        var groups = (await connection.QueryAsync<(string PacId, string? Name, int? Clinicians)>(new CommandDefinition(
                """
                SELECT e.pac_id,
                       COALESCE((SELECT MAX(dg.name) FROM directory_group dg WHERE dg.pac_id = e.pac_id),
                                (SELECT MAX(group_name) FROM cc_group WHERE org_pac_id = e.pac_id),
                                (SELECT MAX(group_name) FROM medicare_reassignment WHERE group_pac_id = e.pac_id),
                                MAX(e.org_name)),
                       (SELECT MAX(dg.clinicians) FROM directory_group dg WHERE dg.pac_id = e.pac_id)
                FROM medicare_enrollment e WHERE e.npi IN @npis AND e.pac_id IS NOT NULL GROUP BY e.pac_id
                """, new { npis }, cancellationToken: ct)))
            .Select(g => new OrganizationGroup(g.PacId, g.Name ?? "Group " + g.PacId, g.Clinicians ?? 0))
            .OrderByDescending(g => g.Clinicians).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var locations = (await connection.QueryAsync<(sbyte IsPrimary, string? Address1, string? Address2, string? City, string? State, string? Zip5, string? Zip4,
                string? PostalCode, string? CountryCode, string? Phone)>(new CommandDefinition(
                """
                SELECT is_primary, address1, address2, city, state, zip5, zip4, postal_code, country_code, phone
                FROM provider_location WHERE npi IN @npis ORDER BY is_primary DESC, npi = @main DESC, npi, id
                """, new { npis, main = profile.MainNpi }, cancellationToken: ct)))
            .DistinctBy(l => string.Join('|', Normalize(l.Address1), Normalize(l.City), Normalize(l.State), l.Zip5 ?? l.PostalCode))
            .Select(l => new ProviderLocation(l.IsPrimary != 0, l.Address1, l.Address2, l.City, l.State, ProviderNames.Zip(l.Zip5, l.Zip4, l.PostalCode),
                l.CountryCode, l.Phone))
            .ToList();
        return profile with
        {
            Main = await details.GetAsync(profile.MainNpi, ct),
            Facilities = facilities,
            Groups = groups,
            Locations = locations,
        };
    }

    private static string Normalize(string? value) =>
        new string((value ?? "").ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}

/// <summary>Search fragments for one row per organization (docs/PRODUCT_PLAN.md step 3.8).</summary>
internal static class OrganizationSearchSql
{
    /// <summary>
    /// A member that isn't its organization's main NPI, on alias {0} (anything with an npi column), whose main NPI matches the same
    /// conditions ({1}, written on alias <c>pm</c>): the search shows the main NPI instead. A member whose main NPI doesn't match
    /// is listed itself.
    /// </summary>
    public static string HiddenMember(string alias, string mainConditions) =>
        string.Format(CultureInfo.InvariantCulture,
            "EXISTS (SELECT /*+ NO_SEMIJOIN() */ 1 FROM org_group_member og JOIN provider pm ON pm.npi = og.main_npi WHERE og.npi = {0}.npi AND og.is_main = 0{1})",
            alias, mainConditions.Length == 0 ? "" : " AND " + mainConditions);
}
