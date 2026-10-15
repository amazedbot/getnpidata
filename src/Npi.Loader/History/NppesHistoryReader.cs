using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Npi.Loader.Csv;

namespace Npi.Loader.History;

/// <summary>What one archived NPPES file held (docs/PRODUCT_PLAN.md step 3.4).</summary>
/// <param name="DataDate">The file's own date: the end of <c>npidata_20050523-YYYYMMDD</c>, else null.</param>
public sealed record HistoryFileSummary(string EntryName, DateOnly? DataDate, int Columns, long Rows, long DeactivatedRows, long BadRows);

/// <summary>
/// Streams the npidata CSV of an NPPES monthly zip of any era (2007 to now) straight from the zip entry, never unzipping to
/// disk, and reads only the ~35 columns the history needs, found by their header names (CLAUDE.md §4's mapping: 187 columns in
/// 2007, 314 in 2011, 329 from 2012 with the taxonomy groups, 330 from 2024; V2 says "Provider Sex Code"). The entry is
/// <c>npidata_*.csv</c> (<c>npidata_pfile_*</c> from 2018), in any folder of the zip, matched case-insensitively; the header
/// files (<c>…NPIDataDisseminationFileHeader.csv</c>, <c>…FileHeader.csv</c>, <c>…_fileheader.csv</c>) are skipped.
/// </summary>
public static partial class NppesHistoryReader
{
    public const int TaxonomySlots = 15;

    [GeneratedRegex(@"npidata_(?:pfile_)?\d{8}-(?<date>\d{8})", RegexOptions.IgnoreCase)]
    private static partial Regex DataDateName();

    private const string StateColumn = "Provider_Business_Practice_Location_Address_State_Name";
    private const string PostalColumn = "Provider_Business_Practice_Location_Address_Postal_Code";
    private const string CountryColumn = "Provider_Business_Practice_Location_Address_Country_Code";

    // The field slots read, by name (after HeaderMapper.ToColumnName).
    private const int Npi = 0, Entity = 1, State = 2, Postal = 3, Country = 4, Enumeration = 5, LastUpdate = 6, Deactivation = 7, Reactivation = 8,
        FirstCode = 9, FirstSwitch = FirstCode + TaxonomySlots, SlotCount = FirstSwitch + TaxonomySlots;

    private static readonly string[] SlotColumns =
    [
        "NPI", "Entity_Type_Code", StateColumn, PostalColumn, CountryColumn, "Provider_Enumeration_Date", "Last_Update_Date", "NPI_Deactivation_Date",
        "NPI_Reactivation_Date",
        .. Enumerable.Range(1, TaxonomySlots).Select(i => $"Healthcare_Provider_Taxonomy_Code_{i}"),
        .. Enumerable.Range(1, TaxonomySlots).Select(i => $"Healthcare_Provider_Primary_Taxonomy_Switch_{i}"),
    ];

    /// <summary>A row read: the NPI's key (<see cref="NpiVersion.Key"/>) and its fields (bookkeeping fields unset).</summary>
    public delegate void RowHandler(int key, in NpiVersion fields);

    /// <summary>The npidata entry of a monthly zip: the largest <c>npidata*.csv</c> that isn't a header file.</summary>
    public static ZipArchiveEntry FindDataEntry(ZipArchive zip) =>
        zip.Entries
            .Where(e => IsDataEntry(e.FullName))
            .MaxBy(e => e.Length)
        ?? throw new InvalidDataException($"No npidata CSV in the zip (entries: {string.Join(", ", zip.Entries.Select(e => e.FullName).Take(10))}).");

    /// <summary>True for <c>npidata_….csv</c> in any folder, not a header file.</summary>
    public static bool IsDataEntry(string fullName)
    {
        var name = fullName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.StartsWith("npidata", StringComparison.OrdinalIgnoreCase)
               && name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
               && !name.Contains("fileheader", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The date at the end of <c>npidata_20050523-20161211.csv</c>, else null.</summary>
    public static DateOnly? DataDateOf(string entryName)
    {
        var match = DataDateName().Match(entryName);
        return match.Success && DateOnly.TryParseExact(match.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>The position of each field slot in the header (-1 for the NPI never: missing columns throw).</summary>
    public static int[] MapHeader(IReadOnlyList<string> headers)
    {
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
        {
            positions.TryAdd(HeaderMapper.ToColumnName(headers[i]), i);
        }

        var missing = SlotColumns.Where(c => !positions.ContainsKey(c)).ToList();
        return missing.Count > 0
            ? throw new InvalidDataException($"The npidata header ({headers.Count} columns) lacks {string.Join(", ", missing)}.")
            : SlotColumns.Select(c => positions[c]).ToArray();
    }

    /// <summary>The data date in the name of the zip's npidata CSV, without reading it.</summary>
    public static DateOnly? PeekDataDate(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return DataDateOf(FindDataEntry(zip).FullName);
    }

    /// <summary>Reads every row of the zip's npidata CSV.</summary>
    public static HistoryFileSummary Read(string zipPath, HistoryValues dictionary, RowHandler onRow, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = FindDataEntry(zip);
        using var stream = entry.Open();
        return Read(stream, entry.FullName, dictionary, onRow, ct);
    }

    /// <summary>Reads every row of an npidata CSV stream (its header row first).</summary>
    public static HistoryFileSummary Read(Stream stream, string entryName, HistoryValues dictionary, RowHandler onRow, CancellationToken ct)
    {
        var header = CsvHeader.Read(stream);
        var slots = MapHeader(header.Columns);
        // field position → slot, -1 for fields not read; the last field read, after which the row is skipped to its end.
        var slotOf = Enumerable.Repeat(-1, header.Columns.Count).ToArray();
        for (var s = 0; s < slots.Length; s++)
        {
            slotOf[slots[s]] = s;
        }

        var lastField = slots.Max();
        var parser = new Parser(dictionary, slotOf, lastField, onRow);
        var buffer = new byte[4 << 20];
        var filled = 0;
        var eof = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!eof)
            {
                while (filled < buffer.Length)
                {
                    var read = stream.Read(buffer, filled, buffer.Length - filled);
                    if (read == 0)
                    {
                        eof = true;
                        break;
                    }

                    filled += read;
                }
            }

            var used = parser.ParseRows(buffer.AsSpan(0, filled), eof);
            if (eof && used == filled)
            {
                break;
            }

            if (used == 0)
            {
                if (eof)
                {
                    throw new InvalidDataException("The last row of the npidata file is incomplete.");
                }

                // A row longer than the buffer: grow it.
                Array.Resize(ref buffer, buffer.Length * 2);
                continue;
            }

            buffer.AsSpan(used, filled - used).CopyTo(buffer);
            filled -= used;
        }

        return new HistoryFileSummary(entryName, DataDateOf(entryName), header.Columns.Count, parser.Rows, parser.DeactivatedRows, parser.BadRows);
    }

    private sealed class Parser(HistoryValues dictionary, int[] slotOf, int lastField, RowHandler onRow)
    {
        private readonly (int Start, int Length)[] _fields = new (int, int)[SlotCount];
        private readonly char[] _chars = new char[64];
        private readonly char[] _codes = new char[TaxonomySlots];

        public long Rows { get; private set; }

        public long DeactivatedRows { get; private set; }

        public long BadRows { get; private set; }

        /// <summary>Parses the complete rows in <paramref name="data"/>; returns the bytes used (the rest starts an incomplete row).</summary>
        public int ParseRows(ReadOnlySpan<byte> data, bool final)
        {
            var position = 0;
            while (position < data.Length)
            {
                var end = ParseRow(data, position, final);
                if (end < 0)
                {
                    break;
                }

                position = end;
            }

            return position;
        }

        // Returns the position after the row's line end, or -1 when the row isn't complete in data.
        private int ParseRow(ReadOnlySpan<byte> data, int start, bool final)
        {
            Array.Clear(_fields);
            var p = start;
            var field = 0;
            while (true)
            {
                if (field > lastField)
                {
                    // Skip the rest of the row: its end is a line end outside quotes, which an even number of quotes before it shows.
                    var quotes = 0;
                    while (true)
                    {
                        var nl = data[p..].IndexOf((byte)'\n');
                        if (nl < 0)
                        {
                            if (!final)
                            {
                                return -1;
                            }

                            nl = data.Length - p;
                            quotes += data.Slice(p, nl).Count((byte)'"');
                            Emit(data);
                            return data.Length;
                        }

                        quotes += data.Slice(p, nl).Count((byte)'"');
                        p += nl + 1;
                        if ((quotes & 1) == 0)
                        {
                            Emit(data);
                            return p;
                        }
                    }
                }

                // One field: quoted ("…", with "" for a quote) or plain.
                int valueStart, valueEnd, next;
                if (p < data.Length && data[p] == (byte)'"')
                {
                    var q = p + 1;
                    while (true)
                    {
                        var close = data[q..].IndexOf((byte)'"');
                        if (close < 0)
                        {
                            return -1;
                        }

                        q += close;
                        if (q + 1 < data.Length && data[q + 1] == (byte)'"')
                        {
                            q += 2;
                            continue;
                        }

                        if (q + 1 >= data.Length && !final)
                        {
                            return -1;
                        }

                        break;
                    }

                    (valueStart, valueEnd, next) = (p + 1, q, q + 1);
                }
                else
                {
                    var stop = data[p..].IndexOfAny((byte)',', (byte)'\n');
                    if (stop < 0)
                    {
                        if (!final)
                        {
                            return -1;
                        }

                        stop = data.Length - p;
                    }

                    (valueStart, valueEnd, next) = (p, p + stop, p + stop);
                }

                var slot = field < slotOf.Length ? slotOf[field] : -1;
                if (slot >= 0)
                {
                    var length = valueEnd - valueStart;
                    if (length > 0 && data[valueEnd - 1] == (byte)'\r')
                    {
                        length--;
                    }

                    _fields[slot] = (valueStart, length);
                }

                if (next >= data.Length)
                {
                    if (!final)
                    {
                        return -1;
                    }

                    Emit(data);
                    return data.Length;
                }

                var separator = data[next];
                if (separator == (byte)',')
                {
                    p = next + 1;
                    field++;
                    continue;
                }

                if (separator == (byte)'\r' && next + 1 < data.Length && data[next + 1] == (byte)'\n')
                {
                    next++;
                    separator = (byte)'\n';
                }

                if (separator == (byte)'\n')
                {
                    // A row ending before the last field read (an old file's short row): what was read counts.
                    Emit(data);
                    return next + 1;
                }

                if (separator == (byte)'\r' && next + 1 >= data.Length && !final)
                {
                    return -1;
                }

                // Text after a closing quote: not CSV as NPPES writes it. Read up to the next separator and go on.
                var resync = data[next..].IndexOfAny((byte)',', (byte)'\n');
                if (resync < 0)
                {
                    return final ? data.Length : -1;
                }

                p = next + resync;
                if (data[p] == (byte)'\n')
                {
                    Emit(data);
                    return p + 1;
                }

                p++;
                field++;
            }
        }

        private ReadOnlySpan<byte> Field(ReadOnlySpan<byte> data, int slot)
        {
            var (start, length) = _fields[slot];
            var value = length <= 0 ? [] : data.Slice(start, length);
            return value.Trim((byte)' ');
        }

        private void Emit(ReadOnlySpan<byte> data)
        {
            var npiText = Field(data, Npi);
            if (npiText.Length != 10 || !long.TryParse(npiText, NumberStyles.None, CultureInfo.InvariantCulture, out var npi) || npi < 1_000_000_000L)
            {
                // The header row of a file joined to another, or a blank line.
                if (!npiText.IsEmpty && !npiText.SequenceEqual("NPI"u8))
                {
                    BadRows++;
                }

                return;
            }

            Rows++;
            var version = new NpiVersion
            {
                Entity = Field(data, Entity) is [var e] && e is (byte)'1' or (byte)'2' ? (byte)(e - '0') : (byte)0,
                Enumeration = DateOf(Field(data, Enumeration)),
                LastUpdate = DateOf(Field(data, LastUpdate)),
                Deactivation = DateOf(Field(data, Deactivation)),
                Reactivation = DateOf(Field(data, Reactivation)),
            };
            version.Status = NpiVersion.StatusOf(version.Deactivation, version.Reactivation);
            if (version.Status == HistoryStatus.Deactivated)
            {
                DeactivatedRows++;
            }

            var country = Field(data, Country);
            version.Country = dictionary.Countries.Add(Upper(country, 2));
            version.State = dictionary.States.Add(Upper(Field(data, State), 40));
            var us = country.IsEmpty || country.SequenceEqual("US"u8) || country.SequenceEqual("us"u8);
            var postal = Field(data, Postal);
            if (us && postal.Length >= 5 && int.TryParse(postal[..5], NumberStyles.None, CultureInfo.InvariantCulture, out var zip))
            {
                version.Zip = zip + 1;
            }

            // The primary taxonomy: the first slot switched Y with a code, else slot 1 (as the projection takes it).
            var codes = 0;
            ushort primary = 0, first = 0;
            for (var i = 0; i < TaxonomySlots; i++)
            {
                var code = Field(data, FirstCode + i);
                if (code.IsEmpty)
                {
                    continue;
                }

                var id = dictionary.Codes.Add(Upper(code, 10));
                if (i == 0)
                {
                    first = id;
                }

                if (primary == 0 && Field(data, FirstSwitch + i) is [(byte)'Y' or (byte)'y'])
                {
                    primary = id;
                }

                _codes[codes++] = (char)id;
            }

            version.Primary = primary != 0 ? primary : first;
            version.TaxonomySet = dictionary.Set(_codes.AsSpan(0, codes));
            onRow(NpiVersion.Key(npi), version);
        }

        // An ASCII field as upper-case chars (NPPES text here is ASCII: states, countries, codes), at most maxLength.
        private ReadOnlySpan<char> Upper(ReadOnlySpan<byte> value, int maxLength)
        {
            var length = Math.Min(value.Length, Math.Min(maxLength, _chars.Length));
            for (var i = 0; i < length; i++)
            {
                var b = value[i];
                _chars[i] = b is >= (byte)'a' and <= (byte)'z' ? (char)(b - 32) : b < 128 ? (char)b : '?';
            }

            return _chars.AsSpan(0, length).Trim();
        }

        // MM/DD/YYYY → DayNumber; 0 for empty or malformed.
        private static int DateOf(ReadOnlySpan<byte> value)
        {
            if (value.Length != 10 || value[2] != (byte)'/' || value[5] != (byte)'/')
            {
                return 0;
            }

            static int Digits(ReadOnlySpan<byte> s)
            {
                var n = 0;
                foreach (var b in s)
                {
                    if (b is < (byte)'0' or > (byte)'9')
                    {
                        return -1;
                    }

                    n = n * 10 + (b - '0');
                }

                return n;
            }

            var month = Digits(value[..2]);
            var day = Digits(value.Slice(3, 2));
            var year = Digits(value[6..]);
            return month is >= 1 and <= 12 && year is >= 1900 and <= 2100 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
                ? new DateOnly(year, month, day).DayNumber
                : 0;
        }
    }

    /// <summary>For logs: a short description of the header layout.</summary>
    public static string Describe(HistoryFileSummary summary) =>
        string.Create(CultureInfo.InvariantCulture, $"{summary.EntryName}: {summary.Columns} columns, {summary.Rows:N0} rows ({summary.DeactivatedRows:N0} deactivated)");
}
