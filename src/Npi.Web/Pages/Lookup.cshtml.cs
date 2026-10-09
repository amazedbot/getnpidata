using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// Bulk NPI lookup (CLAUDE.md §7 Stage 5.5 item 8): paste NPIs or upload a file, get back a CSV with the summary
/// columns and flags for each, in the order given.
/// </summary>
[RequestSizeLimit(MaxUploadBytes + 1_000_000)]
public partial class LookupModel(SearchService search) : PageModel
{
    public const int MaxNpis = 50_000;
    public const int MaxUploadBytes = 10_000_000;

    public string? Error { get; private set; }

    [BindProperty]
    public string? Npis { get; set; }

    [BindProperty]
    public IFormFile? Upload { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var text = new StringBuilder(Npis ?? "");
        if (Upload is { Length: > 0 })
        {
            if (Upload.Length > MaxUploadBytes)
            {
                Error = $"The file is larger than {MaxUploadBytes / 1_000_000} MB.";
                return Page();
            }

            using var reader = new StreamReader(Upload.OpenReadStream());
            text.Append('\n').Append(await reader.ReadToEndAsync(ct));
        }

        var npis = ExtractNpis(text.ToString());
        if (npis.Count == 0)
        {
            Error = "No 10-digit NPIs found. Paste NPIs or upload a CSV or text file that contains them.";
            return Page();
        }

        if (npis.Count > MaxNpis)
        {
            Error = $"{npis.Count:N0} NPIs found; the limit is {MaxNpis:N0} per lookup.";
            return Page();
        }

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"npi_lookup_{DateTime.UtcNow:yyyyMMdd}.csv\"";
        try
        {
            await ProviderCsv.WriteLookupAsync(LookupAllAsync(npis, ct), Response.Body, ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // The client stopped the download.
        }

        return new EmptyResult();
    }

    /// <summary>Every 10-digit number in the text that isn't part of a longer number, in order, duplicates removed.</summary>
    public static IReadOnlyList<string> ExtractNpis(string text) =>
        TenDigits().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();

    private async IAsyncEnumerable<LookupRow> LookupAllAsync(IReadOnlyList<string> npis, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var batch in npis.Chunk(SearchService.MaxLookupBatch))
        {
            foreach (var row in await search.LookupAsync(batch, ct))
            {
                yield return row;
            }
        }
    }

    [GeneratedRegex(@"(?<!\d)\d{10}(?!\d)")]
    private static partial Regex TenDigits();
}
