using System.Net;
using Npi.Loader.Datasets;
using Npi.Loader.Db;
using Serilog.Core;

namespace Npi.Loader.Tests;

/// <summary>The facility sources of docs/PRODUCT_PLAN.md step 3.6 part 2, without MySQL: catalog discovery and the dialysis report's data API.</summary>
public class FacilitySourceTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static CmsCatalog Catalog()
    {
        using var stream = File.OpenRead(Fixtures.Path("datasets/cms_catalog_sample.json"));
        return CmsCatalog.Parse(stream);
    }

    [Fact]
    public void The_data_api_of_the_newest_release_is_the_one_describing_its_csv()
    {
        // The newest year lists a "latest" API first; the one with the CSV's resources is that year's own.
        var release = Catalog().FindLatestApi("Medicare Dialysis Facilities");

        Assert.NotNull(release);
        Assert.Equal("https://cms.test/data-api/v1/dataset/dfr-2026/data", release.Url.AbsoluteUri);
        Assert.Equal("2026-12-31 dfr_facility_socrata_fy2026.csv", release.Version);
        Assert.Equal(2026, release.DataYear);

        // A release without a data API has none.
        Assert.Null(Catalog().FindLatestApi("Order and Referring"));
        Assert.Equal("https://cms.test/files/MUP_INP_RY26_P03_V10_DY24_PrvSvc.CSV",
            Catalog().FindLatest(HospitalVolumeSource.InpatientServiceTitle)?.Url.AbsoluteUri);
    }

    [Fact]
    public void The_newest_esrd_qip_payment_year_is_found_by_its_title()
    {
        using var stream = File.OpenRead(Fixtures.Path("datasets/pdc_list_sample.json"));
        var release = DialysisQipSource.ParseList(stream);
        Assert.Equal(("PY 2026 2025-12-15 TPS.csv", "https://pdc.test/files/TPS.csv", 2026), (release.Version, release.Url.AbsoluteUri, release.DataYear));

        using var none = new MemoryStream("""[{"title":"ESRD QIP - Standardized Readmission Ratio - Payment Year 2026","distribution":[{"downloadURL":"https://x.test/a.csv"}]}]"""u8.ToArray());
        Assert.Throws<InvalidDataException>(() => DialysisQipSource.ParseList(none));
    }

    [Fact]
    public void A_long_list_of_file_versions_is_kept_short_but_still_changes_with_any_file()
    {
        DatasetRelease R(string version) => new(version, new Uri("https://pdc.test/files/a.csv"));
        Assert.Equal("2026-07-22 a.csv | 2026-07-01 b.csv", DialysisFacilitySource.Combined([R("2026-07-22 a.csv"), R("2026-07-01 b.csv")]).Version);

        var files = Enumerable.Range(0, 10).Select(i => R($"2026-07-{10 + i} {new string('x', 50)}{i}.csv")).ToList();
        var compact = DialysisFacilitySource.Combined(files).Version;
        Assert.Matches("^10 files, newest 2026-07-19, [0-9a-f]{12}$", compact);
        files[3] = R($"2026-07-13 {new string('x', 50)}3b.csv");
        Assert.NotEqual(compact, DialysisFacilitySource.Combined(files).Version);
    }

    [Fact]
    public void Data_api_pages_read_as_rows_of_strings()
    {
        var rows = DialysisNpiSource.ParsePage("""[{"CCN":"012306","NPI":"1720166085"},{"CCN":"012500","NPI":null},{"CCN":"012501","NPI":1215900444}]""");
        Assert.Equal([("012306", "1720166085"), ("012500", null), ("012501", "1215900444")], rows.Select(r => (r["ccn"], r["NPI"])));
        Assert.Throws<InvalidDataException>(() => DialysisNpiSource.ParsePage("""{"error":"bad"}"""));
    }

    [Fact]
    public async Task The_dialysis_report_is_read_one_measure_a_page_at_a_time()
    {
        var requests = new List<string>();
        var handler = new Handler(url =>
        {
            requests.Add(url);
            if (url.EndsWith("?size=1&column=Measure_ID", StringComparison.Ordinal))
            {
                return """[{"Measure_ID":"allmcpaty1_f"}]""";
            }

            // A full first page (the page size), then a short one: the CCN/NPI pairs of one measure, a facility per row.
            var offset = int.Parse(url[(url.LastIndexOf("offset=", StringComparison.Ordinal) + 7)..], System.Globalization.CultureInfo.InvariantCulture);
            var count = offset == 0 ? DialysisNpiSource.PageSize : 3;
            var rows = Enumerable.Range(offset, count).Select(i => i switch
            {
                5000 => """{"CCN":"33a001","NPI":"1000000004"}""",           // lower case is the same CCN
                5001 => """{"CCN":"332301","NPI":"0000000000"}""",           // no NPI
                5002 => """{"CCN":"12","NPI":"1000000004"}""",               // no CCN
                _ => $$"""{"CCN":"{{(i % 2 == 0 ? "012306" : "012500")}}","NPI":"{{1000000000 + i}}"}""",
            });
            return "[" + string.Join(",", rows) + "]";
        });
        var context = new DatasetContext(new LoaderOptions(), new Database("Server=localhost;Database=unused"), new HttpClient(handler), Logger.None);

        var pairs = await DialysisNpiSource.FetchAsync(context, new Uri("https://cms.test/data-api/v1/dataset/dfr-2026/data"), _ct);

        Assert.Equal(DialysisNpiSource.PageSize + 1, pairs.Count);
        Assert.Contains(("33A001", "1000000004"), pairs);
        Assert.DoesNotContain(pairs, p => p.Npi == "0000000000" || p.Ccn == "12");
        Assert.Equal(
            [
                "https://cms.test/data-api/v1/dataset/dfr-2026/data?size=1&column=Measure_ID",
                "https://cms.test/data-api/v1/dataset/dfr-2026/data?column=CCN,NPI&filter%5BMeasure_ID%5D=allmcpaty1_f&size=5000&offset=0",
                "https://cms.test/data-api/v1/dataset/dfr-2026/data?column=CCN,NPI&filter%5BMeasure_ID%5D=allmcpaty1_f&size=5000&offset=5000",
            ], requests);
    }

    [Fact]
    public async Task An_empty_dialysis_report_fails_instead_of_emptying_the_table()
    {
        var context = new DatasetContext(new LoaderOptions(), new Database("Server=localhost;Database=unused"), new HttpClient(new Handler(_ => "[]")), Logger.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => DialysisNpiSource.FetchAsync(context, new Uri("https://cms.test/data"), _ct));
    }

    private sealed class Handler(Func<string, string> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(request.RequestUri!.AbsoluteUri)) });
    }
}
