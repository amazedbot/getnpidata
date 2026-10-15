using System.Globalization;
using System.Runtime.CompilerServices;
using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>
/// A Medicare group practice in the Groups &amp; organizations list (docs/PRODUCT_PLAN.md step 3.6), by its PAC ID.
/// <see cref="Clinicians"/> counts its active clinicians as its page does (reassigned to it in Medicare enrollment or listed with it in
/// Care Compare); <see cref="OrganizationNpis"/> the active organization NPIs enrolled under it; <see cref="MipsScore"/> its group
/// MIPS final score (0–100) in <see cref="MipsYear"/>, when it reported as a group.
/// </summary>
public sealed record GroupSummary(string PacId, string Name, string? State, int Clinicians, int OrganizationNpis, bool? AcceptsMedicare, double? MipsScore,
    int? MipsYear);

/// <summary>A page of Medicare groups.</summary>
public sealed record GroupPage(IReadOnlyList<GroupSummary> Items, int Page, int PageSize, int TotalCount);

/// <summary>Groups with <see cref="Min"/> to <see cref="Max"/> active clinicians (no upper bound when null).</summary>
public sealed record GroupSizeBand(string Label, int Min, int? Max, int Groups, long Clinicians);

/// <summary>
/// Active organization NPIs whose primary taxonomy is in a plain category, by record-quality label (an NPI not labelled yet counts
/// in <see cref="Organizations"/> only).
/// </summary>
public sealed record OrganizationCategoryCount(string Category, int Organizations, int Current, int Check, int Stale);

/// <summary>An organization NPI's Medicare Part B payments under its own NPI (Medicare Physician &amp; Other Practitioners, by provider).</summary>
public sealed record OrganizationBilling(string Npi, string Name, string? Specialty, string? City, string? State, double Payments, int? Beneficiaries);

/// <summary>The totals on the Groups &amp; organizations page.</summary>
public sealed record OrganizationOverview(int Groups, int GroupsWithMips, int? MipsYear, IReadOnlyList<GroupSizeBand> Sizes, IReadOnlyList<GroupSummary> Largest,
    int Organizations, IReadOnlyList<OrganizationCategoryCount> Categories, IReadOnlyList<string> States, int BillingOrganizations, double BillingTotal,
    int? BillingYear, IReadOnlyList<OrganizationBilling> TopBilling);

/// <summary>
/// The group list's filters, as the page and /api/v1/groups take them. <see cref="Q"/>: a PAC ID, an NPI (the groups it is enrolled
/// as or bills through), or words of the name (every word must appear). Sorts: clinicians, name, state, mips, organizations; "-"
/// first is descending; the default is the largest first.
/// </summary>
public sealed record GroupFilter
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxQueryLength = 100;
    public const string DefaultSort = "-clinicians";

    public static readonly IReadOnlyList<string> Sorts = ["clinicians", "name", "state", "mips", "organizations"];

    public string? Q { get; init; }

    public string? State { get; init; }

    public int? MinClinicians { get; init; }

    public int? MaxClinicians { get; init; }

    public string? Sort { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>Errors by parameter name; empty when the filter is valid.</summary>
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

        if (MinClinicians is < 0)
        {
            errors["minClinicians"] = ["0 or more."];
        }

        if (MaxClinicians is < 0)
        {
            errors["maxClinicians"] = ["0 or more."];
        }
        else if (MinClinicians is { } min && MaxClinicians is { } max && max < min)
        {
            errors["maxClinicians"] = ["At least minClinicians."];
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

/// <summary>
/// Medicare group practices and organization NPIs (docs/PRODUCT_PLAN.md step 3.6). Two snapshots kept in memory until the data
/// changes (<see cref="SnapshotCache{T}"/>): every group with an active clinician (~200,000) with its size, organization NPIs and
/// group MIPS score; and the active organization NPIs (~1.9M) counted by plain category, state and record-quality label, with
/// the organizations paid the most by Medicare Part B. Both take seconds to build from the database, so the site builds them once
/// after starting and again after a load, in the background.
/// </summary>
public sealed class OrganizationDirectory : IDisposable
{
    public const int LargestCount = 10;
    public const int TopBillingCount = 10;

    public static readonly IReadOnlyList<(string Label, int Min, int? Max)> SizeBands =
        [("1 clinician", 1, 1), ("2–9", 2, 9), ("10–49", 10, 49), ("50–199", 50, 199), ("200–999", 200, 999), ("1,000 or more", 1000, null)];

    private readonly SnapshotCache<GroupSnapshot> _groups;
    private readonly SnapshotCache<OrganizationSnapshot> _organizations;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>> _groupsOfNpi;

    public OrganizationDirectory(string connectionString)
    {
        _groups = new SnapshotCache<GroupSnapshot>(ct => DataStamp.ReadAsync(connectionString, ct), ct => LoadGroupsAsync(connectionString, ct));
        _organizations = new SnapshotCache<OrganizationSnapshot>(ct => DataStamp.ReadAsync(connectionString, ct),
            ct => LoadOrganizationsAsync(connectionString, ct));
        _groupsOfNpi = (npi, ct) => GroupsOfNpiAsync(connectionString, npi, ct);
    }

    /// <summary>For tests: a directory over rows already built.</summary>
    internal OrganizationDirectory(IReadOnlyList<GroupSummary> groups, IReadOnlyList<(string? Code, string? State, int Label, int Organizations)> organizations,
        IReadOnlyList<OrganizationBilling> billing, int? billingYear, IReadOnlyDictionary<string, IReadOnlyList<string>> groupsOfNpi)
    {
        var g = new GroupSnapshot(groups);
        var o = new OrganizationSnapshot(organizations, billing.Count, billing.Sum(b => b.Payments), billingYear, billing.Take(TopBillingCount).ToList());
        _groups = new SnapshotCache<GroupSnapshot>(_ => Task.FromResult(""), _ => Task.FromResult(g));
        _organizations = new SnapshotCache<OrganizationSnapshot>(_ => Task.FromResult(""), _ => Task.FromResult(o));
        _groupsOfNpi = (npi, _) => Task.FromResult(groupsOfNpi.GetValueOrDefault(npi) ?? []);
    }

    public void Dispose()
    {
        _groups.Dispose();
        _organizations.Dispose();
    }

    /// <summary>Fills both caches (the site calls it once after starting, so the first visitor doesn't wait for them).</summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        await _groups.GetAsync(ct);
        await _organizations.GetAsync(ct);
    }

    public async Task<OrganizationOverview> GetOverviewAsync(CancellationToken ct)
    {
        var groups = await _groups.GetAsync(ct);
        var organizations = await _organizations.GetAsync(ct);
        return new OrganizationOverview(groups.Rows.Count, groups.Rows.Count(g => g.MipsScore is not null), groups.Rows.Max(g => g.MipsYear), groups.Sizes,
            groups.Rows.Take(LargestCount).ToList(), organizations.Total, organizations.Categories(null), organizations.States, organizations.BillingCount,
            organizations.BillingTotal, organizations.BillingYear, organizations.TopBilling);
    }

    /// <summary>Active organization NPIs by plain category in one state (or everywhere), most first.</summary>
    public async Task<IReadOnlyList<OrganizationCategoryCount>> GetCategoriesAsync(string? state, CancellationToken ct) =>
        (await _organizations.GetAsync(ct)).Categories(string.IsNullOrWhiteSpace(state) ? null : state.Trim().ToUpperInvariant());

    /// <summary>A group from the snapshot, without waiting for it (null while it is being built or for an unknown PAC ID).</summary>
    public GroupSummary? PeekGroup(string pacId) => _groups.Peek()?.Find(pacId);

    /// <summary>One page of the groups matching a valid filter (<see cref="GroupFilter.Validate"/>).</summary>
    public async Task<GroupPage> SearchGroupsAsync(GroupFilter filter, CancellationToken ct)
    {
        Check(filter);
        var matches = await MatchAsync(filter, ct);
        var items = Sorted(matches, filter.Sort).Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToList();
        return new GroupPage(items, filter.Page, filter.PageSize, matches.Count);
    }

    /// <summary>Every group matching the filter in its sort order (paging ignored), for the CSV.</summary>
    public async IAsyncEnumerable<GroupSummary> AllGroupsAsync(GroupFilter filter, [EnumeratorCancellation] CancellationToken ct)
    {
        Check(filter with { Page = 1, PageSize = GroupFilter.DefaultPageSize });
        foreach (var group in Sorted(await MatchAsync(filter, ct), filter.Sort))
        {
            ct.ThrowIfCancellationRequested();
            yield return group;
        }
    }

    private async Task<List<GroupSummary>> MatchAsync(GroupFilter filter, CancellationToken ct)
    {
        var snapshot = await _groups.GetAsync(ct);
        var q = filter.Q?.Trim() ?? "";
        IEnumerable<GroupSummary> rows = snapshot.Rows;
        if (q.Length == 10 && q.All(char.IsAsciiDigit))
        {
            // A PAC ID, or an NPI: the groups it is enrolled as or bills through. PAC IDs and NPIs are both ten digits.
            var pacs = new HashSet<string>(await _groupsOfNpi(q, ct), StringComparer.Ordinal) { q };
            rows = pacs.Select(snapshot.Find).OfType<GroupSummary>();
        }
        else if (q.Length > 0)
        {
            var words = FacilityDirectory.Normalize(q).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            rows = snapshot.Entries.Where(e => words.All(w => e.Key.Contains(w, StringComparison.Ordinal))).Select(e => e.Group);
        }

        var state = string.IsNullOrWhiteSpace(filter.State) ? null : filter.State.Trim().ToUpperInvariant();
        return rows.Where(g => (state is null || g.State == state)
                               && (filter.MinClinicians is not { } min || g.Clinicians >= min)
                               && (filter.MaxClinicians is not { } max || g.Clinicians <= max))
            .ToList();
    }

    private static void Check(GroupFilter filter)
    {
        if (filter.Validate() is { Count: > 0 } errors)
        {
            throw new ArgumentException(string.Join(" ", errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}")), nameof(filter));
        }
    }

    private static IEnumerable<GroupSummary> Sorted(IEnumerable<GroupSummary> rows, string? sort)
    {
        var key = string.IsNullOrWhiteSpace(sort) ? GroupFilter.DefaultSort : sort.Trim();
        var descending = key.StartsWith('-');
        var byName = StringComparer.OrdinalIgnoreCase;
        IOrderedEnumerable<GroupSummary> ordered = key.TrimStart('-').ToLowerInvariant() switch
        {
            "name" => descending ? rows.OrderByDescending(g => g.Name, byName) : rows.OrderBy(g => g.Name, byName),
            "state" => descending ? rows.OrderByDescending(g => g.State ?? "", byName) : rows.OrderBy(g => g.State ?? "", byName),
            // Groups without a score last both ways; "-mips" is the best first.
            "mips" => descending
                ? rows.OrderBy(g => g.MipsScore is null).ThenByDescending(g => g.MipsScore)
                : rows.OrderBy(g => g.MipsScore is null).ThenBy(g => g.MipsScore),
            "organizations" => descending ? rows.OrderByDescending(g => g.OrganizationNpis) : rows.OrderBy(g => g.OrganizationNpis),
            _ => descending ? rows.OrderByDescending(g => g.Clinicians) : rows.OrderBy(g => g.Clinicians),
        };
        return ordered.ThenBy(g => g.Name, byName).ThenBy(g => g.PacId, StringComparer.Ordinal);
    }

    private sealed record GroupEntry(GroupSummary Group, string Key);

    private sealed class GroupSnapshot
    {
        private readonly Dictionary<string, GroupSummary> _byPac;

        public GroupSnapshot(IReadOnlyList<GroupSummary> rows)
        {
            Rows = rows.OrderByDescending(g => g.Clinicians).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.PacId, StringComparer.Ordinal).ToList();
            Entries = Rows.Select(g => new GroupEntry(g, " " + FacilityDirectory.Normalize(g.Name) + " ")).ToList();
            _byPac = Rows.GroupBy(g => g.PacId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            Sizes = SizeBands.Select(b =>
                {
                    var inBand = Rows.Where(g => g.Clinicians >= b.Min && (b.Max is not { } max || g.Clinicians <= max)).ToList();
                    return new GroupSizeBand(b.Label, b.Min, b.Max, inBand.Count, inBand.Sum(g => (long)g.Clinicians));
                })
                .ToList();
        }

        /// <summary>Largest first.</summary>
        public List<GroupSummary> Rows { get; }

        public IReadOnlyList<GroupEntry> Entries { get; }

        public IReadOnlyList<GroupSizeBand> Sizes { get; }

        public GroupSummary? Find(string? pacId) => pacId is not null && _byPac.TryGetValue(pacId.Trim(), out var g) ? g : null;
    }

    private sealed class OrganizationSnapshot
    {
        // (state or "", category) → counts; state "" = everywhere.
        private readonly Dictionary<(string State, string Category), int[]> _counts = [];

        public OrganizationSnapshot(IReadOnlyList<(string? Code, string? State, int Label, int Organizations)> rows, int billingCount, double billingTotal,
            int? billingYear, IReadOnlyList<OrganizationBilling> topBilling)
        {
            foreach (var (code, rawState, label, count) in rows)
            {
                var category = SpecialtyCategories.Default.Place(code)?.Category ?? SpecialtyCategories.FallbackCategory;
                var state = rawState?.Trim().ToUpperInvariant() is { Length: 2 } s && GeographyCatalog.AllStateNames.ContainsKey(s) ? s : null;
                Add("", category, label, count);
                if (state is not null)
                {
                    Add(state, category, label, count);
                }

                Total += count;
            }

            States = _counts.Keys.Select(k => k.State).Where(s => s.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList();
            BillingCount = billingCount;
            BillingTotal = billingTotal;
            BillingYear = billingYear;
            TopBilling = topBilling;
        }

        public int Total { get; }

        public IReadOnlyList<string> States { get; }

        public int BillingCount { get; }

        public double BillingTotal { get; }

        public int? BillingYear { get; }

        public IReadOnlyList<OrganizationBilling> TopBilling { get; }

        public List<OrganizationCategoryCount> Categories(string? state) =>
            _counts.Where(kv => kv.Key.State == (state ?? ""))
                .Select(kv => new OrganizationCategoryCount(kv.Key.Category, kv.Value[0], kv.Value[(int)QualityLabel.Current], kv.Value[(int)QualityLabel.Check],
                    kv.Value[(int)QualityLabel.Stale]))
                .OrderByDescending(c => c.Organizations).ThenBy(c => c.Category, StringComparer.Ordinal)
                .ToList();

        private void Add(string state, string category, int label, int count)
        {
            if (!_counts.TryGetValue((state, category), out var counts))
            {
                _counts[(state, category)] = counts = new int[4];
            }

            counts[0] += count;
            if (label is >= 1 and <= 3)
            {
                counts[label] += count;
            }
        }
    }

    // Types as MySqlConnector returns them (Dapper maps records by constructor).
    private sealed record CountRow(string PacId, long Clinicians);

    private sealed record NameRow(string PacId, string? Name, string? State, long? Accepts);

    private sealed record MipsRow(string PacId, long? Year, double? Score);

    private sealed record OrganizationRow(string? Code, string? State, long Label, long Organizations);

    private sealed record BillingRow(string Npi, short Year, double Payments, int? Beneficiaries);

    private sealed record NameLookupRow(string Npi, string? OrgName, string? PrimaryTaxonomyCode, string? City, string? State);

    private const int Timeout = 600;

    private static async Task<GroupSnapshot> LoadGroupsAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // The heaviest query of these pages (~5.9M group memberships, each checked against the projection): the active clinicians
        // of every group, counted as OrganizationService counts one group's for its page.
        var counts = await connection.QueryAsync<CountRow>(new CommandDefinition(
            """
            SELECT g.pac AS PacId, COUNT(*) AS Clinicians
            FROM (SELECT group_pac_id AS pac, npi FROM medicare_reassignment WHERE npi IS NOT NULL
                  UNION SELECT org_pac_id, npi FROM cc_group) g
            JOIN provider p ON p.npi = g.npi
            GROUP BY g.pac
            """, commandTimeout: Timeout, cancellationToken: ct));
        var careCompare = (await connection.QueryAsync<NameRow>(new CommandDefinition(
                "SELECT org_pac_id AS PacId, MAX(group_name) AS Name, MAX(state) AS State, CAST(MAX(accepts_assignment) AS SIGNED) AS Accepts FROM cc_group GROUP BY org_pac_id",
                commandTimeout: Timeout, cancellationToken: ct)))
            .ToDictionary(r => r.PacId, StringComparer.Ordinal);
        var reassignment = (await connection.QueryAsync<NameRow>(new CommandDefinition(
                """
                SELECT group_pac_id AS PacId, MAX(group_name) AS Name, MAX(group_state) AS State, CAST(NULL AS SIGNED) AS Accepts
                FROM medicare_reassignment GROUP BY group_pac_id
                """, commandTimeout: Timeout, cancellationToken: ct)))
            .ToDictionary(r => r.PacId, StringComparer.Ordinal);

        // Organization NPIs enrolled under a PAC ID: the enrollment file names organizations in org_name (individuals have none).
        var organizations = (await connection.QueryAsync<CountRow>(new CommandDefinition(
                """
                SELECT e.pac_id AS PacId, COUNT(DISTINCT e.npi) AS Clinicians
                FROM medicare_enrollment e JOIN provider p ON p.npi = e.npi
                WHERE e.pac_id IS NOT NULL AND e.org_name IS NOT NULL
                GROUP BY e.pac_id
                """, commandTimeout: Timeout, cancellationToken: ct)))
            .ToDictionary(r => r.PacId, r => (int)r.Clinicians, StringComparer.Ordinal);
        var mips = (await connection.QueryAsync<MipsRow>(new CommandDefinition(
                """
                SELECT org_pac_id AS PacId, CAST(MAX(program_year) AS SIGNED) AS Year, MAX(final_score) AS Score
                FROM cc_mips WHERE source = 'group' AND org_pac_id IS NOT NULL GROUP BY org_pac_id
                """, commandTimeout: Timeout, cancellationToken: ct)))
            .ToDictionary(r => r.PacId, StringComparer.Ordinal);

        var rows = counts.Select(c =>
            {
                var cc = careCompare.GetValueOrDefault(c.PacId);
                var re = reassignment.GetValueOrDefault(c.PacId);
                var m = mips.GetValueOrDefault(c.PacId);
                return new GroupSummary(c.PacId, cc?.Name ?? re?.Name ?? "Group " + c.PacId, re?.State ?? cc?.State, (int)c.Clinicians,
                    organizations.GetValueOrDefault(c.PacId), cc?.Accepts is { } a ? a != 0 : null, m?.Score is { } score ? Math.Round(score, 2) : null,
                    m?.Score is null ? null : (int?)m.Year);
            })
            .ToList();
        return new GroupSnapshot(rows);
    }

    private static async Task<OrganizationSnapshot> LoadOrganizationsAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // Every active organization NPI by primary taxonomy, primary practice state and label: a pass over provider (~9.5M rows).
        var rows = (await connection.QueryAsync<OrganizationRow>(new CommandDefinition(
                """
                SELECT p.primary_taxonomy_code AS Code, l.state AS State, CAST(COALESCE(q.label, 0) AS SIGNED) AS Label, COUNT(*) AS Organizations
                FROM provider p
                LEFT JOIN provider_location l ON l.npi = p.npi AND l.is_primary = 1
                LEFT JOIN provider_quality q ON q.npi = p.npi
                WHERE p.entity_type = 2
                GROUP BY p.primary_taxonomy_code, l.state, q.label
                """, commandTimeout: Timeout, cancellationToken: ct)))
            .Select(r => (r.Code, r.State, (int)r.Label, (int)r.Organizations))
            .ToList();

        // Organizations' own Medicare Part B payments (most Part B rows are individuals; ~1.3M rows checked against the projection).
        var billing = (await connection.QueryAsync<BillingRow>(new CommandDefinition(
                """
                SELECT u.npi AS Npi, u.data_year AS Year, u.payment_amount AS Payments, u.beneficiaries AS Beneficiaries
                FROM medicare_utilization u JOIN provider p ON p.npi = u.npi
                WHERE p.entity_type = 2 AND u.payment_amount > 0
                """, commandTimeout: Timeout, cancellationToken: ct)))
            .ToList();
        var top = billing.OrderByDescending(b => b.Payments).ThenBy(b => b.Npi, StringComparer.Ordinal).Take(TopBillingCount).ToList();
        var names = top.Count == 0 ? [] : (await connection.QueryAsync<NameLookupRow>(new CommandDefinition(
                """
                SELECT p.npi AS Npi, p.org_name AS OrgName, p.primary_taxonomy_code AS PrimaryTaxonomyCode, l.city AS City, l.state AS State
                FROM provider p LEFT JOIN provider_location l ON l.npi = p.npi AND l.is_primary = 1
                WHERE p.npi IN @npis
                """, new { npis = top.Select(t => t.Npi).ToArray() }, commandTimeout: Timeout, cancellationToken: ct)))
            .GroupBy(n => n.Npi).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var topBilling = top.Select(t =>
            {
                var n = names.GetValueOrDefault(t.Npi);
                return new OrganizationBilling(t.Npi, n?.OrgName ?? t.Npi, SpecialtyCategories.Default.Place(n?.PrimaryTaxonomyCode)?.Singular, n?.City, n?.State,
                    t.Payments, t.Beneficiaries);
            })
            .ToList();
        return new OrganizationSnapshot(rows, billing.Count, billing.Sum(b => b.Payments), billing.Count > 0 ? billing.Max(b => (int)b.Year) : null, topBilling);
    }

    private static async Task<IReadOnlyList<string>> GroupsOfNpiAsync(string connectionString, string npi, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        return (await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT pac_id FROM medicare_enrollment WHERE npi = @npi AND pac_id IS NOT NULL
            UNION SELECT group_pac_id FROM medicare_reassignment WHERE npi = @npi
            UNION SELECT org_pac_id FROM cc_group WHERE npi = @npi
            """, new { npi }, cancellationToken: ct))).ToList();
    }
}

/// <summary>The group list as CSV (UTF-8 with BOM, like the other exports), a link to each group's page.</summary>
public static class GroupCsv
{
    public static readonly string[] Header =
        ["PAC ID", "Group", "State", "Active Clinicians", "Organization NPIs", "Accepts Medicare Assignment", "MIPS Final Score", "MIPS Year", "Page"];

    public static Task<long> WriteAsync(IAsyncEnumerable<GroupSummary> rows, Stream output, string siteBase, CancellationToken ct) =>
        IndustryCsv.WriteRowsAsync(rows, output, Header, g =>
        [
            g.PacId, g.Name, g.State, g.Clinicians.ToString(CultureInfo.InvariantCulture), g.OrganizationNpis.ToString(CultureInfo.InvariantCulture),
            g.AcceptsMedicare switch { true => "Yes", false => "No", null => null }, g.MipsScore?.ToString("0.##", CultureInfo.InvariantCulture),
            g.MipsYear?.ToString(CultureInfo.InvariantCulture), $"{siteBase}/group/{Uri.EscapeDataString(g.PacId)}",
        ], ct);
}
