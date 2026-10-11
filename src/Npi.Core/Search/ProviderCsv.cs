using System.Globalization;
using System.Text;
using CsvHelper;

namespace Npi.Core.Search;

/// <summary>Streams search results as CSV with the summary columns (CLAUDE.md §7.3). UTF-8 with BOM so Excel reads accents correctly.</summary>
public static class ProviderCsv
{
    public static readonly string[] Header = [.. ProviderColumns.SummaryColumns.Select(c => c.Header)];

    /// <param name="columns">The columns to write (<see cref="ProviderColumns"/>); null = the summary columns.</param>
    /// <returns>The number of providers written.</returns>
    public static async Task<long> WriteAsync(IAsyncEnumerable<ProviderSummary> rows, Stream output, CancellationToken ct,
        IReadOnlyList<ProviderColumn>? columns = null)
    {
        columns ??= ProviderColumns.SummaryColumns;
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16, leaveOpen: true);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        foreach (var c in columns)
        {
            csv.WriteField(c.Header);
        }

        await csv.NextRecordAsync();
        long count = 0;
        await foreach (var r in rows.WithCancellation(ct))
        {
            foreach (var c in columns)
            {
                csv.WriteField(c.Value(r));
            }

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
        foreach (var c in ProviderColumns.SummaryColumns)
        {
            csv.WriteField(c.Value(r));
        }
    }
}
