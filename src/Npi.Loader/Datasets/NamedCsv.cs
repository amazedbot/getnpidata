using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace Npi.Loader.Datasets;

/// <summary>
/// A small CSV read by header name, for files parsed in C# rather than streamed into MySQL (a few thousand rows): headers are
/// compared trimmed, without a byte-order mark and case-insensitively ("ACO_ID " = "aco_id"); a row shorter than the header (a
/// header ending in a separator, as HRSA writes it) reads its missing fields as empty.
/// </summary>
public sealed class NamedCsv
{
    private readonly Dictionary<string, int> _positions;
    private readonly List<string[]> _rows;

    private NamedCsv(Dictionary<string, int> positions, List<string[]> rows)
    {
        _positions = positions;
        _rows = rows;
    }

    /// <summary>The headers as read (trimmed, lower-case).</summary>
    public IReadOnlyCollection<string> Headers => _positions.Keys;

    public int Count => _rows.Count;

    public static NamedCsv Read(TextReader reader)
    {
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            BadDataFound = null, MissingFieldFound = null, DetectColumnCountChanges = false, TrimOptions = TrimOptions.None,
        });
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord is not { } header)
        {
            return new NamedCsv(positions, []);
        }

        for (var i = 0; i < header.Length; i++)
        {
            var name = Clean(header[i]);
            if (name.Length > 0)
            {
                positions.TryAdd(name, i);
            }
        }

        var rows = new List<string[]>();
        while (csv.Read())
        {
            var record = csv.Parser.Record ?? [];
            if (record.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            rows.Add(record);
        }

        return new NamedCsv(positions, rows);
    }

    /// <summary>
    /// A file as UTF-8 (a BOM dropped), or as Windows-1252 when it isn't valid UTF-8: AHRQ's Compendium files are Windows-1252
    /// (an en dash is byte 0x96), which UTF-8 would turn into "�".
    /// </summary>
    public static NamedCsv Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var start = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            text = Windows1252.GetString(bytes);
        }

        return Read(new StringReader(text));
    }

    private static readonly Encoding Windows1252 = CodePages(1252);

    private static Encoding CodePages(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }

    public bool Has(string header) => _positions.ContainsKey(Clean(header));

    /// <summary>The first of <paramref name="candidates"/> the file has, or null.</summary>
    public string? First(params string[] candidates) => candidates.FirstOrDefault(Has);

    /// <summary>Throws when the file lacks one of the headers: the source changed its format.</summary>
    public void Require(string file, params string[] headers)
    {
        var missing = headers.Where(h => !Has(h)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidDataException($"{file} has no column(s) {string.Join(", ", missing.Select(m => $"'{m}'"))}; the source changed its format.");
        }
    }

    public IEnumerable<Row> Rows => _rows.Select(r => new Row(this, r));

    /// <summary>One data row.</summary>
    public readonly struct Row(NamedCsv csv, string[] fields)
    {
        /// <summary>The trimmed value of the column, or null when it is empty or the file has no such column.</summary>
        public string? this[string? header]
        {
            get
            {
                if (header is null || !csv._positions.TryGetValue(Clean(header), out var i) || i >= fields.Length)
                {
                    return null;
                }

                var value = fields[i].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        public int? Whole(string? header) =>
            this[header] is { } v && double.TryParse(v.Replace(",", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                && d is >= int.MinValue and <= int.MaxValue
                ? (int)Math.Round(d)
                : null;

        public double? Number(string? header) =>
            this[header] is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

        /// <summary>1/0, Y/N, Yes/No → true/false; anything else null.</summary>
        public bool? Flag(string? header) => this[header]?.ToUpperInvariant() switch
        {
            "1" or "Y" or "YES" or "TRUE" => true,
            "0" or "N" or "NO" or "FALSE" => false,
            _ => null,
        };

        /// <summary>M/D/YYYY (CMS, HRSA) or YYYY-MM-DD; anything else null.</summary>
        public DateTime? Date(string? header) =>
            this[header] is { } v && DateTime.TryParseExact(v.Split(' ')[0], ["M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "yyyyMMdd"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d)
                ? d
                : null;
    }

    private static string Clean(string header) => header.Trim().TrimStart('﻿').Trim().ToLowerInvariant();
}
