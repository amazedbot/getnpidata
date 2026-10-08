using Npi.Core.Search;

namespace Npi.Web.Search;

/// <summary>Streams every match as CSV, no row cap (CLAUDE.md §7 Stage 4). Shared by /export.csv and /api/v1/providers.csv.</summary>
public static class CsvExport
{
    public static async Task<IResult> HandleAsync(HttpContext http, SearchService search, CancellationToken ct)
    {
        var (filter, errors) = SearchQueryString.Parse(http.Request.Query);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(SearchQueryString.ByParameterName(errors));
        }

        filter = filter with { Page = 1, PageSize = SearchFilter.DefaultPageSize };
        try
        {
            // Validate before the response starts, so a bad search is a 400, not a broken download.
            SearchValidation.Normalize(filter);
        }
        catch (SearchValidationException ex)
        {
            return Results.ValidationProblem(SearchQueryString.ByParameterName(ex.Errors));
        }

        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"npi_search_{DateTime.UtcNow:yyyyMMdd}.csv\"";
        await ProviderCsv.WriteAsync(search.SearchAllAsync(filter, ct), http.Response.Body, ct);
        return Results.Empty;
    }
}
