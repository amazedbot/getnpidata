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
    /// <param name="header">Write the BOM and the header row (false for the later parts of the full export file).</param>
    public static async Task<long> WriteAsync(IAsyncEnumerable<ProviderSummary> rows, Stream output, CancellationToken ct,
        IReadOnlyList<ProviderColumn>? columns = null, bool header = true)
    {
        columns ??= ProviderColumns.SummaryColumns;
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: header), 1 << 16, leaveOpen: true);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        if (header)
        {
            foreach (var c in columns)
            {
                csv.WriteField(c.Header);
            }

            await csv.NextRecordAsync();
        }

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
    /// The bulk lookup columns (Stage 5.5 item 8, docs/PRODUCT_PLAN.md step 1.7): "Requested NPI", "Lookup Status", the
    /// deactivation, exclusion and opt-out end dates, then the summary columns (empty when the NPI isn't active).
    /// </summary>
    public static readonly string[] LookupHeader =
        ["Requested NPI", "Lookup Status", "Deactivation Date", "OIG Exclusion Date", "Medicare Opt-Out End Date", .. Header];

    /// <summary>The lookup columns with "changed since" (docs/PRODUCT_PLAN.md step 3.4): the date compared with and what changed since.</summary>
    public static readonly string[] LookupChangesHeader =
        ["Requested NPI", "Lookup Status", "Deactivation Date", "OIG Exclusion Date", "Medicare Opt-Out End Date", "Changed Since", "Changes", .. Header];

    /// <summary>A row's changes as one cell: "2019-03-10 Primary practice location: … → …; …", "none", or empty when not looked up.</summary>
    public static string? ChangesText(LookupRow row) => row.Changes switch
    {
        null => null,
        [] => "none",
        var list => string.Join("; ", list.Select(c => c.On.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + c.Text)),
    };

    /// <summary>The lookup status as written in the CSV and Excel files.</summary>
    public static string StatusLabel(LookupStatus status) => status switch
    {
        LookupStatus.Found => "Active",
        LookupStatus.Deactivated => "Deactivated",
        LookupStatus.NotFound => "Not found",
        _ => "Invalid NPI",
    };

    /// <summary>One lookup row's values, in <see cref="LookupHeader"/> order (<see cref="LookupChangesHeader"/> with <paramref name="withChanges"/>).</summary>
    public static IEnumerable<string?> LookupValues(LookupRow row) => LookupValues(row, false);

    /// <inheritdoc cref="LookupValues(LookupRow)"/>
    public static IEnumerable<string?> LookupValues(LookupRow row, bool withChanges)
    {
        static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string?[] head = withChanges
            ? [row.Npi, StatusLabel(row.Status), Date(row.DeactivationDate), Date(row.ExclusionDate), Date(row.OptOutEndDate), Date(row.ChangedSince), ChangesText(row)]
            : [row.Npi, StatusLabel(row.Status), Date(row.DeactivationDate), Date(row.ExclusionDate), Date(row.OptOutEndDate)];
        return row.Provider is { } p ? head.Concat(ProviderColumns.SummaryColumns.Select(c => c.Value(p))) : head;
    }

    /// <summary>
    /// Bulk lookup results with <see cref="LookupHeader"/> (<see cref="LookupChangesHeader"/> with <paramref name="withChanges"/>):
    /// one row per requested NPI, in request order.
    /// </summary>
    public static async Task<long> WriteLookupAsync(IAsyncEnumerable<LookupRow> rows, Stream output, CancellationToken ct, bool withChanges = false)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16, leaveOpen: true);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        foreach (var h in withChanges ? LookupChangesHeader : LookupHeader)
        {
            csv.WriteField(h);
        }

        await csv.NextRecordAsync();
        long count = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
            foreach (var value in LookupValues(row, withChanges))
            {
                csv.WriteField(value);
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
}
