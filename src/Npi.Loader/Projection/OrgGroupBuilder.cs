using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Npi.Core.Search;
using Npi.Loader.Datasets;
using Npi.Loader.Db;
using Npi.Loader.Geocoding;
using Npi.Loader.Reference;
using Serilog;

namespace Npi.Loader.Projection;

/// <summary>
/// An active organization NPI as the grouping sees it (<see cref="OrgGrouping"/>): its legal name and primary practice address
/// normalized (<see cref="OrgGrouping.NameKey"/>, <see cref="OrgGrouping.Address"/>), primary taxonomy and last NPPES update, the
/// CCNs CMS enrolls it for, the PAC IDs it is enrolled under in Medicare, its NPPES parent organization (subparts) and whether it
/// billed Medicare Part B.
/// </summary>
public sealed record OrgGroupInput(string Npi, string? NameKey, string? Zip5, string? Number, string? Street, string? Taxonomy, DateTime? LastUpdate)
{
    public IReadOnlyList<(string Ccn, string Kind)> Ccns { get; init; } = [];

    public IReadOnlyList<string> PacIds { get; init; } = [];

    /// <summary>Enrolled in Medicare (with or without a PAC ID).</summary>
    public bool MedicareEnrolled { get; init; }

    public bool MedicareBilling { get; init; }

    /// <summary>
    /// The record quality found a sign of current practice at its last build (<c>provider_quality.activity</c>; the signs don't
    /// depend on the groups), for choosing the main NPI.
    /// </summary>
    public bool Active { get; init; }

    /// <summary>The parent organization's legal business name, normalized (<see cref="OrgGrouping.NameKey"/>); NPPES fills it for subparts.</summary>
    public string? ParentKey { get; init; }

    /// <summary>The parent organization's tax ID, digits only.</summary>
    public string? ParentTin { get; init; }

    /// <summary>From the raw values: the name and street address normalized.</summary>
    public static OrgGroupInput Create(string npi, string? orgName, string? zip5, string? address1, string? taxonomy, DateTime? lastUpdate)
    {
        var (number, street) = OrgGrouping.Address(address1);
        return new OrgGroupInput(npi, OrgGrouping.NameKey(orgName), zip5 is { Length: 5 } ? zip5 : null, number, street, taxonomy, lastUpdate);
    }
}

/// <summary>One NPI of a group of two or more: the group's main NPI, its size, the member's CCN and the ties that joined it.</summary>
public sealed record OrgGroupMember(string Npi, string MainNpi, int Members, bool IsMain, string? Ccn, string? FacilityKind, OrganizationTie JoinedBy);

/// <summary>Groups and their NPIs after each kind of tie was added, for the log (the measurement the owner asked for).</summary>
public sealed record OrgGroupStage(string Tie, int Groups, int Npis);

/// <summary>What the grouping found: the stages, the size distribution and the largest groups (main NPI, size).</summary>
public sealed record OrgGroupStats(IReadOnlyList<OrgGroupStage> Stages, IReadOnlyDictionary<string, int> Sizes, IReadOnlyList<(string MainNpi, int Size)> Largest,
    int LargeBuckets);

/// <summary>
/// Groups active organization NPIs into organizations (docs/PRODUCT_PLAN.md step 3.8; owner's rules of 2026-10-15) by union-find over
/// four kinds of tie (<see cref="OrganizationTie"/>):
/// <list type="bullet">
/// <item>the same legal business name (case, spacing and punctuation aside) at the same street address: house number, street
/// (USPS abbreviations, "EXPWY" = "EXPRESSWAY" = "EXPY") and ZIP code; an address without a house number joins the numbered ones of
/// the same street when they all have one number;</item>
/// <item>a shared CCN (<c>cms_facility_npi</c>, <c>cms_dialysis_npi</c>, <c>cms_asc</c>; a hospital unit's CCN counts as its
/// hospital's, <see cref="OrganizationGroups.FacilityKey"/>), in one ZIP code;</item>
/// <item>the same PAC ID (<c>medicare_enrollment</c>), in one ZIP code;</item>
/// <item>the same parent organization: a subpart and the organization of its parent's legal name, or two subparts naming the same
/// parent (legal name or tax ID), in one ZIP code.</item>
/// </list>
/// The ZIP code keeps a chain's national enrollment or parent from joining its stores across the country. A PAC ID or parent tie
/// between two NPIs with the same primary taxonomy at different addresses doesn't count either: those are a chain's stores or a
/// group's offices in one ZIP code, not one organization's NPIs. Similar names alone, or the same name in the same city, never join.
/// </summary>
public static partial class OrgGrouping
{
    /// <summary>A bucket of NPIs compared pair by pair larger than this is counted in the stats (it still is compared).</summary>
    public const int LargeBucket = 500;

    /// <summary>The legal name, letters and digits upper-cased (<see cref="AcoNames.Key"/>); null for none.</summary>
    public static string? NameKey(string? name) => AcoNames.Key(name);

    /// <summary>
    /// The street address as (house number, street): "8900 Van Wyck Expwy" and "8900 VAN WYCK EXPRESS WAY" → ("8900", "VAN WYCK EXPY");
    /// "89-00" = "8900". No house number: ("", the street normalized). Null parts for an empty address.
    /// </summary>
    public static (string? Number, string? Street) Address(string? address1)
    {
        if (string.IsNullOrWhiteSpace(address1))
        {
            return (null, null);
        }

        var text = Suites().Replace(Expressway().Replace(address1.ToUpperInvariant(), "EXPY"), "STE");
        if (AddressNormalizer.Split(text) is var (number, street))
        {
            return (number.Replace("-", "", StringComparison.Ordinal), street);
        }

        var whole = AddressNormalizer.Street(text);
        return whole.Length == 0 ? (null, null) : ("", whole);
    }

    [GeneratedRegex(@"\b(EXPRESS\s+WAY|EXPWY|EXPW|EXPR)\b")]
    private static partial Regex Expressway();

    // "STES 102, 201" and "SUITES 1-3": the suite is dropped like "STE 102" (AddressNormalizer).
    [GeneratedRegex(@"\b(STES|SUITES)\b")]
    private static partial Regex Suites();

    /// <summary>The same street address: house number, street and ZIP code (an address without a number is the same only to itself).</summary>
    public static bool SameAddress(OrgGroupInput a, OrgGroupInput b) =>
        a.Street is not null && a.Zip5 == b.Zip5 && a.Number == b.Number && a.Street == b.Street;

    // A PAC ID or parent tie: not between two NPIs of the same primary taxonomy at different addresses (a chain's stores).
    private static bool Ties(OrgGroupInput a, OrgGroupInput b) => SameAddress(a, b) || a.Taxonomy != b.Taxonomy;

    public static (IReadOnlyList<OrgGroupMember> Members, OrgGroupStats Stats) Build(IReadOnlyList<OrgGroupInput> orgs)
    {
        var parent = Enumerable.Range(0, orgs.Count).ToArray();
        var size = Enumerable.Repeat(1, orgs.Count).ToArray();
        var ties = new OrganizationTie[orgs.Count];
        var stages = new List<OrgGroupStage>();
        var largeBuckets = 0;

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        void Union(int a, int b, OrganizationTie tie)
        {
            ties[a] |= tie;
            ties[b] |= tie;
            var (ra, rb) = (Find(a), Find(b));
            if (ra == rb)
            {
                return;
            }

            if (size[ra] < size[rb])
            {
                (ra, rb) = (rb, ra);
            }

            parent[rb] = ra;
            size[ra] += size[rb];
        }

        void UnionAll(List<int> bucket, OrganizationTie tie)
        {
            for (var k = 1; k < bucket.Count; k++)
            {
                Union(bucket[0], bucket[k], tie);
            }
        }

        void Pairwise(List<int> bucket, OrganizationTie tie)
        {
            if (bucket.Count > LargeBucket)
            {
                largeBuckets++;
            }

            for (var x = 0; x < bucket.Count; x++)
            {
                for (var y = x + 1; y < bucket.Count; y++)
                {
                    if (Ties(orgs[bucket[x]], orgs[bucket[y]]))
                    {
                        Union(bucket[x], bucket[y], tie);
                    }
                }
            }
        }

        void Stage(string name)
        {
            var roots = new Dictionary<int, int>();
            for (var i = 0; i < orgs.Count; i++)
            {
                var r = Find(i);
                roots[r] = roots.GetValueOrDefault(r) + 1;
            }

            var grouped = roots.Values.Where(v => v > 1).ToList();
            stages.Add(new OrgGroupStage(name, grouped.Count, grouped.Sum()));
        }

        static Dictionary<string, List<int>> Buckets(IEnumerable<(string Key, int Index)> keyed)
        {
            var buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var (key, index) in keyed)
            {
                if (!buckets.TryGetValue(key, out var list))
                {
                    buckets[key] = list = [];
                }

                if (list.Count == 0 || list[^1] != index)
                {
                    list.Add(index);
                }
            }

            return buckets;
        }

        IEnumerable<int> Located() => Enumerable.Range(0, orgs.Count).Where(i => orgs[i].Zip5 is not null);

        // 1. The same name at the same street address.
        var named = Located().Where(i => orgs[i].NameKey is not null && orgs[i].Street is not null).ToList();
        foreach (var bucket in Buckets(named.Select(i => ($"{orgs[i].NameKey}|{orgs[i].Zip5}|{orgs[i].Number}|{orgs[i].Street}", i))).Values)
        {
            UnionAll(bucket, OrganizationTie.NameAddress);
        }

        // An address without a house number ("NICOLLS ROAD") joins the numbered ones of its street when they have one number.
        foreach (var bucket in Buckets(named.Select(i => ($"{orgs[i].NameKey}|{orgs[i].Zip5}|{orgs[i].Street}", i))).Values)
        {
            var numbers = bucket.Select(i => orgs[i].Number).Where(n => n is { Length: > 0 }).Distinct(StringComparer.Ordinal).Count();
            if (numbers == 1 && bucket.Any(i => orgs[i].Number == ""))
            {
                UnionAll(bucket, OrganizationTie.NameAddress);
            }
        }

        Stage("same name and address");

        // 2. A shared certified facility (a unit's CCN counts as its hospital's), in one ZIP code.
        foreach (var bucket in Buckets(Located().SelectMany(i => orgs[i].Ccns.Select(c => OrganizationGroups.FacilityKey(c.Ccn)).Distinct(StringComparer.Ordinal)
                     .Select(key => ($"{orgs[i].Zip5}|{key}", i)))).Values)
        {
            UnionAll(bucket, OrganizationTie.Ccn);
        }

        Stage("+ shared CCN");

        // 3. The same Medicare enrollment (PAC ID), in one ZIP code.
        foreach (var bucket in Buckets(Located().SelectMany(i => orgs[i].PacIds.Distinct(StringComparer.Ordinal).Select(pac => ($"{orgs[i].Zip5}|{pac}", i)))).Values)
        {
            Pairwise(bucket, OrganizationTie.MedicareEnrollment);
        }

        Stage("+ same PAC ID");

        // 4. The same parent organization, in one ZIP code: subparts naming a parent, with the organization of that name; subparts
        // naming the same parent tax ID.
        var parentKeys = Located().Where(i => orgs[i].ParentKey is not null).Select(i => $"{orgs[i].Zip5}|{orgs[i].ParentKey}").ToHashSet(StringComparer.Ordinal);
        var byParent = Buckets(Located().SelectMany(i =>
        {
            var keys = new List<(string, int)>(2);
            if (orgs[i].ParentKey is { } pk)
            {
                keys.Add(($"{orgs[i].Zip5}|{pk}", i));
            }

            if (orgs[i].NameKey is { } nk && parentKeys.Contains($"{orgs[i].Zip5}|{nk}") && nk != orgs[i].ParentKey)
            {
                keys.Add(($"{orgs[i].Zip5}|{nk}", i));
            }

            return keys;
        }));
        foreach (var bucket in byParent.Values)
        {
            Pairwise(bucket.Distinct().ToList(), OrganizationTie.Parent);
        }

        foreach (var bucket in Buckets(Located().Where(i => orgs[i].ParentTin is not null).Select(i => ($"{orgs[i].Zip5}|{orgs[i].ParentTin}", i))).Values)
        {
            Pairwise(bucket, OrganizationTie.Parent);
        }

        Stage("+ same parent organization");

        // The groups of two or more, each with its main NPI.
        var groups = Enumerable.Range(0, orgs.Count).GroupBy(Find).Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();
        var members = new List<OrgGroupMember>(groups.Sum(g => g.Count));
        foreach (var group in groups)
        {
            var main = group.OrderByDescending(i => MainRank(orgs[i])).ThenByDescending(i => orgs[i].LastUpdate ?? DateTime.MinValue)
                .ThenBy(i => orgs[i].Npi, StringComparer.Ordinal).First();
            foreach (var i in group)
            {
                var ccn = BestCcn(orgs[i]);
                members.Add(new OrgGroupMember(orgs[i].Npi, orgs[main].Npi, group.Count, i == main, ccn?.Ccn, ccn?.Kind, ties[i]));
            }
        }

        var sizes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var band = group.Count switch
            {
                2 => "2",
                <= 5 => "3-5",
                <= 10 => "6-10",
                <= 25 => "11-25",
                <= 50 => "26-50",
                <= OrganizationGroups.LargeGroup => "51-100",
                _ => "over 100",
            };
            sizes[band] = sizes.GetValueOrDefault(band) + 1;
        }

        var largest = members.Where(m => m.IsMain).OrderByDescending(m => m.Members).ThenBy(m => m.MainNpi, StringComparer.Ordinal).Take(10)
            .Select(m => (m.MainNpi, m.Members)).ToList();
        return (members, new OrgGroupStats(stages, sizes, largest, largeBuckets));
    }

    /// <summary>
    /// Main NPI first: a hospital's own CCN (not a unit's); else a hospital's NUCC taxonomy with a sign of practice (2026-10-16: a
    /// hospital whose CCN CMS lists under another NPI, outside the group, had its dialysis center as main NPI); then, within each,
    /// any CCN, a Medicare enrollment or Part B billing, another sign of current practice (so the main NPI isn't one the "another
    /// NPI is current" reason makes stale), nothing.
    /// </summary>
    internal static int MainRank(OrgGroupInput o)
    {
        var basis = o.Ccns.Count > 0 ? 3
            : o.MedicareEnrolled || o.PacIds.Count > 0 || o.MedicareBilling ? 2
            : o.Active ? 1
            : 0;
        var tier = o.Ccns.Any(c => c.Kind == FacilityTypes.Hospital && !OrganizationGroups.IsUnitCcn(c.Ccn)) ? 2
            : basis > 0 && OrganizationGroups.IsHospitalTaxonomy(o.Taxonomy) ? 1
            : 0;
        return tier * 4 + basis;
    }

    // The CCN shown for a member: a hospital's own first, then any facility's own, then a unit's.
    private static (string Ccn, string Kind)? BestCcn(OrgGroupInput o) =>
        o.Ccns.Count == 0 ? null
            : o.Ccns.OrderBy(c => OrganizationGroups.IsUnitCcn(c.Ccn) ? 2 : c.Kind == FacilityTypes.Hospital ? 0 : 1).ThenBy(c => c.Ccn, StringComparer.Ordinal).First();
}

/// <summary>
/// Builds <c>org_group_member</c> (docs/PRODUCT_PLAN.md step 3.8; migration 117): every active organization NPI that belongs with
/// others (<see cref="OrgGrouping"/>), with its group's main NPI. Rebuilt in full (staging + RENAME) after the projection when the
/// projection or a CCN or enrollment table changed since its last build (~1.98M organization NPIs read, grouped in memory); the
/// NPIs whose group changed are queued for the record quality (<see cref="QualityBuilder.Pending"/>), whose "another NPI of this
/// organization is current" reason reads the groups.
/// </summary>
public sealed class OrgGroupBuilder(Database database, ILogger log, double minRowRatio)
{
    public const string Source = "org_groups";

    public const string Table = "org_group_member";

    // The datasets the grouping reads: CCNs, Medicare enrollment, Part B billing (the main NPI).
    private static readonly string[] Inputs = ["cms_facility_enrollments", "cms_dialysis_npis", "cc_surgery_centers", "medicare_enrollment", "cms_physician_by_provider"];

    /// <summary>True when the groups were never built, or the projection or one of the datasets they read changed since.</summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT NOT EXISTS (SELECT 1 FROM `reference_data` WHERE `source` = @source)
                OR (SELECT `loaded_at` FROM `reference_data` WHERE `source` = @source)
                   < GREATEST(COALESCE((SELECT `projected_at` FROM `data_version` WHERE `id` = 1), '1970-01-01'),
                              COALESCE((SELECT MAX(`loaded_at`) FROM `reference_data` WHERE `source` IN @inputs), '1970-01-01'))
            """, new { source = Source, inputs = Inputs }, cancellationToken: ct)) != 0;
    }

    /// <summary>Reads every active organization NPI with what the grouping needs (streamed; the facts by NPI first).</summary>
    public static async Task<IReadOnlyList<OrgGroupInput>> ReadAsync(MySqlConnection connection, CancellationToken ct)
    {
        const int timeout = 1800;
        var ccns = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
        foreach (var (npi, ccn, kind) in await connection.QueryAsync<(string, string, string)>(new CommandDefinition(
                     $"SELECT DISTINCT npi, ccn, kind FROM ({FacilityDirectory.BillingNpiSql}) b WHERE npi <> ''", commandTimeout: timeout, cancellationToken: ct)))
        {
            if (!ccns.TryGetValue(npi, out var list))
            {
                ccns[npi] = list = [];
            }

            list.Add((ccn.Trim().ToUpperInvariant(), kind));
        }

        // Organizations' enrollments carry an organization name; individuals' (most of the 3M rows) don't.
        var pacs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var enrolled = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var (npi, pac) in connection.QueryUnbufferedAsync<(string, string?)>(
                           "SELECT npi, pac_id FROM medicare_enrollment WHERE org_name IS NOT NULL").WithCancellation(ct))
        {
            enrolled.Add(npi);
            if (pac is { Length: > 0 })
            {
                if (!pacs.TryGetValue(npi, out var list))
                {
                    pacs[npi] = list = [];
                }

                if (!list.Contains(pac))
                {
                    list.Add(pac);
                }
            }
        }

        var billing = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT u.npi FROM medicare_utilization u JOIN provider p ON p.npi = u.npi WHERE p.entity_type = 2", commandTimeout: timeout, cancellationToken: ct)))
            .ToHashSet(StringComparer.Ordinal);

        var active = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT q.npi FROM provider_quality q JOIN provider p ON p.npi = q.npi WHERE p.entity_type = 2 AND q.activity <> 0",
            commandTimeout: timeout, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);

        var parents = (await connection.QueryAsync<(string Npi, string Name)>(new CommandDefinition(
                """
                SELECT pp.npi, pp.parent_org_name FROM provider_profile pp JOIN provider p ON p.npi = pp.npi
                WHERE p.entity_type = 2 AND pp.parent_org_name IS NOT NULL
                """, commandTimeout: timeout, cancellationToken: ct)))
            .ToDictionary(r => r.Npi, r => OrgGrouping.NameKey(r.Name), StringComparer.Ordinal);
        var tins = new Dictionary<string, string>(StringComparer.Ordinal);
        var subparts = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT pp.npi FROM provider_profile pp JOIN provider p ON p.npi = pp.npi WHERE p.entity_type = 2 AND pp.is_subpart = 1",
            commandTimeout: timeout, cancellationToken: ct))).ToList();
        foreach (var batch in subparts.Chunk(1000))
        {
            foreach (var (npi, tin) in await connection.QueryAsync<(string, string)>(new CommandDefinition(
                         "SELECT `NPI`, `Parent_Organization_TIN` FROM `npidata` WHERE `NPI` IN @npis AND `Parent_Organization_TIN` IS NOT NULL",
                         new { npis = batch }, commandTimeout: timeout, cancellationToken: ct)))
            {
                if (new string([.. tin.Where(char.IsAsciiDigit)]) is { Length: > 0 } digits)
                {
                    tins[npi] = digits;
                }
            }
        }

        var taxonomies = new Dictionary<string, string>(StringComparer.Ordinal); // the same few hundred codes, kept once
        var orgs = new List<OrgGroupInput>(2_100_000);
        await foreach (var r in connection.QueryUnbufferedAsync<(string Npi, string? OrgName, string? Taxonomy, DateTime? LastUpdate, string? Address1, string? Zip5)>(
                           """
                           SELECT p.npi, p.org_name, p.primary_taxonomy_code, p.last_update_date, l.address1, l.zip5
                           FROM provider p LEFT JOIN provider_location l ON l.npi = p.npi AND l.is_primary = 1
                           WHERE p.entity_type = 2
                           """).WithCancellation(ct))
        {
            string? taxonomy = null;
            if (r.Taxonomy is { } t && !taxonomies.TryGetValue(t, out taxonomy))
            {
                taxonomies[t] = taxonomy = t;
            }

            orgs.Add(OrgGroupInput.Create(r.Npi, r.OrgName, r.Zip5, r.Address1, taxonomy, r.LastUpdate) with
            {
                Ccns = ccns.TryGetValue(r.Npi, out var c) ? c : [],
                PacIds = pacs.TryGetValue(r.Npi, out var pac) ? pac : [],
                MedicareEnrolled = enrolled.Contains(r.Npi),
                MedicareBilling = billing.Contains(r.Npi),
                Active = active.Contains(r.Npi),
                ParentKey = parents.GetValueOrDefault(r.Npi),
                ParentTin = tins.GetValueOrDefault(r.Npi),
            });
        }

        return orgs;
    }

    /// <summary>Groups the organizations (read only) and logs what each kind of tie adds; with <paramref name="csvPath"/>, every member to that file.</summary>
    public async Task<OrgGroupStats> MeasureAsync(string? csvPath, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        await using var connection = await database.OpenAsync(ct);
        var orgs = await ReadAsync(connection, ct);
        log.Information("Organization groups: {Organizations:N0} organization NPIs read in {Seconds:N1}s", orgs.Count, watch.Elapsed.TotalSeconds);
        watch.Restart();
        var (members, stats) = OrgGrouping.Build(orgs);
        Report(stats, members.Count, watch.Elapsed.TotalSeconds);
        if (csvPath is not null)
        {
            var byNpi = orgs.ToDictionary(o => o.Npi, StringComparer.Ordinal);
            await using var writer = new StreamWriter(csvPath, false, new UTF8Encoding(false));
            await writer.WriteLineAsync("npi,main_npi,members,is_main,ccn,facility_kind,joined_by,name_key,zip5,number,street,taxonomy,last_update,ccns,pac_ids,parent_key,parent_tin");
            foreach (var m in members.OrderBy(m => m.MainNpi, StringComparer.Ordinal).ThenByDescending(m => m.IsMain).ThenBy(m => m.Npi, StringComparer.Ordinal))
            {
                var o = byNpi[m.Npi];
                string[] fields =
                [
                    m.Npi, m.MainNpi, m.Members.ToString(CultureInfo.InvariantCulture), m.IsMain ? "1" : "0", m.Ccn ?? "", m.FacilityKind ?? "",
                    ((int)m.JoinedBy).ToString(CultureInfo.InvariantCulture), o.NameKey ?? "", o.Zip5 ?? "", o.Number ?? "", o.Street ?? "", o.Taxonomy ?? "",
                    o.LastUpdate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", string.Join(' ', o.Ccns.Select(c => c.Ccn)),
                    string.Join(' ', o.PacIds), o.ParentKey ?? "", o.ParentTin is null ? "" : "yes",
                ];
                await writer.WriteLineAsync(string.Join(',', fields.Select(f => f.Contains(',', StringComparison.Ordinal) ? $"\"{f}\"" : f)));
            }

            log.Information("Organization groups: {Members:N0} members written to {Path}", members.Count, csvPath);
        }

        return stats;
    }

    private void Report(OrgGroupStats stats, int members, double seconds)
    {
        foreach (var stage in stats.Stages)
        {
            log.Information("Organization groups by {Tie}: {Groups:N0} groups of {Npis:N0} NPIs", stage.Tie, stage.Groups, stage.Npis);
        }

        log.Information("Organization groups: {Members:N0} NPIs in {Groups:N0} groups, grouped in {Seconds:N1}s; by size {Sizes}; largest {Largest}",
            members, stats.Stages[^1].Groups, seconds, string.Join(", ", stats.Sizes.Select(s => $"{s.Key}: {s.Value:N0}")),
            string.Join(", ", stats.Largest.Select(l => $"{l.MainNpi} ({l.Size})")));
        foreach (var (main, size) in stats.Largest.Where(l => l.Size > OrganizationGroups.LargeGroup))
        {
            log.Warning("Organization group of main NPI {Npi} has {Size:N0} NPIs (more than {Limit}): check it", main, size, OrganizationGroups.LargeGroup);
        }

        if (stats.LargeBuckets > 0)
        {
            log.Information("Organization groups: {Buckets} PAC ID or parent buckets of more than {Size} NPIs compared pair by pair", stats.LargeBuckets, OrgGrouping.LargeBucket);
        }
    }

    /// <returns>The members written.</returns>
    public async Task<long> BuildAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var started = DateTime.UtcNow;
        await using var connection = await database.OpenAsync(ct);
        var orgs = await ReadAsync(connection, ct);
        log.Information("Organization groups: {Organizations:N0} organization NPIs read in {Seconds:N1}s", orgs.Count, watch.Elapsed.TotalSeconds);
        var step = Stopwatch.StartNew();
        var (members, stats) = OrgGrouping.Build(orgs);
        Report(stats, members.Count, step.Elapsed.TotalSeconds);

        // Before the swap: the NPIs whose group changed (a member came or went, the main NPI changed), for the record quality.
        var old = (await connection.QueryAsync<(string Npi, string MainNpi)>(new CommandDefinition(
            "SELECT npi, main_npi FROM org_group_member", commandTimeout: 600, cancellationToken: ct))).ToList();
        var changed = ChangedNpis(old, members.Select(m => (m.Npi, m.MainNpi)));

        var counts = await TableSwap.ReplaceAsync(connection, [Table], minRowRatio, () =>
            ReferenceTables.CopyToStagingAsync(connection, Table, ReferenceTables.ToDataTable(members,
                ("npi", typeof(string), m => m.Npi), ("main_npi", typeof(string), m => m.MainNpi), ("members", typeof(int), m => m.Members),
                ("is_main", typeof(sbyte), m => (sbyte)(m.IsMain ? 1 : 0)), ("ccn", typeof(string), m => m.Ccn),
                ("facility_kind", typeof(string), m => m.FacilityKind), ("joined_by", typeof(short), m => (short)m.JoinedBy)), ct), ct);

        // The search's nationwide list with no filter counts one row per organization: the projection's total less these.
        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `search_total` (`name`, `total`, `counted_at`) VALUES (@name, @total, UTC_TIMESTAMP()) AS new
            ON DUPLICATE KEY UPDATE `total` = new.`total`, `counted_at` = new.`counted_at`
            """, ct, param: new { name = OrganizationGroups.MembersTotal, total = members.Count(m => !m.IsMain) });

        if (changed.Count > 0 && await Database.CountAsync(connection, QualityBuilder.Source, ct) > 0)
        {
            foreach (var batch in changed.Chunk(1000))
            {
                await Database.ExecuteAsync(connection, $"INSERT IGNORE INTO `{QualityBuilder.Pending}` (`npi`) SELECT npi FROM provider WHERE npi IN @npis", ct,
                    param: new { npis = batch });
            }
        }

        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
            VALUES (@source, @version, 'organization groups', @rows, @started, UTC_TIMESTAMP()) AS new
            ON DUPLICATE KEY UPDATE `version` = new.`version`, `rows_loaded` = new.`rows_loaded`, `loaded_at` = new.`loaded_at`, `checked_at` = new.`checked_at`
            """, ct, param: new
            {
                source = Source, started, rows = (int)counts[Table],
                version = $"{stats.Stages[^1].Groups} groups, {members.Count} NPIs",
            });
        log.Information("Organization groups built in {Seconds:N0}s: {Members:N0} NPIs in {Groups:N0} groups; {Changed:N0} NPIs whose group changed queued for the record quality",
            watch.Elapsed.TotalSeconds, members.Count, stats.Stages[^1].Groups, changed.Count);
        return counts[Table];
    }

    /// <summary>
    /// The NPIs whose group isn't what it was: added to or dropped from a group, or in a group whose members or main NPI changed
    /// (every member of such a group, old and new, because the "another NPI is current" reason reads the whole group).
    /// </summary>
    public static IReadOnlyList<string> ChangedNpis(IEnumerable<(string Npi, string MainNpi)> before, IEnumerable<(string Npi, string MainNpi)> after)
    {
        static Dictionary<string, string> Signatures(IEnumerable<(string Npi, string MainNpi)> rows)
        {
            var byGroup = rows.GroupBy(r => r.MainNpi, StringComparer.Ordinal);
            var signature = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in byGroup)
            {
                var text = group.Key + ":" + string.Join(',', group.Select(r => r.Npi).Order(StringComparer.Ordinal));
                foreach (var r in group)
                {
                    signature[r.Npi] = text;
                }
            }

            return signature;
        }

        var old = Signatures(before);
        var now = Signatures(after);
        return old.Keys.Union(now.Keys, StringComparer.Ordinal)
            .Where(n => !old.TryGetValue(n, out var a) || !now.TryGetValue(n, out var b) || !string.Equals(a, b, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
    }
}
