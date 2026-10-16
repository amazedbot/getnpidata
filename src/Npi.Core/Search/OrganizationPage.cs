using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>Where a clinician on an organization's list comes from (docs/PRODUCT_PLAN.md step 3.9); a clinician tied several ways has several bits.</summary>
[Flags]
public enum ClinicianSource
{
    None = 0,

    /// <summary>Lists one of the organization's facilities (CCNs) in Medicare Care Compare (Facility Affiliation Data).</summary>
    FacilityAffiliation = 1,

    /// <summary>Reassigns Medicare payments to one of its Medicare groups (PAC IDs) in Medicare enrollment.</summary>
    Reassignment = 2,

    /// <summary>Lists one of its Medicare groups in Medicare Care Compare.</summary>
    CareCompareGroup = 4,
}

/// <summary>The clinician sources in words and as the API's codes.</summary>
public static class ClinicianSources
{
    public const string FacilityAffiliationCode = "facilityAffiliation";
    public const string ReassignmentCode = "reassignment";
    public const string CareCompareGroupCode = "careCompareGroup";

    private static readonly (ClinicianSource Source, string Code, string Text)[] All =
    [
        (ClinicianSource.FacilityAffiliation, FacilityAffiliationCode, "Works at its facility (Care Compare)"),
        (ClinicianSource.Reassignment, ReassignmentCode, "Bills Medicare through its group"),
        (ClinicianSource.CareCompareGroup, CareCompareGroupCode, "In its group (Care Compare)"),
    ];

    /// <summary>["facilityAffiliation", "reassignment"].</summary>
    public static IReadOnlyList<string> Codes(ClinicianSource sources) => [.. All.Where(s => (sources & s.Source) != 0).Select(s => s.Code)];

    /// <summary>"Works at its facility (Care Compare); bills Medicare through its group".</summary>
    public static string Text(ClinicianSource sources)
    {
        var parts = All.Where(s => (sources & s.Source) != 0).Select(s => s.Text).ToList();
        return parts.Count == 0 ? "" : string.Join("; ", parts.Select((p, i) => i == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]));
    }

    /// <summary>The text of one code (for the client and the page legend).</summary>
    public static string? TextOf(string code) => All.FirstOrDefault(s => s.Code == code).Text;

    public const string Note =
        "One list from three CMS sources, each clinician once: Care Compare's facility affiliations (clinicians who list one of its " +
        "certified facilities), Medicare enrollment reassignments (clinicians who bill Medicare through one of its Medicare groups) and " +
        "Care Compare's group practices (clinicians who list one of its groups). Only active NPIs are listed.";
}

/// <summary>An organization's clinicians in numbers: everyone once, how many each source lists, how many more than one does, by specialty.</summary>
public sealed record OrganizationClinicianSummary(int Total, int FacilityAffiliations, int Reassignments, int CareCompareGroups, int InSeveral,
    IReadOnlyList<SpecialtyCount> BySpecialty);

/// <summary>One page of an organization's clinicians (the rows carry their sources), with the whole list's summary and the sort used.</summary>
public sealed record OrganizationClinicianList(OrganizationClinicianSummary Summary, ClinicianList List, string Sort);

/// <summary>Merging, sorting and paging an organization's clinicians (docs/PRODUCT_PLAN.md step 3.9).</summary>
public static class OrganizationClinicians
{
    public const int PageSize = 25;
    public const string DefaultSort = "name";

    /// <summary>The sorts the list offers: name, plain specialty, location (state, city), and those tied several ways first.</summary>
    public static readonly IReadOnlyList<string> Sorts = ["name", "specialty", "location", "source"];

    /// <summary>A clinician as the database gives it: one row per source, or already merged.</summary>
    public sealed record Entry(string Npi, string SortName, string? Specialty, string? City, string? State, ClinicianSource Sources);

    public static string NormalizeSort(string? sort) =>
        Sorts.FirstOrDefault(s => string.Equals(s, sort?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? DefaultSort;

    /// <summary>Each clinician once, with every source that lists them.</summary>
    public static IReadOnlyList<Entry> Merge(IEnumerable<Entry> rows) =>
        [.. rows.GroupBy(r => r.Npi, StringComparer.Ordinal)
            .Select(g => g.First() with { Sources = g.Aggregate(ClinicianSource.None, (all, r) => all | r.Sources) })];

    public static IReadOnlyList<Entry> Sort(IEnumerable<Entry> entries, string? sort)
    {
        var byName = Comparer<Entry>.Create((a, b) =>
        {
            var c = string.CompareOrdinal(a.SortName, b.SortName);
            return c != 0 ? c : string.CompareOrdinal(a.Npi, b.Npi);
        });
        return NormalizeSort(sort) switch
        {
            "specialty" => [.. entries.OrderBy(e => e.Specialty is null).ThenBy(e => e.Specialty, StringComparer.Ordinal).ThenBy(e => e, byName)],
            "location" => [.. entries.OrderBy(e => e.State is null).ThenBy(e => e.State, StringComparer.Ordinal)
                .ThenBy(e => e.City, StringComparer.OrdinalIgnoreCase).ThenBy(e => e, byName)],
            "source" => [.. entries.OrderByDescending(e => Count(e.Sources)).ThenBy(e => (int)e.Sources).ThenBy(e => e, byName)],
            _ => [.. entries.OrderBy(e => e, byName)],
        };
    }

    public static OrganizationClinicianSummary Summarize(IReadOnlyList<Entry> entries) =>
        new(entries.Count,
            entries.Count(e => (e.Sources & ClinicianSource.FacilityAffiliation) != 0),
            entries.Count(e => (e.Sources & ClinicianSource.Reassignment) != 0),
            entries.Count(e => (e.Sources & ClinicianSource.CareCompareGroup) != 0),
            entries.Count(e => Count(e.Sources) > 1),
            [.. entries.GroupBy(e => e.Specialty ?? "Other or not listed")
                .Select(g => new SpecialtyCount(g.Key, g.Count()))
                .OrderByDescending(s => s.Clinicians).ThenBy(s => s.Specialty, StringComparer.Ordinal)]);

    private static int Count(ClinicianSource s) => ((s & ClinicianSource.FacilityAffiliation) != 0 ? 1 : 0)
                                                    + ((s & ClinicianSource.Reassignment) != 0 ? 1 : 0)
                                                    + ((s & ClinicianSource.CareCompareGroup) != 0 ? 1 : 0);
}

/// <summary>One owner's role at one of the organization's facilities.</summary>
public sealed record OrganizationOwnerFacility(string Ccn, string Facility, string Roles, double? Percentage, DateOnly? Since);

/// <summary>
/// An owner, manager or officer of an organization's facilities, once per role group (owners, control, lenders, officers) with every
/// facility it holds a role of that group in. <see cref="OwnerId"/> is null for a person, who is shown by name, title and role only
/// (owner, 2026-10-15); a person's roles at the organization's facilities are joined by name.
/// </summary>
public sealed record OrganizationOwner(string? OwnerId, bool IsOrganization, string Name, string? DoingBusinessAs, string? Title, string RoleGroup,
    IReadOnlyList<string> Roles, double? Percentage, DateOnly? Since, IReadOnlyList<string> Kinds, string? City, string? State,
    IReadOnlyList<OrganizationOwnerFacility> Facilities);

/// <summary>
/// Who owns and runs an organization's facilities (CMS All Owners and Change of Ownership files, docs/PRODUCT_PLAN.md step 3.9):
/// each owner once per role group with its facilities, the facilities' Medicare enrollments, and the ownership changes of any of them.
/// </summary>
public sealed record OrganizationOwnership(IReadOnlyList<OrganizationOwner> Owners, IReadOnlyList<OwnershipChange> Changes,
    IReadOnlyList<FacilityEnrollment> Enrollments, DateOnly? OwnersAsOf, DateOnly? ChangesAsOf);

/// <summary>A facility's Medicare enrollment, as the owners file lists it.</summary>
public sealed record FacilityEnrollment(string Ccn, string Facility, string? EnrollmentId, string? LegalName);

/// <summary>Merges the owners of several facilities into one list (docs/PRODUCT_PLAN.md step 3.9).</summary>
public static class OrganizationOwnershipMerge
{
    /// <summary>
    /// Every owner once per role group, with the facilities it holds a role of that group in (in the parts' order), the largest stake
    /// first; the changes of every facility once, newest first. Null when no part lists an owner or a change.
    /// </summary>
    public static OrganizationOwnership? Merge(IReadOnlyList<(string Ccn, string Facility, FacilityOwnership Ownership)> parts)
    {
        if (parts.Count == 0 || parts.All(p => p.Ownership.Owners.Count == 0 && p.Ownership.Changes.Count == 0))
        {
            return null;
        }

        var owners = parts
            .SelectMany((p, order) => p.Ownership.Owners.Select(o => (Part: p, Order: order, Owner: o)))
            .GroupBy(x => (x.Owner.RoleGroup, Key: x.Owner.OwnerId ?? "person:" + x.Owner.Name.Trim().ToUpperInvariant()))
            .Select(g =>
            {
                var first = g.First().Owner;
                var facilities = g.GroupBy(x => x.Part.Ccn, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(f => f.Min(x => x.Order))
                    .Select(f => new OrganizationOwnerFacility(f.Key, f.First().Part.Facility,
                        string.Join("; ", f.Select(x => x.Owner.Role).Distinct(StringComparer.Ordinal)),
                        f.Max(x => x.Owner.Percentage is > 0 ? x.Owner.Percentage : null), f.Min(x => x.Owner.Since)))
                    .ToList();
                return new OrganizationOwner(first.OwnerId, first.IsOrganization, first.Name, g.Select(x => x.Owner.DoingBusinessAs).FirstOrDefault(d => d is not null),
                    g.Select(x => x.Owner.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)), g.Key.RoleGroup,
                    [.. g.Select(x => x.Owner.Role).Distinct(StringComparer.Ordinal)], facilities.Max(f => f.Percentage), facilities.Min(f => f.Since),
                    first.Kinds, first.City, first.State, facilities);
            })
            .OrderBy(o => OwnerRoles.Order(o.RoleGroup))
            .ThenByDescending(o => o.Percentage ?? -1)
            .ThenByDescending(o => o.IsOrganization)
            .ThenByDescending(o => o.Facilities.Count)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var changes = parts.SelectMany(p => p.Ownership.Changes)
            .DistinctBy(c => (c.EffectiveDate, c.Kind, c.BuyerId, c.BuyerName, c.BuyerCcn, c.SellerId, c.SellerName, c.SellerCcn))
            .OrderByDescending(c => c.EffectiveDate)
            .ToList();
        return new OrganizationOwnership(owners, changes,
            [.. parts.Where(p => p.Ownership.EnrollmentId is not null).Select(p => new FacilityEnrollment(p.Ccn, p.Facility, p.Ownership.EnrollmentId, p.Ownership.LegalName))],
            parts.Max(p => p.Ownership.OwnersAsOf), parts.Max(p => p.Ownership.ChangesAsOf));
    }
}

/// <summary>
/// One organization NPI's Medicare billing (docs/PRODUCT_PLAN.md step 3.9): Part B in the newest data year (patients, services,
/// Medicare's payment), Part D prescribing, and the last year it billed in the CMS data (older years included).
/// </summary>
public sealed record OrganizationMedicareNpi(string Npi, string Name, string Role, int? PartBYear, int? Beneficiaries, double? Services, double? Payment,
    int? PartDYear, int? PartDClaims, double? DrugCost, int? LastBilledYear);

/// <summary>A change recorded for one of an organization's NPIs (the provider page's Changes, with the NPI).</summary>
public sealed record OrganizationChange(string Npi, DateOnly RecordedOn, string Type, string? OldValue, string? NewValue);

/// <summary>
/// The one page per organization (docs/PRODUCT_PLAN.md step 3.9, owner 2026-10-16): the combined profile (every NPI, facility and
/// Medicare group) with the data of all of them: each facility's quality and Medicare volume, the owners once across the facilities,
/// one clinician list from every source, Medicare billing and the recorded changes of all its NPIs, health systems, ACOs and 340B.
/// </summary>
public sealed class OrganizationPageService(string connectionString, OrganizationGroupService groups, ProviderDetailService details, OwnershipService ownership)
{
    /// <summary>The facilities whose own records the page shows in full; the rest are listed, each with its own page.</summary>
    public const int MaxFacilityDetails = 12;

    private sealed record CcnRow(string Ccn, string Kind, string Npi);

    private sealed record ClinicianDbRow(string Npi, long Sources, string? SortName, string? Taxonomy, string? City, string? State);

    private sealed record MedicareRow(string Npi, short? PartBYear, int? Beneficiaries, double? Services, double? Payment, short? PartDYear, int? Claims,
        double? DrugCost, short? LastYear);

    private sealed record ChangeRow(string Npi, DateTime DetectedAt, string ChangeType, string? OldValue, string? NewValue);

    /// <summary>The page of the organization <paramref name="npi"/> belongs to (any of its NPIs); null when it has no organization page.</summary>
    public async Task<OrganizationProfile?> GetAsync(string npi, CancellationToken ct)
    {
        if (await groups.GetCombinedAsync(npi, details, ct) is not { } profile)
        {
            return null;
        }

        await using var connection = await OpenAsync(ct);
        var npis = profile.Npis.Select(m => m.Npi).ToList();
        var ccns = await CcnsAsync(connection, profile, ct);
        var facilities = new List<FacilityProfile>();
        foreach (var ccn in ccns.Take(MaxFacilityDetails))
        {
            if (await OrganizationService.FacilityAsync(connection, ccn, 1, clinicians: false, ct) is not { } facility)
            {
                continue;
            }

            facilities.Add(FacilityTypes.IsNursingHome(facility.Facility)
                ? facility with { Inspections = await ownership.GetInspectionsAsync(ccn, ct) }
                : facility);
        }

        var names = facilities.ToDictionary(f => f.Facility.Ccn, f => f.Facility.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var f in profile.Facilities)
        {
            names.TryAdd(f.Ccn, f.Name);
        }

        var owned = await ownership.GetFacilitiesOwnershipAsync(ccns, ct);
        var combinedOwnership = OrganizationOwnershipMerge.Merge([.. ccns.Where(owned.ContainsKey)
            .Select(c => (c, names.GetValueOrDefault(c) ?? owned[c].LegalName ?? "CCN " + c, owned[c]))]);
        var pacIds = profile.Groups.Select(g => g.PacId).ToList();
        var clinicians = OrganizationClinicians.Merge(await ClinicianEntriesAsync(connection, ccns, pacIds, ct));
        var roles = profile.Npis.ToDictionary(m => m.Npi, StringComparer.Ordinal);
        return profile with
        {
            Ccns = ccns,
            FacilityProfiles = facilities,
            Ownership = combinedOwnership,
            Clinicians = OrganizationClinicians.Summarize(clinicians),
            MedicareActivity = await MedicareAsync(connection, npis, roles, ct),
            Changes = await ChangesAsync(connection, npis, ct),
            HealthSystems = await SystemsAsync(connection, npis, pacIds, ct),
            Acos = (await AcoService.MembershipsAsync(connection, "m.npi IN @npis", new { npis }, ct)).DistinctBy(a => a.AcoId).ToList(),
            Covered340B = [.. facilities.SelectMany(f => f.Covered340B).DistinctBy(e => e.Id340B).OrderByDescending(e => e.Participating)],
        };
    }

    /// <summary>One page of the organization's clinicians, sorted (<see cref="OrganizationClinicians.Sorts"/>), with the whole list's summary.</summary>
    public async Task<OrganizationClinicianList> GetCliniciansAsync(OrganizationProfile profile, int page, string? sort, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var entries = OrganizationClinicians.Merge(await ClinicianEntriesAsync(connection, profile.Ccns, [.. profile.Groups.Select(g => g.PacId)], ct));
        var summary = OrganizationClinicians.Summarize(entries);
        var sorted = OrganizationClinicians.Sort(entries, sort);
        var pages = Math.Max(1, (sorted.Count + OrganizationClinicians.PageSize - 1) / OrganizationClinicians.PageSize);
        page = Math.Clamp(page, 1, pages);
        var shown = sorted.Skip((page - 1) * OrganizationClinicians.PageSize).Take(OrganizationClinicians.PageSize).ToList();
        var sources = shown.ToDictionary(e => e.Npi, e => e.Sources, StringComparer.Ordinal);
        var rows = (await OrganizationService.ClinicianRowsAsync(connection, [.. shown.Select(e => e.Npi)], ct))
            .Select(r => r with { Sources = ClinicianSources.Codes(sources[r.Npi]) })
            .ToList();
        return new OrganizationClinicianList(summary, new ClinicianList(summary.Total, summary.BySpecialty, rows, page, OrganizationClinicians.PageSize),
            OrganizationClinicians.NormalizeSort(sort));
    }

    /// <summary>Every clinician of the organization by name, with their sources, for the CSV.</summary>
    public async Task<IReadOnlyList<OrganizationClinicians.Entry>> GetAllCliniciansAsync(OrganizationProfile profile, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return OrganizationClinicians.Sort(
            OrganizationClinicians.Merge(await ClinicianEntriesAsync(connection, profile.Ccns, [.. profile.Groups.Select(g => g.PacId)], ct)), null);
    }

    /// <summary>Every CCN CMS ties the organization's NPIs to, the main facility first (<see cref="OrganizationProfile.Ccns"/>).</summary>
    public async Task<IReadOnlyList<string>> GetCcnsAsync(OrganizationProfile profile, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return await CcnsAsync(connection, profile, ct);
    }

    // Every CCN of the NPIs: the main NPI's first, then hospitals and their units, then the other kinds; each CCN once.
    private static async Task<IReadOnlyList<string>> CcnsAsync(MySqlConnection connection, OrganizationProfile profile, CancellationToken ct)
    {
        var npis = profile.Npis.Select(m => m.Npi).ToList();
        var rows = (await connection.QueryAsync<CcnRow>(new CommandDefinition(
            $"SELECT b.ccn AS Ccn, b.kind AS Kind, b.npi AS Npi FROM ({FacilityDirectory.BillingNpiSql}) b WHERE b.npi IN @npis",
            new { npis }, cancellationToken: ct))).ToList();
        var mainCcn = profile.Npis.FirstOrDefault(m => m.IsMain)?.Ccn;
        return [.. rows
            .GroupBy(r => r.Ccn.ToUpperInvariant(), StringComparer.Ordinal)
            .OrderBy(g => string.Equals(g.Key, mainCcn, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(g => g.Min(r => FacilityTypes.Order(r.Kind)))
            .ThenBy(g => OrganizationGroups.FacilityKey(g.Key), StringComparer.Ordinal)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)];
    }

    // One row per (clinician, source): Care Compare affiliations with the CCNs, reassignments to the PAC IDs, Care Compare groups.
    private static async Task<IReadOnlyList<OrganizationClinicians.Entry>> ClinicianEntriesAsync(MySqlConnection connection, IReadOnlyList<string> ccns,
        List<string> pacIds, CancellationToken ct)
    {
        var parts = new List<string>();
        if (ccns.Count > 0)
        {
            parts.Add($"SELECT DISTINCT npi, {(int)ClinicianSource.FacilityAffiliation} AS src FROM cc_facility_affiliation WHERE ccn IN @ccns");
        }

        if (pacIds.Count > 0)
        {
            parts.Add($"SELECT DISTINCT npi, {(int)ClinicianSource.Reassignment} AS src FROM medicare_reassignment WHERE group_pac_id IN @pacIds AND npi IS NOT NULL");
            parts.Add($"SELECT DISTINCT npi, {(int)ClinicianSource.CareCompareGroup} AS src FROM cc_group WHERE org_pac_id IN @pacIds");
        }

        if (parts.Count == 0)
        {
            return [];
        }

        return [.. (await connection.QueryAsync<ClinicianDbRow>(new CommandDefinition(
                $"""
                SELECT x.npi AS Npi, CAST(BIT_OR(x.src) AS SIGNED) AS Sources, MAX(p.sort_name) AS SortName, MAX(p.primary_taxonomy_code) AS Taxonomy,
                       MAX(l.city) AS City, MAX(l.state) AS State
                FROM ({string.Join(" UNION ALL ", parts)}) x
                JOIN provider p ON p.npi = x.npi
                LEFT JOIN provider_location l ON l.npi = p.npi AND l.is_primary = 1
                GROUP BY x.npi
                """, new { ccns, pacIds }, commandTimeout: 60, cancellationToken: ct)))
            .Select(r => new OrganizationClinicians.Entry(r.Npi, r.SortName ?? "", OrganizationService.PlainSpecialty(r.Taxonomy), r.City, r.State,
                (ClinicianSource)r.Sources))];
    }

    private static async Task<IReadOnlyList<OrganizationMedicareNpi>> MedicareAsync(MySqlConnection connection, IReadOnlyList<string> npis,
        IReadOnlyDictionary<string, OrganizationMember> members, CancellationToken ct) =>
        [.. (await connection.QueryAsync<MedicareRow>(new CommandDefinition(
                """
                SELECT p.npi AS Npi, u.data_year AS PartBYear, u.beneficiaries AS Beneficiaries, u.services AS Services, u.payment_amount AS Payment,
                       d.data_year AS PartDYear, d.claims AS Claims, d.drug_cost AS DrugCost,
                       (SELECT MAX(y.data_year) FROM medicare_activity_year y WHERE y.npi = p.npi) AS LastYear
                FROM provider p
                LEFT JOIN medicare_utilization u ON u.npi = p.npi
                LEFT JOIN medicare_part_d d ON d.npi = p.npi
                WHERE p.npi IN @npis
                  AND (u.npi IS NOT NULL OR d.npi IS NOT NULL OR EXISTS (SELECT 1 FROM medicare_activity_year y WHERE y.npi = p.npi))
                """, new { npis }, cancellationToken: ct)))
            .Select(r => new OrganizationMedicareNpi(r.Npi, members.GetValueOrDefault(r.Npi)?.Name ?? "", members.GetValueOrDefault(r.Npi)?.Role ?? "",
                r.PartBYear, r.Beneficiaries, r.Services, r.Payment, r.PartDYear, r.Claims, r.DrugCost,
                new int?[] { r.PartBYear, r.PartDYear, r.LastYear }.Max()))
            .OrderByDescending(m => m.Payment ?? -1).ThenByDescending(m => m.LastBilledYear ?? 0).ThenBy(m => m.Npi, StringComparer.Ordinal)];

    private static async Task<IReadOnlyList<OrganizationChange>> ChangesAsync(MySqlConnection connection, IReadOnlyList<string> npis, CancellationToken ct) =>
        [.. (await connection.QueryAsync<ChangeRow>(new CommandDefinition(
                $"""
                SELECT c.npi AS Npi, c.detected_at AS DetectedAt, c.change_type AS ChangeType,
                       COALESCE(NULLIF(CONCAT_WS(' – ', o.Classification, o.Specialization), ''), c.old_value) AS OldValue,
                       COALESCE(NULLIF(CONCAT_WS(' – ', n.Classification, n.Specialization), ''), c.new_value) AS NewValue
                FROM provider_change c
                LEFT JOIN taxonomy_codes o ON c.change_type IN ({ChangeTypes.TaxonomySqlList}) AND o.Taxonomy_Code = c.old_value
                LEFT JOIN taxonomy_codes n ON c.change_type IN ({ChangeTypes.TaxonomySqlList}) AND n.Taxonomy_Code = c.new_value
                WHERE c.npi IN @npis AND c.change_type IN ({ChangeTypes.ProfileSqlList})
                ORDER BY c.detected_at DESC, c.id DESC LIMIT 200
                """, new { npis }, cancellationToken: ct)))
            .Select(r => new OrganizationChange(r.Npi, DateOnly.FromDateTime(r.DetectedAt), r.ChangeType, r.OldValue, r.NewValue))];

    // Through a facility an NPI of the organization bills for, or one an NPI under its Medicare groups bills for.
    private static async Task<IReadOnlyList<HealthSystemLink>> SystemsAsync(MySqlConnection connection, List<string> npis,
        List<string> pacIds, CancellationToken ct) =>
        [.. (await connection.QueryAsync<HealthSystemLink>(new CommandDefinition(
                $"""
                SELECT s.system_id AS SystemId, s.name AS Name, f.ccn AS Ccn, f.name AS FacilityName
                FROM (SELECT b.ccn FROM ({FacilityDirectory.BillingNpiSql}) b WHERE b.npi IN @npis
                      UNION SELECT n.ccn FROM medicare_enrollment e JOIN cms_facility_npi n ON n.npi = e.npi WHERE e.pac_id IN @pacIds) c
                JOIN health_system_facility f ON f.ccn = c.ccn
                JOIN health_system s ON s.system_id = f.system_id
                ORDER BY s.name, f.ccn
                """, new { npis, pacIds }, cancellationToken: ct)))
            .DistinctBy(l => l.SystemId)];

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
