using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>
/// Areas over time (docs/PRODUCT_PLAN.md step 3.3): the monthly figures the loader precomputes per area (area_month_stat,
/// migration 102), the values of one statistic for every state or county (the area maps), and the providers behind each
/// change count. Every read is a primary-key range of a small table, so county pages stay fast.
/// </summary>
public sealed class AreaHistoryService(string connectionString)
{
    public const int PageSize = 50;

    // An area's NPIs issued in a month, as the loader counts them (by their location now): the list behind "new".
    private static string InAreaSql(string type) => type switch
    {
        "county" => "EXISTS (SELECT 1 FROM provider_location l JOIN zip_county z ON z.zip5 = l.zip5 WHERE l.npi = p.npi AND z.county_fips = @id AND l.state IS NOT NULL AND CHAR_LENGTH(l.state) <= 5)",
        "state" => "EXISTS (SELECT 1 FROM provider_location l WHERE l.npi = p.npi AND l.state = @id)",
        _ => "EXISTS (SELECT 1 FROM provider_location l WHERE l.npi = p.npi AND l.state IS NOT NULL AND CHAR_LENGTH(l.state) <= 5)",
    };

    private const string InCategorySql =
        "EXISTS (SELECT 1 FROM provider_taxonomy t JOIN specialty_code_category m ON m.code = t.taxonomy_code WHERE t.npi = p.npi AND m.category = @category)";

    private sealed record MetaRow(DateTime? DerivedFrom, DateTime? TrackedFrom, DateTime? ChangesFrom, DateTime? AsOfMonth);

    private sealed record Row(string Category, DateTime Month, sbyte Basis, int? Providers, int? NewProviders, int? Deactivated, int? MovedIn,
        int? MovedOut, int? SpecialtyChanges, int? LicenseChanges, int? ExclusionChanges, int? QualityCurrent, int? QualityCheck, int? QualityStale);

    /// <summary>(type, id) of the US (<paramref name="state"/> null), a state or a county; null when malformed or the county isn't in the state.</summary>
    public static (string Type, string Id)? Area(string? state, string? county)
    {
        if (county is not null)
        {
            return county.Length == 5 && county.All(char.IsAsciiDigit) && state is not null && GeographyCatalog.AllStateNames.ContainsKey(state)
                ? ("county", county)
                : null;
        }

        return state is null ? ("us", "US") : GeographyCatalog.AllStateNames.ContainsKey(state) ? ("state", state.ToUpperInvariant()) : null;
    }

    /// <summary>
    /// Every month of an area's figures, for the chosen category (only <see cref="AreaMonth.Providers"/> and
    /// <see cref="AreaMonth.NewProviders"/> are per category). Null for an unknown area.
    /// </summary>
    public async Task<AreaHistory?> GetAsync(string? state, string? county, string? category, CancellationToken ct)
    {
        if (Area(state, county) is not (string type, string id))
        {
            return null;
        }

        var cat = SpecialtyCategories.Default.Find(category)?.Name ?? "";
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        string name;
        string? stateCode = null;
        if (type == "county")
        {
            var row = await connection.QuerySingleOrDefaultAsync<(string Name, string State)?>(new CommandDefinition(
                "SELECT county_name, state FROM county WHERE county_fips = @id", new { id }, cancellationToken: ct));
            if (row is not { } c || !string.Equals(c.State, state, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            (name, stateCode) = (c.Name, c.State);
        }
        else
        {
            (name, stateCode) = type == "state" ? (GeographyCatalog.StateName(id), id) : ("United States", null);
        }

        var coverage = await CoverageAsync(connection, ct);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT category AS Category, month AS Month, basis AS Basis, providers AS Providers, new_providers AS NewProviders, deactivated AS Deactivated,
                   moved_in AS MovedIn, moved_out AS MovedOut, specialty_changes AS SpecialtyChanges, license_changes AS LicenseChanges,
                   exclusion_changes AS ExclusionChanges, quality_current AS QualityCurrent, quality_check AS QualityCheck, quality_stale AS QualityStale
            FROM area_month_stat
            WHERE area_type = @type AND area_id = @id AND category IN ('', @cat) AND subcategory = ''
            """, new { type, id, cat }, cancellationToken: ct)))
            .Select(ToStat).ToList();
        return new AreaHistory(type, id, name, stateCode, cat, AreaHistoryMath.Fill(rows, coverage, type, cat), coverage);
    }

    private static AreaHistoryMath.StatRow ToStat(Row r) => new(r.Category, DateOnly.FromDateTime(r.Month), r.Basis, r.Providers, r.NewProviders,
        r.Deactivated, r.MovedIn, r.MovedOut, r.SpecialtyChanges, r.LicenseChanges, r.ExclusionChanges, r.QualityCurrent, r.QualityCheck, r.QualityStale);

    private static async Task<AreaHistoryCoverage> CoverageAsync(MySqlConnection connection, CancellationToken ct)
    {
        var meta = await connection.QuerySingleOrDefaultAsync<MetaRow>(new CommandDefinition(
            "SELECT derived_from AS DerivedFrom, tracked_from AS TrackedFrom, changes_from AS ChangesFrom, as_of_month AS AsOfMonth FROM area_history_meta WHERE id = 1",
            cancellationToken: ct));
        var asOf = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition("SELECT as_of_date FROM data_version WHERE id = 1", cancellationToken: ct));
        static DateOnly? D(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;
        return new AreaHistoryCoverage(D(meta?.DerivedFrom), D(meta?.TrackedFrom), D(meta?.ChangesFrom), D(meta?.AsOfMonth), D(asOf));
    }

    /// <summary>The months a monthly statistic can be shown for, newest first.</summary>
    public static IReadOnlyList<DateOnly> MonthsOf(AreaHistoryCoverage coverage)
    {
        if (coverage is not { AsOfMonth: { } last })
        {
            return [];
        }

        var first = coverage.DerivedFrom ?? coverage.TrackedFrom ?? last;
        var months = new List<DateOnly>();
        for (var m = last; m >= first; m = m.AddMonths(-1))
        {
            months.Add(m);
        }

        return months;
    }

    /// <summary>
    /// One statistic (<see cref="AreaMapStats"/>) for every state (<paramref name="level"/> "state") or every county
    /// ("county"; only <paramref name="state"/>'s when given), for a month (default: the data's as-of month) and a specialty.
    /// Null when the level or state is unknown.
    /// </summary>
    public async Task<AreaMapData?> GetMapAsync(string level, string? state, string stat, string? category, string? subcategory, DateOnly? month,
        CancellationToken ct)
    {
        if (level is not ("state" or "county") || !AreaMapStats.IsKnown(stat)
            || (state is not null && (level == "state" || !GeographyCatalog.AllStateNames.ContainsKey(state))))
        {
            return null;
        }

        var (_, label, monthly, bySpecialty) = AreaMapStats.Get(stat);
        var specialty = SpecialtyCategories.Default.Find(category);
        var cat = bySpecialty ? specialty?.Name ?? "" : "";
        var sub = bySpecialty && !monthly ? specialty?.Find(subcategory)?.Name ?? "" : "";
        state = state?.ToUpperInvariant();

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var coverage = await CoverageAsync(connection, ct);
        var months = MonthsOf(coverage);
        var m = monthly ? month ?? coverage.AsOfMonth : null;

        // The areas with their population.
        var areas = level == "county"
            ? (await connection.QueryAsync<(string Id, string Name, string State, long? Population)>(new CommandDefinition(
                $"""
                SELECT c.county_fips, c.county_name, c.state, CAST(p.population AS SIGNED)
                FROM county c LEFT JOIN county_population p ON p.county_fips = c.county_fips
                {(state is null ? "" : "WHERE c.state = @state")}
                ORDER BY c.county_fips
                """, new { state }, cancellationToken: ct))).ToList()
            : (await connection.QueryAsync<(string Id, string Name, string State, long? Population)>(new CommandDefinition(
                """
                SELECT c.state, c.state, c.state, CAST(SUM(p.population) AS SIGNED)
                FROM county c LEFT JOIN county_population p ON p.county_fips = c.county_fips
                GROUP BY c.state ORDER BY c.state
                """, cancellationToken: ct)))
                .Where(s => GeographyCatalog.AllStateNames.ContainsKey(s.Id))
                .Select(s => (s.Id, Name: GeographyCatalog.StateName(s.Id), s.State, s.Population)).ToList();

        var values = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        if (!monthly)
        {
            foreach (var (id, providers) in await connection.QueryAsync<(string, int)>(new CommandDefinition(
                         "SELECT area_id, providers FROM area_provider_count WHERE category = @cat AND subcategory = @sub AND area_type = @level",
                         new { cat, sub, level }, cancellationToken: ct)))
            {
                values[id] = providers;
            }
        }
        else if (m is { } when)
        {
            var rows = (await connection.QueryAsync<MapRow>(new CommandDefinition(
                    """
                    SELECT area_id AS Id, category AS Category, month AS Month, providers AS Providers, new_providers AS NewProviders,
                           deactivated AS Deactivated, moved_in AS MovedIn, moved_out AS MovedOut,
                           quality_current AS QualityCurrent, quality_check AS QualityCheck, quality_stale AS QualityStale
                    FROM area_month_stat
                    WHERE month IN (@when, @before) AND area_type = @level AND category IN ('', @cat) AND subcategory = ''
                    """, new { when = when.ToDateTime(TimeOnly.MinValue), before = when.AddMonths(-1).ToDateTime(TimeOnly.MinValue), level, cat },
                    cancellationToken: ct)))
                .ToLookup(r => r.Id, StringComparer.OrdinalIgnoreCase);
            var tracked = coverage.TrackedFrom is { } t && when >= t;
            var derived = coverage.DerivedFrom is { } d && when >= d;
            foreach (var area in areas)
            {
                var own = rows[area.Id].ToList();
                MapRow? Find(string c, DateOnly at) => own.FirstOrDefault(r => r.Category == c && DateOnly.FromDateTime(r.Month) == at);
                var all = Find("", when);
                var sel = Find(cat, when);
                // A snapshot month: the area's every-provider row has a count; a category without a row then has none.
                int? Providers(DateOnly at) => Find("", at)?.Providers is null ? null : Find(cat, at)?.Providers ?? 0;
                values[area.Id] = stat switch
                {
                    AreaMapStats.New or AreaMapStats.NewPer100k when derived => sel?.NewProviders ?? 0,
                    AreaMapStats.MovedIn when tracked => all?.MovedIn ?? 0,
                    AreaMapStats.MovedOut when tracked => all?.MovedOut ?? 0,
                    AreaMapStats.Deactivated when tracked => all?.Deactivated ?? 0,
                    AreaMapStats.Stale => AreaHistoryMath.QualityShare(all?.QualityStale, all?.QualityCurrent, all?.QualityCheck, all?.QualityStale),
                    AreaMapStats.Net => Providers(when) is { } now && Providers(when.AddMonths(-1)) is { } before ? now - before : null,
                    _ => null,
                };
            }
        }

        var shortages = level == "county"
            ? (await connection.QueryAsync<(string Fips, string Discipline)>(new CommandDefinition(
                $"SELECT s.county_fips, s.discipline FROM county_shortage s {(state is null ? "" : "JOIN county c ON c.county_fips = s.county_fips WHERE c.state = @state")}",
                new { state }, cancellationToken: ct)))
            .ToLookup(s => s.Fips, s => s.Discipline)
            : null;

        var perPerson = stat is AreaMapStats.Per100k or AreaMapStats.NewPer100k;
        var result = areas.Select(a =>
        {
            var value = values.TryGetValue(a.Id, out var v) ? v : monthly ? null : 0;
            if (perPerson)
            {
                value = value is { } n && a.Population is > 0 ? n * 100_000.0 / a.Population.Value : null;
            }

            return new AreaMapValue(a.Id, a.Name, a.State, value, a.Population, shortages is null ? [] : [.. shortages[a.Id].Order(StringComparer.Ordinal)]);
        }).ToList();
        return new AreaMapData(level, state, stat, label, cat, sub, m, months, result);
    }

    private sealed record MapRow(string Id, string Category, DateTime Month, int? Providers, int? NewProviders, int? Deactivated, int? MovedIn,
        int? MovedOut, int? QualityCurrent, int? QualityCheck, int? QualityStale);

    // The providers behind one count: (FROM … WHERE …, the NPI column) of each kind. "new" is worked out as the loader counts it
    // (NPIs issued in the month whose holders practise in the area now); the US's deactivations come from CMS's dates; the
    // other kinds are the loader's recorded events.
    private static (string From, string Npi) ListSql(string type, string kind, bool category) => kind switch
    {
        AreaChangeKinds.New => ($"provider p WHERE p.enumeration_date >= @month AND p.enumeration_date < @next AND {InAreaSql(type)}{(category ? " AND " + InCategorySql : "")}", "p.npi"),
        AreaChangeKinds.Deactivated when type == "us" => ("provider_deactivated d WHERE d.deactivation_date >= @month AND d.deactivation_date < @next", "d.npi"),
        _ => ("area_month_event e WHERE e.area_type = @type AND e.area_id = @id AND e.month = @month AND e.kind = @kind", "e.npi"),
    };

    /// <summary>True when the area has lists of that kind (the US counts no moves).</summary>
    public static bool HasKind(string type, string kind) =>
        AreaChangeKinds.IsKnown(kind) && !(type == "us" && kind is AreaChangeKinds.MovedIn or AreaChangeKinds.MovedOut);

    /// <summary>
    /// One page (<see cref="PageSize"/> NPIs, by NPI) of the providers behind a change count: <paramref name="kind"/> is one of
    /// <see cref="AreaChangeKinds"/>; <paramref name="category"/> narrows "new" (the other kinds count every provider). Null for an
    /// unknown area or kind. Deactivated NPIs come with their last name on file and deactivation date.
    /// </summary>
    public async Task<AreaChangePage?> GetChangesAsync(string? state, string? county, string kind, DateOnly month, string? category, int page,
        CancellationToken ct)
    {
        if (Area(state, county) is not (string type, string id) || !HasKind(type, kind))
        {
            return null;
        }

        var cat = kind == AreaChangeKinds.New ? SpecialtyCategories.Default.Find(category)?.Name ?? "" : "";
        var (from, npi) = ListSql(type, kind, cat.Length > 0);
        page = Math.Max(1, page);
        var param = Param(type, id, kind, month, cat, page);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT COUNT(*) FROM {from}", param, cancellationToken: ct));
        var npis = (await connection.QueryAsync<string>(new CommandDefinition(
            $"SELECT {npi} FROM {from} ORDER BY {npi} LIMIT @limit OFFSET @offset", param, cancellationToken: ct))).ToList();
        return new AreaChangePage(kind, month, (int)total, page, PageSize, await EntriesAsync(connection, kind, npis, ct));
    }

    /// <summary>Every NPI behind a change count, by NPI (for the CSV); empty for an unknown area or kind.</summary>
    public async Task<IReadOnlyList<string>> GetChangeNpisAsync(string? state, string? county, string kind, DateOnly month, string? category, CancellationToken ct)
    {
        if (Area(state, county) is not (string type, string id) || !HasKind(type, kind))
        {
            return [];
        }

        var cat = kind == AreaChangeKinds.New ? SpecialtyCategories.Default.Find(category)?.Name ?? "" : "";
        var (from, npi) = ListSql(type, kind, cat.Length > 0);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return (await connection.QueryAsync<string>(new CommandDefinition(
            $"SELECT {npi} FROM {from} ORDER BY {npi}", Param(type, id, kind, month, cat, 1), commandTimeout: 120, cancellationToken: ct))).ToList();
    }

    private static object Param(string type, string id, string kind, DateOnly month, string category, int page) => new
    {
        type, id, kind, category,
        month = month.ToDateTime(TimeOnly.MinValue),
        next = month.AddMonths(1).ToDateTime(TimeOnly.MinValue),
        limit = PageSize,
        offset = (page - 1) * PageSize,
    };

    // Deactivated NPIs are gone from the projection: their last name comes from the change log, their date from provider_deactivated.
    private static async Task<IReadOnlyList<AreaChangeEntry>> EntriesAsync(MySqlConnection connection, string kind, List<string> npis, CancellationToken ct)
    {
        if (kind != AreaChangeKinds.Deactivated || npis.Count == 0)
        {
            return npis.Select(n => new AreaChangeEntry(n, null, null)).ToList();
        }

        var rows = (await connection.QueryAsync<(string Npi, string? Name, DateTime? Date)>(new CommandDefinition(
                """
                SELECT d.npi,
                       (SELECT c.old_value FROM provider_change c WHERE c.npi = d.npi AND c.change_type IN ('deactivated', 'removed') ORDER BY c.id DESC LIMIT 1),
                       d.deactivation_date
                FROM provider_deactivated d WHERE d.npi IN @npis
                """, new { npis }, cancellationToken: ct)))
            .ToDictionary(r => r.Npi);
        return npis.Select(n => rows.TryGetValue(n, out var r)
                ? new AreaChangeEntry(n, r.Name, r.Date is { } d ? DateOnly.FromDateTime(d) : null)
                : new AreaChangeEntry(n, null, null))
            .ToList();
    }
}
