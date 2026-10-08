using System.Globalization;
using System.Text.RegularExpressions;
using Npi.Core;

namespace Npi.Loader.Reference;

public sealed record County(string CountyFips, string State, string Name, string Source);

public sealed record ZipCentroid(string Zip5, decimal Lat, decimal Lon);

/// <summary>Census county names and ZCTA centroids (CLAUDE.md §6.3).</summary>
public static partial class CensusGeography
{
    public const string GazetteerSource = "census-gazetteer";
    public const string CountyCodes2020Source = "census-2020-codes";

    [GeneratedRegex(@"href\s*=\s*[""'](?<y>\d{4})_Gazetteer/?[""']", RegexOptions.IgnoreCase)]
    private static partial Regex GazetteerYear();

    /// <summary>The newest year in the Gazetteer directory listing (…/gazetteer/), or null.</summary>
    public static int? FindLatestGazetteerYear(string listingHtml) =>
        GazetteerYear().Matches(listingHtml).Select(m => (int?)int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture)).Max();

    public static Uri CountiesUrl(Uri baseUrl, int year) => new(baseUrl, $"{year}_Gazetteer/{year}_Gaz_counties_national.zip");

    public static Uri ZctaUrl(Uri baseUrl, int year) => new(baseUrl, $"{year}_Gazetteer/{year}_Gaz_zcta_national.zip");

    /// <summary>Gazetteer counties file: pipe-delimited, columns USPS, GEOID, …, NAME, ….</summary>
    public static IReadOnlyList<County> ParseGazetteerCounties(Stream txt) =>
        ReadPipeFile(txt, "Gazetteer counties", ["USPS", "GEOID", "NAME"])
            .Select(r => new County(Fips(r["GEOID"], 5), State(r["USPS"]), Name(r["NAME"]), GazetteerSource))
            .ToList();

    /// <summary>Gazetteer ZCTA file: pipe-delimited, columns GEOID, …, INTPTLAT, INTPTLONG.</summary>
    public static IReadOnlyList<ZipCentroid> ParseGazetteerZcta(Stream txt) =>
        ReadPipeFile(txt, "Gazetteer ZCTA", ["GEOID", "INTPTLAT", "INTPTLONG"])
            .Select(r => new ZipCentroid(
                InputFormats.IsZip5(r["GEOID"]) ? r["GEOID"] : throw new InvalidDataException($"'{r["GEOID"]}' is not a 5-digit ZCTA."),
                Coordinate(r["INTPTLAT"], 90), Coordinate(r["INTPTLONG"], 180)))
            .ToList();

    /// <summary>Census 2020 county codes file (national_county2020.txt): STATE, STATEFP, COUNTYFP, …, COUNTYNAME.</summary>
    public static IReadOnlyList<County> ParseCountyCodes2020(Stream txt) =>
        ReadPipeFile(txt, "2020 county codes", ["STATE", "STATEFP", "COUNTYFP", "COUNTYNAME"])
            .Select(r => new County(Fips(r["STATEFP"] + r["COUNTYFP"], 5), State(r["STATE"]), Name(r["COUNTYNAME"]), CountyCodes2020Source))
            .ToList();

    /// <summary>
    /// Gazetteer counties, plus the 2020-file counties of states the Gazetteer doesn't cover at all
    /// (AS, GU, MP, VI, UM). Retired counties of covered states, such as Connecticut's pre-2022
    /// counties, are left out so the county dropdown only offers current ones.
    /// </summary>
    public static IReadOnlyList<County> Merge(IReadOnlyList<County> gazetteer, IReadOnlyList<County> codes2020)
    {
        var coveredStates = gazetteer.Select(c => c.State).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return gazetteer.Concat(codes2020.Where(c => !coveredStates.Contains(c.State))).ToList();
    }

    private static List<Dictionary<string, string>> ReadPipeFile(Stream txt, string what, string[] required)
    {
        using var reader = new StreamReader(txt, detectEncodingFromByteOrderMarks: true);
        var header = reader.ReadLine()?.Split('|').Select(h => h.Trim()).ToArray()
            ?? throw new InvalidDataException($"{what} file is empty.");
        var missing = required.Where(c => !header.Contains(c)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidDataException($"{what} file has no {string.Join(", ", missing)} column; the format changed.");
        }

        var rows = new List<Dictionary<string, string>>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split('|');
            if (fields.Length != header.Length)
            {
                throw new InvalidDataException($"{what} file line {rows.Count + 2} has {fields.Length} fields, expected {header.Length}.");
            }

            rows.Add(header.Zip(fields).ToDictionary(p => p.First, p => p.Second.Trim()));
        }

        return rows;
    }

    private static string Fips(string value, int length) =>
        value.Length == length && value.All(char.IsAsciiDigit) ? value : throw new InvalidDataException($"'{value}' is not a {length}-digit FIPS code.");

    private static string State(string value) =>
        value.Length == 2 ? value : throw new InvalidDataException($"'{value}' is not a 2-letter state code.");

    private static string Name(string value) =>
        value.Length is > 0 and <= 100 ? value : throw new InvalidDataException($"County name '{value}' is empty or too long.");

    private static decimal Coordinate(string value, int limit) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d) <= limit
            ? Math.Round(d, 6)
            : throw new InvalidDataException($"'{value}' is not a valid coordinate.");
}
