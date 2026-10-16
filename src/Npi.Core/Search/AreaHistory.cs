using System.Globalization;

namespace Npi.Core.Search;

/// <summary>
/// One month of an area's figures (docs/PRODUCT_PLAN.md step 3.3; migration 102). <see cref="Providers"/> and
/// <see cref="NewProviders"/> are for the chosen specialty category; the other counts are for every provider in the area.
/// Null means not known for that month (before its series starts, or not applicable: moves for the US).
/// </summary>
public sealed record AreaMonth(DateOnly Month, int? Providers, int? NewProviders, int? Deactivated, int? MovedIn, int? MovedOut,
    int? SpecialtyChanges, int? LicenseChanges, int? ExclusionChanges, int? QualityCurrent, int? QualityCheck, int? QualityStale)
{
    /// <summary>Providers at the month's end minus the month before; null unless both months were counted.</summary>
    public int? NetChange { get; init; }

    /// <summary>The data's as-of month: still filling as weekly files arrive.</summary>
    public bool Partial { get; init; }

    /// <summary>"derived" (worked out from today's data), "recorded" (as the loads arrived) or "archive" (past NPPES files).</summary>
    public string Basis { get; init; } = AreaHistoryMath.Derived;
}

/// <summary>The first month of each series (null before the history was first built) and the data's as-of date.</summary>
public sealed record AreaHistoryCoverage(DateOnly? DerivedFrom, DateOnly? TrackedFrom, DateOnly? ChangesFrom, DateOnly? AsOfMonth, DateOnly? AsOfDate)
{
    /// <summary>
    /// The first month of the figures from the archived monthly NPPES files (docs/PRODUCT_PLAN.md step 3.4: CMS NPPES, historical
    /// copies via NBER); null when the archive isn't loaded.
    /// </summary>
    public DateOnly? ArchiveFrom { get; init; }

    /// <summary>
    /// The first archive month counted by every practice location (the first file listing secondary ones, July 2018), as
    /// the recorded and derived series count; the archive's earlier months count primary practice locations only.
    /// </summary>
    public DateOnly? AnyLocationFrom { get; init; }

    /// <summary>The archive's last month shown: the month before the recorded series starts, or its newest file.</summary>
    public DateOnly? ArchiveTo { get; init; }

    /// <summary>True for a month whose figures come from the archive.</summary>
    public bool InArchive(DateOnly month) => ArchiveFrom is { } from && ArchiveTo is { } to && month >= from && month <= to;

    /// <summary>True when the month's providers are counted by every practice location (false: the archive's primary-only months).</summary>
    public bool EveryLocation(DateOnly month) => !InArchive(month) || AnyLocationFrom is { } any && month >= any;

    /// <summary>The archive month where the count changes from primary to every practice location (null when there is none).</summary>
    public DateOnly? DefinitionStep() => AnyLocationFrom is { } any && ArchiveFrom is { } from && any > from && InArchive(any) ? any : null;
}

/// <summary>An area's monthly figures, oldest month first.</summary>
public sealed record AreaHistory(string Type, string Id, string Name, string? State, string Category, IReadOnlyList<AreaMonth> Months,
    AreaHistoryCoverage Coverage);

/// <summary>The kinds of change an area page counts and lists (<c>area_month_event.kind</c>, plus "new").</summary>
public static class AreaChangeKinds
{
    public const string New = "new";
    public const string Deactivated = "deactivated";
    public const string MovedIn = "moved_in";
    public const string MovedOut = "moved_out";
    public const string Specialty = "specialty";
    public const string License = "license";
    public const string Exclusion = "exclusion";

    /// <summary>Every kind with its label, in the order pages show them.</summary>
    public static readonly IReadOnlyList<(string Kind, string Label)> All =
    [
        (New, "New NPIs"),
        (Deactivated, "Deactivated"),
        (MovedIn, "Moved in"),
        (MovedOut, "Moved out"),
        (Specialty, "Specialty changes"),
        (License, "License changes"),
        (Exclusion, "Exclusions & opt-outs"),
    ];

    /// <summary>The provider_change types (ChangeTypes) each counted kind gathers; the moves and deactivations come from the loader's location comparison.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> FromChangeLog = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [Specialty] = ["specialty", "taxonomy_added", "taxonomy_removed"],
        [License] = ["license_added", "license_removed", "license_status", "license_renewed", "board_action"],
        [Exclusion] = ["excluded", "exclusion_ended", "opted_out", "opt_out_renewed", "opt_out_ended"],
    };

    public static bool IsKnown(string? kind) => kind is not null && All.Any(k => k.Kind == kind);

    public static string Label(string kind) => All.FirstOrDefault(k => k.Kind == kind).Label ?? kind;

    /// <summary>SQL: the counted kind of a provider_change row (<paramref name="changeType"/> its change_type column), else NULL.</summary>
    public static string KindSql(string changeType) =>
        "CASE " + string.Concat(FromChangeLog.Select(k => $"WHEN {changeType} IN ({string.Join(", ", k.Value.Select(v => $"'{v}'"))}) THEN '{k.Key}' ")) + "END";

    /// <summary>Every change_type counted, as an SQL list.</summary>
    public static string ChangeTypeSqlList { get; } = string.Join(", ", FromChangeLog.Values.SelectMany(v => v).Select(v => $"'{v}'"));
}

/// <summary>The month arithmetic of area histories: buckets, the frozen months, and filling a series' gaps.</summary>
public static class AreaHistoryMath
{
    public const string Derived = "derived";
    public const string Recorded = "recorded";
    public const string Archive = "archive";

    /// <summary>Months the first build works out from today's data (NPIs issued per month, US deactivations), the as-of month included.</summary>
    public const int DerivedMonths = 60;

    /// <summary>The first day of the month of <paramref name="date"/>.</summary>
    public static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    public static DateOnly MonthOf(DateTime date) => new(date.Year, date.Month, 1);

    /// <summary>The first month a build recomputes: the month before the as-of month. Earlier months are frozen.</summary>
    public static DateOnly OpenFrom(DateOnly asOfMonth) => asOfMonth.AddMonths(-1);

    /// <summary>The month an event dated <paramref name="date"/> counts in: its own, or the first open month if that one is frozen (or no date: the as-of month).</summary>
    public static DateOnly EventMonth(DateOnly? date, DateOnly openFrom, DateOnly asOfMonth) =>
        date is not { } d ? asOfMonth : MonthOf(d) < openFrom ? openFrom : MonthOf(d);

    /// <summary>The first month of the derived series for a first build in <paramref name="asOfMonth"/>.</summary>
    public static DateOnly DerivedFrom(DateOnly asOfMonth) => asOfMonth.AddMonths(1 - DerivedMonths);

    /// <summary>"2026-10" → October 2026; null for anything else (a day, a year out of range).</summary>
    public static DateOnly? ParseMonth(string? text) =>
        text is { Length: 7 } && DateOnly.TryParseExact(text + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
        && month.Year is >= 2005 and <= 2100
            ? month
            : null;

    public static string MonthKey(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>"October 2026".</summary>
    public static string MonthName(DateOnly month) => month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>A stored row of area_month_stat (category '' or the chosen one).</summary>
    public sealed record StatRow(string Category, DateOnly Month, int Basis, int? Providers, int? NewProviders, int? Deactivated, int? MovedIn,
        int? MovedOut, int? SpecialtyChanges, int? LicenseChanges, int? ExclusionChanges, int? QualityCurrent, int? QualityCheck, int? QualityStale);

    /// <summary>
    /// Every month from the first series start (or stored row) to the as-of month, gaps filled: inside a series' months a missing
    /// count is 0, outside it null. Providers and the quality mix are snapshots: known only in months a build stored them (a
    /// category without a row in such a month has 0 providers). Moves are not counted for the US.
    /// </summary>
    public static IReadOnlyList<AreaMonth> Fill(IReadOnlyList<StatRow> rows, AreaHistoryCoverage coverage, string type, string category)
    {
        var all = rows.Where(r => r.Category.Length == 0).ToDictionary(r => r.Month);
        var selected = category.Length == 0 ? all : rows.Where(r => r.Category == category).ToDictionary(r => r.Month);
        var starts = new[] { coverage.ArchiveFrom, coverage.DerivedFrom, coverage.TrackedFrom, coverage.ChangesFrom }.OfType<DateOnly>()
            .Concat(rows.Select(r => r.Month)).ToList();
        if (starts.Count == 0)
        {
            return [];
        }

        var first = starts.Min();
        var last = new[] { coverage.AsOfMonth }.OfType<DateOnly>().Concat(rows.Select(r => r.Month)).DefaultIfEmpty(first).Max();
        var us = type == "us";
        static bool From(DateOnly month, DateOnly? start) => start is { } s && month >= s;

        var months = new List<AreaMonth>();
        int? previousProviders = null;
        var previousEvery = true;
        for (var m = first; m <= last; m = m.AddMonths(1))
        {
            all.TryGetValue(m, out var a);
            selected.TryGetValue(m, out var s);
            var counted = a?.Providers is not null;
            int? providers = counted ? s?.Providers ?? 0 : null;
            int? Count(Func<StatRow, int?> value, bool known) => known ? (a is null ? 0 : value(a) ?? 0) : a is null ? null : value(a);
            var tracked = From(m, coverage.TrackedFrom);
            // The archive (step 3.4): new NPIs and deactivations every month (a month without a file gets them from the next one);
            // moves and specialty changes in the months with a file, after the first.
            var archived = coverage.InArchive(m);
            var compared = archived && counted && m > coverage.ArchiveFrom;
            var basis = (a ?? s)?.Basis switch
            {
                3 => Archive,
                2 => Recorded,
                1 => Derived,
                _ => archived ? Archive : tracked ? Recorded : Derived,
            };
            months.Add(new AreaMonth(m, providers,
                archived || From(m, coverage.DerivedFrom) ? s?.NewProviders ?? 0 : s?.NewProviders,
                archived ? a?.Deactivated ?? 0 : Count(r => r.Deactivated, us ? From(m, coverage.DerivedFrom) : tracked),
                us ? null : archived ? (compared ? a?.MovedIn ?? 0 : null) : Count(r => r.MovedIn, tracked),
                us ? null : archived ? (compared ? a?.MovedOut ?? 0 : null) : Count(r => r.MovedOut, tracked),
                archived ? (compared ? a?.SpecialtyChanges ?? 0 : null) : Count(r => r.SpecialtyChanges, From(m, coverage.ChangesFrom)),
                Count(r => r.LicenseChanges, From(m, coverage.ChangesFrom)),
                Count(r => r.ExclusionChanges, From(m, coverage.ChangesFrom)),
                a?.QualityCurrent, a?.QualityCheck, a?.QualityStale)
            {
                // Not where the definition changes (the archive counts primary practice locations before July 2018, every one after).
                NetChange = providers is { } now && previousProviders is { } before && previousEvery == coverage.EveryLocation(m) ? now - before : null,
                Partial = coverage.AsOfMonth == m,
                Basis = basis,
            });
            previousProviders = providers;
            previousEvery = coverage.EveryLocation(m);
        }

        return months;
    }

    /// <summary>The share of <paramref name="part"/> in the record-quality mix, as a percentage; null when the mix is unknown or empty.</summary>
    public static double? QualityShare(int? part, int? current, int? check, int? stale)
    {
        var total = (current ?? 0) + (check ?? 0) + (stale ?? 0);
        return part is null || total == 0 ? null : part.Value * 100.0 / total;
    }
}

/// <summary>The statistics an area map can colour by (docs/PRODUCT_PLAN.md step 3.3).</summary>
public static class AreaMapStats
{
    public const string Per100k = "per100k";
    public const string Providers = "providers";
    public const string New = "new";
    public const string NewPer100k = "newPer100k";
    public const string Net = "net";
    public const string MovedIn = "movedIn";
    public const string MovedOut = "movedOut";
    public const string Deactivated = "deactivated";
    public const string Stale = "stale";

    /// <summary>Every statistic: its label, whether it is for one month, and whether a specialty narrows it (else: every provider).</summary>
    public static readonly IReadOnlyList<(string Stat, string Label, bool Monthly, bool BySpecialty)> All =
    [
        (Per100k, "Providers per 100,000 people", false, true),
        (Providers, "Providers", false, true),
        (New, "New NPIs in the month", true, true),
        (NewPer100k, "New NPIs per 100,000 people", true, true),
        (Net, "Net change in the month", true, true),
        (MovedIn, "Moved in during the month", true, false),
        (MovedOut, "Moved out during the month", true, false),
        (Deactivated, "Deactivated in the month", true, false),
        (Stale, "Share of stale records (%)", true, false),
    ];

    public static bool IsKnown(string? stat) => stat is not null && All.Any(s => s.Stat == stat);

    public static (string Stat, string Label, bool Monthly, bool BySpecialty) Get(string stat) => All.First(s => s.Stat == stat);
}

/// <summary>A request for map values, checked (<see cref="Parse"/>): the same rules for the site's /area/map.json and the API.</summary>
public sealed record AreaMapQuery(string Level, string? State, string Stat, string? Category, string? Subcategory, DateOnly? Month)
{
    /// <summary>
    /// Parses the query parameters: <c>level</c> state | county (default: county with a state, else state), <c>state</c> (a
    /// state's counties only), <c>stat</c> (<see cref="AreaMapStats"/>, default per100k), <c>category</c> and <c>subcategory</c>
    /// (plain specialty names), <c>month</c> (yyyy-MM). Errors are keyed by parameter name.
    /// </summary>
    public static (AreaMapQuery? Query, Dictionary<string, string[]> Errors) Parse(string? level, string? state, string? stat, string? category,
        string? subcategory, string? month)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        state = string.IsNullOrWhiteSpace(state) ? null : state.Trim().ToUpperInvariant();
        level = string.IsNullOrWhiteSpace(level) ? state is null ? "state" : "county" : level.Trim().ToLowerInvariant();
        stat = string.IsNullOrWhiteSpace(stat) ? AreaMapStats.Per100k : stat.Trim();
        if (level is not ("state" or "county"))
        {
            errors["level"] = ["One of state, county."];
        }

        if (state is not null && !GeographyCatalog.AllStateNames.ContainsKey(state))
        {
            errors["state"] = ["A two-letter state or territory code."];
        }
        else if (state is not null && level == "state")
        {
            errors["state"] = ["A state narrows a county map: send level=county."];
        }

        if (!AreaMapStats.IsKnown(stat))
        {
            errors["stat"] = [$"One of {string.Join(", ", AreaMapStats.All.Select(s => s.Stat))}."];
        }

        var specialty = SpecialtyCategories.Default.Find(category);
        if (!string.IsNullOrWhiteSpace(category) && specialty is null)
        {
            errors["category"] = ["Not a specialty category (see /api/v1/taxonomy/categories)."];
        }
        else if (!string.IsNullOrWhiteSpace(subcategory) && specialty?.Find(subcategory) is null)
        {
            errors["subcategory"] = ["Not a subcategory of the category."];
        }

        DateOnly? parsed = null;
        if (!string.IsNullOrWhiteSpace(month) && (parsed = AreaHistoryMath.ParseMonth(month.Trim())) is null)
        {
            errors["month"] = ["A month as yyyy-MM, e.g. 2026-10."];
        }

        return errors.Count > 0
            ? (null, errors)
            : (new AreaMapQuery(level, state, stat, specialty?.Name, specialty?.Find(subcategory)?.Name, parsed), errors);
    }
}

/// <summary>One area on a map: its value for the chosen statistic (null when not known), population and shortage areas (county maps).</summary>
public sealed record AreaMapValue(string Id, string Name, string? State, double? Value, long? Population, IReadOnlyList<string> Shortages);

/// <summary>The values of one statistic for every state, or every county (of one state, or nationwide).</summary>
public sealed record AreaMapData(string Level, string? State, string Stat, string StatLabel, string Category, string Subcategory, DateOnly? Month,
    IReadOnlyList<DateOnly> Months, IReadOnlyList<AreaMapValue> Values);

/// <summary>A provider in an area's list of changes: the NPI, and for a deactivated NPI its last name on file and the date.</summary>
public sealed record AreaChangeEntry(string Npi, string? Name, DateOnly? DeactivationDate);

/// <summary>One page of the providers behind a change count.</summary>
public sealed record AreaChangePage(string Kind, DateOnly Month, int Total, int Page, int PageSize, IReadOnlyList<AreaChangeEntry> Entries);
