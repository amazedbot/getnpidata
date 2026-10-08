using System.Text;
using Npi.Loader.Reference;

namespace Npi.Loader.Tests;

public class ReferenceParserTests
{
    [Fact]
    public void The_newest_nucc_csv_is_found_on_the_page()
    {
        var latest = NuccTaxonomy.FindLatest(Fixtures.Text("reference/nucc_csv_page.html"), new Uri("https://www.nucc.org/index.php/x/csv"));

        Assert.NotNull(latest);
        Assert.Equal("261", latest.Value.Version);
        Assert.Equal("https://www.nucc.org/images/stories/CSV/nucc_taxonomy_261.csv", latest.Value.Url.AbsoluteUri);
    }

    [Fact]
    public void A_page_without_csv_links_has_no_latest_version() =>
        Assert.Null(NuccTaxonomy.FindLatest("<a href='taxonomy.pdf'>x</a>", new Uri("https://www.nucc.org/")));

    [Fact]
    public void The_real_nucc_csv_format_parses()
    {
        var codes = NuccTaxonomy.Parse(File.OpenRead(Fixtures.Path("reference/nucc_taxonomy_sample.csv")));

        Assert.Equal(6, codes.Count);
        var chiro = codes.Single(c => c.Code == "111N00000X");
        Assert.Equal("Chiropractic Providers", chiro.Grouping);
        Assert.Equal("Chiropractor", chiro.Classification);
        Assert.Null(chiro.Specialization);
        Assert.Equal("Individual", chiro.Section);
        Assert.Contains(",", chiro.Definition); // quoted field with commas
        Assert.Contains(codes, c => c.Classification == "Chiropractor" && c.Specialization is not null);
    }

    [Fact]
    public void A_changed_nucc_header_is_rejected()
    {
        var csv = "Code,Grouping,Classification\n111N00000X,Chiropractic Providers,Chiropractor\n";

        Assert.Throws<InvalidDataException>(() => NuccTaxonomy.Parse(new MemoryStream(Encoding.UTF8.GetBytes(csv))));
    }

    [Fact]
    public void The_hud_crosswalk_parses_and_skips_rows_without_a_county()
    {
        var crosswalk = HudZipCounty.Parse(File.OpenRead(Fixtures.Path("reference/hud_zip_county_sample.json")));

        Assert.Equal("2026Q2", crosswalk.Version);
        Assert.Equal(2, crosswalk.Skipped); // 77352 → "48" and 96799 → "60" are state-level only
        Assert.DoesNotContain(crosswalk.Rows, r => r.Zip5 is "77352" or "96799");
        var holtsville = Assert.Single(crosswalk.Rows, r => r.Zip5 == "00501");
        Assert.Equal(("36103", 1m, 0m), (holtsville.CountyFips, holtsville.TotRatio, holtsville.ResRatio));
        Assert.True(crosswalk.Rows.Count(r => r.Zip5 == "00602") > 2); // a ZIP in several counties keeps them all
        Assert.All(crosswalk.Rows, r => Assert.InRange(r.TotRatio, 0m, 1m));
    }

    [Fact]
    public void A_crosswalk_of_another_type_is_rejected()
    {
        var json = """{"data":{"year":"2026","quarter":"2","crosswalk_type":"zip-tract","results":[]}}""";

        Assert.Throws<InvalidDataException>(() => HudZipCounty.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json))));
    }

    [Fact]
    public void The_newest_gazetteer_year_is_found() =>
        Assert.Equal(2026, CensusGeography.FindLatestGazetteerYear(Fixtures.Text("reference/gazetteer_listing.html")));

    [Fact]
    public void Gazetteer_files_parse_with_current_county_names_and_centroids()
    {
        var counties = CensusGeography.ParseGazetteerCounties(File.OpenRead(Fixtures.Path("reference/2026_Gaz_counties_sample.txt")));
        var centroids = CensusGeography.ParseGazetteerZcta(File.OpenRead(Fixtures.Path("reference/2026_Gaz_zcta_sample.txt")));

        Assert.Contains(new County("36103", "NY", "Suffolk County", CensusGeography.GazetteerSource), counties);
        Assert.Contains(new County("09110", "CT", "Capitol Planning Region", CensusGeography.GazetteerSource), counties);
        Assert.Contains(counties, c => c is { CountyFips: "35013", Name: "Doña Ana County" }); // UTF-8
        Assert.Equal(new ZipCentroid("11701", 40.682177m, -73.414596m), Assert.Single(centroids, c => c.Zip5 == "11701")); // Amityville, NY
    }

    [Fact]
    public void Territories_missing_from_the_gazetteer_come_from_the_2020_codes_file()
    {
        var gazetteer = CensusGeography.ParseGazetteerCounties(File.OpenRead(Fixtures.Path("reference/2026_Gaz_counties_sample.txt")));
        var codes2020 = CensusGeography.ParseCountyCodes2020(File.OpenRead(Fixtures.Path("reference/national_county2020_sample.txt")));

        var merged = CensusGeography.Merge(gazetteer, codes2020);

        Assert.Contains(new County("66010", "GU", "Guam", CensusGeography.CountyCodes2020Source), merged);
        Assert.Contains(merged, c => c is { CountyFips: "78030", Name: "St. Thomas Island" });
        Assert.Single(merged, c => c.CountyFips == "01001"); // the Gazetteer wins when both have it
        Assert.DoesNotContain(merged, c => c.CountyFips == "09001"); // CT's pre-2022 counties are retired; HUD uses the planning regions
    }

    [Fact]
    public void A_malformed_gazetteer_line_is_rejected()
    {
        var txt = "USPS|GEOID|NAME\nNY|36103\n";

        Assert.Throws<InvalidDataException>(() => CensusGeography.ParseGazetteerCounties(new MemoryStream(Encoding.UTF8.GetBytes(txt))));
    }
}
