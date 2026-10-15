using Dapper;
using MySqlConnector;
using Npi.Core.Search;

namespace Npi.Loader.Projection;

/// <summary>
/// An active organization NPI with what the matcher needs to know about it: its legal name, whether it is a hospital (a NUCC 27
/// hospital unit or 28 hospital taxonomy, or CMS's enrollment of the NPI as a hospital), the CCNs CMS enrolls it for, and the parent
/// organization its NPPES record names (legal business name and tax ID; NPPES fills them for subparts).
/// </summary>
public sealed record AcoOrganization(string Npi, string OrgName)
{
    public bool Hospital { get; init; }

    public IReadOnlyList<string> Ccns { get; init; } = [];

    public string? ParentName { get; init; }

    public string? ParentTin { get; init; }
}

/// <summary>
/// A participant linked to an organization NPI by name: the ACO, the participant's name as CMS lists it, the NPI, and how many share
/// it; <see cref="HospitalGroup"/> when every one of them is a hospital, with the reason they belong together (<see cref="AcoNames"/>).
/// </summary>
public sealed record AcoParticipantLink(string AcoId, string Name, string Npi, int Candidates)
{
    public bool HospitalGroup { get; init; }

    public string? GroupedBy { get; init; }

    public string? GroupedValue { get; init; }
}

/// <summary>
/// How the participants matched: exactly one organization, 2 to <see cref="AcoNames.MaxCandidates"/>, a hospital group of more (up
/// to <see cref="AcoNames.MaxHospitalCandidates"/>, all linked), more (not linked), none.
/// </summary>
public sealed record AcoMatchCounts(int Participants, int One, int Several, int HospitalGroups, int TooMany, int None);

/// <summary>
/// Links ACO participants (CMS: legal business names only) to active organization NPIs whose NPPES legal business name is the
/// same name, case, spacing and punctuation aside (<see cref="AcoNames.Key"/>; owner, 2026-10-15). A name shared by up to
/// <see cref="AcoNames.MaxCandidates"/> organizations links each as a candidate; more, and the participant isn't linked (a common
/// name such as "FAMILY PRACTICE ASSOCIATES PC" says nothing about which one takes part), unless every one of them is a hospital:
/// then up to <see cref="AcoNames.MaxHospitalCandidates"/> are linked (a hospital and its subpart NPIs; owner, 2026-10-15), and
/// the reason they belong together is recorded when there is one: a CCN they all share, or the one parent organization their
/// subparts name (legal business name, else tax ID).
/// </summary>
public static class AcoParticipantMatcher
{
    public static (IReadOnlyList<AcoParticipantLink> Links, AcoMatchCounts Counts) Match(IEnumerable<(string AcoId, string Name)> participants,
        IEnumerable<(string Npi, string OrgName)> organizations) =>
        Match(participants, organizations.Select(o => new AcoOrganization(o.Npi, o.OrgName)));

    public static (IReadOnlyList<AcoParticipantLink> Links, AcoMatchCounts Counts) Match(IEnumerable<(string AcoId, string Name)> participants,
        IEnumerable<AcoOrganization> organizations)
    {
        var byKey = new Dictionary<string, List<(string AcoId, string Name)>>(StringComparer.Ordinal);
        var count = 0;
        var unkeyed = 0;
        foreach (var p in participants)
        {
            count++;
            if (AcoNames.Key(p.Name) is not { } key)
            {
                unkeyed++;
                continue;
            }

            if (!byKey.TryGetValue(key, out var list))
            {
                byKey[key] = list = [];
            }

            list.Add(p);
        }

        var named = new Dictionary<string, Dictionary<string, AcoOrganization>>(StringComparer.Ordinal);
        foreach (var organization in organizations)
        {
            if (AcoNames.Key(organization.OrgName) is { } key && byKey.ContainsKey(key))
            {
                if (!named.TryGetValue(key, out var set))
                {
                    named[key] = set = new Dictionary<string, AcoOrganization>(StringComparer.Ordinal);
                }

                set.TryAdd(organization.Npi, organization); // the same NPI twice counts once
            }
        }

        var links = new List<AcoParticipantLink>();
        int one = 0, several = 0, hospitals = 0, tooMany = 0, none = unkeyed;
        foreach (var (key, members) in byKey)
        {
            var found = named.TryGetValue(key, out var set) ? set.Values.OrderBy(o => o.Npi, StringComparer.Ordinal).ToList() : [];
            var allHospitals = found.Count > 1 && found.All(o => o.Hospital);
            var linked = found.Count is > 0 and <= AcoNames.MaxCandidates || (allHospitals && found.Count <= AcoNames.MaxHospitalCandidates);
            var (groupedBy, groupedValue) = allHospitals && linked ? Grouping(found) : (null, null);
            foreach (var (acoId, name) in members)
            {
                if (found.Count == 0)
                {
                    none++;
                    continue;
                }

                if (!linked)
                {
                    tooMany++;
                    continue;
                }

                if (found.Count == 1)
                {
                    one++;
                }
                else if (found.Count <= AcoNames.MaxCandidates)
                {
                    several++;
                }
                else
                {
                    hospitals++;
                }

                links.AddRange(found.Select(o => new AcoParticipantLink(acoId, name, o.Npi, found.Count)
                {
                    HospitalGroup = allHospitals, GroupedBy = groupedBy, GroupedValue = groupedValue,
                }));
            }
        }

        return (links, new AcoMatchCounts(count, one, several, hospitals, tooMany, none));
    }

    /// <summary>
    /// Why hospital NPIs of one name belong together: a CCN every one of them is enrolled for; else one parent organization named
    /// by all but at most one of them (the parent itself names none), by legal business name, else by tax ID; else none.
    /// </summary>
    public static (string? GroupedBy, string? GroupedValue) Grouping(IReadOnlyList<AcoOrganization> organizations)
    {
        if (organizations.All(o => o.Ccns.Count > 0))
        {
            var shared = organizations.Skip(1).Aggregate(new SortedSet<string>(organizations[0].Ccns, StringComparer.Ordinal),
                (common, o) => { common.IntersectWith(o.Ccns); return common; });
            if (shared.Count > 0)
            {
                return (AcoNames.GroupedByCcn, shared.Min);
            }
        }

        if (SameParent(organizations, o => AcoNames.Key(o.ParentName)) is { } parent)
        {
            return (AcoNames.GroupedByParent, organizations.First(o => AcoNames.Key(o.ParentName) == parent).ParentName!.Trim());
        }

        return SameParent(organizations, o => Digits(o.ParentTin)) is not null ? (AcoNames.GroupedByParentTin, null) : (null, null);
    }

    private static string? SameParent(IReadOnlyList<AcoOrganization> organizations, Func<AcoOrganization, string?> parentOf)
    {
        var parents = organizations.Select(parentOf).ToList();
        var given = parents.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        return given.Count == 1 && parents.Count(p => p is null) <= 1 ? given[0] : null;
    }

    private static string? Digits(string? tin) => tin is null ? null : new string([.. tin.Where(char.IsAsciiDigit)]) is { Length: > 0 } d ? d : null;

    /// <summary>
    /// Reads the participants and every active organization's legal name (streamed, ~1.9M rows), then, for the names shared by 2 to
    /// <see cref="AcoNames.MaxHospitalCandidates"/> organizations, whether each is a hospital, its CCNs and its parent, and matches.
    /// </summary>
    public static async Task<(IReadOnlyList<AcoParticipantLink> Links, AcoMatchCounts Counts)> MatchAsync(MySqlConnection connection, CancellationToken ct)
    {
        var participants = new List<(string, string)>();
        await using (var command = new MySqlCommand("SELECT `aco_id`, `name` FROM `aco_participant`", connection) { CommandTimeout = 600 })
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                participants.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        if (participants.Count == 0)
        {
            return ([], new AcoMatchCounts(0, 0, 0, 0, 0, 0));
        }

        var organizations = new List<(string Npi, string OrgName, string Key)>();
        var keys = participants.Select(p => AcoNames.Key(p.Item2)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        await using (var command = new MySqlCommand(
                         "SELECT `npi`, `org_name` FROM `provider` WHERE `entity_type` = 2 AND `org_name` IS NOT NULL", connection) { CommandTimeout = 600 })
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                // Only the organizations with a participant's name are kept, so the 1.9M names aren't held at once.
                var name = reader.GetString(1);
                if (AcoNames.Key(name) is { } key && keys.Contains(key))
                {
                    organizations.Add((reader.GetString(0), name, key));
                }
            }
        }

        // Facts only for the NPIs of names shared by 2 to 50 organizations: a one-NPI name needs none, and more is never linked.
        var shared = organizations.GroupBy(o => o.Key, StringComparer.Ordinal)
            .Where(g => g.Select(o => o.Npi).Distinct(StringComparer.Ordinal).Count() is > 1 and <= AcoNames.MaxHospitalCandidates)
            .SelectMany(g => g.Select(o => o.Npi)).Distinct(StringComparer.Ordinal).ToList();
        var hospital = new HashSet<string>(StringComparer.Ordinal);
        var ccns = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var parentNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var parentTins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var batch in shared.Chunk(1000))
        {
            var npis = new { npis = batch };
            hospital.UnionWith(await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT DISTINCT `npi` FROM `provider_taxonomy` WHERE `npi` IN @npis AND (`taxonomy_code` LIKE '27%' OR `taxonomy_code` LIKE '28%')",
                npis, commandTimeout: 600, cancellationToken: ct)));
            foreach (var (npi, ccn, kind) in await connection.QueryAsync<(string, string, string)>(new CommandDefinition(
                         "SELECT `npi`, `ccn`, `kind` FROM `cms_facility_npi` WHERE `npi` IN @npis", npis, commandTimeout: 600, cancellationToken: ct)))
            {
                if (kind == "hospital")
                {
                    hospital.Add(npi);
                }

                if (!ccns.TryGetValue(npi, out var list))
                {
                    ccns[npi] = list = [];
                }

                list.Add(ccn);
            }

            foreach (var (npi, parent) in await connection.QueryAsync<(string, string)>(new CommandDefinition(
                         "SELECT `npi`, `parent_org_name` FROM `provider_profile` WHERE `npi` IN @npis AND `parent_org_name` IS NOT NULL", npis,
                         commandTimeout: 600, cancellationToken: ct)))
            {
                parentNames[npi] = parent;
            }

            foreach (var (npi, tin) in await connection.QueryAsync<(string, string)>(new CommandDefinition(
                         "SELECT `NPI`, `Parent_Organization_TIN` FROM `npidata` WHERE `NPI` IN @npis AND `Parent_Organization_TIN` IS NOT NULL", npis,
                         commandTimeout: 600, cancellationToken: ct)))
            {
                parentTins[npi] = tin;
            }
        }

        return Match(participants, organizations.Select(o => new AcoOrganization(o.Npi, o.OrgName)
        {
            Hospital = hospital.Contains(o.Npi),
            Ccns = ccns.TryGetValue(o.Npi, out var list) ? list : [],
            ParentName = parentNames.GetValueOrDefault(o.Npi),
            ParentTin = parentTins.GetValueOrDefault(o.Npi),
        }));
    }
}
