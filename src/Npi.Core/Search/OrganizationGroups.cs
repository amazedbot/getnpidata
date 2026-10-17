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
        "The main NPI is the one CMS ties to a hospital (its CCN), else one registered as a hospital in NPPES with a sign of current " +
        "practice, else one CMS ties to any certified facility, else one enrolled in or billing Medicare, else one with another sign " +
        "of current practice, else the most recently updated in NPPES.";

    /// <summary>The note for an organization with one NPI (docs/PRODUCT_PLAN.md step 3.9): nothing to group.</summary>
    public const string SingleRule =
        "This organization has one active NPI. Its page brings together what CMS lists for it under that NPI: its Medicare-certified " +
        "facilities (CCNs) and Medicare groups (PAC IDs).";

    /// <summary>
    /// True for an NPI, on alias {0} of <c>provider</c>, that has an organization page (docs/PRODUCT_PLAN.md step 3.9, owner 2026-10-16):
    /// an organization NPI with a Medicare-certified facility (<see cref="FacilityDirectory.BillingNpiSql"/>'s tables), a Medicare group
    /// (a PAC ID it is enrolled under as an organization) or other NPIs of its organization (<c>org_group_member</c>).
    /// </summary>
    public static string HasPageSql(string alias) => string.Format(CultureInfo.InvariantCulture,
        """
        ({0}.entity_type = 2 AND (EXISTS (SELECT 1 FROM org_group_member x WHERE x.npi = {0}.npi)
            OR EXISTS (SELECT 1 FROM cms_facility_npi x WHERE x.npi = {0}.npi)
            OR EXISTS (SELECT 1 FROM cms_dialysis_npi x WHERE x.npi = {0}.npi)
            OR EXISTS (SELECT 1 FROM cms_asc x WHERE x.npi = {0}.npi AND x.ccn <> '{1}')
            OR EXISTS (SELECT 1 FROM medicare_enrollment x WHERE x.npi = {0}.npi AND x.pac_id IS NOT NULL AND x.org_name IS NOT NULL)))
        """, alias, FacilityMeasures.National);

    /// <summary>A hospital's NUCC taxonomy (the 28 group: general acute care, psychiatric, rehabilitation, long-term care …; not the 27 units).</summary>
    public static bool IsHospitalTaxonomy(string? code) => code is { Length: 10 } && code.StartsWith("28", StringComparison.Ordinal);

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

    /// <summary>
    /// For each key (a CCN or a PAC ID) and the NPIs it lists, the organization page covering most of those NPIs; ties go to the
    /// organization with more NPIs, then the lower main NPI. Keys whose NPIs have no page are left out.
    /// </summary>
    public static IReadOnlyDictionary<string, ProviderOrganization> PickPages(IEnumerable<(string Id, string Npi)> pairs,
        IReadOnlyDictionary<string, ProviderOrganization> pages) =>
        pairs.Where(p => pages.ContainsKey(p.Npi))
            .DistinctBy(p => (p.Id, p.Npi))
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => pages[p.Npi])
                .GroupBy(o => o.MainNpi, StringComparer.Ordinal)
                .OrderByDescending(o => o.Count()).ThenByDescending(o => o.First().Npis).ThenBy(o => o.Key, StringComparer.Ordinal)
                .First().First(), StringComparer.Ordinal);

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

    // The one page per organization (docs/PRODUCT_PLAN.md step 3.9): what OrganizationPageService adds to the combined profile.

    /// <summary>Every CCN CMS ties any of the NPIs to (also those Care Compare doesn't list), the main facility first.</summary>
    public IReadOnlyList<string> Ccns { get; init; } = [];

    /// <summary>
    /// The facilities' own records (ratings, how they compare, Medicare volume, a nursing home's inspections …), in <see cref="Ccns"/>'
    /// order, at most <see cref="OrganizationPageService.MaxFacilityDetails"/>; each facility's own page has the rest. Their clinician
    /// lists carry only the count (<see cref="Clinicians"/> lists them together) and their owners are in <see cref="Ownership"/>.
    /// </summary>
    public IReadOnlyList<FacilityProfile> FacilityProfiles { get; init; } = [];

    /// <summary>The owners, managers and ownership changes of all the facilities, each owner once; null when CMS lists none.</summary>
    public OrganizationOwnership? Ownership { get; init; }

    /// <summary>How many clinicians the organization has and where the ties come from (the list itself: the page and its CSV).</summary>
    public OrganizationClinicianSummary? Clinicians { get; init; }

    /// <summary>Medicare billing of the organization's NPIs (newest data year, and the last year in the older years), largest first.</summary>
    public IReadOnlyList<OrganizationMedicareNpi> MedicareActivity { get; init; } = [];

    /// <summary>
    /// Medicaid billing and enrollment of the organization's NPIs (T-MSIS, step 3.7): the newest data year, the last year billed and
    /// the states enrolled in, largest first.
    /// </summary>
    public IReadOnlyList<OrganizationMedicaidNpi> MedicaidActivity { get; init; } = [];

    /// <summary>The changes recorded for any of its NPIs, newest first (at most 200).</summary>
    public IReadOnlyList<OrganizationChange> Changes { get; init; } = [];

    /// <summary>The health systems (AHRQ Compendium) of its facilities, and of the facilities its Medicare groups bill for.</summary>
    public IReadOnlyList<HealthSystemLink> HealthSystems { get; init; } = [];

    /// <summary>The Medicare ACOs any of its NPIs takes part in: name matches (see <see cref="AcoNames"/>).</summary>
    public IReadOnlyList<AcoMembership> Acos { get; init; } = [];

    /// <summary>The 340B registrations of its facilities (HRSA OPAIS), participating first.</summary>
    public IReadOnlyList<Covered340BEntity> Covered340B { get; init; } = [];
}

/// <summary>Reads the organization groups the loader built (<c>org_group_member</c>, docs/PRODUCT_PLAN.md step 3.8).</summary>
public sealed class OrganizationGroupService(string connectionString)
{
    private sealed record MemberRow(string Npi, string MainNpi, sbyte IsMain, string? Ccn, string? FacilityKind, short JoinedBy, sbyte EntityType,
        string? OrgName, string? PrimaryTaxonomyCode, DateTime? LastUpdateDate, string? Address1, string? City, string? State, string? Zip5,
        string? Zip4, string? PostalCode, sbyte? Label, long? Activity);

    private sealed record SingleRow(string Npi, string? Ccn, string? FacilityKind, sbyte EntityType, string? OrgName, string? PrimaryTaxonomyCode,
        DateTime? LastUpdateDate, string? Address1, string? City, string? State, string? Zip5, string? Zip4, string? PostalCode, sbyte? Label, long? Activity);

    /// <summary>
    /// The organization page of an NPI (main NPI, name, number of NPIs: 1 for an organization of one NPI, docs/PRODUCT_PLAN.md step
    /// 3.9); null when it has none (a person, an organization NPI without a certified facility, Medicare group or other NPIs, an
    /// inactive NPI).
    /// </summary>
    public async Task<ProviderOrganization?> GetMembershipAsync(string npi, CancellationToken ct)
    {
        if (!InputFormats.IsNpi(npi))
        {
            return null;
        }

        await using var connection = await OpenAsync(ct);
        return await MembershipAsync(connection, npi, ct);
    }

    /// <summary>
    /// The organization page an NPI is part of (docs/PRODUCT_PLAN.md step 3.9): its organization's when it has several NPIs, else its
    /// own (one NPI) when it is an organization NPI with a certified facility or a Medicare group; null otherwise.
    /// </summary>
    internal static async Task<ProviderOrganization?> MembershipAsync(MySqlConnection connection, string npi, CancellationToken ct) =>
        (await MembershipsAsync(connection, [npi], ct)).GetValueOrDefault(npi);

    private static async Task<Dictionary<string, ProviderOrganization>> MembershipsAsync(MySqlConnection connection, IReadOnlyCollection<string> npis,
        CancellationToken ct)
    {
        var found = (await connection.QueryAsync<(string Npi, string MainNpi, int Members, string? Name)>(new CommandDefinition(
                "SELECT g.npi, g.main_npi, g.members, p.org_name FROM org_group_member g LEFT JOIN provider p ON p.npi = g.main_npi WHERE g.npi IN @npis",
                new { npis }, cancellationToken: ct)))
            .ToDictionary(r => r.Npi, r => new ProviderOrganization(r.MainNpi, r.Name ?? "", r.Members), StringComparer.Ordinal);
        var rest = npis.Where(n => !found.ContainsKey(n)).Distinct(StringComparer.Ordinal).ToList();
        if (rest.Count > 0)
        {
            foreach (var (npi, name) in await connection.QueryAsync<(string Npi, string? Name)>(new CommandDefinition(
                         $"SELECT p.npi, p.org_name FROM provider p WHERE p.npi IN @rest AND {OrganizationGroups.HasPageSql("p")}",
                         new { rest }, cancellationToken: ct)))
            {
                found[npi] = new ProviderOrganization(npi, name ?? "", 1);
            }
        }

        return found;
    }

    /// <summary>
    /// The main NPI of the organization page an NPI opens on (docs/PRODUCT_PLAN.md step 3.9): its organization's main NPI, or itself
    /// for an organization with one NPI and a certified facility or Medicare group; null when it has no organization page.
    /// </summary>
    public async Task<string?> FindPageNpiAsync(string npi, CancellationToken ct)
    {
        if (!InputFormats.IsNpi(npi))
        {
            return null;
        }

        await using var connection = await OpenAsync(ct);
        return (await MembershipAsync(connection, npi, ct))?.MainNpi;
    }

    /// <summary>
    /// The organization page of each facility (CCN) given, by the NPIs it bills under: the organization covering most of them (its
    /// main NPI and name); CCNs without one are left out. For links from facility lists to the full page (step 3.9).
    /// </summary>
    public Task<IReadOnlyDictionary<string, ProviderOrganization>> GetPagesForCcnsAsync(IReadOnlyCollection<string> ccns, CancellationToken ct) =>
        PagesForAsync($"SELECT b.ccn AS id, b.npi FROM ({FacilityDirectory.BillingNpiSql}) b WHERE b.ccn IN @ids", ccns, ct);

    /// <summary>The organization page of each Medicare group (PAC ID) given, by the organization NPIs enrolled under it (as <see cref="GetPagesForCcnsAsync"/>).</summary>
    public Task<IReadOnlyDictionary<string, ProviderOrganization>> GetPagesForPacIdsAsync(IReadOnlyCollection<string> pacIds, CancellationToken ct) =>
        PagesForAsync("SELECT e.pac_id AS id, e.npi FROM medicare_enrollment e WHERE e.pac_id IN @ids AND e.org_name IS NOT NULL", pacIds, ct);

    private async Task<IReadOnlyDictionary<string, ProviderOrganization>> PagesForAsync(string keySql, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var wanted = ids.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return new Dictionary<string, ProviderOrganization>();
        }

        await using var connection = await OpenAsync(ct);
        var pairs = (await connection.QueryAsync<(string Id, string Npi)>(new CommandDefinition(
            $"SELECT DISTINCT k.id, k.npi FROM ({keySql}) k", new { ids = wanted }, cancellationToken: ct))).ToList();
        if (pairs.Count == 0)
        {
            return new Dictionary<string, ProviderOrganization>();
        }

        var pages = await MembershipsAsync(connection, [.. pairs.Select(p => p.Npi).Distinct(StringComparer.Ordinal)], ct);
        return OrganizationGroups.PickPages(pairs, pages);
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

    /// <summary>
    /// The organization pages of these NPIs (those that have one, docs/PRODUCT_PLAN.md step 3.9), by NPI: the organization's main NPI,
    /// name and number of NPIs (1 for an organization of one NPI).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ProviderOrganization>> GetMembershipsAsync(IReadOnlyCollection<string> npis, CancellationToken ct)
    {
        var valid = npis.Where(InputFormats.IsNpi).Distinct(StringComparer.Ordinal).ToList();
        if (valid.Count == 0)
        {
            return new Dictionary<string, ProviderOrganization>();
        }

        await using var connection = await OpenAsync(ct);
        return await MembershipsAsync(connection, valid, ct);
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
        List<MemberRow> rows;
        var single = main is null;
        if (single)
        {
            // An organization of one NPI with a certified facility or a Medicare group (step 3.9): the NPI is its own main NPI, with
            // its first CCN (a hospital's first). The CCN subqueries name the NPI itself: correlated (b.npi = p.npi), MySQL builds the
            // whole union of billing NPIs for them (0.7 s).
            rows = [.. (await connection.QueryAsync<SingleRow>(new CommandDefinition(
                    $"""
                    SELECT p.npi AS Npi,
                           (SELECT b.ccn FROM ({FacilityDirectory.BillingNpiSql}) b WHERE b.npi = @npi ORDER BY b.kind <> 'hospital', b.ccn LIMIT 1) AS Ccn,
                           (SELECT b.kind FROM ({FacilityDirectory.BillingNpiSql}) b WHERE b.npi = @npi ORDER BY b.kind <> 'hospital', b.ccn LIMIT 1) AS FacilityKind,
                           p.entity_type AS EntityType, p.org_name AS OrgName, p.primary_taxonomy_code AS PrimaryTaxonomyCode,
                           p.last_update_date AS LastUpdateDate, l.address1 AS Address1, l.city AS City, l.state AS State, l.zip5 AS Zip5, l.zip4 AS Zip4,
                           l.postal_code AS PostalCode, q.label AS Label, CAST(q.activity AS SIGNED) AS Activity
                    FROM provider p
                    LEFT JOIN provider_location l ON l.npi = p.npi AND l.is_primary = 1
                    LEFT JOIN provider_quality q ON q.npi = p.npi
                    WHERE p.npi = @npi AND {OrganizationGroups.HasPageSql("p")}
                    """, new { npi }, cancellationToken: ct)))
                .Select(r => new MemberRow(r.Npi, r.Npi, 1, r.Ccn, r.FacilityKind, 0, r.EntityType, r.OrgName, r.PrimaryTaxonomyCode, r.LastUpdateDate,
                    r.Address1, r.City, r.State, r.Zip5, r.Zip4, r.PostalCode, r.Label, r.Activity))];
        }
        else
        {
            rows = (await connection.QueryAsync<MemberRow>(new CommandDefinition(
                """
                SELECT g.npi AS Npi, g.main_npi AS MainNpi, g.is_main AS IsMain, g.ccn AS Ccn, g.facility_kind AS FacilityKind, g.joined_by AS JoinedBy,
                       p.entity_type AS EntityType, p.org_name AS OrgName, p.primary_taxonomy_code AS PrimaryTaxonomyCode, p.last_update_date AS LastUpdateDate,
                       l.address1 AS Address1, l.city AS City, l.state AS State, l.zip5 AS Zip5, l.zip4 AS Zip4, l.postal_code AS PostalCode,
                       q.label AS Label, CAST(q.activity AS SIGNED) AS Activity
                FROM org_group_member g
                JOIN provider p ON p.npi = g.npi
                LEFT JOIN provider_location l ON l.npi = g.npi AND l.is_primary = 1
                LEFT JOIN provider_quality q ON q.npi = g.npi
                WHERE g.main_npi = @main
                ORDER BY g.is_main DESC, g.ccn IS NULL, p.org_name, g.npi
                """, new { main }, cancellationToken: ct))).ToList();
        }

        if (rows.Count == 0)
        {
            return null; // no page, or replaced by a concurrent rebuild
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
        return new OrganizationProfile(mainRow.Npi, mainRow.OrgName ?? "", members)
        {
            Grouping = single ? OrganizationGroups.SingleRule : OrganizationGroups.Rule + " " + OrganizationGroups.MainRule,
        };
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
                string? PostalCode, string? CountryCode, string? Phone, double? Lat, double? Lon, sbyte? Approximate)>(new CommandDefinition(
                """
                SELECT l.is_primary, l.address1, l.address2, l.city, l.state, l.zip5, l.zip4, l.postal_code, l.country_code, l.phone, m.lat, m.lon, m.approximate
                FROM provider_location l LEFT JOIN provider_map m ON m.npi = l.npi AND m.addr_key = l.addr_key
                WHERE l.npi IN @npis ORDER BY l.is_primary DESC, l.npi = @main DESC, l.npi, l.id
                """, new { npis, main = profile.MainNpi }, cancellationToken: ct)))
            .DistinctBy(l => string.Join('|', Normalize(l.Address1), Normalize(l.City), Normalize(l.State), l.Zip5 ?? l.PostalCode))
            .Select(l => new ProviderLocation(l.IsPrimary != 0, l.Address1, l.Address2, l.City, l.State, ProviderNames.Zip(l.Zip5, l.Zip4, l.PostalCode),
                l.CountryCode, l.Phone)
            {
                Latitude = l.Lat,
                Longitude = l.Lon,
                Approximate = l.Approximate == 1,
            })
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
    /// A member that isn't its organization's main NPI, on alias {0} (anything with an npi column), with another member of its
    /// organization before it that matches the same conditions ({1}, written on alias <c>pm</c>): the main NPI first, then the
    /// others by NPI. So the search lists each organization once, as its main NPI when that one matches, else as its first member
    /// that does (a hospital's dialysis center as main NPI and four hospital NPIs: a hospital search shows the first of the four).
    /// </summary>
    public static string HiddenMember(string alias, string mainConditions) =>
        string.Format(CultureInfo.InvariantCulture,
            "EXISTS (SELECT /*+ NO_SEMIJOIN() */ 1 FROM org_group_member og JOIN org_group_member oy ON oy.main_npi = og.main_npi AND oy.npi <> og.npi "
            + "AND (oy.is_main = 1 OR oy.npi < og.npi) JOIN provider pm ON pm.npi = oy.npi WHERE og.npi = {0}.npi AND og.is_main = 0{1})",
            alias, mainConditions.Length == 0 ? "" : " AND " + mainConditions);
}
