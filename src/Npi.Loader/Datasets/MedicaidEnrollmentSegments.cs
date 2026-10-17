using System.Globalization;
using Npi.Core;

namespace Npi.Loader.Datasets;

/// <summary>One enrollment period of an NPI in a state's Medicaid program (<c>medicaid_enrollment_period</c>).</summary>
/// <param name="From">Null when the state gave no real start date (1900-01-01, 1970-01-01 and the like).</param>
/// <param name="Until">Null when still enrolled at the end of the data.</param>
/// <param name="EndedStatus">The termination that ended the period, in T-MSIS's words, when one follows it.</param>
public sealed record MedicaidEnrollmentRow(string Npi, string State, int Period, DateOnly? From, DateOnly? Until, string? EndedStatus);

/// <summary>What <see cref="MedicaidEnrollmentSegments.Summarize"/> read: the months covered, the lines, those skipped for their NPI, and the NPIs and periods written.</summary>
public sealed record MedicaidEnrollmentSummary(DateOnly FirstMonth, DateOnly LastMonth, long Lines, long SkippedNpis, long Periods,
    IReadOnlyDictionary<string, int> NpisByState);

/// <summary>
/// HHS's "Medicaid Provider Enrollment Segments" (T-MSIS; docs/PRODUCT_PLAN.md step 3.7): one CSV row per provider, state and
/// enrollment segment (NPI, STATE_CD, ENRL_START_DT, ENRL_END_DT, ENRL_STATUS_CD/DESC, ENRL_PLAN_CD/DESC, PRVDR_TYPE/DESC), ~66M rows
/// in 11.5 GB for January 2018 to December 2024; many states send a segment per month, and a Medicaid and a CHIP row for each.
/// Summarized per NPI and state into enrollment periods: the active segments (status "ACTIVE - …") joined where they overlap, touch
/// or are at most <see cref="GapDays"/> days apart; a termination ("TERM - …") starting inside or right after a period ends it there
/// and names why. Rows whose NPI is blank, 0000000000, not ten digits or fails the check digit are skipped (the file doesn't
/// validate them). Every segment is kept in memory as 12 bytes (sorted in chunks, then merged), so ~50M segments take ~0.6 GB.
/// </summary>
public static class MedicaidEnrollmentSegments
{
    /// <summary>Active segments this many days apart or less are one period (a late renewal isn't a break).</summary>
    public const int GapDays = 31;

    // Dates as days since 1900-01-01 in 17 bits; 0 = no real date, Open = open-ended (blank, or 2258 and later).
    private static readonly DateOnly Epoch = new(1900, 1, 1);
    private const int Open = (1 << 17) - 1;
    private static readonly DateOnly FirstRealDate = new(1966, 1, 1); // Medicaid began in 1966; earlier dates are placeholders
    private static readonly DateOnly Placeholder1970 = new(1970, 1, 1);

    private const int ChunkSize = 8 << 20;

    private enum Kind : byte { Other, Active, Terminated }

    /// <summary>
    /// Reads the CSV (header first) and hands <paramref name="write"/> the enrollment periods, sorted by NPI, state and period.
    /// <paramref name="published"/> (the release date) bounds the start dates taken as the end of the data.
    /// </summary>
    public static MedicaidEnrollmentSummary Summarize(TextReader reader, DateOnly published, Action<MedicaidEnrollmentRow> write)
    {
        var header = new List<string>();
        HhsOpenData.Split(reader.ReadLine() ?? throw new InvalidDataException("The enrollment segments file is empty."), header);
        const string what = "The enrollment segments file";
        int cNpi = HhsOpenData.Column(header, "NPI", what), cState = HhsOpenData.Column(header, "STATE_CD", what),
            cStart = HhsOpenData.Column(header, "ENRL_START_DT", what), cEnd = HhsOpenData.Column(header, "ENRL_END_DT", what),
            cCode = HhsOpenData.Column(header, "ENRL_STATUS_CD", what), cDesc = HhsOpenData.Column(header, "ENRL_STATUS_DESC", what);
        var needed = new[] { cNpi, cState, cStart, cEnd, cCode, cDesc }.Max() + 1;

        var states = new List<string>();
        var stateIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var statuses = new List<string>();
        var statusIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var chunks = new List<(ulong[] Keys, uint[] Ends, int Count)>();
        var keys = new ulong[ChunkSize];
        var ends = new uint[ChunkSize];
        var count = 0;
        var fields = new List<string>();
        long lines = 0, skipped = 0;
        ulong lastKey = ulong.MaxValue;
        uint lastEnd = uint.MaxValue;
        int minEnd = Open, maxStart = 0;
        var publishedDays = Days(published);

        while (reader.ReadLine() is { } line)
        {
            lines++;
            HhsOpenData.Split(line, fields);
            if (fields.Count < needed)
            {
                throw new InvalidDataException($"{what}: line {lines + 1:N0} has {fields.Count} fields, not {header.Count}.");
            }

            var npi = fields[cNpi].Trim();
            if (!InputFormats.HasValidCheckDigit(npi) || npi[0] is not ('1' or '2'))
            {
                skipped++;
                continue;
            }

            var state = fields[cState].Trim().ToUpperInvariant();
            if (state.Length != 2)
            {
                skipped++;
                continue;
            }

            if (!stateIndex.TryGetValue(state, out var s))
            {
                if (states.Count == 63)
                {
                    throw new InvalidDataException($"{what} names more than 63 states and territories.");
                }

                stateIndex[state] = s = states.Count;
                states.Add(state);
            }

            var status = fields[cDesc].Trim() is { Length: > 0 } desc ? desc : fields[cCode].Trim();
            if (!statusIndex.TryGetValue(status, out var st))
            {
                // Six bits: the 64th and later statuses share the last slot (T-MSIS has ~40).
                st = statuses.Count < 63 ? statuses.Count : 63;
                if (statuses.Count < 64)
                {
                    statuses.Add(statuses.Count < 63 ? status : "OTHER");
                }

                statusIndex[status] = st;
            }

            var start = ParseDays(fields[cStart], isEnd: false);
            var end = ParseDays(fields[cEnd], isEnd: true);
            if (start > 0 && start <= publishedDays)
            {
                maxStart = Math.Max(maxStart, start);
            }

            if (end != Open && end >= Days(new DateOnly(2000, 1, 1)))
            {
                minEnd = Math.Min(minEnd, end);
            }

            var key = (ulong)(long.Parse(npi, CultureInfo.InvariantCulture) - 1_000_000_000L) << 33 | (ulong)s << 27 | (ulong)start << 10 | (ulong)st << 4;
            if (key == lastKey && (uint)end == lastEnd)
            {
                continue; // the CHIP twin of the Medicaid row before it
            }

            lastKey = key;
            lastEnd = (uint)end;
            keys[count] = key;
            ends[count] = (uint)end;
            if (++count == ChunkSize)
            {
                Array.Sort(keys, ends, 0, count);
                chunks.Add((keys, ends, count));
                keys = new ulong[ChunkSize];
                ends = new uint[ChunkSize];
                count = 0;
            }
        }

        Array.Sort(keys, ends, 0, count);
        chunks.Add((keys, ends, count));

        var kinds = statuses.Select(x => x.StartsWith("ACTIVE", StringComparison.OrdinalIgnoreCase) ? Kind.Active
            : x.StartsWith("TERM", StringComparison.OrdinalIgnoreCase) ? Kind.Terminated : Kind.Other).ToArray();
        var byState = new Dictionary<string, int>(StringComparer.Ordinal);
        long periodCount = 0;
        void Write(MedicaidEnrollmentRow row)
        {
            periodCount++;
            if (row.Period == 1)
            {
                byState[row.State] = byState.GetValueOrDefault(row.State) + 1;
            }

            write(row);
        }

        var dataEnd = maxStart == 0 ? publishedDays : Days(MonthEnd(Date(maxStart)));
        var group = new List<(int Start, int End, int Status)>();
        ulong groupKey = ulong.MaxValue;
        foreach (var (key, end) in Merge(chunks))
        {
            var k = key >> 27;
            if (k != groupKey && group.Count > 0)
            {
                Periods(groupKey, group, kinds, statuses, states, dataEnd, Write);
                group.Clear();
            }

            groupKey = k;
            group.Add(((int)(key >> 10 & ((1 << 17) - 1)), (int)end, (int)(key >> 4 & 63)));
        }

        if (group.Count > 0)
        {
            Periods(groupKey, group, kinds, statuses, states, dataEnd, Write);
        }

        var first = minEnd == Open ? new DateOnly(2018, 1, 1) : Date(minEnd);
        return new MedicaidEnrollmentSummary(new DateOnly(first.Year, first.Month, 1), new DateOnly(Date(dataEnd).Year, Date(dataEnd).Month, 1),
            lines, skipped, periodCount, byState);
    }

    // One NPI and state: its active segments (sorted by start) joined into periods, each cut and named by a termination after it.
    private static void Periods(ulong npiState, List<(int Start, int End, int Status)> segments, Kind[] kinds, List<string> statuses,
        List<string> states, int dataEnd, Action<MedicaidEnrollmentRow> write)
    {
        var periods = new List<(int Start, int End, string? Ended)>();
        foreach (var x in segments)
        {
            if (kinds[x.Status] != Kind.Active)
            {
                continue;
            }

            if (periods.Count > 0 && (periods[^1].End == Open || x.Start <= periods[^1].End + GapDays))
            {
                var p = periods[^1];
                periods[^1] = (p.Start, Math.Max(p.End, x.End), null);
            }
            else
            {
                periods.Add((x.Start, x.End, null));
            }
        }

        if (periods.Count == 0)
        {
            return;
        }

        for (var i = 0; i < periods.Count; i++)
        {
            var p = periods[i];
            foreach (var t in segments)
            {
                if (kinds[t.Status] == Kind.Terminated && t.Start > p.Start && (p.End == Open || t.Start <= p.End + GapDays))
                {
                    periods[i] = (p.Start, Math.Min(p.End, t.Start - 1), statuses[t.Status]);
                    break;
                }
            }
        }

        var npi = ((long)(npiState >> 6) + 1_000_000_000L).ToString(CultureInfo.InvariantCulture);
        var state = states[(int)(npiState & 63)];
        for (var i = 0; i < periods.Count; i++)
        {
            var (start, end, ended) = periods[i];
            write(new MedicaidEnrollmentRow(npi, state, i + 1, start == 0 ? null : Date(start), end == Open || end > dataEnd ? null : Date(end),
                end == Open || end > dataEnd ? null : ended));
        }
    }

    // The sorted chunks as one sorted sequence.
    private static IEnumerable<(ulong Key, uint End)> Merge(List<(ulong[] Keys, uint[] Ends, int Count)> chunks)
    {
        var queue = new PriorityQueue<int, (ulong, uint)>();
        var at = new int[chunks.Count];
        for (var c = 0; c < chunks.Count; c++)
        {
            if (chunks[c].Count > 0)
            {
                queue.Enqueue(c, (chunks[c].Keys[0], chunks[c].Ends[0]));
            }
        }

        while (queue.TryDequeue(out var c, out var item))
        {
            yield return item;
            if (++at[c] < chunks[c].Count)
            {
                queue.Enqueue(c, (chunks[c].Keys[at[c]], chunks[c].Ends[at[c]]));
            }
        }
    }

    private static int Days(DateOnly date) => date.DayNumber - Epoch.DayNumber;

    private static DateOnly Date(int days) => DateOnly.FromDayNumber(Epoch.DayNumber + days);

    private static DateOnly MonthEnd(DateOnly d) => new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    // yyyy-MM-dd (the file's form); a blank end, or one from 2258 on, is open; a missing or placeholder start is 0.
    private static int ParseDays(string value, bool isEnd)
    {
        var v = value.Trim();
        if (v.Length >= 10 && DateOnly.TryParseExact(v.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            if (!isEnd && (date < FirstRealDate || date == Placeholder1970))
            {
                return 0;
            }

            var days = Days(date);
            return days < 0 ? 0 : days >= Open ? Open : days;
        }

        return isEnd ? Open : 0;
    }
}
