using Npi.Core.Search;
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
             ("cc_hcahps", "2026-07-22 HCAHPS-Hospital.csv"), ("cc_home_health", "2026-05-27 HH_Provider_Jul2026.csv"),
             ("cc_hospices", "2026-08-19 Hospice_General-Information_Aug2026_2.csv | 2026-08-07 Provider_CAHPS_Hospice_Survey_Data_Aug2026.csv"),
             ("cc_hospitals", "2026-07-22 Hospital_General_Information.csv"), ("cc_mips", "PY 2024 2026-08-18 ec_score_file.csv"),
             ("cc_nursing_homes", "2026-09-30 NH_ProviderInfo_Sep2026.csv"),
             ("census_county_population", "vintage 2025"),
             ("cms_facility_enrollments", "2026-07-31 Hospital_Enrollments_2026.07.31.csv | 2026-07-31 SNF_Enrollments_2026.07.31.csv"
                 + " | 2026-07-17 HHA_Enrollments_2026.07.17.csv | 2026-07-17 Hospice_Enrollments_2026.07.17.csv"),
             ("cms_opt_out", "2026-08-31 OptOut_August2026.csv"), ("cms_order_referring", "2026-10-08 OrderReferring_20261008.csv"),
             ("cms_part_d_by_provider", "2024-12-31 mup_dpr_dy24_npi.csv"), ("cms_physician_by_provider", "2024-12-31 MUP_PHY_D24_Prov.csv"),
             ("cms_physician_by_service", "2024-12-31 MUP_PHY_D24_Prov_Svc.csv"), ("hrsa_hpsa", "HPSA 2026-10-08"),
             ("oig_leie", "2026-10-01T12:00:00Z 827"), ("open_payments", "2025 OP_DTL_GNRL_PGYR2025_P06302026_06032026.csv"),
             ("open_payments_companies", "PBLCTN_SMRY_BY_CR_BY_AMGPO_PGYRall_P06302026_06032026.csv"),
             ("open_payments_years", "PBLCTN_PHYSN_NON_PHYSN_PRCTNR_SMRY_P06302026_06032026.csv"),
             ("state_licenses", "NY 2026-10-02T20:04:24Z | TX 2026-10-02T20:04:24Z | WA 2026-10-02T20:04:24Z | IL 2026-10-02T20:04:24Z | CO 2026-10-02T20:04:24Z")],
            await db.QueryAsync<(string, string)>("SELECT source, version FROM reference_data ORDER BY source"));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND (table_name LIKE '%\\_staging' " +
            "OR table_name LIKE 'cc\\_%\\_old' OR table_name LIKE 'cms\\_%\\_old' OR table_name LIKE 'medicare\\_%\\_old' OR table_name LIKE 'county\\_%\\_old' OR table_name LIKE 'open\\_payments\\_%\\_old' OR table_name IN ('oig_exclusion_old', 'medicare_opt_out_old', 'medicare_order_referring_old'))"));
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
        Assert.Equal([("330045", "1000000038", "hospital"), ("331501", "1000000038", "hospice"), ("335001", "1000000046", "nursing_home"),
                ("337001", "1000000038", "home_health")],
            await db.QueryAsync<(string, string, string)>("SELECT ccn, npi, kind FROM cms_facility_npi ORDER BY ccn"));
    }

    [Fact]
    public async Task State_licenses_match_by_state_number_and_last_name()
    {
        await using var db = await TestDatabase.CreateAsync();
        // Providers with NPPES licenses written the way NPPES has them (punctuation, leading zeros, other prefixes).
        await db.ExecuteAsync(
            """
            INSERT INTO provider (npi, entity_type, last_name, first_name, sort_name) VALUES
              ('1000000004', 1, 'O''BRIEN', 'JOSE', 'O''BRIEN, JOSE'), ('1000000012', 1, 'GARCIA', 'MARIA', 'GARCIA, MARIA'),
              ('1000000020', 1, 'LEE', 'ANN', 'LEE, ANN'), ('1000000038', 1, 'NUÑEZ', 'ELENA', 'NUÑEZ, ELENA'), ('1000000046', 1, 'OTHER', 'PAT', 'OTHER, PAT');
            INSERT INTO provider_taxonomy (npi, slot, taxonomy_code, is_primary, license_no, license_state) VALUES
              ('1000000004', 1, '207Q00000X', 1, 'MD-174744', 'NY'), ('1000000012', 1, '207Q00000X', 1, 'N1234', 'TX'),
              ('1000000020', 1, '207Q00000X', 1, 'MD00012345', 'WA'), ('1000000020', 2, '207Q00000X', 0, '036.098765', 'IL'),
              ('1000000038', 1, '207Q00000X', 1, 'DR.0042345', 'CO'), ('1000000046', 1, '207Q00000X', 1, '1234', 'TX');
            """);
        Assert.True(await Loader(db, new FakeSources(), new FakeClock()).RefreshAsync(force: false, only: "state_licenses", _ct));

        // O'BRIEN = "OBrien" is not equal (the apostrophe), but the second row ("O'BRIEN") is; GARCIA's N1234 matches, and the
        // other TX provider's "1234" doesn't (name); the placeholder "000000" and "Someone Else" never match.
        Assert.Equal(
            [
                ("1000000004", "NY", "action", (DateTime?)new DateTime(2019, 1, 15)), ("1000000012", "TX", "license", new DateTime(2010, 1, 1)),
                ("1000000020", "IL", "license", new DateTime(2024, 5, 1)), ("1000000020", "WA", "license", null),
                ("1000000038", "CO", "license", new DateTime(2021, 7, 11)), ("1000000038", "CO", "license", new DateTime(2023, 10, 9)),
            ],
            await db.QueryAsync<(string, string, string, DateTime?)>(
                "SELECT npi, state, kind, action_date FROM provider_state_license ORDER BY npi, state, action_date"));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'state_license_raw_staging'"));

        // The provider page groups a license's rows and lists its actions, newest first.
        var details = new ProviderDetailService(db.ConnectionString);
        var co = Assert.Single((await details.GetAsync("1000000038", _ct))!.StateLicenses);
        Assert.Equal(("CO", "42345", "Active", new DateOnly(2027, 4, 30)), (co.State, co.LicenseNumber, co.Status, co.ExpirationDate));
        Assert.Equal(["Stipulation", "Letter of Admonition"], co.Actions.Select(a => a.Action));
        Assert.StartsWith("https://www.colorado.gov/", co.VerifyUrl, StringComparison.Ordinal);
        var lee = (await details.GetAsync("1000000020", _ct))!.StateLicenses;
        Assert.Equal([("IL", "Y", 1), ("WA", "Yes", 0)], lee.Select(l => (l.State, l.Discipline, l.Actions.Count)));
        Assert.Equal("FAILURE TO COMPLETE CME", lee[0].Actions[0].Description);
        var ny = Assert.Single((await details.GetAsync("1000000004", _ct))!.StateLicenses);
        Assert.Equal((null, "Censure and reprimand."), (ny.Status, ny.Actions.Single().Action));
    }

    [Fact]
    public async Task Quality_and_outcome_datasets_load()
    {
        await using var db = await TestDatabase.CreateAsync();
        Assert.True(await Loader(db, new FakeSources(), new FakeClock()).RefreshAsync(force: false, only: null, _ct));

        // Hospital outcome counts; "Not Available" → NULL.
        Assert.Equal([("330045", (short?)8, (short?)1, (short?)0, (short?)7, (short?)3, (short?)1, (short?)11, (short?)1, (short?)2),
                ("330999", null, null, null, null, null, null, null, null, null)],
            await db.QueryAsync<(string, short?, short?, short?, short?, short?, short?, short?, short?, short?)>(
                """
                SELECT ccn, mort_measures, mort_better, mort_worse, safety_measures, safety_better, safety_worse, readm_measures, readm_better, readm_worse
                FROM cms_hospital ORDER BY ccn
                """));
        // HCAHPS: only the summary star row per hospital.
        Assert.Equal([("330045", (sbyte?)3, (int?)1392, (double?)17), ("330999", null, null, null)],
            await db.QueryAsync<(string, sbyte?, int?, double?)>("SELECT ccn, star_rating, surveys, response_rate FROM cms_hospital_survey ORDER BY ccn"));
        // Home health half stars; "-" → NULL. Hospice family survey stars.
        Assert.Equal([("337001", (double?)3.5), ("337002", null)],
            await db.QueryAsync<(string, double?)>("SELECT ccn, quality_rating FROM cms_home_health ORDER BY ccn"));
        Assert.Equal(("GOOD SHEPHERD HOSPICE", "Non-Profit", (sbyte?)4), (await db.QueryAsync<(string, string, sbyte?)>(
            "SELECT name, ownership, family_rating FROM cms_hospice WHERE ccn = '331501'")).Single());
        // MIPS: the newest program year (2024), every score kept; a score without an NPI has a NULL npi.
        Assert.Equal([((short)2024, "group", 100.0, (double?)100), ((short)2024, "individual", 87.2722, (double?)null)],
            await db.QueryAsync<(short, string, double, double?)>(
                "SELECT program_year, source, final_score, pi_score FROM cc_mips WHERE npi = '1000000004' ORDER BY source"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM cc_mips WHERE npi IS NULL"));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN ('cms_hcahps_raw_staging', 'cms_hospice_cahps_raw_staging')"));
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
    public async Task Shortage_areas_and_population_load_per_county()
    {
        await using var db = await TestDatabase.CreateAsync();
        Assert.True(await Loader(db, new FakeSources(), new FakeClock()).RefreshAsync(force: false, only: null, _ct));

        // Withdrawn HPSAs are left out; duplicate components count once; a blank score stays NULL.
        Assert.Equal(
            [("35013", "PC", (sbyte)1, 1, (int?)18), ("36047", "MH", (sbyte)0, 2, (int?)19), ("36103", "DH", (sbyte)1, 1, (int?)null),
             ("36103", "PC", (sbyte)0, 1, (int?)14)],
            await db.QueryAsync<(string, string, sbyte, int, int?)>(
                "SELECT county_fips, discipline, whole_county, hpsa_count, max_score FROM county_shortage ORDER BY county_fips, discipline"));

        // County rows only, from the newest vintage (2025), read as Windows-1252.
        Assert.Equal([("35013", 226500), ("36047", 2620000), ("36103", 1530000)],
            await db.QueryAsync<(string, int)>("SELECT county_fips, population FROM county_population ORDER BY county_fips"));
        Assert.Equal(2025, await db.ScalarAsync<int>("SELECT MIN(year) FROM county_population"));
    }

    [Fact]
    public async Task Open_payments_are_summarized_per_npi()
    {
        await using var db = await TestDatabase.CreateAsync();
        Assert.True(await Loader(db, new FakeSources(), new FakeClock()).RefreshAsync(force: false, only: null, _ct));

        // The NPI-less teaching hospital payment is left out; amounts sum exactly to the cent.
        Assert.Equal([("1000000012", (short)2025, 2563.35, 5, 4), ("1000000046", (short)2025, 35.5, 1, 1)],
            await db.QueryAsync<(string, short, double, int, int)>("SELECT npi, program_year, total_amount, records, payers FROM open_payments_summary ORDER BY npi"));
        Assert.Equal([("Consulting Fee", 2500.0, 1), ("Food and Beverage", 63.35, 4)], await db.QueryAsync<(string, double, int)>(
            "SELECT nature, amount, records FROM open_payments_nature WHERE npi = '1000000012' ORDER BY nature"));
        Assert.Equal([("Medtronic USA Inc.", 2500.0), ("Pfizer, Inc.", 40.0), ("AbbVie Inc.", 12.25)], await db.QueryAsync<(string, double)>(
            "SELECT payer, amount FROM open_payments_payer WHERE npi = '1000000012' ORDER BY payer_rank"));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'open_payments_raw_staging'"));
    }

    [Fact]
    public async Task Open_payments_history_sums_profiles_per_year_and_keeps_the_top_companies()
    {
        await using var db = await TestDatabase.CreateAsync();
        Assert.True(await Loader(db, new FakeSources(), new FakeClock()).RefreshAsync(force: false, only: null, _ct));

        // Two CMS profiles of one NPI are summed per year; the "All" rows and the NPI-less row are left out.
        Assert.Equal(
            [
                ("1000000012", (short)2024, 100.5, 3, 0.0, 0.0, 0.0, 0.0),
                ("1000000012", (short)2025, 2563.35, 5, 1000.0, 50000.0, 7000.0, 7500.0),
                ("1000000046", (short)2025, 35.5, 1, 0.0, 0.0, 0.0, 0.0),
            ],
            await db.QueryAsync<(string, short, double, int, double, double, double, double)>(
                """
                SELECT npi, program_year, general_amount, general_records, research_amount, associated_research_amount, invested_amount, interest_value
                FROM open_payments_year ORDER BY npi, program_year
                """));

        // The top five by total over every payment type ("Tiny Co" is sixth); the teaching hospital (no NPI) is left out.
        Assert.Equal(
            [
                ("Medtronic USA Inc.", 52500.0, 2500.0, 0.0, 50000.0, 0.0), ("Acme Devices LLC", 7000.0, 0.0, 0.0, 0.0, 7000.0),
                ("Pfizer, Inc.", 1040.0, 40.0, 1000.0, 0.0, 0.0), ("AbbVie Inc.", 12.25, 12.25, 0.0, 0.0, 0.0), ("Small Co", 5.0, 5.0, 0.0, 0.0, 0.0),
            ],
            await db.QueryAsync<(string, double, double, double, double, double)>(
                """
                SELECT company, total_amount, general_amount, research_amount, associated_research_amount, ownership_amount
                FROM open_payments_company WHERE npi = '1000000012' ORDER BY company_rank
                """));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM open_payments_company WHERE npi = '1000000046'"));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN ('open_payments_year_raw_staging', 'open_payments_company_raw_staging')"));
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
             "https://pdc.test/items/xubh-q36u", "https://pdc.test/items/4pq5-n9py", "https://pdc.test/items/dgck-syfz", "https://pdc.test/items/6jpm-sxkc",
             "https://pdc.test/items/yc9t-dgbk", "https://pdc.test/items/gxki-hrr8"],
            server.Requests); // HRSA, Census and Open Payments are checked weekly

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
                HrsaHpsaUrlTemplate = "https://hrsa.test/BCD_HPSA_FCT_DET_{discipline}.csv",
                CensusPopulationBaseUrl = "https://census.test/popest/",
                OpenPaymentsCatalogUrl = "https://op.test/items",
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
            ["https://pdc.test/items/dgck-syfz"] = ("2026-07-22", "HCAHPS-Hospital.csv"),
            ["https://pdc.test/items/6jpm-sxkc"] = ("2026-05-27", "HH_Provider_Jul2026.csv"),
            ["https://pdc.test/items/yc9t-dgbk"] = ("2026-08-19", "Hospice_General-Information_Aug2026_2.csv"),
            ["https://pdc.test/items/gxki-hrr8"] = ("2026-08-07", "Provider_CAHPS_Hospice_Survey_Data_Aug2026.csv"),
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

            if (url.Contains("/api/views/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"rowsUpdatedAt":1790971464}""") });
            }

            if (url.Contains("/resource/", StringComparison.Ordinal))
            {
                var id = url.Split("/resource/")[1].Split('.')[0];
                var bytes1 = File.ReadAllBytes(Fixtures.Path($"datasets/state_{id}.csv"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes1) });
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
                "https://pdc.test/items/" => "datasets/pdc_list_sample.json",
                "https://pdc.test/files/ec_score_file.csv" => "datasets/mips_sample.csv",
                "https://pdc.test/files/HCAHPS-Hospital.csv" => "datasets/hcahps_sample.csv",
                "https://pdc.test/files/HH_Provider_Jul2026.csv" => "datasets/home_health_sample.csv",
                "https://pdc.test/files/Hospice_General-Information_Aug2026_2.csv" => "datasets/hospice_general_sample.csv",
                "https://pdc.test/files/Provider_CAHPS_Hospice_Survey_Data_Aug2026.csv" => "datasets/hospice_cahps_sample.csv",
                "https://cms.test/files/HHA_Enrollments_2026.07.17.csv" => "datasets/hha_enrollments_sample.csv",
                "https://cms.test/files/Hospice_Enrollments_2026.07.17.csv" => "datasets/hospice_enrollments_sample.csv",
                "https://cms.test/files/MUP_PHY_D24_Prov.csv" => "datasets/physician_by_provider_sample.csv",
                "https://cms.test/files/MUP_PHY_D24_Prov_Svc.csv" => "datasets/physician_by_service_sample.csv",
                "https://cms.test/files/mup_dpr_dy24_npi.csv" => "datasets/part_d_by_provider_sample.csv",
                "https://hrsa.test/BCD_HPSA_FCT_DET_PC.csv" => "datasets/hpsa_PC_sample.csv",
                "https://hrsa.test/BCD_HPSA_FCT_DET_DH.csv" => "datasets/hpsa_DH_sample.csv",
                "https://hrsa.test/BCD_HPSA_FCT_DET_MH.csv" => "datasets/hpsa_MH_sample.csv",
                "https://census.test/popest/" => "datasets/census_popest_listing.html",
                "https://op.test/items" => "datasets/open_payments_catalog_sample.json",
                "https://op.test/PGYR2025_P06302026/OP_DTL_GNRL_PGYR2025_P06302026_06032026.csv" => "datasets/open_payments_sample.csv",
                "https://op.test/SMRY_P06302026/PBLCTN_PHYSN_NON_PHYSN_PRCTNR_SMRY_P06302026_06032026.csv" => "datasets/open_payments_years_sample.csv",
                "https://op.test/SMRY_P06302026/PBLCTN_SMRY_BY_CR_BY_AMGPO_PGYRall_P06302026_06032026.csv" => "datasets/open_payments_companies_sample.csv",
                "https://census.test/popest/2020-2025/counties/totals/co-est2025-alldata.csv" => "datasets/census_population_sample.csv",
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
            else if (url.StartsWith("https://hrsa.test", StringComparison.Ordinal))
            {
                content.Headers.LastModified = new DateTimeOffset(2026, 10, 8, 11, 47, 41, TimeSpan.Zero);
            }

            content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
