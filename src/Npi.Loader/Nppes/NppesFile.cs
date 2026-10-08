using System.Globalization;
using System.Text.RegularExpressions;

namespace Npi.Loader.Nppes;

public enum NppesFileKind
{
    Monthly,
    Weekly,
    Deactivation,
}

/// <summary>
/// A downloadable NPPES zip. <see cref="FileDate"/> orders files of the same kind: the first of the
/// month for monthlies, the end of the covered week for weeklies, the report date for deactivations.
/// </summary>
public sealed record NppesFile(string FileName, NppesFileKind Kind, DateOnly FileDate, DateOnly? WeekStart = null)
{
    public override string ToString() => FileName;
}

/// <summary>Classifies zip names published on NPI_Files.html (CLAUDE.md §4, §7 Stage 1.1). V2 only.</summary>
public static partial class NppesFileClassifier
{
    [GeneratedRegex(@"^NPPES_Data_Dissemination_(?<month>[A-Za-z]+)_(?<year>\d{4})_V2\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex MonthlyName();

    [GeneratedRegex(@"^NPPES_Data_Dissemination_(?<from>\d{6})_(?<to>\d{6})_Weekly_V2\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex WeeklyName();

    [GeneratedRegex(@"^NPPES_Deactivated_NPI_Report_(?<date>\d{6})_V2\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex DeactivationName();

    /// <summary>Returns the file, or null when the name is not a recognised V2 NPPES zip.</summary>
    public static NppesFile? Classify(string fileName)
    {
        var m = MonthlyName().Match(fileName);
        if (m.Success)
        {
            return DateTime.TryParseExact($"{m.Groups["month"].Value} {m.Groups["year"].Value}", "MMMM yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
                ? new NppesFile(fileName, NppesFileKind.Monthly, DateOnly.FromDateTime(month))
                : null;
        }

        m = WeeklyName().Match(fileName);
        if (m.Success)
        {
            return TryParseMmddyy(m.Groups["from"].Value, out var from) && TryParseMmddyy(m.Groups["to"].Value, out var to) && from <= to
                ? new NppesFile(fileName, NppesFileKind.Weekly, to, from)
                : null;
        }

        m = DeactivationName().Match(fileName);
        if (m.Success)
        {
            return TryParseMmddyy(m.Groups["date"].Value, out var date)
                ? new NppesFile(fileName, NppesFileKind.Deactivation, date)
                : null;
        }

        return null;
    }

    private static bool TryParseMmddyy(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "MMddyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}

/// <summary>What a file inside an NPPES data zip contains.</summary>
public enum NppesEntryKind
{
    Unknown,
    NpiData,
    OtherName,
    PracticeLocation,
    Endpoint,
    FileHeader,
    Readme,
}

public static class NppesEntryClassifier
{
    /// <summary>Classifies a zip entry by name, case-insensitively (legacy defect #7).</summary>
    public static NppesEntryKind Classify(string entryName)
    {
        var name = Path.GetFileName(entryName);
        if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return NppesEntryKind.Readme;
        }

        if (!name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return NppesEntryKind.Unknown;
        }

        if (name.EndsWith("_fileheader.csv", StringComparison.OrdinalIgnoreCase))
        {
            return NppesEntryKind.FileHeader;
        }

        return name switch
        {
            _ when name.StartsWith("npidata_pfile_", StringComparison.OrdinalIgnoreCase) => NppesEntryKind.NpiData,
            _ when name.StartsWith("othername_pfile_", StringComparison.OrdinalIgnoreCase) => NppesEntryKind.OtherName,
            _ when name.StartsWith("pl_pfile_", StringComparison.OrdinalIgnoreCase) => NppesEntryKind.PracticeLocation,
            _ when name.StartsWith("endpoint_pfile_", StringComparison.OrdinalIgnoreCase) => NppesEntryKind.Endpoint,
            _ => NppesEntryKind.Unknown,
        };
    }
}
