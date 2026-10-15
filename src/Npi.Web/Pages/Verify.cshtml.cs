using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// Verify NPIs (docs/PRODUCT_PLAN.md step 1.7; the bulk lookup of CLAUDE.md §7 Stage 5.5 item 8): paste NPIs or upload a
/// CSV, text or Excel file, see a summary (active, deactivated with the date, excluded, opted out, not found, invalid),
/// filter the rows, and download every row as CSV or Excel. Served at <c>/verify</c> and <c>/lookup</c>. With "Changed since"
/// (docs/PRODUCT_PLAN.md step 3.4) each row also says what changed after that date, from the archived monthly NPPES files and
/// the changes recorded since October 2026, with a tab and download columns for it.
/// </summary>
[RequestSizeLimit(MaxUploadBytes + 2_000_000)]
public partial class VerifyModel(SearchService search) : PageModel
{
    public const int MaxNpis = 50_000;
    public const int MaxUploadBytes = 10_000_000;
    public const int RowsPerPage = 100;

    /// <summary>The datasets the results come from besides NPPES (<c>reference_data</c> sources), for the "as of" line.</summary>
    public static readonly IReadOnlyList<(string Source, string Label)> Sources =
        [("oig_leie", "HHS-OIG exclusions (LEIE)"), ("cms_opt_out", "CMS Medicare opt-out"), ("cms_order_referring", "CMS order and referring")];

    /// <summary>The result tabs: key, label.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Tabs =
    [
        ("attention", "Needs attention"), ("all", "All"), ("active", "Active"), ("deactivated", "Deactivated"), ("excluded", "Excluded (OIG)"),
        ("optedout", "Opted out of Medicare"), ("stale", "Stale record"), ("check", "Record to check"), ("changed", "Changed since"), ("notfound", "Not found"),
        ("invalid", "Invalid"),
    ];

    /// <summary>The earliest "changed since" date accepted: the archive's first file is from November 2007.</summary>
    public static readonly DateOnly EarliestChangedSince = new(2005, 1, 1);

    static VerifyModel() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // old .xls files

    [BindProperty]
    public string? Npis { get; set; }

    [BindProperty]
    public IFormFile? Upload { get; set; }

    /// <summary>The NPIs of a checked list, carried by the results form so tabs, pages and downloads needn't upload again.</summary>
    [BindProperty]
    public string? List { get; set; }

    [BindProperty]
    public string? ListName { get; set; }

    /// <summary>A tab button's value.</summary>
    [BindProperty]
    public string? Show { get; set; }

    /// <summary>The tab being shown (kept while paging).</summary>
    [BindProperty]
    public string? Tab { get; set; }

    [BindProperty(Name = "p")]
    public int PageNumber { get; set; } = 1;

    /// <summary>Optional: say what changed for each NPI after this date.</summary>
    [BindProperty]
    public DateOnly? ChangedSince { get; set; }

    public string? Error { get; private set; }

    /// <summary>Every checked NPI in list order; null before a check.</summary>
    public IReadOnlyList<LookupRow>? Rows { get; private set; }

    public IReadOnlyDictionary<string, int> Counts { get; private set; } = new Dictionary<string, int>();

    /// <summary>Individuals among the active NPIs, and how many of them can order and refer for Medicare.</summary>
    public (int Individuals, int OrderRefer, int BilledMedicare) Medicare { get; private set; }

    public string CurrentTab { get; private set; } = "all";

    /// <summary>The rows of the current tab and page, with their position in the list (1-based).</summary>
    public IReadOnlyList<(int Number, LookupRow Row)> Shown { get; private set; } = [];

    public int ShownTotal { get; private set; }

    public IReadOnlyDictionary<string, DateTime> SourceDates { get; private set; } = new Dictionary<string, DateTime>();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (await GatherAsync(ct) is not { } npis)
        {
            return Page();
        }

        var rows = new List<LookupRow>(npis.Count);
        await foreach (var row in LookupAllAsync(npis, ct))
        {
            rows.Add(row);
        }

        Rows = rows;
        List = string.Join(' ', npis);
        Counts = Tabs.ToDictionary(t => t.Key, t => rows.Count(r => InTab(t.Key, r)));
        var individuals = rows.Where(r => r.Provider is { EntityType: 1 }).ToList();
        Medicare = (individuals.Count, individuals.Count(r => r.Provider!.Flags.CanOrderAndRefer), rows.Count(r => r.Provider?.Flags.BilledMedicare == true));
        var tab = Show ?? Tab;
        CurrentTab = tab is not null && Tabs.Any(t => t.Key == tab) ? tab : Counts["attention"] > 0 ? "attention" : "all";
        var matching = rows.Select((r, i) => (Number: i + 1, Row: r)).Where(x => InTab(CurrentTab, x.Row)).ToList();
        ShownTotal = matching.Count;
        PageNumber = Math.Clamp(Show is null ? PageNumber : 1, 1, Math.Max(1, (matching.Count + RowsPerPage - 1) / RowsPerPage));
        Shown = matching.Skip((PageNumber - 1) * RowsPerPage).Take(RowsPerPage).ToList();
        SourceDates = await search.GetSourceLoadDatesAsync(Sources.Select(s => s.Source).ToList(), ct);
        return Page();
    }

    public Task<IActionResult> OnPostCsvAsync(CancellationToken ct) => DownloadAsync(excel: false, ct);

    public Task<IActionResult> OnPostXlsxAsync(CancellationToken ct) => DownloadAsync(excel: true, ct);

    private async Task<IActionResult> DownloadAsync(bool excel, CancellationToken ct)
    {
        if (await GatherAsync(ct) is not { } npis)
        {
            return Page();
        }

        var name = $"npi_verify_{DateTime.UtcNow:yyyyMMdd}";
        var withChanges = ChangedSince is not null;
        try
        {
            if (excel)
            {
                // The zip writer needs a seekable stream; 50,000 rows fit in memory.
                using var buffer = new MemoryStream();
                await ProviderXlsx.WriteLookupAsync(LookupAllAsync(npis, ct), buffer, ct, withChanges);
                return File(buffer.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx");
            }

            Response.ContentType = "text/csv; charset=utf-8";
            Response.Headers.ContentDisposition = $"attachment; filename=\"{name}.csv\"";
            await ProviderCsv.WriteLookupAsync(LookupAllAsync(npis, ct), Response.Body, ct, withChanges);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // The client stopped the download.
        }

        return new EmptyResult();
    }

    /// <summary>Whether a row belongs to a tab.</summary>
    public static bool InTab(string tab, LookupRow row) => tab switch
    {
        "attention" => row.Status != LookupStatus.Found || row.Provider!.Flags.Excluded || row.Provider.Flags.OptedOutOfMedicare,
        "active" => row.Status == LookupStatus.Found,
        "deactivated" => row.Status == LookupStatus.Deactivated,
        "excluded" => row.Provider?.Flags.Excluded == true,
        "optedout" => row.Provider?.Flags.OptedOutOfMedicare == true,
        "stale" => row.Provider?.Quality?.Label == QualityLabel.Stale,
        "check" => row.Provider?.Quality?.Label == QualityLabel.Check,
        "changed" => row.Changes is { Count: > 0 },
        "notfound" => row.Status == LookupStatus.NotFound,
        "invalid" => row.Status == LookupStatus.Invalid,
        _ => true,
    };

    /// <summary>What to say about a row: text and pill style (ok, warn, alert or none).</summary>
    public static IReadOnlyList<(string Text, string? Style)> Results(LookupRow row)
    {
        static string Day(DateOnly d) => d.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        switch (row.Status)
        {
            case LookupStatus.Invalid:
                return [("Invalid NPI: the check digit doesn't match (often a typo)", "warn")];
            case LookupStatus.NotFound:
                return [("Not found in NPPES", "warn")];
            case LookupStatus.Deactivated:
                return [(row.DeactivationDate is { } d ? "Deactivated on " + Day(d) : "Deactivated", "alert"), .. ChangedResult(row)];
        }

        var flags = row.Provider!.Flags;
        var results = new List<(string, string?)>();
        if (flags.Excluded)
        {
            results.Add((row.ExclusionDate is { } d ? "On the OIG exclusion list since " + Day(d) : "On the OIG exclusion list", "alert"));
        }

        if (flags.OptedOutOfMedicare)
        {
            results.Add((row.OptOutEndDate is { } d ? "Opted out of Medicare until " + Day(d) : "Opted out of Medicare", "warn"));
        }

        if (results.Count == 0)
        {
            results.Add(("Active", "ok"));
        }

        // Record quality (docs/PRODUCT_PLAN.md step 3.1), with the main reason.
        if (row.Provider.Quality is { Label: not QualityLabel.Current } quality)
        {
            var reasons = quality.ReasonTexts;
            results.Add(((quality.Label == QualityLabel.Stale ? "Stale record" : "Record to check") + (reasons.Count > 0 ? ": " + reasons[0].ToLowerInvariant() : ""),
                quality.Label == QualityLabel.Stale ? "warn" : null));
        }

        if (row.Provider.EntityType == 1 && flags.CanOrderAndRefer)
        {
            results.Add(("Can order and refer", null));
        }

        results.AddRange(ChangedResult(row));
        return results;
    }

    // "Changed since [date]: moved, specialty" or "No change since [date]" when the check was asked for.
    private static IEnumerable<(string Text, string? Style)> ChangedResult(LookupRow row)
    {
        if (row is not { ChangedSince: { } since, Changes: { } changes })
        {
            yield break;
        }

        var day = since.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        yield return changes.Count == 0
            ? ($"No change since {day}", null)
            : ($"Changed since {day}: {string.Join(", ", changes.Select(c => HistoryChangeKinds.Brief(c.Kind)).Distinct(StringComparer.Ordinal))}", "warn");
    }

    /// <summary>The changes of a row in words, oldest first ("March 2019: Primary practice location: … → …").</summary>
    public static IReadOnlyList<string> ChangeLines(LookupRow row) =>
        row.Changes is { } changes ? [.. changes.Select(c => c.On.ToString("MMM yyyy", CultureInfo.InvariantCulture) + ": " + c.Text)] : [];

    /// <summary>The NPIs of the request: the carried list, else every 10-digit number pasted or in the file. Sets <see cref="Error"/>.</summary>
    private async Task<IReadOnlyList<string>?> GatherAsync(CancellationToken ct)
    {
        IReadOnlyList<string> npis;
        if (!string.IsNullOrWhiteSpace(List))
        {
            npis = ExtractNpis(List);
        }
        else
        {
            var text = new StringBuilder(Npis ?? "");
            if (Upload is { Length: > 0 })
            {
                if (Upload.Length > MaxUploadBytes)
                {
                    Error = $"The file is larger than {MaxUploadBytes / 1_000_000} MB.";
                    return null;
                }

                ListName = Path.GetFileName(Upload.FileName);
                try
                {
                    text.Append('\n').Append(await ReadUploadAsync(Upload, ct));
                }
                catch (Exception e) when (e is ExcelDataReader.Exceptions.ExcelReaderException or InvalidDataException or NotSupportedException)
                {
                    Error = "The file couldn't be read. Upload a CSV, text or Excel file.";
                    return null;
                }
            }

            npis = ExtractNpis(text.ToString());
        }

        if (npis.Count == 0)
        {
            Error = "No 10-digit NPIs found. Paste NPIs or upload a CSV, text or Excel file that contains them.";
            return null;
        }

        if (npis.Count > MaxNpis)
        {
            Error = $"{npis.Count:N0} NPIs found; the limit is {MaxNpis:N0} per check.";
            return null;
        }

        if (ChangedSince is { } since && (since < EarliestChangedSince || since > DateOnly.FromDateTime(DateTime.UtcNow)))
        {
            Error = "Choose a \"changed since\" date between January 2005 and today.";
            return null;
        }

        return npis;
    }

    /// <summary>
    /// The text to look for NPIs in: a CSV or Excel sheet whose first row names columns with "NPI" contributes only those
    /// columns (so phone numbers and IDs in other columns aren't taken for NPIs); anything else, all of its text.
    /// </summary>
    private static async Task<string> ReadUploadAsync(IFormFile upload, CancellationToken ct)
    {
        var extension = Path.GetExtension(upload.FileName).ToLowerInvariant();
        await using var stream = upload.OpenReadStream();
        if (extension is not (".xlsx" or ".xls"))
        {
            using var reader = new StreamReader(stream);
            var all = await reader.ReadToEndAsync(ct);
            using var parser = new CsvHelper.CsvParser(new StringReader(all),
                new CsvHelper.Configuration.CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = false, DetectDelimiter = true, BadDataFound = null });
            var rows = new List<string?[]>();
            while (await parser.ReadAsync())
            {
                rows.Add(parser.Record ?? []);
            }

            return NpiColumnsText(rows) ?? all;
        }

        using var excel = ExcelReaderFactory.CreateReader(stream);
        var text = new StringBuilder();
        do
        {
            var sheet = new List<string?[]>();
            while (excel.Read())
            {
                // An NPI typed as a number is a double: 1234567893 → "1234567893".
                sheet.Add([.. Enumerable.Range(0, excel.FieldCount).Select(i => Convert.ToString(excel.GetValue(i), CultureInfo.InvariantCulture))]);
            }

            text.Append(NpiColumnsText(sheet) ?? string.Join('\n', sheet.Select(r => string.Join(',', r)))).Append('\n');
        }
        while (excel.NextResult());
        return text.ToString();
    }

    /// <summary>The cells of the columns whose header (first row) contains "NPI", one row a line; null when no header does.</summary>
    public static string? NpiColumnsText(IReadOnlyList<string?[]> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var columns = rows[0].Select((h, i) => (h, i)).Where(c => c.h?.Contains("NPI", StringComparison.OrdinalIgnoreCase) == true).Select(c => c.i).ToList();
        return columns.Count == 0
            ? null
            : string.Join('\n', rows.Skip(1).Select(r => string.Join(',', columns.Where(i => i < r.Length).Select(i => r[i]))));
    }

    /// <summary>Every 10-digit number in the text that isn't part of a longer number, in order, duplicates removed.</summary>
    public static IReadOnlyList<string> ExtractNpis(string text) =>
        TenDigits().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();

    private async IAsyncEnumerable<LookupRow> LookupAllAsync(IReadOnlyList<string> npis, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var batch in npis.Chunk(SearchService.MaxLookupBatch))
        {
            foreach (var row in await search.LookupAsync(batch, ChangedSince, ct))
            {
                yield return row;
            }
        }
    }

    [GeneratedRegex(@"(?<!\d)\d{10}(?!\d)")]
    private static partial Regex TenDigits();
}
