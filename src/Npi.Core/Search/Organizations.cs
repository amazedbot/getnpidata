using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>A clinician in a group's or facility's list, with the specialty in plain words.</summary>
public sealed record ClinicianRow(string Npi, string Name, string? Credential, string? Gender, string? Specialty, string? City, string? State);

/// <summary>How many clinicians have this plain specialty.</summary>
public sealed record SpecialtyCount(string Specialty, int Clinicians);

/// <summary>An NPI an organization or facility is registered or bills under.</summary>
public sealed record OrganizationNpi(string Npi, string Name, string? Specialty, string? City, string? State);

/// <summary>One page of a clinician list, with the specialty breakdown of the whole list.</summary>
public sealed record ClinicianList(int Total, IReadOnlyList<SpecialtyCount> BySpecialty, IReadOnlyList<ClinicianRow> Page, int PageNumber, int PageSize)
{
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
}

/// <summary>
/// A Medicare group practice, by its PAC ID (docs/PRODUCT_PLAN.md step 1.5): its name, the organization NPIs enrolled under it,
/// and its clinicians: those who reassign benefits to it (Medicare enrollment) or list it in Care Compare.
/// </summary>
public sealed record GroupProfile(string PacId, string Name, string? State, bool? AcceptsMedicare, IReadOnlyList<OrganizationNpi> Organizations,
    ClinicianList Clinicians);

/// <summary>A Medicare-certified facility, by its CCN: its ratings, the NPIs it bills under and the clinicians affiliated with it.</summary>
public sealed record FacilityProfile(CertifiedFacility Facility, string? Address, string? Zip, double? Latitude, double? Longitude,
    IReadOnlyList<OrganizationNpi> BillingNpis, ClinicianList Clinicians);

/// <summary>A Medicare group an organization NPI is enrolled as, with its clinician count.</summary>
public sealed record OrganizationGroup(string PacId, string Name, int Clinicians);

/// <summary>Reads group practices, facilities and an organization's groups (docs/PRODUCT_PLAN.md step 1.5).</summary>
public sealed class OrganizationService(string connectionString)
{
    public const int PageSize = 50;

    // Every clinician of a group: reassigned to it in Medicare enrollment, or listed with it in Care Compare.
    private const string GroupNpis = """
        SELECT npi FROM medicare_reassignment WHERE group_pac_id = @id AND npi IS NOT NULL
        UNION SELECT npi FROM cc_group WHERE org_pac_id = @id
        """;

    private const string FacilityNpis = "SELECT DISTINCT npi FROM cc_facility_affiliation WHERE ccn = @id";

    private sealed record ClinicianDbRow(string Npi, sbyte EntityType, string? LastName, string? FirstName, string? MiddleName, string? NameSuffix,
        string? Credential, string? OrgName, string? Gender, string? PrimaryTaxonomyCode, string? City, string? State);

    private sealed record CodeCount(string? Code, long Clinicians);

    private sealed record NpiRow(string Npi, sbyte EntityType, string? LastName, string? FirstName, string? MiddleName, string? NameSuffix,
        string? OrgName, string? PrimaryTaxonomyCode, string? City, string? State);

    public static bool IsPacId(string? id) => id is { Length: 10 } && id.All(char.IsAsciiDigit);

    /// <summary>A CCN: six letters or digits (hospitals, nursing homes, home health, hospices).</summary>
    public static bool IsCcn(string? id) => id is { Length: 6 } && id.All(char.IsAsciiLetterOrDigit);

    public async Task<GroupProfile?> GetGroupAsync(string pacId, int page, CancellationToken ct)
    {
        if (!IsPacId(pacId))
        {
            return null;
        }

        await using var connection = await OpenAsync(ct);
        var name = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT COALESCE(
              (SELECT MAX(group_name) FROM cc_group WHERE org_pac_id = @id),
              (SELECT MAX(group_name) FROM medicare_reassignment WHERE group_pac_id = @id),
              (SELECT MAX(org_name) FROM medicare_enrollment WHERE pac_id = @id))
            """, new { id = pacId }, cancellationToken: ct));
        if (name is null)
        {
            return null;
        }

        var state = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT COALESCE((SELECT MAX(group_state) FROM medicare_reassignment WHERE group_pac_id = @id), (SELECT MAX(state) FROM cc_group WHERE org_pac_id = @id))",
            new { id = pacId }, cancellationToken: ct));
        var accepts = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT MAX(accepts_assignment) FROM cc_group WHERE org_pac_id = @id", new { id = pacId }, cancellationToken: ct));
        var organizations = await NpisAsync(connection, "SELECT DISTINCT npi FROM medicare_enrollment WHERE pac_id = @id", pacId, ct);
        var clinicians = await ClinicianListAsync(connection, GroupNpis, pacId, page, ct);
        return new GroupProfile(pacId, name, state, accepts is null ? null : accepts != 0, organizations, clinicians);
    }

    public async Task<FacilityProfile?> GetFacilityAsync(string ccn, int page, CancellationToken ct)
    {
        if (!IsCcn(ccn))
        {
            return null;
        }

        ccn = ccn.ToUpperInvariant();
        await using var connection = await OpenAsync(ct);
        var facility = await ProviderDetailService.FacilitiesAsync(connection,
            """
            FROM (SELECT ccn, 'hospital' AS kind FROM cms_hospital WHERE ccn = @id
                  UNION ALL SELECT ccn, 'nursing_home' FROM cms_nursing_home WHERE ccn = @id
                  UNION ALL SELECT ccn, 'home_health' FROM cms_home_health WHERE ccn = @id
                  UNION ALL SELECT ccn, 'hospice' FROM cms_hospice WHERE ccn = @id) f
            """, "1 = 1", new { id = ccn }, ct);
        if (facility.Count == 0)
        {
            return null;
        }

        var place = await connection.QueryFirstOrDefaultAsync<(string? Address, string? Zip)>(new CommandDefinition(
            """
            SELECT COALESCE(h.address, n.address, hh.address, hs.address), COALESCE(h.zip, n.zip, hh.zip, hs.zip)
            FROM (SELECT @id AS ccn) f
            LEFT JOIN cms_hospital h ON h.ccn = f.ccn LEFT JOIN cms_nursing_home n ON n.ccn = f.ccn
            LEFT JOIN cms_home_health hh ON hh.ccn = f.ccn LEFT JOIN cms_hospice hs ON hs.ccn = f.ccn
            """, new { id = ccn }, cancellationToken: ct));
        var billing = await NpisAsync(connection, "SELECT DISTINCT npi FROM cms_facility_npi WHERE ccn = @id", ccn, ct);

        // On the map: the primary practice address of a billing NPI, else the centre of the facility's ZIP.
        var point = await connection.QueryFirstOrDefaultAsync<(double Lat, double Lon)?>(new CommandDefinition(
            """
            SELECT m.lat, m.lon FROM cms_facility_npi f
            JOIN provider_location l ON l.npi = f.npi AND l.is_primary = 1
            JOIN provider_map m ON m.npi = l.npi AND m.addr_key = l.addr_key
            WHERE f.ccn = @id AND m.approximate = 0 LIMIT 1
            """, new { id = ccn }, cancellationToken: ct));
        if (point is null && place.Zip is { Length: >= 5 } zip)
        {
            point = await connection.QueryFirstOrDefaultAsync<(double Lat, double Lon)?>(new CommandDefinition(
                "SELECT lat, lon FROM zip_centroid WHERE zip5 = @zip", new { zip = zip[..5] }, cancellationToken: ct));
        }

        var clinicians = await ClinicianListAsync(connection, FacilityNpis, ccn, page, ct);
        return new FacilityProfile(facility[0], place.Address, place.Zip, point?.Lat, point?.Lon, billing, clinicians);
    }

    /// <summary>The Medicare groups an organization NPI is enrolled as, largest first.</summary>
    public async Task<IReadOnlyList<OrganizationGroup>> GetGroupsForNpiAsync(string npi, CancellationToken ct)
    {
        if (!InputFormats.IsNpi(npi))
        {
            return [];
        }

        await using var connection = await OpenAsync(ct);
        var pacIds = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT pac_id FROM medicare_enrollment WHERE npi = @npi AND pac_id IS NOT NULL", new { npi }, cancellationToken: ct))).ToList();
        var groups = new List<OrganizationGroup>();
        foreach (var pac in pacIds)
        {
            var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                $"SELECT COUNT(*) FROM ({GroupNpis}) g JOIN provider p ON p.npi = g.npi", new { id = pac }, cancellationToken: ct));
            var name = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                """
                SELECT COALESCE((SELECT MAX(group_name) FROM cc_group WHERE org_pac_id = @id),
                                (SELECT MAX(group_name) FROM medicare_reassignment WHERE group_pac_id = @id),
                                (SELECT MAX(org_name) FROM medicare_enrollment WHERE pac_id = @id))
                """, new { id = pac }, cancellationToken: ct));
            groups.Add(new OrganizationGroup(pac, name ?? "Group " + pac, (int)count));
        }

        return [.. groups.OrderByDescending(g => g.Clinicians).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The Medicare groups a clinician reassigns benefits to (bills through), by name.</summary>
    public async Task<IReadOnlyList<OrganizationGroup>> GetGroupsForClinicianAsync(string npi, CancellationToken ct)
    {
        if (!InputFormats.IsNpi(npi))
        {
            return [];
        }

        await using var connection = await OpenAsync(ct);
        return (await connection.QueryAsync<(string PacId, string? Name)>(new CommandDefinition(
                "SELECT group_pac_id, MAX(group_name) FROM medicare_reassignment WHERE npi = @npi GROUP BY group_pac_id ORDER BY MAX(group_name)",
                new { npi }, cancellationToken: ct)))
            .Select(g => new OrganizationGroup(g.PacId, g.Name ?? "Group " + g.PacId, 0))
            .ToList();
    }

    /// <summary>Every active clinician NPI of a group (PAC ID) or facility (CCN), for the CSV export.</summary>
    public async Task<IReadOnlyList<string>> GetClinicianNpisAsync(string id, bool facility, CancellationToken ct)
    {
        if (facility ? !IsCcn(id) : !IsPacId(id))
        {
            return [];
        }

        await using var connection = await OpenAsync(ct);
        return (await connection.QueryAsync<string>(new CommandDefinition(
            $"SELECT p.npi FROM ({(facility ? FacilityNpis : GroupNpis)}) g JOIN provider p ON p.npi = g.npi ORDER BY p.sort_name, p.npi",
            new { id = facility ? id.ToUpperInvariant() : id }, cancellationToken: ct))).ToList();
    }

    private static async Task<ClinicianList> ClinicianListAsync(MySqlConnection connection, string npisSql, string id, int page, CancellationToken ct)
    {
        // Only active providers (in the projection); the breakdown is by primary taxonomy, named in plain words.
        var byCode = (await connection.QueryAsync<CodeCount>(new CommandDefinition(
            $"SELECT p.primary_taxonomy_code AS Code, COUNT(*) AS Clinicians FROM ({npisSql}) g JOIN provider p ON p.npi = g.npi GROUP BY p.primary_taxonomy_code",
            new { id }, cancellationToken: ct))).ToList();
        var total = (int)byCode.Sum(c => c.Clinicians);
        var bySpecialty = byCode
            .GroupBy(c => PlainSpecialty(c.Code) ?? "Other or not listed")
            .Select(g => new SpecialtyCount(g.Key, (int)g.Sum(c => c.Clinicians)))
            .OrderByDescending(s => s.Clinicians).ThenBy(s => s.Specialty, StringComparer.Ordinal)
            .ToList();
        var pages = Math.Max(1, (total + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 1, pages);
        var rows = await connection.QueryAsync<ClinicianDbRow>(new CommandDefinition(
            $"""
            SELECT p.npi AS Npi, p.entity_type AS EntityType, p.last_name AS LastName, p.first_name AS FirstName, p.middle_name AS MiddleName,
                   p.name_suffix AS NameSuffix,
                   COALESCE((SELECT GROUP_CONCAT(pc.credential ORDER BY pc.ord SEPARATOR ', ') FROM provider_credential pc WHERE pc.npi = p.npi AND pc.ord < 100),
                            p.credential) AS Credential,
                   p.org_name AS OrgName, p.gender AS Gender,
                   p.primary_taxonomy_code AS PrimaryTaxonomyCode, l.city AS City, l.state AS State
            FROM (SELECT p.npi FROM ({npisSql}) g JOIN provider p ON p.npi = g.npi
                  ORDER BY p.sort_name, p.npi LIMIT {PageSize} OFFSET {(page - 1) * PageSize}) pg
            JOIN provider p ON p.npi = pg.npi
            LEFT JOIN provider_location l ON l.npi = p.npi AND l.is_primary = 1
            ORDER BY p.sort_name, p.npi
            """, new { id }, cancellationToken: ct));
        var list = rows.Select(r => new ClinicianRow(r.Npi,
                ProviderNames.Display(r.EntityType, r.LastName, r.FirstName, r.MiddleName, r.NameSuffix, r.OrgName),
                r.Credential, r.Gender, PlainSpecialty(r.PrimaryTaxonomyCode), r.City, r.State))
            .ToList();
        return new ClinicianList(total, bySpecialty, list, page, PageSize);
    }

    private static async Task<IReadOnlyList<OrganizationNpi>> NpisAsync(MySqlConnection connection, string npisSql, string id, CancellationToken ct) =>
        (await connection.QueryAsync<NpiRow>(new CommandDefinition(
            $"""
            SELECT p.npi AS Npi, p.entity_type AS EntityType, p.last_name AS LastName, p.first_name AS FirstName, p.middle_name AS MiddleName,
                   p.name_suffix AS NameSuffix, p.org_name AS OrgName, p.primary_taxonomy_code AS PrimaryTaxonomyCode, l.city AS City, l.state AS State
            FROM ({npisSql}) g
            JOIN provider p ON p.npi = g.npi
            LEFT JOIN provider_location l ON l.npi = p.npi AND l.is_primary = 1
            ORDER BY p.sort_name LIMIT 200
            """, new { id }, cancellationToken: ct)))
        .Select(r => new OrganizationNpi(r.Npi, ProviderNames.Display(r.EntityType, r.LastName, r.FirstName, r.MiddleName, r.NameSuffix, r.OrgName),
            PlainSpecialty(r.PrimaryTaxonomyCode), r.City, r.State))
        .ToList();

    private static string? PlainSpecialty(string? code) =>
        SpecialtyCategories.Default.Place(code) is { } p && p.Category != SpecialtyCategories.FallbackCategory ? p.Singular : null;

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
