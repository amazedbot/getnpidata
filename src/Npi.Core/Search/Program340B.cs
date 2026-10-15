using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

// HRSA's 340B Drug Pricing Program covered entities (docs/PRODUCT_PLAN.md step 3.6 part 2), from the OPAIS covered entity export
// the operator downloads (table hrsa_340b_entity, hrsa_340b_npi; loader source hrsa_340b). Linked to facilities by identifiers
// only: the Medicare provider number (a CCN), an NPI the export lists (through CMS's enrollment of that NPI for a CCN), or the
// HRSA site ID of a health center site.

/// <summary>The 340B entity type codes in plain words (HRSA OPAIS; the codes the October 2026 export uses); a code not listed is shown as written.</summary>
public static class Program340B
{
    public const string MatchedByCcn = "ccn";
    public const string MatchedByNpi = "npi";
    public const string MatchedBySite = "site";

    public static readonly IReadOnlyDictionary<string, string> EntityTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["DSH"] = "Disproportionate share hospital",
        ["CAH"] = "Critical access hospital",
        ["SCH"] = "Sole community hospital",
        ["RRC"] = "Rural referral center",
        ["PED"] = "Children's hospital",
        ["CAN"] = "Free-standing cancer hospital",
        ["CH"] = "Community health center (FQHC)",
        ["FQHCLA"] = "FQHC look-alike",
        ["NH"] = "Native Hawaiian health center",
        ["UI"] = "Urban Indian organization",
        ["FQHC638"] = "Tribal health center (FQHC 638)",
        // The Ryan White codes (RWI, RWII, RWIIR, RWIID, RW4 in October 2026) are named without their part, which the codes don't
        // reliably give; HV and SPNS are shown as written.
        ["RWI"] = "Ryan White HIV/AIDS program grantee",
        ["RWII"] = "Ryan White HIV/AIDS program grantee",
        ["RWIIR"] = "Ryan White HIV/AIDS program grantee",
        ["RWIID"] = "Ryan White HIV/AIDS program grantee",
        ["RW4"] = "Ryan White HIV/AIDS program grantee",
        ["BL"] = "Black lung clinic",
        ["HM"] = "Hemophilia treatment center",
        ["STD"] = "STD clinic",
        ["TB"] = "Tuberculosis clinic",
        ["FP"] = "Family planning clinic",
    };

    /// <summary>"Disproportionate share hospital" for DSH; an unknown code as written.</summary>
    public static string TypeName(string? code) =>
        code is null ? "Covered entity" : EntityTypes.TryGetValue(code.Trim(), out var name) ? name : code.Trim().ToUpperInvariant();

    /// <summary>"340B covered entity (disproportionate share hospital, since March 1, 2012)".</summary>
    public static string Describe(string? code, DateOnly? since)
    {
        var name = TypeName(code);
        if (name.Length > 1 && char.IsLower(name[1]))
        {
            name = char.ToLowerInvariant(name[0]) + name[1..]; // "Children's hospital" → "children's hospital"; "FQHC look-alike" stays
        }

        return $"340B covered entity ({name}"
            + (since is { } d ? ", since " + d.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture) : "") + ")";
    }
}

/// <summary>
/// A 340B covered entity as the facility list uses it: its type, whether it participates now and since when, and the identifiers
/// that can link it to a facility (<see cref="Ccn"/>, HRSA <see cref="SiteId"/>, <see cref="Npis"/>).
/// </summary>
public sealed record Program340BListing(string Id340B, string EntityType, bool Participating, DateOnly? StartDate, string? Ccn, string? SiteId,
    IReadOnlyList<string> Npis);

/// <summary>
/// A 340B registration on a facility or health center page. <see cref="MatchedBy"/>: "ccn" (the Medicare provider number the
/// export gives), "npi" (an NPI the export lists, which CMS enrolls for the facility) or "site" (the HRSA site ID).
/// </summary>
public sealed record Covered340BEntity(string Id340B, string EntityType, string EntityTypeName, bool Participating, DateOnly? StartDate,
    DateOnly? TerminationDate, string Name, string? SubName, string MatchedBy);

/// <summary>Reads 340B registrations for the facility and health center pages.</summary>
public static class Program340BService
{
    /// <summary>The 340B registrations of a certified facility: by its CCN, an NPI enrolled for it, or an HRSA site that is it.</summary>
    internal static async Task<IReadOnlyList<Covered340BEntity>> ForCcnAsync(MySqlConnection connection, string ccn, CancellationToken ct) =>
        Distinct(await connection.QueryAsync<EntityRow>(new CommandDefinition(
            $"""
            {Select}, 'ccn' AS MatchedBy FROM hrsa_340b_entity e WHERE e.ccn = @ccn
            UNION ALL
            {Select}, 'npi' FROM hrsa_340b_entity e JOIN hrsa_340b_npi n ON n.id_340b = e.id_340b
              JOIN cms_facility_npi f ON f.npi = n.npi WHERE f.ccn = @ccn
            UNION ALL
            {Select}, 'site' FROM hrsa_340b_entity e JOIN hrsa_health_center_site s ON s.site_id = e.site_id
              WHERE s.ccn = @ccn OR s.npi IN (SELECT npi FROM cms_facility_npi WHERE ccn = @ccn)
            """, new { ccn }, cancellationToken: ct)));

    /// <summary>The 340B registrations of an HRSA health center site: by its site ID or its NPI.</summary>
    internal static async Task<IReadOnlyList<Covered340BEntity>> ForSiteAsync(MySqlConnection connection, string siteId, string? npi, CancellationToken ct) =>
        Distinct(await connection.QueryAsync<EntityRow>(new CommandDefinition(
            $"""
            {Select}, 'site' AS MatchedBy FROM hrsa_340b_entity e WHERE e.site_id = @siteId
            UNION ALL
            {Select}, 'npi' FROM hrsa_340b_entity e JOIN hrsa_340b_npi n ON n.id_340b = e.id_340b WHERE n.npi = @npi
            """, new { siteId, npi = npi ?? "" }, cancellationToken: ct)));

    private const string Select =
        """
        SELECT e.id_340b AS Id340B, e.entity_type AS EntityType, CAST(e.participating AS SIGNED) AS Participating, e.start_date AS StartDate,
               e.termination_date AS TerminationDate, e.name AS Name, e.sub_name AS SubName
        """;

    // The participating first, then the most recent start; one row per 340B ID (the first way it was found).
    private static List<Covered340BEntity> Distinct(IEnumerable<EntityRow> rows) =>
        rows.GroupBy(r => r.Id340B, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(r => new Covered340BEntity(r.Id340B, r.EntityType, Program340B.TypeName(r.EntityType), r.Participating != 0,
                r.StartDate is { } s ? DateOnly.FromDateTime(s) : null, r.TerminationDate is { } t ? DateOnly.FromDateTime(t) : null, r.Name, r.SubName,
                r.MatchedBy))
            .OrderByDescending(e => e.Participating).ThenByDescending(e => e.StartDate).ThenBy(e => e.Id340B, StringComparer.Ordinal)
            .ToList();

    // Types as MySqlConnector returns them (Dapper maps records by constructor).
    private sealed record EntityRow(string Id340B, string EntityType, long Participating, DateTime? StartDate, DateTime? TerminationDate, string Name,
        string? SubName, string MatchedBy);
}
