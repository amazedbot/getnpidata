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
            [("cc_clinicians", "2026-08-18 DAC_NationalDownloadableFile.csv"), ("cc_facility_affiliations", "2026-08-18 Facility_Affiliation.csv"),
             ("cc_hospitals", "2026-07-22 Hospital_General_Information.csv"), ("cc_nursing_homes", "2026-09-30 NH_ProviderInfo_Sep2026.csv"),
             ("cms_facility_enrollments", "2026-07-31 Hospital_Enrollments_2026.07.31.csv | 2026-07-31 SNF_Enrollments_2026.07.31.csv"),
             ("cms_opt_out", "2026-08-31 OptOut_August2026.csv"), ("cms_order_referring", "2026-10-08 OrderReferring_20261008.csv"),
             ("cms_part_d_by_provider", "2024-12-31 mup_dpr_dy24_npi.csv"), ("cms_physician_by_provider", "2024-12-31 MUP_PHY_D24_Prov.csv"),
             ("cms_physician_by_service", "2024-12-31 MUP_PHY_D24_Prov_Svc.csv"),
             ("oig_leie", "2026-10-01T12:00:00Z 827")],
            await db.QueryAsync<(string, string)>("SELECT source, version FROM reference_data ORDER BY source"));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND (table_name LIKE '%\\_staging' " +
            "OR table_name LIKE 'cc\\_%\\_old' OR table_name LIKE 'cms\\_%\\_old' OR table_name LIKE 'medicare\\_%\\_old' OR table_name IN ('oig_exclusion_old', 'medicare_opt_out_old', 'medicare_order_referring_old'))"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "datasets"))); // downloads are deleted after loading
    }

    [Fact]
    public async Task Care_compare_and_facility_datasets_load_and_summarize()
    {
        await using var db = await TestDatabase.CreateAsync();
        Assert.True(await Loader(db, new FakeSources(), new FakeClock()).RefreshAsync(force: false, only: null, _ct));

        // Three DAC rows for 1000000004 (two groups, two addresses) become one clinician row: accepts assignment
        // because one enrollment says Y, telehealth because one practice offers it.
        Assert.Equal(("NEW YORK CHIROPRACTIC COLLEGE", (short)2005, "CHIROPRACTIC", (sbyte)1, (sbyte)1), (await db.QueryAsync<(string, short, string, sbyte, sbyte)>(
            "SELECT medical_school, graduation_year, primary_specialty, accepts_assignment, telehealth FROM cc_clinician WHERE npi = '1000000004'")).Single());
        Assert.Equal(((sbyte)0, (sbyte)0, "INTERNAL MEDICINE"), (await db.QueryAsync<(sbyte, sbyte, string)>(
            "SELECT accepts_assignment, telehealth, secondary_specialties FROM cc_clinician WHERE npi = '1000000012'")).Single());
        Assert.Equal([("1234567890", "ISLAND SPINE, PLLC", 12, (sbyte)1), ("2234567890", "SOUTH SHORE MEDICAL GROUP", 250, (sbyte)0)],
            await db.QueryAsync<(string, string, int, sbyte)>("SELECT org_pac_id, group_name, members, accepts_assignment FROM cc_group WHERE npi = '1000000004' ORDER BY org_pac_id"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM cc_group WHERE npi = '1000000012'"));

        Assert.Equal(4L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM cc_facility_affiliation"));
        Assert.Equal([("330045", "GOOD SAMARITAN HOSPITAL, WEST ISLIP", (sbyte?)1, (sbyte?)3), ("330999", "NOT RATED HOSPITAL", (sbyte?)0, (sbyte?)null)],
            await db.QueryAsync<(string, string, sbyte?, sbyte?)>("SELECT ccn, name, emergency_services, overall_rating FROM cms_hospital ORDER BY ccn"));
        Assert.Equal((157, (sbyte?)4, (sbyte?)null, (sbyte?)5), (await db.QueryAsync<(int, sbyte?, sbyte?, sbyte?)>(
            "SELECT certified_beds, overall_rating, staffing_rating, quality_rating FROM cms_nursing_home WHERE ccn = '335001'")).Single());

        // Windows-1252 enrollment files load (their names contain bytes that aren't valid UTF-8).
        Assert.Equal([("330045", "1000000038", "hospital"), ("335001", "1000000046", "nursing_home")],
            await db.QueryAsync<(string, string, string)>("SELECT ccn, npi, kind FROM cms_facility_npi ORDER BY ccn"));
    }

    [Fact]
    public async Task Medicare_activity_loads_with_its_data_year_and_top_services()
    {
        await using var db = await TestDatabase.CreateAsync();
        Assert.True(await Loader(db, new FakeSources(), new FakeClock()).RefreshAsync(force: false, only: null, _ct));

        // The newest data year (2024, not 2023); suppressed counts stay NULL; long decimals load without warnings.
        Assert.Equal(((short)2024, 212, 1840.0, 31234.56789012, 0.9123), (await db.QueryAsync<(short, int, double, double, double)>(
            "SELECT data_year, beneficiaries, services, payment_amount, avg_risk_score FROM medicare_utilization WHERE npi = '1000000004'")).Single());
        Assert.Equal(((sbyte?)0, (int?)null), (await db.QueryAsync<(sbyte?, int?)>(
            "SELECT participating, beneficiaries FROM medicare_utilization WHERE npi = '1000000012'")).Single());

        // Top five per NPI by services, ties broken by patients.
        Assert.Equal(["98940", "98941", "97140", "97012", "97110"],
            await db.QueryAsync<string>("SELECT hcpcs FROM medicare_top_service WHERE npi = '1000000004' ORDER BY service_rank"));
        Assert.Equal(("Chiropractic manipulative treatment, spinal, 98940", (short)2024, "O"), (await db.QueryAsync<(string, short, string)>(
            "SELECT description, data_year, place_of_service FROM medicare_top_service WHERE npi = '1000000004' AND service_rank = 1")).Single());
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM medicare_top_service WHERE npi = '1000000012'"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'medicare_service_raw_staging'"));

        Assert.Equal((1200, 45678.91, 400, 2.0), (await db.QueryAsync<(int, double, int, double)>(
            "SELECT claims, drug_cost, brand_claims, opioid_rate FROM medicare_part_d WHERE npi = '1000000012'")).Single());
        Assert.Equal(((int?)null, (double?)null), (await db.QueryAsync<(int?, double?)>(
            "SELECT brand_claims, opioid_rate FROM medicare_part_d WHERE npi = '1000000046'")).Single());
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

        // A day later only the version probes go out: the LEIE HEAD, the CMS catalog (once) and the Care Compare entries, but no files.
        clock.Advance(TimeSpan.FromDays(1));
        Assert.True(await Loader(db, server, clock).RefreshAsync(force: false, only: null, _ct));
        Assert.Equal(
            ["HEAD https://oig.test/UPDATED.csv", "https://cms.test/data.json", "https://pdc.test/items/mj5m-pzi6", "https://pdc.test/items/27ea-46a8",
             "https://pdc.test/items/xubh-q36u", "https://pdc.test/items/4pq5-n9py"],
            server.Requests);

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
                ProviderDataMetastoreUrl = "https://pdc.test/items/",
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

        private static readonly Dictionary<string, (string Modified, string File)> ProviderData = new()
        {
            ["https://pdc.test/items/mj5m-pzi6"] = ("2026-08-18", "DAC_NationalDownloadableFile.csv"),
            ["https://pdc.test/items/27ea-46a8"] = ("2026-08-18", "Facility_Affiliation.csv"),
            ["https://pdc.test/items/xubh-q36u"] = ("2026-07-22", "Hospital_General_Information.csv"),
            ["https://pdc.test/items/4pq5-n9py"] = ("2026-09-30", "NH_ProviderInfo_Sep2026.csv"),
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(request.Method == HttpMethod.Head ? "HEAD " + url : url);
            if (ProviderData.TryGetValue(url, out var entry))
            {
                var json = $$"""{"modified":"{{entry.Modified}}","distribution":[{"downloadURL":"https://pdc.test/files/{{entry.File}}","mediaType":"text/csv"}]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }

            string? fixture = url switch
            {
                "https://oig.test/UPDATED.csv" when LeieAvailable => "datasets/leie_sample.csv",
                "https://cms.test/data.json" => "datasets/cms_catalog_sample.json",
                "https://cms.test/files/OptOut_August2026.csv" => "datasets/optout_sample.csv",
                "https://cms.test/files/OrderReferring_20261008.csv" => "datasets/orderref_sample.csv",
                "https://cms.test/files/Hospital_Enrollments_2026.07.31.csv" => "datasets/hospital_enrollments_sample.csv",
                "https://cms.test/files/SNF_Enrollments_2026.07.31.csv" => "datasets/snf_enrollments_sample.csv",
                "https://pdc.test/files/DAC_NationalDownloadableFile.csv" => "datasets/dac_sample.csv",
                "https://pdc.test/files/Facility_Affiliation.csv" => "datasets/facaff_sample.csv",
                "https://pdc.test/files/Hospital_General_Information.csv" => "datasets/hospitals_sample.csv",
                "https://pdc.test/files/NH_ProviderInfo_Sep2026.csv" => "datasets/nursing_homes_sample.csv",
                "https://cms.test/files/MUP_PHY_D24_Prov.csv" => "datasets/physician_by_provider_sample.csv",
                "https://cms.test/files/MUP_PHY_D24_Prov_Svc.csv" => "datasets/physician_by_service_sample.csv",
                "https://cms.test/files/mup_dpr_dy24_npi.csv" => "datasets/part_d_by_provider_sample.csv",
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
