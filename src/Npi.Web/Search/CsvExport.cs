using System.IO.Compression;
using Microsoft.AspNetCore.Http.Extensions;
using Npi.Core.Search;

namespace Npi.Web.Search;

/// <summary>
/// Streams every match as CSV, no row cap (CLAUDE.md §7 Stage 4). Shared by /export.csv and /api/v1/providers.csv. The site's
/// export panel (docs/PRODUCT_PLAN.md step 1.3) also picks a column set (<c>columns</c>) and Excel (<see cref="HandleXlsxAsync"/>).
/// With no filter (every active provider, owner 2026-10-14) the summary CSV comes from the ready-made file the loader writes
/// (<see cref="FullExport"/>, sorted by NPI) instead of the search. The site's exports send a bad search back to the search
/// page, which shows what's wrong; the API answers with a problem.
/// </summary>
public static class CsvExport
{
    /// <summary>/api/v1/providers.csv: the summary columns; a bad search is a 400 problem.</summary>
    public static Task<IResult> HandleAsync(HttpContext http, SearchService search, IConfiguration config, CancellationToken ct) =>
        CsvAsync(http, search, config, null, site: false, ct);

    /// <summary>/export.csv: the summary columns; a bad search goes back to the search page.</summary>
    public static Task<IResult> HandleSiteAsync(HttpContext http, SearchService search, IConfiguration config, CancellationToken ct) =>
        CsvAsync(http, search, config, null, site: true, ct);

    /// <summary>The export panel: CSV or Excel (<c>format=xlsx</c>) with the chosen column set (<c>columns</c>).</summary>
    public static Task<IResult> HandleExportAsync(HttpContext http, SearchService search, IConfiguration config, CancellationToken ct)
    {
        var columns = ProviderColumns.For(http.Request.Query["columns"]);
        return string.Equals(http.Request.Query["format"], "xlsx", StringComparison.OrdinalIgnoreCase)
            ? XlsxAsync(http, search, columns, ct)
            : CsvAsync(http, search, config, columns, site: true, ct);
    }

    /// <summary>/export.xlsx: Excel with the chosen column set.</summary>
    public static Task<IResult> HandleXlsxAsync(HttpContext http, SearchService search, CancellationToken ct) =>
        XlsxAsync(http, search, ProviderColumns.For(http.Request.Query["columns"]), ct);

    /// <summary>/export/all.csv.gz: the ready-made file itself, gzipped (~1/6 of the CSV's size).</summary>
    public static IResult HandleFullFile(HttpContext http, IConfiguration config)
    {
        var folder = Folder(config);
        if (FullExport.Read(folder) is not { } info)
        {
            return Results.NotFound();
        }

        return Results.Stream(FullExport.Open(folder), "application/gzip", FullFileName(info) + ".gz", enableRangeProcessing: true);
    }

    /// <summary>The folder of the ready-made file: <c>FullExport:Folder</c>, else the loader's default (the same computer).</summary>
    public static string Folder(IConfiguration config) => config["FullExport:Folder"] is { Length: > 0 } folder ? folder : FullExport.DefaultFolder;

    private static string FullFileName(FullExport.Info info) => $"npi_all_providers_{info.AsOf?.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) ?? "latest"}.csv";

    private static async Task<IResult> CsvAsync(HttpContext http, SearchService search, IConfiguration config, IReadOnlyList<ProviderColumn>? columns,
        bool site, CancellationToken ct)
    {
        var (filter, errors) = Validate(http);
        if (filter is null)
        {
            return site ? BackToSearch(http) : Results.ValidationProblem(errors);
        }

        // Every active provider in the summary columns: the ready-made file, when there is one.
        if (ServesFullFile(http, columns) && FullExport.Read(Folder(config)) is { } info)
        {
            await SendFullFileAsync(http, Folder(config), info, ct);
            return Results.Empty;
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

    // No filter, the summary columns, and no sort other than by NPI (the file's order).
    private static bool ServesFullFile(HttpContext http, IReadOnlyList<ProviderColumn>? columns) =>
        !SearchQueryString.HasAnyFilter(http.Request.Query)
        && (columns is null || ReferenceEquals(columns, ProviderColumns.SummaryColumns))
        && http.Request.Query["sort"].ToString() is "" or "npi";

    // Gzipped as stored when the client accepts it (browsers do, and save the CSV unpacked); otherwise unpacked on the way out.
    private static async Task SendFullFileAsync(HttpContext http, string folder, FullExport.Info info, CancellationToken ct)
    {
        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{FullFileName(info)}\"";
        await using var file = FullExport.Open(folder);
        try
        {
            if (http.Request.Headers.AcceptEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                http.Response.Headers.ContentEncoding = "gzip";
                http.Response.Headers.Vary = "Accept-Encoding";
                http.Response.ContentLength = file.Length;
                await file.CopyToAsync(http.Response.Body, ct);
            }
            else
            {
                await using var unpacked = new GZipStream(file, CompressionMode.Decompress);
                await unpacked.CopyToAsync(http.Response.Body, ct);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // The client stopped the download.
        }
    }

    private static async Task<IResult> XlsxAsync(HttpContext http, SearchService search, IReadOnlyList<ProviderColumn> columns, CancellationToken ct)
    {
        var (filter, _) = Validate(http);
        if (filter is null)
        {
            return BackToSearch(http);
        }

        // More rows than a sheet holds: say so on the search page instead of a cut-off file.
        if ((await search.SearchAsync(filter with { PageSize = 1 }, ct)).TotalCount > ProviderXlsx.MaxRows)
        {
            return BackToSearch(http, "excel-limit");
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

    // The search page with the same filters (it shows what's wrong), optionally with a notice about the export.
    private static IResult BackToSearch(HttpContext http, string? notice = null)
    {
        var query = new QueryBuilder(http.Request.Query
            .Where(kv => kv.Key is not ("format" or "columns" or "notice"))
            .SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string>(kv.Key, v ?? ""))));
        if (notice is not null)
        {
            query.Add("notice", notice);
        }

        return Results.Redirect("/search" + query.ToQueryString());
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
