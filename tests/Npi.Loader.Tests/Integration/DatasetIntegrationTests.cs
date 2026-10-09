using System.Net;
using System.Net.Http.Headers;
using Npi.Loader.Datasets;
using Serilog.Core;

namespace Npi.Loader.Tests.Integration;

/// <summary>Stage 5.5 dataset loads into a scratch database, with the OIG and CMS files served from fixtures.</summary>
public sealed class DatasetIntegrationTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("npi-ds-").FullName;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Compliance_datasets_load_and_record_their_versions()
    {
        await using var db = await TestDatabase.CreateAsync();
        var server = new FakeSources();

        Assert.True(await Loader(db, server, new FakeClock()).RefreshAsync(force: false, only: null, _ct));

        // LEIE: quoted fields with commas and braces, CRLF lines, 0000000000 → NULL NPI, 00000000 → NULL date.
        Assert.Equal(4L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM oig_exclusion"));
        Assert.Equal(("#1 MARKETING SERVICE, INC", (string?)null), (await db.QueryAsync<(string, string?)>(
            "SELECT business_name, npi FROM oig_exclusion WHERE npi IS NULL")).Single());
        Assert.Equal(("1 BROADWAY, SUITE {2}", "1128b4", new DateTime(2025, 1, 15), (DateTime?)null), (await db.QueryAsync<(string, string, DateTime, DateTime?)>(
            "SELECT address, exclusion_type, exclusion_date, reinstatement_date FROM oig_exclusion WHERE npi = '1000000004'")).Single());
        Assert.Equal((new DateTime(2024, 6, 1), "NY"), (await db.QueryAsync<(DateTime, string)>(
            "SELECT waiver_date, waiver_state FROM oig_exclusion WHERE waiver_date IS NOT NULL")).Single());

        // Opt-outs: the newest catalog period (August), one NPI listed twice.
        Assert.Equal(3L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM medicare_opt_out"));
        Assert.Equal((new DateTime(2099, 1, 30), (sbyte)1), (await db.QueryAsync<(DateTime, sbyte)>(
            "SELECT end_date, can_order_refer FROM medicare_opt_out WHERE npi = '1000000012' ORDER BY end_date DESC LIMIT 1")).Single());
        Assert.Contains("https://cms.test/files/OptOut_August2026.csv", server.Requests);

        Assert.Equal([((sbyte)1, (sbyte)1, (sbyte)0), ((sbyte)0, (sbyte)0, (sbyte)0)], await db.QueryAsync<(sbyte, sbyte, sbyte)>(
            "SELECT part_b, dme, hha FROM medicare_order_referring WHERE npi IN ('1000000004', '1000000012') ORDER BY npi"));

        Assert.Equal(
            [("cms_opt_out", "2026-08-31 OptOut_August2026.csv"), ("cms_order_referring", "2026-10-08 OrderReferring_20261008.csv"),
             ("oig_leie", "2026-10-01T12:00:00Z 827")],
            await db.QueryAsync<(string, string)>("SELECT source, version FROM reference_data ORDER BY source"));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND (table_name LIKE '%\\_staging' " +
            "OR table_name IN ('oig_exclusion_old', 'medicare_opt_out_old', 'medicare_order_referring_old'))"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "datasets"))); // downloads are deleted after loading
    }

    [Fact]
    public async Task Unchanged_datasets_are_not_downloaded_again()
    {
        await using var db = await TestDatabase.CreateAsync();
        var server = new FakeSources();
        var clock = new FakeClock();
        Assert.True(await Loader(db, server, clock).RefreshAsync(force: false, only: null, _ct));

        // Within the check interval nothing is requested at all.
        server.Requests.Clear();
        Assert.True(await Loader(db, server, clock).RefreshAsync(force: false, only: null, _ct));
        Assert.Empty(server.Requests);

        // A day later only the version probes go out: the LEIE HEAD and the catalog, but no files.
        clock.Advance(TimeSpan.FromDays(1));
        Assert.True(await Loader(db, server, clock).RefreshAsync(force: false, only: null, _ct));
        Assert.Equal(["HEAD https://oig.test/UPDATED.csv", "https://cms.test/data.json"], server.Requests);

        // The datasets command forces a reload of one source.
        server.Requests.Clear();
        Assert.True(await Loader(db, server, clock).RefreshAsync(force: true, only: "cms_order_referring", _ct));
        Assert.Contains("https://cms.test/files/OrderReferring_20261008.csv", server.Requests);
        Assert.False(await Loader(db, server, clock).RefreshAsync(force: true, only: "no_such_source", _ct));
    }

    [Fact]
    public async Task A_failing_source_does_not_stop_the_others_or_replace_data_with_too_few_rows()
    {
        await using var db = await TestDatabase.CreateAsync();
        var server = new FakeSources { LeieAvailable = false };

        Assert.False(await Loader(db, server, new FakeClock()).RefreshAsync(force: false, only: null, _ct));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM oig_exclusion"));
        Assert.Equal(4L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM medicare_order_referring"));

        // A release with far fewer rows than the current table is refused (≥ 95% check), keeping the old data.
        await db.ExecuteAsync("INSERT INTO medicare_order_referring (npi, part_b) SELECT LPAD(n, 10, '9'), 1 FROM (SELECT 1 n UNION SELECT 2 UNION SELECT 3 UNION SELECT 4 UNION SELECT 5) x");
        Assert.False(await Loader(db, server, new FakeClock()).RefreshAsync(force: true, only: "cms_order_referring", _ct));
        Assert.Equal(9L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM medicare_order_referring"));
    }

    private DatasetLoader Loader(TestDatabase db, FakeSources server, FakeClock clock) =>
        new(new LoaderOptions
            {
                WorkFolder = _folder,
                LogFolder = _folder,
                DownloadAttempts = 1,
                CmsCatalogUrl = "https://cms.test/data.json",
                LeieUrl = "https://oig.test/UPDATED.csv",
            },
            db.Database, new HttpClient(server), Logger.None, clock: clock);

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeSources : HttpMessageHandler
    {
        public bool LeieAvailable { get; init; } = true;

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(request.Method == HttpMethod.Head ? "HEAD " + url : url);
            string? fixture = url switch
            {
                "https://oig.test/UPDATED.csv" when LeieAvailable => "datasets/leie_sample.csv",
                "https://cms.test/data.json" => "datasets/cms_catalog_sample.json",
                "https://cms.test/files/OptOut_August2026.csv" => "datasets/optout_sample.csv",
                "https://cms.test/files/OrderReferring_20261008.csv" => "datasets/orderref_sample.csv",
                _ => null,
            };
            if (fixture is null)
            {
                return Task.FromResult(new HttpResponseMessage(url.StartsWith("https://oig.test", StringComparison.Ordinal)
                    ? HttpStatusCode.InternalServerError
                    : HttpStatusCode.NotFound));
            }

            var bytes = File.ReadAllBytes(Fixtures.Path(fixture));
            var content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : bytes);
            content.Headers.ContentLength = bytes.Length;
            if (url.StartsWith("https://oig.test", StringComparison.Ordinal))
            {
                content.Headers.LastModified = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
            }

            content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
