using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

// Health systems, accountable care organizations and HRSA health center sites (docs/PRODUCT_PLAN.md step 3.6 part 2). Every link
// between them and our providers, groups and facilities is by an identifier: a CMS Certification Number (the AHRQ Compendium's
// linkage files, HRSA's Medicare billing number), an NPI (HRSA; CMS's facility enrollments), or a Medicare PAC ID through the
// enrollment of an NPI that bills for a facility. ACO participants are published by name only: they are linked to an
// organization NPI whose NPPES legal business name is the same name (case, spacing and punctuation aside), and every place that
// shows such a link says it is a name match (owner, 2026-10-15).

/// <summary>A health system a facility, organization NPI or group belongs to, and the facility (CCN) that links it.</summary>
public sealed record HealthSystemLink(string SystemId, string Name, string? Ccn, string? FacilityName);

/// <summary>
/// A health system in AHRQ's Compendium of U.S. Health Systems (<see cref="DataYear"/>): AHRQ's counts of its physicians, nurse
/// practitioners, physician assistants, group practices, hospitals, nursing homes and home health agencies, its beds and
/// discharges, whether it includes a major teaching hospital and offers health insurance (a Medicare Advantage plan).
/// <see cref="LinkedFacilities"/> counts the facilities its linkage files place in it (by CCN).
/// </summary>
public sealed record HealthSystemSummary(string SystemId, string Name, string? City, string? State, int DataYear, int? Physicians, int? PrimaryCarePhysicians,
    int? NursePractitioners, int? PhysicianAssistants, int? GroupPractices, int? Hospitals, int? NursingHomes, int? HomeHealthAgencies, int? Beds,
    int? Discharges, bool? MajorTeaching, bool? OffersInsurance, bool? MedicareAdvantage, int LinkedFacilities);

/// <summary>A page of health systems.</summary>
public sealed record HealthSystemPage(IReadOnlyList<HealthSystemSummary> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// A facility in a health system: <see cref="Type"/> as on the Facilities &amp; hospitals list (hospital, nursing_home, home_health …),
/// <see cref="Active"/> and <see cref="Rating"/> from that list (null before it was built, or for a CCN it doesn't have).
/// </summary>
public sealed record HealthSystemFacility(string Ccn, string Type, string Name, string? City, string? State, bool? Active, double? Rating);

/// <summary>A Medicare group that is the same organization as one of the system's facilities (its PAC ID enrolls an NPI billing for it).</summary>
public sealed record HealthSystemGroup(string PacId, string Name, string? State, int Clinicians);

/// <summary>A health system's page: the system, the states its facilities are in, its facilities and its Medicare groups.</summary>
public sealed record HealthSystemDetail(HealthSystemSummary System, IReadOnlyList<string> States, IReadOnlyList<HealthSystemFacility> Facilities,
    IReadOnlyList<HealthSystemGroup> Groups);

/// <summary>
/// The health system list's filters. <see cref="Q"/>: an AHRQ system ID or words of the name or headquarters city (every word must
/// appear); <see cref="State"/>: headquartered there; sorts name, hospitals, physicians, beds ("-" first descending; default
/// "-hospitals").
/// </summary>
public sealed record HealthSystemFilter
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxQueryLength = 100;
    public const string DefaultSort = "-hospitals";

    public static readonly IReadOnlyList<string> Sorts = ["name", "hospitals", "physicians", "beds"];

    public string? Q { get; init; }

    public string? State { get; init; }

    public string? Sort { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = DefaultPageSize;

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (Q?.Trim().Length > MaxQueryLength)
        {
            errors["q"] = [$"At most {MaxQueryLength} characters."];
        }

        if (!string.IsNullOrWhiteSpace(State) && !(State.Trim() is { Length: 2 } state && state.All(char.IsAsciiLetter)))
        {
            errors["state"] = ["A two-letter state code."];
        }

        if (!string.IsNullOrWhiteSpace(Sort) && !Sorts.Contains(Sort.Trim().TrimStart('-'), StringComparer.OrdinalIgnoreCase))
        {
            errors["sort"] = [$"One of {string.Join(", ", Sorts)}, with \"-\" first for descending."];
        }

        if (Page < 1)
        {
            errors["page"] = ["Must be 1 or more."];
        }

        if (PageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"Must be 1–{MaxPageSize}."];
        }

        return errors;
    }
}

/// <summary>How many systems, ACOs and health center sites there are (the Groups &amp; organizations page).</summary>
public sealed record NetworkOverview(int Systems, int? SystemYear, int SystemFacilities, int Acos, int? AcoYear, int AcoParticipants, int HealthCenterSites,
    int HealthCenters);

/// <summary>Health systems from AHRQ's Compendium (tables <c>health_system</c>, <c>health_system_facility</c>; loader source ahrq_compendium).</summary>
public sealed partial class HealthSystemService(string connectionString)
{
    private static readonly TimeSpan OverviewLifetime = TimeSpan.FromMinutes(10);
    private (DateTime At, NetworkOverview Value)? _overview;

    /// <summary>AHRQ's system ID: letters then digits (HSI00000010), at most 20 characters.</summary>
    public static bool IsSystemId(string? id) => id is { Length: >= 2 and <= 20 } && SystemIdPattern().IsMatch(id);

    public async Task<HealthSystemPage> SearchAsync(HealthSystemFilter filter, CancellationToken ct)
    {
        if (filter.Validate() is { Count: > 0 } errors)
        {
            throw new ArgumentException(string.Join(" ", errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}")), nameof(filter));
        }

        var q = filter.Q?.Trim() ?? "";
        var where = new List<string>();
        var parameters = new DynamicParameters();
        if (IsSystemId(q) && q.Any(char.IsAsciiDigit))
        {
            where.Add("(s.system_id = @id OR CONCAT(s.name, ' ', COALESCE(s.city, '')) LIKE @like0)");
            parameters.Add("id", q);
            parameters.Add("like0", "%" + SearchQuery.Prefix(q));
        }
        else
        {
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < words.Length && i < 10; i++)
            {
                where.Add($"CONCAT(s.name, ' ', COALESCE(s.city, '')) LIKE @like{i}");
                parameters.Add($"like{i}", "%" + SearchQuery.Prefix(words[i]));
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.State))
        {
            where.Add("s.state = @state");
            parameters.Add("state", filter.State.Trim().ToUpperInvariant());
        }

        var sort = string.IsNullOrWhiteSpace(filter.Sort) ? HealthSystemFilter.DefaultSort : filter.Sort.Trim();
        var direction = sort.StartsWith('-') ? "DESC" : "ASC";
        var order = sort.TrimStart('-').ToLowerInvariant() switch
        {
            "name" => $"s.name {direction}",
            "physicians" => $"s.physicians IS NULL, s.physicians {direction}, s.name",
            "beds" => $"s.beds IS NULL, s.beds {direction}, s.name",
            _ => $"s.hospitals IS NULL, s.hospitals {direction}, s.name",
        };
        var whereSql = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        parameters.Add("offset", (filter.Page - 1) * filter.PageSize);
        parameters.Add("pageSize", filter.PageSize);
        await using var connection = await OpenAsync(ct);
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT COUNT(*) FROM health_system s {whereSql}", parameters, cancellationToken: ct));
        var rows = await connection.QueryAsync<SystemRow>(new CommandDefinition(
            $"{SystemSelect} {whereSql} ORDER BY {order}, s.system_id LIMIT @offset, @pageSize", parameters, cancellationToken: ct));
        return new HealthSystemPage([.. rows.Select(r => r.ToSummary())], filter.Page, filter.PageSize, total);
    }

    public async Task<HealthSystemDetail?> GetAsync(string id, CancellationToken ct)
    {
        if (!IsSystemId(id))
        {
            return null;
        }

        await using var connection = await OpenAsync(ct);
        var system = await connection.QueryFirstOrDefaultAsync<SystemRow>(new CommandDefinition($"{SystemSelect} WHERE s.system_id = @id", new { id }, cancellationToken: ct));
        if (system is null)
        {
            return null;
        }

        var facilities = (await connection.QueryAsync<FacilityRow>(new CommandDefinition(
                """
                SELECT f.ccn AS Ccn, COALESCE(d.type, f.kind) AS Type, COALESCE(d.name, f.name, p.name, CONCAT('CCN ', f.ccn)) AS Name,
                       COALESCE(d.city, f.city, p.city) AS City, COALESCE(d.state, f.state, p.state) AS State,
                       CAST(d.active AS SIGNED) AS Active, d.rating AS Rating
                FROM health_system_facility f
                LEFT JOIN directory_facility d ON d.ccn = f.ccn
                LEFT JOIN cms_pos_facility p ON p.ccn = f.ccn
                WHERE f.system_id = @id
                """, new { id }, cancellationToken: ct)))
            .Select(r => new HealthSystemFacility(r.Ccn, r.Type, r.Name, r.City, r.State?.ToUpperInvariant(), r.Active is { } a ? a != 0 : null, r.Rating))
            .OrderBy(f => TypeOrder(f.Type)).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Ccn, StringComparer.Ordinal)
            .ToList();
        var groups = (await connection.QueryAsync<GroupRow>(new CommandDefinition(
                """
                SELECT g.pac_id AS PacId, g.name AS Name, g.state AS State, g.clinicians AS Clinicians
                FROM directory_group g
                WHERE g.pac_id IN (SELECT e.pac_id FROM health_system_facility f
                                   JOIN cms_facility_npi n ON n.ccn = f.ccn
                                   JOIN medicare_enrollment e ON e.npi = n.npi
                                   WHERE f.system_id = @id AND e.pac_id IS NOT NULL)
                ORDER BY g.clinicians DESC, g.name, g.pac_id
                LIMIT 200
                """, new { id }, cancellationToken: ct)))
            .Select(g => new HealthSystemGroup(g.PacId, g.Name, g.State, g.Clinicians))
            .ToList();
        var states = facilities.Select(f => f.State).OfType<string>().Where(s => s.Length == 2).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new HealthSystemDetail(system.ToSummary(), states, facilities, groups);
    }

    /// <summary>The systems a facility belongs to (by its CCN).</summary>
    public async Task<IReadOnlyList<HealthSystemLink>> ForCcnAsync(string ccn, CancellationToken ct)
    {
        if (!OrganizationService.IsCcn(ccn))
        {
            return [];
        }

        await using var connection = await OpenAsync(ct);
        return await ForCcnAsync(connection, ccn.ToUpperInvariant(), ct);
    }

    internal static async Task<IReadOnlyList<HealthSystemLink>> ForCcnAsync(MySqlConnection connection, string ccn, CancellationToken ct) =>
        Distinct(await connection.QueryAsync<HealthSystemLink>(new CommandDefinition(
            """
            SELECT s.system_id AS SystemId, s.name AS Name, f.ccn AS Ccn, f.name AS FacilityName
            FROM health_system_facility f JOIN health_system s ON s.system_id = f.system_id
            WHERE f.ccn = @ccn
            ORDER BY s.name
            """, new { ccn }, cancellationToken: ct)));

    /// <summary>The systems of the facilities an organization NPI bills for (CMS's facility enrollments, CCN to NPI).</summary>
    public async Task<IReadOnlyList<HealthSystemLink>> ForNpiAsync(string npi, CancellationToken ct)
    {
        if (!InputFormats.IsNpi(npi))
        {
            return [];
        }

        await using var connection = await OpenAsync(ct);
        return Distinct(await connection.QueryAsync<HealthSystemLink>(new CommandDefinition(
            """
            SELECT s.system_id AS SystemId, s.name AS Name, f.ccn AS Ccn, f.name AS FacilityName
            FROM cms_facility_npi n
            JOIN health_system_facility f ON f.ccn = n.ccn
            JOIN health_system s ON s.system_id = f.system_id
            WHERE n.npi = @npi
            ORDER BY s.name, f.ccn
            """, new { npi }, cancellationToken: ct)));
    }

    /// <summary>
    /// The systems of a Medicare group: the group's PAC ID is the PECOS identity of an organization; when that organization is
    /// also enrolled as a facility in a system (an NPI under the PAC ID bills for the facility's CCN), the group is the system's.
    /// </summary>
    public async Task<IReadOnlyList<HealthSystemLink>> ForPacAsync(string pacId, CancellationToken ct)
    {
        if (!OrganizationService.IsPacId(pacId))
        {
            return [];
        }

        await using var connection = await OpenAsync(ct);
        return Distinct(await connection.QueryAsync<HealthSystemLink>(new CommandDefinition(
            """
            SELECT s.system_id AS SystemId, s.name AS Name, f.ccn AS Ccn, f.name AS FacilityName
            FROM medicare_enrollment e
            JOIN cms_facility_npi n ON n.npi = e.npi
            JOIN health_system_facility f ON f.ccn = n.ccn
            JOIN health_system s ON s.system_id = f.system_id
            WHERE e.pac_id = @pacId
            ORDER BY s.name, f.ccn
            """, new { pacId }, cancellationToken: ct)));
    }

    /// <summary>The counts on the Groups &amp; organizations page (cached for a few minutes).</summary>
    public async Task<NetworkOverview> GetOverviewAsync(CancellationToken ct)
    {
        if (_overview is { } cached && DateTime.UtcNow - cached.At < OverviewLifetime)
        {
            return cached.Value;
        }

        await using var connection = await OpenAsync(ct);
        var row = await connection.QuerySingleAsync<OverviewRow>(new CommandDefinition(
            """
            SELECT (SELECT COUNT(*) FROM health_system) AS Systems, (SELECT MAX(data_year) FROM health_system) AS SystemYear,
                   (SELECT COUNT(DISTINCT ccn) FROM health_system_facility) AS SystemFacilities,
                   (SELECT COUNT(*) FROM aco) AS Acos, (SELECT MAX(program_year) FROM aco) AS AcoYear,
                   (SELECT COUNT(*) FROM aco_participant) AS AcoParticipants,
                   (SELECT COUNT(*) FROM hrsa_health_center_site) AS HealthCenterSites,
                   (SELECT COUNT(DISTINCT grant_number) FROM hrsa_health_center_site) AS HealthCenters
            """, cancellationToken: ct));
        var overview = new NetworkOverview((int)row.Systems, row.SystemYear, (int)row.SystemFacilities, (int)row.Acos, row.AcoYear, (int)row.AcoParticipants,
            (int)row.HealthCenterSites, (int)row.HealthCenters);
        _overview = (DateTime.UtcNow, overview);
        return overview;
    }

    private static List<HealthSystemLink> Distinct(IEnumerable<HealthSystemLink> links) =>
        links.GroupBy(l => l.SystemId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

    private static int TypeOrder(string type)
    {
        for (var i = 0; i < FacilityTypes.All.Count; i++)
        {
            if (FacilityTypes.All[i].Code == type)
            {
                return i;
            }
        }

        return FacilityTypes.All.Count;
    }

    private const string SystemSelect =
        """
        SELECT s.system_id AS SystemId, s.name AS Name, s.city AS City, s.state AS State, s.data_year AS DataYear, s.physicians AS Physicians,
               s.primary_care_physicians AS PrimaryCarePhysicians, s.nurse_practitioners AS NursePractitioners, s.physician_assistants AS PhysicianAssistants,
               s.group_practices AS GroupPractices, s.hospitals AS Hospitals, s.nursing_homes AS NursingHomes, s.home_health_agencies AS HomeHealthAgencies,
               s.beds AS Beds, s.discharges AS Discharges, CAST(s.major_teaching AS SIGNED) AS MajorTeaching, CAST(s.offers_insurance AS SIGNED) AS OffersInsurance,
               CAST(s.medicare_advantage AS SIGNED) AS MedicareAdvantage,
               (SELECT COUNT(*) FROM health_system_facility f WHERE f.system_id = s.system_id) AS LinkedFacilities
        FROM health_system s
        """;

    // Types as MySqlConnector returns them (Dapper maps records by constructor).
    private sealed record SystemRow(string SystemId, string Name, string? City, string? State, short DataYear, int? Physicians, int? PrimaryCarePhysicians,
        int? NursePractitioners, int? PhysicianAssistants, int? GroupPractices, int? Hospitals, int? NursingHomes, int? HomeHealthAgencies, int? Beds,
        int? Discharges, long? MajorTeaching, long? OffersInsurance, long? MedicareAdvantage, long LinkedFacilities)
    {
        public HealthSystemSummary ToSummary() => new(SystemId, Name, City, State, DataYear, Physicians, PrimaryCarePhysicians, NursePractitioners,
            PhysicianAssistants, GroupPractices, Hospitals, NursingHomes, HomeHealthAgencies, Beds, Discharges, Bool(MajorTeaching), Bool(OffersInsurance),
            Bool(MedicareAdvantage), (int)LinkedFacilities);

        private static bool? Bool(long? value) => value is { } v ? v != 0 : null;
    }

    private sealed record FacilityRow(string Ccn, string Type, string Name, string? City, string? State, long? Active, double? Rating);

    private sealed record GroupRow(string PacId, string Name, string? State, int Clinicians);

    private sealed record OverviewRow(long Systems, short? SystemYear, long SystemFacilities, long Acos, short? AcoYear, long AcoParticipants,
        long HealthCenterSites, long HealthCenters);

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    [GeneratedRegex("^[A-Za-z]{1,6}[0-9]{1,14}$")]
    private static partial Regex SystemIdPattern();
}

/// <summary>A Medicare Shared Savings Program ACO in <see cref="ProgramYear"/>, with how many participants and SNF affiliates CMS lists.</summary>
public sealed record AcoSummary(string AcoId, string Name, int ProgramYear, string? ServiceArea, string? Track, int? AgreementPeriod,
    DateOnly? InitialStartDate, DateOnly? CurrentStartDate, int Participants, int SnfAffiliates, string? Website);

/// <summary>A page of ACOs.</summary>
public sealed record AcoPage(IReadOnlyList<AcoSummary> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// An ACO's page: its participants by legal business name, as CMS publishes them (no TIN, NPI or CCN), each with the organization
/// NPIs of the same name, and its skilled nursing facility affiliates by name (not linked).
/// </summary>
public sealed record AcoDetail(AcoSummary Aco, string? Address, double? Latitude, double? Longitude, IReadOnlyList<AcoParticipant> Participants,
    IReadOnlyList<string> SnfAffiliates);

/// <summary>An active organization NPI whose NPPES legal business name is an ACO participant's name, with its primary practice city.</summary>
public sealed record AcoParticipantMatch(string Npi, string Name, string? City, string? State);

/// <summary>
/// An ACO participant as CMS lists it (legal business name) and the active organization NPIs with exactly that legal name
/// (<see cref="AcoNames.Key"/>): one is a link, two to <see cref="AcoNames.MaxCandidates"/> are candidates, more are not linked,
/// unless they are a hospital and its own NPIs (<see cref="HospitalGroup"/>, up to <see cref="AcoNames.MaxHospitalCandidates"/>).
/// <see cref="MatchedBy"/> is "name" whenever <see cref="Organizations"/> isn't empty: CMS publishes no identifier to match by.
/// <see cref="GroupedBy"/> / <see cref="GroupedValue"/>: why the NPIs of a hospital group belong together (see <see cref="AcoNames"/>).
/// </summary>
public sealed record AcoParticipant(string Name, IReadOnlyList<AcoParticipantMatch> Organizations)
{
    public string? MatchedBy => Organizations.Count > 0 ? AcoNames.MatchedByName : null;

    public bool HospitalGroup { get; init; }

    public string? GroupedBy { get; init; }

    public string? GroupedValue { get; init; }
}

/// <summary>
/// An organization NPI's place in an ACO, found by name (<see cref="MatchedBy"/> = "name"): the participant's name as CMS lists it,
/// and how many active organizations share it (<see cref="Candidates"/>: 1 = the only one). <see cref="HospitalGroup"/>: every
/// they are a hospital and its own NPIs (its clinics, pharmacies, suppliers and groups under its legal name), with <see cref="GroupedBy"/> / <see cref="GroupedValue"/> as on
/// <see cref="AcoParticipant"/>.
/// </summary>
public sealed record AcoMembership(string AcoId, string AcoName, int ProgramYear, string ParticipantName, string Npi, int Candidates)
{
    public string MatchedBy { get; init; } = AcoNames.MatchedByName;

    public bool HospitalGroup { get; init; }

    public string? GroupedBy { get; init; }

    public string? GroupedValue { get; init; }
}

/// <summary>
/// The exact legal-name match of ACO participants to organization NPIs (owner, 2026-10-15): the participant's legal business name
/// and the NPPES legal business name compared after folding case, spacing and punctuation only (letters and digits, upper-cased);
/// no words are dropped and no company name key is used, so "Harbor Medical Group, P.C." = "HARBOR MEDICAL GROUP PC" but
/// "Harbor Medical Group" ≠ "Harbor Medical Group PC".
/// </summary>
public static class AcoNames
{
    /// <summary>A name shared by more active organizations than this is not linked at all, unless they are a hospital and its own NPIs.</summary>
    public const int MaxCandidates = 5;

    /// <summary>
    /// A name shared by a hospital (a NUCC 27 hospital unit or 28 hospital taxonomy, or CMS's enrollment of the NPI as a hospital) and
    /// its own NPIs links up to this many (owner, 2026-10-15): every other NPI of the name shares the hospital's CCN or names the
    /// hospital, or its parent organization, as parent organization in NPPES: its clinics, pharmacies, suppliers and groups under its
    /// legal name. 50 is the owner's ceiling; in a 300-name sample of the 2026 participants the names that include a hospital had
    /// 2–15 NPIs (one health system's name 78), so 50 is well above a hospital and its own NPIs while a name shared by more is
    /// treated as common.
    /// </summary>
    public const int MaxHospitalCandidates = 50;

    public const string MatchedByName = "name";

    /// <summary>A hospital group's NPIs share a CCN (<c>grouped_value</c> = the CCN).</summary>
    public const string GroupedByCcn = "ccn";

    /// <summary>Its subparts name the same parent organization (<c>grouped_value</c> = the parent's legal business name).</summary>
    public const string GroupedByParent = "parent";

    /// <summary>Its subparts name the same parent organization tax ID (not stored or shown).</summary>
    public const string GroupedByParentTin = "parent_tin";

    /// <summary>
    /// They are a hospital's own NPIs, some sharing its CCN and others naming it as parent organization (<c>grouped_value</c> = the
    /// hospital's legal business name).
    /// </summary>
    public const string GroupedByHospital = "hospital";

    /// <summary>"They share CCN 330045." / "They name the same parent organization, X." / "… the same parent tax ID."; null otherwise.</summary>
    public static string? GroupReason(string? groupedBy, string? groupedValue) => groupedBy switch
    {
        GroupedByCcn => $"They share Medicare CCN {groupedValue}.",
        GroupedByParent => string.IsNullOrEmpty(groupedValue) ? "They name the same parent organization." : $"They name the same parent organization, {groupedValue}.",
        GroupedByParentTin => "They name the same parent organization tax ID.",
        GroupedByHospital => $"They are the hospital's own NPIs: each shares its CCN or names {groupedValue} as its parent organization.",
        _ => null,
    };

    /// <summary>The letters and digits of a name, upper-cased; null when it has none.</summary>
    public static string? Key(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var key = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                key.Append(char.ToUpperInvariant(ch));
            }
        }

        return key.Length == 0 ? null : key.ToString();
    }
}

/// <summary>The ACO list's filters: <see cref="Q"/> = an ACO ID or words of its name or a participant's name; <see cref="State"/> = in its service area.</summary>
public sealed record AcoFilter
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxQueryLength = 100;

    public string? Q { get; init; }

    public string? State { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = DefaultPageSize;

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (Q?.Trim().Length > MaxQueryLength)
        {
            errors["q"] = [$"At most {MaxQueryLength} characters."];
        }

        if (!string.IsNullOrWhiteSpace(State) && !(State.Trim() is { Length: 2 } state && state.All(char.IsAsciiLetter)))
        {
            errors["state"] = ["A two-letter state code."];
        }

        if (Page < 1)
        {
            errors["page"] = ["Must be 1 or more."];
        }

        if (PageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"Must be 1–{MaxPageSize}."];
        }

        return errors;
    }
}

/// <summary>Medicare Shared Savings Program ACOs (tables <c>aco</c>, <c>aco_participant</c>, <c>aco_snf_affiliate</c>; loader source cms_aco).</summary>
public sealed class AcoService(string connectionString)
{
    /// <summary>A letter and digits (A1234), at most 10 characters.</summary>
    public static bool IsAcoId(string? id) => id is { Length: >= 2 and <= 10 } && char.IsAsciiLetter(id[0]) && id.All(char.IsAsciiLetterOrDigit);

    public async Task<AcoPage> SearchAsync(AcoFilter filter, CancellationToken ct)
    {
        if (filter.Validate() is { Count: > 0 } errors)
        {
            throw new ArgumentException(string.Join(" ", errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}")), nameof(filter));
        }

        var q = filter.Q?.Trim() ?? "";
        var where = new List<string>();
        var parameters = new DynamicParameters();
        if (q.Length > 0)
        {
            // Every word in the ACO's name, or the whole text in a participant's name, or the ACO ID.
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(10).ToList();
            var byName = string.Join(" AND ", words.Select((_, i) => $"a.name LIKE @w{i}"));
            for (var i = 0; i < words.Count; i++)
            {
                parameters.Add($"w{i}", "%" + SearchQuery.Prefix(words[i]));
            }

            where.Add($"(a.aco_id = @q OR ({byName}) OR EXISTS (SELECT 1 FROM aco_participant p WHERE p.aco_id = a.aco_id AND p.name LIKE @whole))");
            parameters.Add("q", q);
            parameters.Add("whole", "%" + SearchQuery.Prefix(q));
        }

        if (!string.IsNullOrWhiteSpace(filter.State))
        {
            // The service area lists states: "AZ, NV".
            where.Add("CONCAT(',', REPLACE(COALESCE(a.service_area, ''), ' ', ''), ',') LIKE @state");
            parameters.Add("state", $"%,{filter.State.Trim().ToUpperInvariant()},%");
        }

        var whereSql = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        parameters.Add("offset", (filter.Page - 1) * filter.PageSize);
        parameters.Add("pageSize", filter.PageSize);
        await using var connection = await OpenAsync(ct);
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT COUNT(*) FROM aco a {whereSql}", parameters, cancellationToken: ct));
        var rows = await connection.QueryAsync<AcoRow>(new CommandDefinition(
            $"{AcoSelect} {whereSql} ORDER BY a.name, a.aco_id LIMIT @offset, @pageSize", parameters, cancellationToken: ct));
        return new AcoPage([.. rows.Select(r => r.ToSummary())], filter.Page, filter.PageSize, total);
    }

    public async Task<AcoDetail?> GetAsync(string id, CancellationToken ct)
    {
        if (!IsAcoId(id))
        {
            return null;
        }

        await using var connection = await OpenAsync(ct);
        var aco = await connection.QueryFirstOrDefaultAsync<AcoRow>(new CommandDefinition($"{AcoSelect} WHERE a.aco_id = @id", new { id }, cancellationToken: ct));
        if (aco is null)
        {
            return null;
        }

        var participants = (await connection.QueryAsync<ParticipantRow>(new CommandDefinition(
                """
                SELECT p.name AS Name, m.npi AS Npi, pr.org_name AS OrgName, l.city AS City, l.state AS State,
                       CAST(m.hospital_group AS SIGNED) AS HospitalGroup, m.grouped_by AS GroupedBy, m.grouped_value AS GroupedValue
                FROM aco_participant p
                LEFT JOIN aco_participant_npi m ON m.aco_id = p.aco_id AND m.name = p.name
                LEFT JOIN provider pr ON pr.npi = m.npi
                LEFT JOIN provider_location l ON l.npi = m.npi AND l.is_primary = 1
                WHERE p.aco_id = @id
                ORDER BY p.name, pr.org_name, m.npi
                """, new { id }, cancellationToken: ct)))
            .GroupBy(r => r.Name, StringComparer.Ordinal)
            .Select(g => new AcoParticipant(g.Key, [.. g.Where(r => r.Npi is not null && r.OrgName is not null)
                .DistinctBy(r => r.Npi)
                .Select(r => new AcoParticipantMatch(r.Npi!, r.OrgName!, r.City, r.State?.ToUpperInvariant()))])
            {
                HospitalGroup = g.Any(r => r.HospitalGroup == 1),
                GroupedBy = g.Select(r => r.GroupedBy).FirstOrDefault(v => v is not null),
                GroupedValue = g.Select(r => r.GroupedValue).FirstOrDefault(v => v is not null),
            })
            .ToList();
        var snf = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM aco_snf_affiliate WHERE aco_id = @id ORDER BY name", new { id }, cancellationToken: ct))).ToList();
        return new AcoDetail(aco.ToSummary(), aco.Address, aco.Lat, aco.Lon, participants, snf);
    }

    /// <summary>The ACOs an organization NPI is a participant of, by its legal name (see <see cref="AcoNames"/>).</summary>
    public async Task<IReadOnlyList<AcoMembership>> ForNpiAsync(string npi, CancellationToken ct)
    {
        if (!InputFormats.IsNpi(npi))
        {
            return [];
        }

        await using var connection = await OpenAsync(ct);
        return await MembershipsAsync(connection, "m.npi = @id", new { id = npi }, ct);
    }

    /// <summary>The ACOs of a Medicare group: those its organization NPIs (enrolled under its PAC ID) are participants of, by name.</summary>
    public async Task<IReadOnlyList<AcoMembership>> ForPacAsync(string pacId, CancellationToken ct)
    {
        if (!OrganizationService.IsPacId(pacId))
        {
            return [];
        }

        await using var connection = await OpenAsync(ct);
        return await MembershipsAsync(connection, "m.npi IN (SELECT npi FROM medicare_enrollment WHERE pac_id = @id)", new { id = pacId }, ct);
    }

    internal static async Task<IReadOnlyList<AcoMembership>> MembershipsAsync(MySqlConnection connection, string condition, object parameters, CancellationToken ct) =>
        (await connection.QueryAsync<MembershipRow>(new CommandDefinition(
            $"""
            SELECT a.aco_id AS AcoId, a.name AS AcoName, a.program_year AS ProgramYear, m.name AS ParticipantName, m.npi AS Npi,
                   CAST(m.candidates AS SIGNED) AS Candidates, CAST(m.hospital_group AS SIGNED) AS HospitalGroup, m.grouped_by AS GroupedBy,
                   m.grouped_value AS GroupedValue
            FROM aco_participant_npi m JOIN aco a ON a.aco_id = m.aco_id
            WHERE {condition}
            ORDER BY a.name, a.aco_id, m.npi
            LIMIT 50
            """, parameters, cancellationToken: ct)))
        .Select(r => new AcoMembership(r.AcoId, r.AcoName, r.ProgramYear, r.ParticipantName, r.Npi, (int)r.Candidates)
        {
            HospitalGroup = r.HospitalGroup == 1, GroupedBy = r.GroupedBy, GroupedValue = r.GroupedValue,
        })
        .ToList();

    private sealed record ParticipantRow(string Name, string? Npi, string? OrgName, string? City, string? State, long? HospitalGroup, string? GroupedBy,
        string? GroupedValue);

    private sealed record MembershipRow(string AcoId, string AcoName, short ProgramYear, string ParticipantName, string Npi, long Candidates,
        long HospitalGroup, string? GroupedBy, string? GroupedValue);

    private const string AcoSelect =
        """
        SELECT a.aco_id AS AcoId, a.name AS Name, a.program_year AS ProgramYear, a.service_area AS ServiceArea, a.track AS Track,
               CAST(a.agreement_period AS SIGNED) AS AgreementPeriod, a.initial_start_date AS InitialStartDate, a.current_start_date AS CurrentStartDate,
               (SELECT COUNT(*) FROM aco_participant p WHERE p.aco_id = a.aco_id) AS Participants,
               (SELECT COUNT(*) FROM aco_snf_affiliate s WHERE s.aco_id = a.aco_id) AS SnfAffiliates,
               a.website AS Website, a.address AS Address, a.lat AS Lat, a.lon AS Lon
        FROM aco a
        """;

    // Types as MySqlConnector returns them (Dapper maps records by constructor).
    private sealed record AcoRow(string AcoId, string Name, short ProgramYear, string? ServiceArea, string? Track, long? AgreementPeriod, DateTime? InitialStartDate,
        DateTime? CurrentStartDate, long Participants, long SnfAffiliates, string? Website, string? Address, double? Lat, double? Lon)
    {
        public AcoSummary ToSummary() => new(AcoId, Name, ProgramYear, ServiceArea, Track, AgreementPeriod is { } p ? (int)p : null,
            InitialStartDate is { } i ? DateOnly.FromDateTime(i) : null, CurrentStartDate is { } c ? DateOnly.FromDateTime(c) : null, (int)Participants,
            (int)SnfAffiliates, Website);
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}

/// <summary>
/// A site of an HRSA-funded health center (FQHC) or look-alike: where it is, its hours, its setting (school, hospital …), the
/// health center (<see cref="GrantNumber"/>, <see cref="HealthCenter"/>) and the Medicare number (<see cref="Ccn"/>) and NPI HRSA lists for it.
/// </summary>
public sealed record HealthCenterSite(string SiteId, string Name, string? Address, string? City, string? State, string? Zip, string? Phone, string? Website,
    double? HoursPerWeek, string? Setting, string? LocationType, string? SiteType, bool LookAlike, string? GrantNumber, string? HealthCenter,
    string? HealthCenterCity, string? HealthCenterState, string? Ccn, string? Npi, DateOnly? AddedDate, double? Latitude, double? Longitude);

/// <summary>
/// A health center site's page: the site, whether its CCN has a facility page (<see cref="CcnListed"/>), the active provider its NPI
/// is (<see cref="NpiName"/>), and the health center's other sites (the first 100 of <see cref="OtherSiteCount"/>).
/// </summary>
public sealed record HealthCenterSiteDetail(HealthCenterSite Site, bool CcnListed, string? NpiName, IReadOnlyList<HealthCenterSite> OtherSites, int OtherSiteCount)
{
    /// <summary>Its 340B registrations (HRSA OPAIS), by its HRSA site ID or its NPI; participating first.</summary>
    public IReadOnlyList<Covered340BEntity> Covered340B { get; init; } = [];
}

/// <summary>A health center site near a patient's ZIP (Find care), and how far it is.</summary>
public sealed record NearbyHealthCenter(HealthCenterSite Site, double DistanceMiles);

/// <summary>HRSA's health center sites (table <c>hrsa_health_center_site</c>; loader source hrsa_health_center_sites).</summary>
public sealed partial class HealthCenterService(string connectionString)
{
    public const int OtherSitesShown = 100;

    /// <summary>HRSA's site number: letters, digits and dashes (BPS-H80-031385), at most 20 characters.</summary>
    public static bool IsSiteId(string? id) => id is { Length: >= 3 and <= 20 } && SiteIdPattern().IsMatch(id);

    public async Task<HealthCenterSiteDetail?> GetSiteAsync(string id, CancellationToken ct)
    {
        if (!IsSiteId(id))
        {
            return null;
        }

        await using var connection = await OpenAsync(ct);
        var site = (await connection.QueryAsync<SiteRow>(new CommandDefinition($"{SiteSelect} WHERE site_id = @id", new { id }, cancellationToken: ct)))
            .Select(r => r.ToSite()).FirstOrDefault();
        if (site is null)
        {
            return null;
        }

        var listed = site.Ccn is not null && await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM cms_pos_facility WHERE ccn = @ccn) OR EXISTS (SELECT 1 FROM directory_facility WHERE ccn = @ccn)",
            new { ccn = site.Ccn }, cancellationToken: ct)) != 0;
        var npiName = site.Npi is null ? null : await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT COALESCE(org_name, sort_name) FROM provider WHERE npi = @npi", new { npi = site.Npi }, cancellationToken: ct));
        var others = new List<HealthCenterSite>();
        var count = 0;
        if (site.GrantNumber is { } grant)
        {
            count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM hrsa_health_center_site WHERE grant_number = @grant AND site_id <> @id", new { grant, id = site.SiteId }, cancellationToken: ct));
            others = (await connection.QueryAsync<SiteRow>(new CommandDefinition(
                    $"{SiteSelect} WHERE grant_number = @grant AND site_id <> @id ORDER BY name, site_id LIMIT {OtherSitesShown}",
                    new { grant, id = site.SiteId }, cancellationToken: ct)))
                .Select(r => r.ToSite()).ToList();
        }

        return new HealthCenterSiteDetail(site, listed, npiName, others, count)
        {
            Covered340B = await Program340BService.ForSiteAsync(connection, site.SiteId, site.Npi, ct),
        };
    }

    /// <summary>
    /// Health center sites nearest a point (Find care): the <paramref name="limit"/> nearest within <paramref name="radiusMiles"/>,
    /// by HRSA's point of each site. Mobile vans are left out (their address is a base, not where care is given).
    /// </summary>
    public async Task<IReadOnlyList<NearbyHealthCenter>> NearAsync(double lat, double lon, int radiusMiles, int limit, CancellationToken ct)
    {
        var halfLat = radiusMiles / 69.0;
        var halfLon = radiusMiles / (69.0 * Math.Max(0.2, Math.Cos(lat * Math.PI / 180)));
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<SiteRow>(new CommandDefinition(
            $"""
            {SiteSelect}
            WHERE lat BETWEEN @south AND @north AND lon BETWEEN @west AND @east AND COALESCE(location_type, '') <> 'Mobile Van'
            """, new { south = lat - halfLat, north = lat + halfLat, west = lon - halfLon, east = lon + halfLon }, cancellationToken: ct));
        return [.. rows.Select(r => r.ToSite())
            .Select(s => new NearbyHealthCenter(s, Miles(lat, lon, s.Latitude!.Value, s.Longitude!.Value)))
            .Where(n => n.DistanceMiles <= radiusMiles)
            .OrderBy(n => n.DistanceMiles).ThenBy(n => n.Site.Name, StringComparer.Ordinal)
            .Take(limit)];
    }

    /// <summary>Great-circle distance in miles.</summary>
    public static double Miles(double lat1, double lon1, double lat2, double lon2)
    {
        const double radius = 3958.8;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * radius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>The HRSA sites that are a certified facility: by the Medicare number HRSA lists, or an NPI CMS links to the CCN.</summary>
    public async Task<IReadOnlyList<HealthCenterSite>> ForCcnAsync(string ccn, CancellationToken ct)
    {
        if (!OrganizationService.IsCcn(ccn))
        {
            return [];
        }

        await using var connection = await OpenAsync(ct);
        return await ForCcnAsync(connection, ccn.ToUpperInvariant(), ct);
    }

    internal static async Task<IReadOnlyList<HealthCenterSite>> ForCcnAsync(MySqlConnection connection, string ccn, CancellationToken ct) =>
        (await connection.QueryAsync<SiteRow>(new CommandDefinition(
            $"""
            {SiteSelect}
            WHERE ccn = @ccn OR npi IN (SELECT npi FROM cms_facility_npi WHERE ccn = @ccn)
            ORDER BY name, site_id
            LIMIT 20
            """, new { ccn }, cancellationToken: ct)))
        .Select(r => r.ToSite()).ToList();

    private const string SiteSelect =
        """
        SELECT site_id AS SiteId, name AS Name, address AS Address, city AS City, state AS State, zip5 AS Zip, phone AS Phone, website AS Website,
               hours_per_week AS HoursPerWeek, setting AS Setting, location_type AS LocationType, site_type AS SiteType, CAST(look_alike AS SIGNED) AS LookAlike,
               grant_number AS GrantNumber, health_center AS HealthCenter, health_center_city AS HealthCenterCity, health_center_state AS HealthCenterState,
               ccn AS Ccn, npi AS Npi, added_date AS AddedDate, lat AS Lat, lon AS Lon
        FROM hrsa_health_center_site
        """;

    // Types as MySqlConnector returns them (Dapper maps records by constructor).
    private sealed record SiteRow(string SiteId, string Name, string? Address, string? City, string? State, string? Zip, string? Phone, string? Website,
        double? HoursPerWeek, string? Setting, string? LocationType, string? SiteType, long LookAlike, string? GrantNumber, string? HealthCenter,
        string? HealthCenterCity, string? HealthCenterState, string? Ccn, string? Npi, DateTime? AddedDate, double? Lat, double? Lon)
    {
        public HealthCenterSite ToSite() => new(SiteId, Name, Address, City, State, Zip, Phone, Website, HoursPerWeek, Setting, LocationType, SiteType,
            LookAlike != 0, GrantNumber, HealthCenter, HealthCenterCity, HealthCenterState, Ccn, Npi, AddedDate is { } d ? DateOnly.FromDateTime(d) : null, Lat, Lon);
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex SiteIdPattern();
}

/// <summary>A facility's place in a health system, as the AHRQ linkage files give it (by CCN).</summary>
public sealed record SystemMembership(string Ccn, string SystemId, string SystemName);

/// <summary>An HRSA health center site as the facility list uses it.</summary>
public sealed record HealthCenterListing(string SiteId, string Name, string? Address, string? City, string? State, string? Zip5, string? Phone, string? Website,
    string? Setting, string? LocationType, bool LookAlike, string? HealthCenter, string? Ccn, string? Npi, double? Latitude, double? Longitude);

/// <summary>
/// What the Facilities &amp; hospitals list adds from step 3.6 part 2 (<see cref="FacilityDirectory.Merge"/>): each facility's health
/// system (AHRQ, by CCN), and HRSA's health center sites, each matched to a certified facility by the Medicare number HRSA lists,
/// else by its NPI (CMS's enrollment of that NPI for a CCN, <see cref="CcnOfNpi"/>), else by the same street address and ZIP as an
/// FQHC; a site matched nowhere is a row of its own (type health_center, source hrsa). Participating 340B covered entities mark
/// the rows they are: by the Medicare provider number, else the HRSA site ID, else an NPI the export lists (through
/// <see cref="CcnOfNpi"/>).
/// </summary>
public sealed class FacilityNetworks(IReadOnlyList<SystemMembership> systems, IReadOnlyList<HealthCenterListing> sites, IReadOnlyDictionary<string, string> ccnOfNpi,
    IReadOnlyList<Program340BListing>? entities340B = null)
{
    public IReadOnlyList<SystemMembership> Systems => systems;

    public IReadOnlyList<HealthCenterListing> Sites => sites;

    public IReadOnlyDictionary<string, string> CcnOfNpi => ccnOfNpi;

    public IReadOnlyList<Program340BListing> Entities340B => entities340B ?? [];

    private sealed record NpiCcn(string Npi, string Ccn);

    private sealed record Entity340BRow(string Id340B, string EntityType, DateTime? StartDate, string? Ccn, string? SiteId, string? Npis);

    /// <summary>The systems, sites, participating 340B entities and the CCN each of their NPIs is enrolled for, from the loaded tables.</summary>
    public static async Task<FacilityNetworks> ReadAsync(MySqlConnection connection, int timeout, CancellationToken ct)
    {
        var entities = (await connection.QueryAsync<Entity340BRow>(new CommandDefinition(
                """
                SELECT e.id_340b AS Id340B, e.entity_type AS EntityType, e.start_date AS StartDate, e.ccn AS Ccn, e.site_id AS SiteId,
                       (SELECT GROUP_CONCAT(n.npi) FROM hrsa_340b_npi n WHERE n.id_340b = e.id_340b) AS Npis
                FROM hrsa_340b_entity e WHERE e.participating = 1
                """, commandTimeout: timeout, cancellationToken: ct)))
            .Select(e => new Program340BListing(e.Id340B, e.EntityType, true, e.StartDate is { } d ? DateOnly.FromDateTime(d) : null, e.Ccn, e.SiteId,
                (e.Npis ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)))
            .ToList();
        var memberships = await connection.QueryAsync<SystemMembership>(new CommandDefinition(
            "SELECT f.ccn AS Ccn, s.system_id AS SystemId, s.name AS SystemName FROM health_system_facility f JOIN health_system s ON s.system_id = f.system_id ORDER BY f.ccn, s.system_id",
            commandTimeout: timeout, cancellationToken: ct));
        var listings = await connection.QueryAsync<HealthCenterRow>(new CommandDefinition(
            """
            SELECT site_id AS SiteId, name AS Name, address AS Address, city AS City, state AS State, zip5 AS Zip5, phone AS Phone, website AS Website,
                   setting AS Setting, location_type AS LocationType, CAST(look_alike AS SIGNED) AS LookAlike, health_center AS HealthCenter, ccn AS Ccn, npi AS Npi,
                   lat AS Lat, lon AS Lon
            FROM hrsa_health_center_site
            """, commandTimeout: timeout, cancellationToken: ct));
        var npis = await connection.QueryAsync<NpiCcn>(new CommandDefinition(
            """
            SELECT n.npi AS Npi, MIN(n.ccn) AS Ccn FROM cms_facility_npi n
            JOIN (SELECT npi FROM hrsa_health_center_site WHERE npi IS NOT NULL UNION SELECT npi FROM hrsa_340b_npi) h ON h.npi = n.npi
            GROUP BY n.npi
            """, commandTimeout: timeout, cancellationToken: ct));
        return new FacilityNetworks([.. memberships],
            [.. listings.Select(r => new HealthCenterListing(r.SiteId, r.Name, r.Address, r.City, r.State, r.Zip5, r.Phone, r.Website, r.Setting, r.LocationType,
                r.LookAlike != 0, r.HealthCenter, r.Ccn, r.Npi, r.Lat, r.Lon))],
            npis.ToDictionary(n => n.Npi, n => n.Ccn, StringComparer.Ordinal), entities);
    }

    private sealed record HealthCenterRow(string SiteId, string Name, string? Address, string? City, string? State, string? Zip5, string? Phone, string? Website,
        string? Setting, string? LocationType, long LookAlike, string? HealthCenter, string? Ccn, string? Npi, double? Lat, double? Lon);

    /// <summary>The facility rows with their system and health center, and a row for each health center site no facility stands for.</summary>
    public IReadOnlyList<FacilitySummary> Apply(IReadOnlyList<FacilitySummary> facilities, IReadOnlyDictionary<string, (double Lat, double Lon)> centroids)
    {
        var rows = facilities.ToList();
        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++)
        {
            position.TryAdd(rows[i].Ccn, i);
        }

        foreach (var membership in systems)
        {
            if (position.TryGetValue(membership.Ccn, out var i) && rows[i].SystemId is null)
            {
                rows[i] = rows[i] with { SystemId = membership.SystemId, SystemName = membership.SystemName };
            }
        }

        // The FQHCs by street address and ZIP, for sites HRSA lists without a number.
        var byAddress = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Type == FacilityTypes.Fqhc && AddressKey(rows[i].Address, rows[i].Zip) is { } key)
            {
                byAddress.TryAdd(key, i);
            }
        }

        // The row each HRSA site ended up in (a facility it matched, or its own), for 340B entities registered by site ID.
        var siteRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites)
        {
            int? target = site.Ccn is not null && position.TryGetValue(site.Ccn, out var byCcn) ? byCcn
                : site.Npi is not null && ccnOfNpi.TryGetValue(site.Npi, out var enrolled) && position.TryGetValue(enrolled, out var byNpi) ? byNpi
                : AddressKey(site.Address, site.Zip5) is { } key && byAddress.TryGetValue(key, out var byStreet) ? byStreet
                : null;
            if (target is { } t)
            {
                rows[t] = rows[t] with { HealthCenter = rows[t].HealthCenter ?? site.HealthCenter, Website = rows[t].Website ?? site.Website };
                siteRow.TryAdd(site.SiteId, t);
                continue;
            }

            (double Lat, double Lon)? point = site.Latitude is { } lat && site.Longitude is { } lon ? (lat, lon)
                : site.Zip5 is not null && centroids.TryGetValue(site.Zip5, out var c) ? c : null;
            siteRow.TryAdd(site.SiteId, rows.Count);
            rows.Add(new FacilitySummary(site.SiteId, FacilityTypes.HealthCenterSite, FacilityTypes.Get(FacilityTypes.HealthCenterSite).Singular, SiteKind(site),
                site.Name.Trim(), site.Address, site.City, site.State, site.Zip5, site.Phone, null, null, null, true, null, null, 0, point?.Lat, point?.Lon)
            {
                Source = FacilitySummary.SourceHrsa, HealthCenter = site.HealthCenter, Website = site.Website,
            });
        }

        // 340B: the row of the Medicare provider number, else of the HRSA site, else of a CCN an NPI it lists is enrolled for; a row
        // that several entities are keeps the earliest start.
        foreach (var entity in Entities340B.Where(e => e.Participating))
        {
            int? target = entity.Ccn is not null && position.TryGetValue(entity.Ccn, out var byCcn) ? byCcn
                : entity.SiteId is not null && siteRow.TryGetValue(entity.SiteId, out var bySite) ? bySite
                : entity.Npis.Select(n => ccnOfNpi.TryGetValue(n, out var enrolled) && position.TryGetValue(enrolled, out var i) ? i : (int?)null)
                    .FirstOrDefault(i => i is not null);
            if (target is not { } t)
            {
                continue;
            }

            var row = rows[t];
            if (row.Program340B is null || (entity.StartDate is { } start && (row.Program340BSince is not { } known || start < known)))
            {
                rows[t] = row with { Program340B = entity.EntityType, Program340BSince = entity.StartDate };
            }
        }

        return rows;
    }

    /// <summary>"Look-alike site, school", "Health center site, mobile van" ….</summary>
    public static string SiteKind(HealthCenterListing site)
    {
        var parts = new List<string> { site.LookAlike ? "Look-alike site" : "Health center site" };
        if (site.Setting is { } setting)
        {
            parts.Add(setting.ToLowerInvariant());
        }

        if (site.LocationType is { } type && !type.Equals("Permanent", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(type.ToLowerInvariant());
        }

        return string.Join(", ", parts);
    }

    private static string? AddressKey(string? address, string? zip) =>
        zip is { Length: 5 } && FacilityDirectory.Normalize(address) is { Length: > 0 } street ? zip + "|" + street : null;
}

/// <summary>Plain words for the Compendium's numbers.</summary>
public static class HealthSystemText
{
    /// <summary>
    /// The credit every page showing the Compendium carries (owner, 2026-10-15): "Agency for Healthcare Research and Quality (AHRQ),
    /// Compendium of U.S. Health Systems, 2023."
    /// </summary>
    public static string Credit(int? year) =>
        "Agency for Healthcare Research and Quality (AHRQ), Compendium of U.S. Health Systems" + (year is { } y ? ", " + y.ToString(CultureInfo.InvariantCulture) : "") + ".";

    /// <summary>Where AHRQ's data come from, said with the credit.</summary>
    public const string Basis = "AHRQ built the Compendium from IQVIA OneKey and the American Hospital Association (AHA) Annual Survey.";

    /// <summary>"Adena Health System, Chillicothe, OH: 4 hospitals, 336 physicians (AHRQ Compendium 2023)."</summary>
    public static string Summary(HealthSystemSummary s)
    {
        var parts = new List<string>();
        if (s.Hospitals is { } h)
        {
            parts.Add(h == 1 ? "1 hospital" : $"{h.ToString("N0", CultureInfo.InvariantCulture)} hospitals");
        }

        if (s.Physicians is { } p)
        {
            parts.Add($"{p.ToString("N0", CultureInfo.InvariantCulture)} physicians");
        }

        if (s.GroupPractices is { } g)
        {
            parts.Add(g == 1 ? "1 group practice" : $"{g.ToString("N0", CultureInfo.InvariantCulture)} group practices");
        }

        var where = s.City is null ? s.State : s.State is null ? CityCatalog.TitleCase(s.City) : $"{CityCatalog.TitleCase(s.City)}, {s.State}";
        return $"Health system{(where is null ? "" : " based in " + where)}" + (parts.Count == 0 ? "" : " with " + string.Join(", ", parts)) +
               $" (AHRQ Compendium {s.DataYear}).";
    }
}
