using System.Runtime.InteropServices;

namespace Npi.Loader.History;

/// <summary>What one file changed in the history (docs/PRODUCT_PLAN.md step 3.4).</summary>
/// <param name="Changed">The NPIs with a new version from this file (their new version is in the state).</param>
/// <param name="LocationsChanged">Active NPIs whose secondary locations changed (of <see cref="Moved"/>).</param>
/// <param name="WithLocations">Active NPIs with secondary locations after the file.</param>
public sealed record PeriodDiff(DateOnly DataDate, HistoryFileSummary File, IReadOnlyList<int> Changed, int NewNpis, int Moved, int SpecialtyChanged,
    int Deactivated, int Gone, int Reactivated, int Active, int LocationsChanged = 0, int WithLocations = 0)
{
    public int Versions => Changed.Count;
}

/// <summary>
/// Every NPI's current history version, in memory (~10M NPIs, ~60 bytes each with the dictionary), compared with each file
/// in turn: a row whose fields differ, a new NPI, or an active NPI missing from the file (before November 2012 a deactivated
/// NPI simply drops out: <see cref="HistoryStatus.Gone"/>) gets a new version starting at the file's data date. A stopped
/// run starts again from the open versions in MySQL (<see cref="Load"/>).
/// </summary>
public sealed class HistoryState(HistoryValues dictionary, int capacity = 0)
{
    private readonly Dictionary<int, NpiVersion> _npis = new(capacity);
    private ushort _sequence;

    public HistoryValues Dictionary { get; } = dictionary;

    public int Count => _npis.Count;

    /// <summary>The rows the last file listed (to check the next one isn't truncated).</summary>
    public long LastListed { get; private set; }

    public bool TryGet(int key, out NpiVersion version) => _npis.TryGetValue(key, out version);

    /// <summary>An open version read back from MySQL (resuming).</summary>
    public void Load(int key, in NpiVersion version) => _npis[key] = version;

    /// <summary>The rows the last completed file listed, read back from history_period when resuming.</summary>
    public void SetLastListed(long rows) => LastListed = rows;

    /// <summary>Every NPI's current version.</summary>
    public IEnumerable<KeyValuePair<int, NpiVersion>> All => _npis;

    /// <summary>
    /// Compares one file with the state and moves the state on to it. <paramref name="read"/> reads the file, calling back once
    /// per row. Throws when the file lists fewer than <paramref name="minRowRatio"/> of the rows the previous file did (a
    /// truncated file would otherwise make most NPIs look deactivated).
    /// </summary>
    public PeriodDiff Apply(DateOnly dataDate, Func<NppesHistoryReader.RowHandler, HistoryFileSummary> read, double minRowRatio = 0.95)
    {
        var period = dataDate.DayNumber;
        var sequence = ++_sequence;
        var changed = new List<int>(256 * 1024);
        int newNpis = 0, moved = 0, specialty = 0, deactivated = 0, reactivated = 0, locations = 0;

        void OnRow(int key, in NpiVersion fields)
        {
            ref var current = ref CollectionsMarshal.GetValueRefOrAddDefault(_npis, key, out var exists);
            var row = fields;
            if (row.LocationSet < 0)
            {
                // A file without secondary locations after one with them: each NPI keeps the set it had.
                row.LocationSet = exists && row.Status == HistoryStatus.Active ? Math.Max(current.LocationSet, 0) : 0;
            }

            if (!exists)
            {
                current = row;
                current.FromPeriod = period;
                current.Seen = sequence;
                changed.Add(key);
                newNpis++;
                return;
            }

            var again = current.Seen == sequence;
            current.Seen = sequence;
            if (current.Same(row))
            {
                return;
            }

            if (!again || current.FromPeriod != period)
            {
                if (current.Status == HistoryStatus.Active && row.Status == HistoryStatus.Active)
                {
                    moved += current.State != row.State || current.Zip != row.Zip || current.LocationSet != row.LocationSet ? 1 : 0;
                    specialty += current.Primary != row.Primary ? 1 : 0;
                    locations += current.LocationSet != row.LocationSet ? 1 : 0;
                }

                deactivated += current.Status == HistoryStatus.Active && row.Status != HistoryStatus.Active ? 1 : 0;
                reactivated += current.Status != HistoryStatus.Active && row.Status == HistoryStatus.Active ? 1 : 0;
                changed.Add(key);
            }

            // A second row for the NPI in the same file replaces the first (both start at this file).
            current = row;
            current.FromPeriod = period;
            current.Seen = sequence;
        }

        var file = read(OnRow);
        if (LastListed > 0 && file.Rows < LastListed * minRowRatio)
        {
            throw new InvalidDataException($"{file.EntryName} lists {file.Rows:N0} NPIs, under {minRowRatio:P0} of the {LastListed:N0} the previous file listed: truncated?");
        }

        // Active NPIs the file no longer lists: deactivated (inferred).
        var gone = new List<int>();
        foreach (var (key, version) in _npis)
        {
            if (version.Seen != sequence && version.Status == HistoryStatus.Active)
            {
                gone.Add(key);
            }
        }

        foreach (var key in gone)
        {
            ref var current = ref CollectionsMarshal.GetValueRefOrNullRef(_npis, key);
            var seen = current.Seen;
            current = NpiVersion.GoneVersion();
            current.FromPeriod = period;
            current.Seen = seen;
            changed.Add(key);
        }

        int active = 0, withLocations = 0;
        foreach (var version in _npis.Values)
        {
            active += version.Status == HistoryStatus.Active ? 1 : 0;
            withLocations += version.Status == HistoryStatus.Active && version.LocationSet > 0 ? 1 : 0;
        }

        LastListed = file.Rows;
        return new PeriodDiff(dataDate, file, changed, newNpis, moved, specialty, deactivated + gone.Count, gone.Count, reactivated, active, locations,
            withLocations);
    }
}
