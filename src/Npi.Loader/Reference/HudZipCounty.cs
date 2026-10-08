using System.Globalization;
using System.Text.Json;
using Npi.Core;

namespace Npi.Loader.Reference;

public sealed record ZipCounty(string Zip5, string CountyFips, decimal ResRatio, decimal BusRatio, decimal OthRatio, decimal TotRatio, string? City, string? State);

public sealed record HudCrosswalk(int Year, int Quarter, IReadOnlyList<ZipCounty> Rows, int Skipped)
{
    public string Version => $"{Year}Q{Quarter}";
}

/// <summary>
/// The HUD USPS ZIP–county crosswalk API response (type=2, query=All; CLAUDE.md §6.3):
/// <c>{"data": {"year", "quarter", "crosswalk_type": "zip-county", "results": [{zip, geoid, city, state, res_ratio, bus_ratio, oth_ratio, tot_ratio}]}}</c>.
/// </summary>
public static class HudZipCounty
{
    /// <summary>
    /// Parses the response. Rows whose geoid is not a 5-digit county FIPS are skipped and counted:
    /// HUD maps a few ZIPs (freely associated states, one in Texas) only to a 2-digit state code.
    /// </summary>
    public static HudCrosswalk Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        if (data.ValueKind == JsonValueKind.Array)
        {
            data = data[0];
        }

        var type = data.GetProperty("crosswalk_type").GetString();
        if (!string.Equals(type, "zip-county", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"HUD returned crosswalk_type '{type}', expected 'zip-county'. Check the type parameter.");
        }

        var year = ParseInt(data.GetProperty("year"));
        var quarter = ParseInt(data.GetProperty("quarter"));
        var rows = new List<ZipCounty>();
        var keys = new HashSet<(string, string)>();
        var skipped = 0;
        foreach (var r in data.GetProperty("results").EnumerateArray())
        {
            var zip = r.GetProperty("zip").GetString() ?? "";
            var geoid = r.GetProperty("geoid").GetString() ?? "";
            if (!InputFormats.IsZip5(zip) || geoid.Length != 5 || !geoid.All(char.IsAsciiDigit) || !keys.Add((zip, geoid)))
            {
                skipped++;
                continue;
            }

            rows.Add(new ZipCounty(zip, geoid,
                Ratio(r, "res_ratio"), Ratio(r, "bus_ratio"), Ratio(r, "oth_ratio"), Ratio(r, "tot_ratio"),
                r.TryGetProperty("city", out var city) ? city.GetString() : null,
                r.TryGetProperty("state", out var state) ? state.GetString() : null));
        }

        return new HudCrosswalk(year, quarter, rows, skipped);
    }

    private static int ParseInt(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number ? e.GetInt32() : int.Parse(e.GetString()!, CultureInfo.InvariantCulture);

    // Ratios arrive as 0, 1 or a double; DECIMAL(12,10) keeps 10 decimal places.
    private static decimal Ratio(JsonElement row, string name) =>
        Math.Round((decimal)row.GetProperty(name).GetDouble(), 10, MidpointRounding.AwayFromZero);
}
