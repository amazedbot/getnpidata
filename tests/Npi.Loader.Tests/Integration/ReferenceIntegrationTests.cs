using System.IO.Compression;
using System.Net;
using System.Text;
using Npi.Loader.Reference;
using Serilog.Core;

namespace Npi.Loader.Tests.Integration;

/// <summary>Stage 2 reference loads into a scratch database, with NUCC/HUD/Census served from fixtures.</summary>
public sealed class ReferenceIntegrationTests
{
    private const string Token = "test-token";
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task All_reference_tables_load_from_their_sources()
    {
        await using var db = await TestDatabase.CreateAsync();
        var server = new FakeSources();

        Assert.True(await Loader(db, server).RefreshAllAsync(force: false, _ct));

        Assert.Equal(6L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM taxonomy_codes"));
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM taxonomy_codes WHERE Classification = 'Chiropractor'"));
        Assert.Equal("261", await db.ScalarAsync<string>("SELECT Nucc_Version FROM taxonomy_codes WHERE Taxonomy_Code = '111N00000X'"));

        // "All Chiropractors in Suffolk County, NY": ZIP 11701 (Amityville) straddles Nassau and Suffolk;
        // a ZIP matches every county it overlaps (CLAUDE.md §2).
        Assert.Equal(["Nassau County", "Suffolk County"], await db.QueryAsync<string>(
            "SELECT c.county_name FROM zip_county z JOIN county c USING (county_fips) WHERE z.zip5 = '11701' ORDER BY c.county_name"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM zip_county WHERE zip5 IN ('77352', '96799')"));
        Assert.True(await db.ScalarAsync<long>("SELECT COUNT(*) FROM zip_county WHERE zip5 = '00602'") > 2);
        Assert.Equal("Bearer " + Token, server.HudAuthorization);

        Assert.Equal("Guam", await db.ScalarAsync<string>("SELECT county_name FROM county WHERE county_fips = '66010'"));
        Assert.Equal("Doña Ana County", await db.ScalarAsync<string>("SELECT county_name FROM county WHERE county_fips = '35013'"));
        Assert.Equal(5L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM zip_centroid"));

        Assert.Equal([("census_gazetteer", "2026"), ("hud_zip_county", "2026Q2"), ("nucc", "261")],
            await db.QueryAsync<(string, string)>("SELECT source, version FROM reference_data ORDER BY source"));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND (table_name LIKE '%\\_staging' OR table_name IN ('county_old', 'zip_county_old', 'zip_centroid_old'))"));
    }

    [Fact]
    public async Task Unchanged_sources_are_not_downloaded_again()
    {
        await using var db = await TestDatabase.CreateAsync();
        var server = new FakeSources();
        Assert.True(await Loader(db, server).RefreshAllAsync(force: false, _ct));
        server.Requests.Clear();

        Assert.True(await Loader(db, server).RefreshAllAsync(force: false, _ct));

        // Only the two index pages are read; no CSV, HUD call or Gazetteer zip.
        Assert.Equal(["https://nucc.test/csv", "https://census.test/gazetteer/"], server.Requests);
    }

    [Fact]
    public async Task Hud_is_checked_again_after_the_refresh_interval()
    {
        await using var db = await TestDatabase.CreateAsync();
        var server = new FakeSources();
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
        Assert.True(await Loader(db, server, clock: clock).RefreshAllAsync(force: false, _ct));

        clock.Now = clock.Now.AddDays(15);
        server.Requests.Clear();
        Assert.True(await Loader(db, server, clock: clock).RefreshAllAsync(force: false, _ct));

        Assert.Contains("https://hud.test/usps", server.Requests);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM reference_data WHERE source = 'hud_zip_county' AND checked_at > loaded_at"));
    }

    [Fact]
    public async Task A_new_nucc_release_updates_codes_and_keeps_retired_ones()
    {
        await using var db = await TestDatabase.CreateAsync();
        var server = new FakeSources();
        Assert.True(await Loader(db, server).RefreshAllAsync(force: false, _ct));

        var csv = Fixtures.Text("reference/nucc_taxonomy_sample.csv").Split('\n');
        var changed = string.Join('\n', csv.Where(l => !l.StartsWith("193200000X", StringComparison.Ordinal)))
            .Replace(",Chiropractor,,", ",Chiropractor (renamed),,", StringComparison.Ordinal);
        server.NuccPage = server.NuccPage.Replace("nucc_taxonomy_261.csv", "nucc_taxonomy_270.csv", StringComparison.Ordinal);
        server.NuccCsv = Encoding.UTF8.GetBytes(changed);

        Assert.True(await Loader(db, server).RefreshAllAsync(force: false, _ct));

        Assert.Equal(("Chiropractor (renamed)", "270"), (await db.QueryAsync<(string, string)>(
            "SELECT Classification, Nucc_Version FROM taxonomy_codes WHERE Taxonomy_Code = '111N00000X'")).Single());
        Assert.Equal("261", await db.ScalarAsync<string>("SELECT Nucc_Version FROM taxonomy_codes WHERE Taxonomy_Code = '193200000X'"));
    }

    [Fact]
    public async Task A_missing_hud_token_fails_only_hud()
    {
        await using var db = await TestDatabase.CreateAsync();

        Assert.False(await Loader(db, new FakeSources(), token: "").RefreshAllAsync(force: false, _ct));

        Assert.Equal(["census_gazetteer", "nucc"], await db.QueryAsync<string>("SELECT source FROM reference_data ORDER BY source"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM zip_county"));
    }

    private static ReferenceLoader Loader(TestDatabase db, FakeSources server, string token = Token, TimeProvider? clock = null)
    {
        var options = new LoaderOptions
        {
            NuccPageUrl = "https://nucc.test/csv",
            HudApiUrl = "https://hud.test/usps",
            HudApiToken = token,
            HudRefreshDays = 14,
            MinTaxonomyCodes = 1, // the fixture has 6 codes
            GazetteerBaseUrl = "https://census.test/gazetteer/",
            CountyCodes2020Url = "https://census.test/national_county2020.txt",
        };
        return new ReferenceLoader(options, db.Database, new HttpClient(server), Logger.None, clock);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeSources : HttpMessageHandler
    {
        public string NuccPage { get; set; } = Fixtures.Text("reference/nucc_csv_page.html");

        public byte[] NuccCsv { get; set; } = File.ReadAllBytes(Fixtures.Path("reference/nucc_taxonomy_sample.csv"));

        public List<string> Requests { get; } = [];

        public string? HudAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            byte[]? body = url switch
            {
                "https://nucc.test/csv" => Encoding.UTF8.GetBytes(NuccPage),
                _ when url.StartsWith("https://nucc.test/images/stories/CSV/nucc_taxonomy_", StringComparison.Ordinal) => NuccCsv,
                "https://hud.test/usps" => Hud(request),
                "https://census.test/gazetteer/" => Encoding.UTF8.GetBytes(Fixtures.Text("reference/gazetteer_listing.html")),
                "https://census.test/gazetteer/2026_Gazetteer/2026_Gaz_counties_national.zip" => Zip("2026_Gaz_counties_national.txt", "reference/2026_Gaz_counties_sample.txt"),
                "https://census.test/gazetteer/2026_Gazetteer/2026_Gaz_zcta_national.zip" => Zip("2026_Gaz_zcta_national.txt", "reference/2026_Gaz_zcta_sample.txt"),
                "https://census.test/national_county2020.txt" => File.ReadAllBytes(Fixtures.Path("reference/national_county2020_sample.txt")),
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }

        private byte[] Hud(HttpRequestMessage request)
        {
            HudAuthorization = request.Headers.Authorization?.ToString();
            return File.ReadAllBytes(Fixtures.Path("reference/hud_zip_county_sample.json"));
        }

        private static byte[] Zip(string entryName, string fixture)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var entry = zip.CreateEntry(entryName).Open();
                entry.Write(File.ReadAllBytes(Fixtures.Path(fixture)));
            }

            return ms.ToArray();
        }
    }
}
