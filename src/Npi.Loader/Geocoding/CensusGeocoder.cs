using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace Npi.Loader.Geocoding;

/// <summary>A street address to geocode; <see cref="Key"/> is provider_location.addr_key.</summary>
public sealed record GeocodeRequest(string Key, string Street, string? City, string State, string Zip5);

/// <summary>One Census Geocoder answer. Lat/Lon only for <c>Match</c>.</summary>
public sealed record GeocodeResult(string Key, string Status, string? MatchType, string? MatchedAddress, double? Lat, double? Lon);

/// <summary>
/// The US Census Bureau batch geocoder (CLAUDE.md §7 Stage 5.5 item 10): up to 10,000 addresses per POST as a
/// CSV of "id, street, city, state, zip", answered with one CSV line per address. Free, no key, public-domain results.
/// Measured Oct 2026: 10,000 practice addresses in ~50 s, 94% matched.
/// </summary>
public sealed class CensusGeocoder(HttpClient http, string url, string benchmark)
{
    /// <summary>The Census limit per request.</summary>
    public const int MaxBatch = 10_000;

    public string Benchmark => benchmark;

    /// <summary>Geocodes one batch. Addresses missing from the answer are left out of the result (retried on a later run).</summary>
    public async Task<IReadOnlyList<GeocodeResult>> GeocodeAsync(IReadOnlyList<GeocodeRequest> batch, CancellationToken ct)
    {
        if (batch.Count is 0 or > MaxBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(batch), $"A batch holds 1 to {MaxBatch} addresses.");
        }

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(WriteBatch(batch)));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "addressFile", "addresses.csv");
        form.Add(new StringContent(benchmark), "benchmark");

        using var response = await http.PostAsync(url, form, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        return ParseResults(body, batch);
    }

    /// <summary>The request CSV: the row number as the id, so answers map back to keys.</summary>
    public static string WriteBatch(IReadOnlyList<GeocodeRequest> batch)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = false });
        for (var i = 0; i < batch.Count; i++)
        {
            var a = batch[i];
            csv.WriteField(i + 1);
            csv.WriteField(a.Street);
            csv.WriteField(a.City ?? "");
            csv.WriteField(a.State);
            csv.WriteField(a.Zip5);
            csv.NextRecord();
        }

        return writer.ToString();
    }

    /// <summary>
    /// Parses the answer: <c>"id","input","Match","Exact","matched address","lon,lat","tiger id","side"</c>, or just
    /// <c>"id","input","No_Match"</c> / <c>"Tie"</c>. Unknown ids and malformed lines are ignored.
    /// </summary>
    public static IReadOnlyList<GeocodeResult> ParseResults(string body, IReadOnlyList<GeocodeRequest> batch)
    {
        var results = new List<GeocodeResult>(batch.Count);
        using var reader = new StringReader(body);
        using var csv = new CsvParser(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = false, BadDataFound = null, MissingFieldFound = null });
        while (csv.Read())
        {
            var row = csv.Record;
            if (row is null || row.Length < 3 || !int.TryParse(row[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1 || id > batch.Count)
            {
                continue;
            }

            var key = batch[id - 1].Key;
            var status = row[2].Trim();
            if (status == "Match" && row.Length >= 6 && TryParseLonLat(row[5], out var lat, out var lon))
            {
                results.Add(new GeocodeResult(key, "Match", Clip(row[3], 10), Clip(row[4], 255), lat, lon));
            }
            else if (status is "No_Match" or "Tie")
            {
                results.Add(new GeocodeResult(key, status, null, null, null, null));
            }
        }

        return results;
    }

    private static bool TryParseLonLat(string value, out double lat, out double lon)
    {
        lat = lon = 0;
        var parts = value.Split(',');
        return parts.Length == 2
               && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lon)
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
               && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
    }

    private static string? Clip(string value, int max)
    {
        var v = value.Trim();
        return v.Length == 0 ? null : v.Length <= max ? v : v[..max];
    }
}
