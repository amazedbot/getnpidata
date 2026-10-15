namespace Npi.Loader.History;

/// <summary>
/// Which archived months to look for, process or record as Missing (docs/PRODUCT_PLAN.md step 3.4), from each month's status
/// in history_period. Months go strictly in order: once a month is Completed, an earlier one can't be added (the later file
/// was compared with the one before it already), so an earlier month never done counts as Missing.
/// </summary>
public static class HistoryPlan
{
    public enum Append
    {
        /// <summary>The history hasn't been built yet (<c>Npi.Loader history</c> does it).</summary>
        NotStarted,

        /// <summary>The month, or a later one, is Completed.</summary>
        AlreadyDone,

        /// <summary>Months between the last Completed one and this one aren't done: <c>history</c> adds them first.</summary>
        Blocked,

        /// <summary>The month comes next: add it.</summary>
        Next,
    }

    public static DateOnly? LastCompleted(IReadOnlyDictionary<DateOnly, string> statuses) =>
        statuses.Where(s => s.Value == HistoryPeriodStatus.Completed).Select(s => (DateOnly?)s.Key).Max();

    /// <summary>The months not to look for: Completed or Missing ones, and every month up to the last Completed one.</summary>
    public static HashSet<DateOnly> Skip(IReadOnlyDictionary<DateOnly, string> statuses)
    {
        var skip = statuses.Where(s => s.Value is HistoryPeriodStatus.Completed or HistoryPeriodStatus.Missing).Select(s => s.Key).ToHashSet();
        if (LastCompleted(statuses) is { } last)
        {
            skip.UnionWith(HistoryCatalog.Months(HistoryCatalog.FirstPeriod, last));
        }

        return skip;
    }

    /// <summary>Months before the last Completed one that were never done (to record as Missing).</summary>
    public static IReadOnlyList<DateOnly> Superseded(IReadOnlyDictionary<DateOnly, string> statuses) =>
        LastCompleted(statuses) is { } last
            ? HistoryCatalog.Months(HistoryCatalog.FirstPeriod, last)
                .Where(m => !statuses.TryGetValue(m, out var s) || s is not (HistoryPeriodStatus.Completed or HistoryPeriodStatus.Missing)).ToList()
            : [];

    /// <summary>
    /// Months looked for that have no file, up to the newest month with one (a later month may not be published yet): to record
    /// as Missing.
    /// </summary>
    public static IReadOnlyList<DateOnly> Missing(IReadOnlySet<DateOnly> found, IReadOnlySet<DateOnly> skip, DateOnly? lastCompleted)
    {
        var newest = found.Count > 0 ? found.Max() : lastCompleted;
        return newest is { } n
            ? HistoryCatalog.Months(HistoryCatalog.FirstPeriod, n).Where(m => !skip.Contains(m) && !found.Contains(m)).ToList()
            : [];
    }

    /// <summary>Whether <c>run</c>'s monthly file for <paramref name="month"/> can be added now; <paramref name="first"/> = the months that come first.</summary>
    public static Append CanAppend(IReadOnlyDictionary<DateOnly, string> statuses, DateOnly month, out IReadOnlyList<DateOnly> first)
    {
        first = [];
        if (LastCompleted(statuses) is not { } last)
        {
            return Append.NotStarted;
        }

        if (month <= last)
        {
            return Append.AlreadyDone;
        }

        first = HistoryCatalog.Months(last.AddMonths(1), month.AddMonths(-1))
            .Where(m => !statuses.TryGetValue(m, out var s) || s != HistoryPeriodStatus.Missing).ToList();
        return first.Count > 0 ? Append.Blocked : Append.Next;
    }
}
