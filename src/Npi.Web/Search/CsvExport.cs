using Npi.Core.Search;

namespace Npi.Web.Search;

/// <summary>
/// Streams every match as CSV, no row cap (CLAUDE.md §7 Stage 4). Shared by /export.csv and /api/v1/providers.csv. The site's
/// export panel (docs/PRODUCT_PLAN.md step 1.3) also picks a column set (<c>columns</c>) and Excel (<see cref="HandleXlsxAsync"/>).
/// </summary>
public static class CsvExport
{
    /// <summary>The API's CSV and /export.csv: the summary columns.</summary>
    public static Task<IResult> HandleAsync(HttpContext http, SearchService search, CancellationToken ct) => CsvAsync(http, search, null, ct);

    /// <summary>The export panel: CSV or Excel (<c>format=xlsx</c>) with the chosen column set (<c>columns</c>).</summary>
    public static Task<IResult> HandleExportAsync(HttpContext http, SearchService search, CancellationToken ct)
    {
        var columns = ProviderColumns.For(http.Request.Query["columns"]);
        return string.Equals(http.Request.Query["format"], "xlsx", StringComparison.OrdinalIgnoreCase)
            ? XlsxAsync(http, search, columns, ct)
            : CsvAsync(http, search, columns, ct);
    }

    /// <summary>/export.xlsx: Excel with the chosen column set.</summary>
    public static Task<IResult> HandleXlsxAsync(HttpContext http, SearchService search, CancellationToken ct) =>
        XlsxAsync(http, search, ProviderColumns.For(http.Request.Query["columns"]), ct);

    private static async Task<IResult> CsvAsync(HttpContext http, SearchService search, IReadOnlyList<ProviderColumn>? columns, CancellationToken ct)
    {
        var (filter, errors) = Validate(http);
        if (filter is null)
        {
            return Results.ValidationProblem(errors);
        }

        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"npi_search_{DateTime.UtcNow:yyyyMMdd}.csv\"";
        try
        {
            await ProviderCsv.WriteAsync(search.SearchAllAsync(filter, ct), http.Response.Body, ct, columns);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // The client stopped the download; MySqlConnector reports that as a cancelled or interrupted query. Nothing to answer.
        }

        return Results.Empty;
    }

    private static async Task<IResult> XlsxAsync(HttpContext http, SearchService search, IReadOnlyList<ProviderColumn> columns, CancellationToken ct)
    {
        var (filter, errors) = Validate(http);
        if (filter is null)
        {
            return Results.ValidationProblem(errors);
        }

        // The zip format is written synchronously, which a response body doesn't allow: build the workbook in a temporary file.
        var path = Path.Combine(Path.GetTempPath(), $"npi-export-{Guid.NewGuid():N}.xlsx");
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                await ProviderXlsx.WriteAsync(search.SearchAllAsync(filter, ct), file, columns, ct);
            }

            http.Response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"npi_search_{DateTime.UtcNow:yyyyMMdd}.xlsx\"";
            await using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
            http.Response.ContentLength = read.Length;
            await read.CopyToAsync(http.Response.Body, ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // The client stopped the download.
        }
        finally
        {
            File.Delete(path);
        }

        return Results.Empty;
    }

    // Validated before the response starts, so a bad search is a 400 problem, not a broken download.
    private static (SearchFilter? Filter, Dictionary<string, string[]> Errors) Validate(HttpContext http)
    {
        var (filter, errors) = SearchQueryString.Parse(http.Request.Query);
        if (errors.Count > 0)
        {
            return (null, SearchQueryString.ByParameterName(errors));
        }

        filter = filter with { Page = 1, PageSize = SearchFilter.DefaultPageSize };
        try
        {
            SearchValidation.Normalize(filter);
            return (filter, []);
        }
        catch (SearchValidationException ex)
        {
            return (null, SearchQueryString.ByParameterName(ex.Errors));
        }
    }
}
