using System.Text;
using Npi.Loader.Datasets;

namespace Npi.Loader.Tests;

public class DatasetCatalogTests
{
    private static CmsCatalog Catalog()
    {
        using var stream = File.OpenRead(Fixtures.Path("datasets/cms_catalog_sample.json"));
        return CmsCatalog.Parse(stream);
    }

    [Fact]
    public void Newest_release_is_chosen_by_data_period_not_by_modified_date()
    {
        // The January 2019 release was re-modified most recently; the August 2026 period is still the newest.
        var release = Catalog().FindLatest("Opt Out Affidavits");

        Assert.NotNull(release);
        Assert.Equal("https://cms.test/files/OptOut_August2026.csv", release.Url.AbsoluteUri);
        Assert.Equal("2026-08-31 OptOut_August2026.csv", release.Version);
    }

    [Fact]
    public void Title_match_ignores_case_and_needs_a_csv()
    {
        Assert.Equal("https://cms.test/files/OrderReferring_20261008.csv", Catalog().FindLatest("order and referring")?.Url.AbsoluteUri);
        Assert.Null(Catalog().FindLatest("Some Dataset Without CSV"));
        Assert.Null(Catalog().FindLatest("Unknown"));
    }

    [Fact]
    public void Provider_data_catalog_entry_gives_the_current_file()
    {
        const string json = """
            {"identifier":"mj5m-pzi6","title":"National Downloadable File","modified":"2026-08-18",
             "distribution":[{"downloadURL":"https://pdc.test/resources/abc_123/DAC_NationalDownloadableFile.csv","mediaType":"text/csv"}]}
            """;

        var release = ProviderDataCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)), "mj5m-pzi6");

        Assert.Equal("2026-08-18 DAC_NationalDownloadableFile.csv", release.Version);
        Assert.Equal("https://pdc.test/resources/abc_123/DAC_NationalDownloadableFile.csv", release.Url.AbsoluteUri);
    }

    [Fact]
    public void Open_payments_uses_the_newest_general_payment_year()
    {
        using var stream = File.OpenRead(Fixtures.Path("datasets/open_payments_catalog_sample.json"));
        var release = OpenPaymentsSource.ParseCatalog(stream);

        Assert.Equal("2025 OP_DTL_GNRL_PGYR2025_P06302026_06032026.csv", release.Version);
        Assert.Equal(2025, release.DataYear);
        Assert.EndsWith("/OP_DTL_GNRL_PGYR2025_P06302026_06032026.csv", release.Url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_payments_summaries_are_found_by_their_exact_title()
    {
        using var stream = File.OpenRead(Fixtures.Path("datasets/open_payments_catalog_sample.json"));
        var release = OpenPaymentsSummarySource.ParseCatalog(stream, "Payments grouped by physician (distinct) for all years");

        Assert.Equal("PBLCTN_PHYSN_NON_PHYSN_PRCTNR_SMRY_P06302026_06032026.csv", release.Version);

        using var again = File.OpenRead(Fixtures.Path("datasets/open_payments_catalog_sample.json"));
        Assert.Throws<InvalidDataException>(() => OpenPaymentsSummarySource.ParseCatalog(again, "Payments grouped by physician"));
    }

    [Theory]
    [InlineData(CsvValue.Npi, "IF(TRIM(@c0) REGEXP '^[0-9]{10}$' AND TRIM(@c0) <> '0000000000', TRIM(@c0), NULL)")]
    [InlineData(CsvValue.DateYmd, "STR_TO_DATE(NULLIF(NULLIF(TRIM(@c0), ''), '00000000'), '%Y%m%d')")]
    [InlineData(CsvValue.YesNo, "CASE UPPER(TRIM(@c0)) WHEN 'Y' THEN 1 WHEN 'YES' THEN 1 WHEN 'N' THEN 0 WHEN 'NO' THEN 0 END")]
    public void Values_are_converted_in_sql(CsvValue kind, string expected) =>
        Assert.Equal(expected, CsvTableLoader.Expression("@c0", kind));
}
