using System.Globalization;
using System.Text;
using CsvHelper;

namespace Npi.Core.Search;

/// <summary>Streams a product's paid prescribers (Stage 5.5 item 19, part 4) as CSV. UTF-8 with BOM so Excel reads accents correctly.</summary>
public static class ProductPrescriberCsv
{
    public static readonly string[] Header =
    [
        "NPI", "Name", "Credential", "Primary Specialty", "City", "State", "Paid (USD)", "Payments", "Medicare Part D Claims", "Part D Drug Cost (USD)",
        "Part D Beneficiaries",
    ];

    /// <returns>The number of rows written.</returns>
    public static async Task<long> WriteAsync(IAsyncEnumerable<ProductPrescriber> rows, Stream output, CancellationToken ct)
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
            csv.WriteField(r.Npi);
            csv.WriteField(r.Name);
            csv.WriteField(r.Credential);
            csv.WriteField(r.Specialty);
            csv.WriteField(r.City);
            csv.WriteField(r.State);
            csv.WriteField(r.Paid.ToString("0.00", CultureInfo.InvariantCulture));
            csv.WriteField(r.Payments);
            csv.WriteField(r.Claims);
            csv.WriteField(r.DrugCost?.ToString("0.00", CultureInfo.InvariantCulture));
            csv.WriteField(r.Beneficiaries);
            await csv.NextRecordAsync();
            if (++count % 1000 == 0)
            {
                await csv.FlushAsync();
            }
        }

        await csv.FlushAsync();
        return count;
    }
}
