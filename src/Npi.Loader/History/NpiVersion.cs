using System.Runtime.InteropServices;

namespace Npi.Loader.History;

/// <summary>What an NPI's history version holds (docs/PRODUCT_PLAN.md step 3.4).</summary>
public enum HistoryStatus : byte
{
    /// <summary>Active (listed with its details).</summary>
    Active = 0,

    /// <summary>Listed as deactivated: from November 2012 the files list a deactivated NPI with its deactivation date only.</summary>
    Deactivated = 1,

    /// <summary>No longer in the file: before November 2012 a deactivated NPI simply dropped out, so the deactivation is inferred.</summary>
    Gone = 2,
}

/// <summary>
/// One version of an NPI's fields that matter for the history, compact (about 40 bytes) so ~10M NPIs fit in memory. Strings
/// are indexes into <see cref="HistoryValues"/> tables (0 = none); dates are <see cref="DateOnly.DayNumber"/> (0 = none).
/// <see cref="Same"/> compares the fields; <see cref="FromPeriod"/> and <see cref="Seen"/> are bookkeeping.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct NpiVersion
{
    public byte Entity;
    public HistoryStatus Status;
    public ushort State;
    public ushort Country;
    public ushort Primary;
    public int Zip;
    public int TaxonomySet;

    /// <summary>The set of secondary practice locations (<see cref="HistoryValues.LocationSet"/>; 0 = none, -1 = the file didn't list them).</summary>
    public int LocationSet;
    public int Enumeration;
    public int LastUpdate;
    public int Deactivation;
    public int Reactivation;

    /// <summary>The data date of the file this version first appeared in (DayNumber).</summary>
    public int FromPeriod;

    /// <summary>The run's sequence number of the last file that listed the NPI (to find those that dropped out).</summary>
    public ushort Seen;

    /// <summary>True when the history fields are equal (not the bookkeeping).</summary>
    public readonly bool Same(in NpiVersion other) =>
        Entity == other.Entity && Status == other.Status && State == other.State && Country == other.Country && Primary == other.Primary
        && Zip == other.Zip && TaxonomySet == other.TaxonomySet && LocationSet == other.LocationSet && Enumeration == other.Enumeration && LastUpdate == other.LastUpdate
        && Deactivation == other.Deactivation && Reactivation == other.Reactivation;

    /// <summary>The status a file row says: deactivated when it has a deactivation date and no reactivation on or after it (CLAUDE.md §6.1).</summary>
    public static HistoryStatus StatusOf(int deactivation, int reactivation) =>
        deactivation != 0 && (reactivation == 0 || reactivation < deactivation) ? HistoryStatus.Deactivated : HistoryStatus.Active;

    /// <summary>An NPI no longer listed: only the status, as a listed deactivation has only its date.</summary>
    public static NpiVersion GoneVersion() => new() { Status = HistoryStatus.Gone };

    /// <summary>The NPI stored in an int: NPIs are 10 digits from 1000000000.</summary>
    public static int Key(long npi) => checked((int)(npi - 1_000_000_000L));

    public static long Npi(int key) => key + 1_000_000_000L;

    public static DateOnly? Date(int dayNumber) => dayNumber == 0 ? null : DateOnly.FromDayNumber(dayNumber);
}
