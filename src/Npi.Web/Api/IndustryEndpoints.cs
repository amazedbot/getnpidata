using Npi.Core.Search;

namespace Npi.Web.Api;

/// <summary>The all-companies and all-products CSV files, shared by the Companies &amp; products page and /api/v1.</summary>
public static class IndustryEndpoints
{
    public static Task<IResult> CompaniesCsvAsync(HttpContext http, IndustryService industry, CancellationToken ct) =>
        CsvAsync(http, $"companies_{DateTime.UtcNow:yyyyMMdd}.csv",
            (body, site) => IndustryCsv.WriteCompaniesAsync(industry.AllCompaniesAsync(ct), body, site, ct), ct);

    public static Task<IResult> ProductsCsvAsync(HttpContext http, IndustryService industry, CancellationToken ct) =>
        CsvAsync(http, $"products_{DateTime.UtcNow:yyyyMMdd}.csv",
            (body, site) => IndustryCsv.WriteProductsAsync(industry.AllProductsAsync(ct), body, site, ct), ct);

    private static async Task<IResult> CsvAsync(HttpContext http, string fileName, Func<Stream, string, Task<long>> write, CancellationToken ct)
    {
        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        try
        {
            await write(http.Response.Body, $"{http.Request.Scheme}://{http.Request.Host}");
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // The client stopped the download.
        }

        return Results.Empty;
    }
}
