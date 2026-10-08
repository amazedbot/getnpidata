using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;

namespace Npi.Loader.Reference;

public sealed record TaxonomyCode(
    string Code, string? Grouping, string? Classification, string? Specialization,
    string? Definition, string? Notes, string? DisplayName, string? Section);

/// <summary>The NUCC Health Care Provider Taxonomy code set (CLAUDE.md §6.3).</summary>
public static partial class NuccTaxonomy
{
    [GeneratedRegex(@"href\s*=\s*[""'](?<u>[^""']*nucc_taxonomy_(?<v>\d+)\.csv)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex CsvLink();

    /// <summary>Finds the newest versioned CSV link (nucc_taxonomy_&lt;ver&gt;.csv) on the NUCC CSV page.</summary>
    public static (string Version, Uri Url)? FindLatest(string html, Uri pageUrl)
    {
        var best = CsvLink().Matches(html)
            .Select(m => (Version: int.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture), Href: m.Groups["u"].Value))
            .OrderByDescending(x => x.Version)
            .FirstOrDefault();
        return best.Href is null ? null : (best.Version.ToString(CultureInfo.InvariantCulture), new Uri(pageUrl, best.Href));
    }

    /// <summary>Parses the NUCC CSV (header: Code, Grouping, Classification, Specialization, Definition, Notes, Display Name, Section).</summary>
    public static IReadOnlyList<TaxonomyCode> Parse(Stream csv)
    {
        using var reader = new StreamReader(csv, detectEncodingFromByteOrderMarks: true);
        using var parser = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { TrimOptions = TrimOptions.Trim });
        parser.Read();
        parser.ReadHeader();
        foreach (var required in new[] { "Code", "Grouping", "Classification", "Specialization", "Definition", "Notes", "Display Name", "Section" })
        {
            if (!parser.HeaderRecord!.Contains(required))
            {
                throw new InvalidDataException($"NUCC CSV has no '{required}' column; the format changed.");
            }
        }

        var codes = new List<TaxonomyCode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (parser.Read())
        {
            var code = parser.GetField("Code")?.Trim() ?? "";
            if (code.Length != 10)
            {
                throw new InvalidDataException($"NUCC CSV row {parser.Parser.Row}: '{code}' is not a 10-character taxonomy code.");
            }

            if (!seen.Add(code))
            {
                throw new InvalidDataException($"NUCC CSV lists {code} twice.");
            }

            codes.Add(new TaxonomyCode(code,
                Empty(parser.GetField("Grouping")), Empty(parser.GetField("Classification")), Empty(parser.GetField("Specialization")),
                Empty(parser.GetField("Definition")), Empty(parser.GetField("Notes")), Empty(parser.GetField("Display Name")),
                Empty(parser.GetField("Section"))));
        }

        return codes;
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
