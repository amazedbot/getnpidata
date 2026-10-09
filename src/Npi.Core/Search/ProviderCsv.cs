using System.Globalization;
using System.Text;
using CsvHelper;

namespace Npi.Core.Search;

/// <summary>Streams search results as CSV with the summary columns (CLAUDE.md §7.3). UTF-8 with BOM so Excel reads accents correctly.</summary>
public static class ProviderCsv
{
    public static readonly string[] Header =
    [
        "NPI", "Entity Type", "Name", "Credential", "Primary Specialty", "Address 1", "Address 2", "City", "State", "ZIP", "County",
        "Phone", "Gender", "Enumeration Date", "Last Update Date", "OIG Excluded", "Medicare Opt-Out", "Medicare Order/Refer",
        "Accepts Medicare Assignment", "Telehealth", "Billed Medicare",
    ];

    /// <returns>The number of providers written.</returns>
    public static async Task<long> WriteAsync(IAsyncEnumerable<ProviderSummary> rows, Stream output, CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16, leaveOpen: true);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        foreach (var h in Header)
        {
            csv.WriteField(h);
        }

        await csv.NextRecordAsync();
        long count = 0;
        await foreach (var r in rows.WithCancellation(ct))
        {
            WriteSummary(csv, r);
            await csv.NextRecordAsync();
            if (++count % 1000 == 0)
            {
                await csv.FlushAsync(); // keep the response streaming
            }
        }

        await csv.FlushAsync();
        return count;
    }

    /// <summary>
    /// Bulk lookup results (Stage 5.5 item 8): "Requested NPI" and "Lookup Status" columns, then the summary
    /// columns (empty when the NPI wasn't found). One row per requested NPI, in request order.
    /// </summary>
    public static async Task<long> WriteLookupAsync(IAsyncEnumerable<LookupRow> rows, Stream output, CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16, leaveOpen: true);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        csv.WriteField("Requested NPI");
        csv.WriteField("Lookup Status");
        foreach (var h in Header)
        {
            csv.WriteField(h);
        }

        await csv.NextRecordAsync();
        long count = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
            csv.WriteField(row.Npi);
            csv.WriteField(row.Status switch
            {
                LookupStatus.Found => "Found",
                LookupStatus.NotFound => "Not found or deactivated",
                _ => "Invalid NPI",
            });
            if (row.Provider is { } provider)
            {
                WriteSummary(csv, provider);
            }

            await csv.NextRecordAsync();
            if (++count % 1000 == 0)
            {
                await csv.FlushAsync();
            }
        }

        await csv.FlushAsync();
        return count;
    }

    private static void WriteSummary(CsvWriter csv, ProviderSummary r)
    {
        csv.WriteField(r.Npi);
        csv.WriteField(r.EntityTypeName);
        csv.WriteField(r.Name);
        csv.WriteField(r.Credential);
        csv.WriteField(r.PrimarySpecialty);
        csv.WriteField(r.Address1);
        csv.WriteField(r.Address2);
        csv.WriteField(r.City);
        csv.WriteField(r.State);
        csv.WriteField(r.Zip);
        csv.WriteField(r.County);
        csv.WriteField(r.Phone);
        csv.WriteField(r.Gender);
        csv.WriteField(r.EnumerationDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        csv.WriteField(r.LastUpdateDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        csv.WriteField(r.Flags.Excluded ? "Yes" : "");
        csv.WriteField(r.Flags.OptedOutOfMedicare ? "Yes" : "");
        csv.WriteField(r.Flags.CanOrderAndRefer ? "Yes" : "");
        csv.WriteField(r.Flags.AcceptsMedicareAssignment ? "Yes" : "");
        csv.WriteField(r.Flags.OffersTelehealth ? "Yes" : "");
        csv.WriteField(r.Flags.BilledMedicare ? "Yes" : "");
    }
}
