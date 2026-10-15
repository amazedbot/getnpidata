using Npi.Core.Search;
using System.IO.Compression;
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

    /// <summary>Every source loaded once from the fixtures, into its own database and work folder.</summary>
    private sealed record SharedLoad(TestDatabase Db, FakeSources Server, string Folder);

    /// <summary>
    /// The load the tests that only read the loaded tables share (loading every source took ~20 s, and each of seven tests
    /// did it). Kept for the whole run: its pool database is released when the test process ends, and its folder deleted.
    /// When MySQL isn't configured, the skip it throws skips each of those tests.
    /// </summary>
    private static readonly Lazy<Task<SharedLoad>> Shared = new(async () =>
    {
        var db = await TestDatabase.CreateAsync();
        var folder = Directory.CreateTempSubdirectory("npi-ds-shared-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // a temp folder; Windows cleans it up eventually
            }
        };
        var server = new FakeSources();
        Assert.True(await Loader(db, server, new FakeClock(), folder).RefreshAsync(force: false, only: null, CancellationToken.None));
        return new SharedLoad(db, server, folder);
    });

    [Fact]
    public async Task Compliance_datasets_load_and_record_their_versions()
    {
        var (db, server, folder) = await Shared.Value;

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

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture); // the API caches' version is the day
        Assert.Equal(
            [("cc_clinicians", "2026-08-18 DAC_NationalDownloadableFile.csv"), ("cc_facility_affiliations", "2026-08-18 Facility_Affiliation.csv"),
             ("cc_hcahps", "2026-07-22 HCAHPS-Hospital.csv"), ("cc_home_health", "2026-05-27 HH_Provider_Jul2026.csv"),
             ("cc_hospices", "2026-08-19 Hospice_General-Information_Aug2026_2.csv | 2026-08-07 Provider_CAHPS_Hospice_Survey_Data_Aug2026.csv"),
             ("cc_hospitals", "2026-07-22 Hospital_General_Information.csv"), ("cc_mips", "PY 2024 2026-08-18 ec_score_file.csv"),
             ("cc_nursing_home_inspections", "2026-09-30 NH_HealthCitations_Sep2026.csv | 2026-09-30 NH_FireSafetyCitations_Sep2026.csv | 2026-09-30 NH_Penalties_Sep2026.csv"),
             ("cc_nursing_homes", "2026-09-30 NH_ProviderInfo_Sep2026.csv"),
             ("census_county_population", "vintage 2025"), ("cms_dme_suppliers", "2024-12-31 mup_dme_ry26_p05_v10_dy24_supr.csv"),
             ("cms_facility_enrollments", "2026-07-31 Hospital_Enrollments_2026.07.31.csv | 2026-07-31 SNF_Enrollments_2026.07.31.csv"
                 + " | 2026-07-17 HHA_Enrollments_2026.07.17.csv | 2026-07-17 Hospice_Enrollments_2026.07.17.csv"
                 + " | 2026-07-17 FQHC_Enrollments_2026.07.17.csv | 2026-07-17 RHC_Enrollments_2026.07.17.csv | enrollments"),
             ("cms_facility_owners", "2026-08-31 Hospital_All_Owners_2026.07.31_update.csv | 2026-08-31 SNF_All_Owners_2026.07.31_update.csv"
                 + " | 2026-09-30 HHA_All_Owners_2026.07.17.csv | 2026-09-30 Hospice_All_Owners_2026.07.17.csv"
                 + " | 2026-09-30 FQHC_All_Owners_2026.07.17.csv | 2026-09-30 RHC_All_Owners_2026.07.17.csv"),
             ("cms_opt_out", "2026-08-31 OptOut_August2026.csv"), ("cms_order_referring", "2026-10-08 OrderReferring_20261008.csv"),
             ("cms_otp_providers", "2026-10-03 OPIOID_TREATMENT_PROGRAM_PROVIDERS_10052026.csv"),
             ("cms_ownership_changes", "2026-06-30 Hospital_CHOW_2026.07.17.csv | 2026-06-30 SNF_CHOW_2026.07.17.csv"
                 + " | 2026-06-30 Hospital_CHOW_Owners_2026.07.17.csv | 2026-06-30 SNF_CHOW_Owners_2026.07.17.csv"),
             ("cms_part_d_by_provider", "2024-12-31 mup_dpr_dy24_npi.csv"), ("cms_physician_by_provider", "2024-12-31 MUP_PHY_D24_Prov.csv"),
             ("cms_physician_by_service", "2024-12-31 MUP_PHY_D24_Prov_Svc.csv"),
             ("cms_provider_of_services", "2026-06-30 Hospital_and_other.DATA.Q2_2026.csv | 2026-06-30 POS_iQIES.DATA.Q2_2026.csv"),
             ("cms_revalidation", "2026-10-31 revalidation_base.csv"),
             ("drug_spending", "Part D 2024-12-31 | Part B 2024-12-31 | Medicaid 2024-12-31"), ("fda_enforcement", "drug 2026-10-06 | device 2026-10-06"),
             ("fda_products", "ndc 2026-10-06 | drugsfda 2026-10-06 | label 2026-10-06 | udi 2026-10-06 | 510k 2026-10-06 | pma 2026-10-06"),
             ("fda_shortages", "shortages 2026-10-09"), ("hrsa_hpsa", "HPSA 2026-10-08"),
             ("medicare_enrollment", "2026-09-30 PPEF_Enrollment_Extract_2026.07.17.csv | 2026-10-31 revalidation_clinic_group_practice_reassignment_pa_xref.csv"),
             ("medicare_history", MedicareHistoryVersion()),
             ("nadac", "2026 nadac-national-average-drug-acquisition-cost-10-07-2026.csv"),
             ("oig_leie", "2026-10-01T12:00:00Z 808"), ("open_payments", "2025 OP_DTL_GNRL_PGYR2025_P06302026_06032026.csv"),
             ("open_payments_companies", "PBLCTN_SMRY_BY_CR_BY_AMGPO_PGYRall_P06302026_06032026.csv"),
             ("open_payments_entities", "PBLCTN_RPTG_ORG_PRFL_SRCH_P06302026_06032026.csv | PBLCTN_RPTG_ORG_SMRY_P06302026_06032026.csv"),
             ("open_payments_research", "2025 OP_DTL_RSRCH_PGYR2025_P06302026_06032026.csv"),
             ("open_payments_years", "PBLCTN_PHYSN_NON_PHYSN_PRCTNR_SMRY_P06302026_06032026.csv"), ("part_d_prescribers", "2024-12-31 MUP_DPR_DY24_NPIBN.csv"), ("product_adverse_events", $"faers 2026-10-12 | maude 2026-10-12 | {today}"), ("product_trials", today),
             ("sec_companies", $"2026-10-09T06:00:00Z parents {SecCompanySource.ParentsHash()}"),
             ("state_licenses", StateLicenseSource.CombineVersions(
                 [("NY", "2026-10-02T20:04:24Z"), ("TX", "2026-10-02T20:04:24Z"), ("WA", "2026-10-02T20:04:24Z"), ("IL", "2026-10-02T20:04:24Z"), ("CO", "2026-10-02T20:04:24Z"), ("DE", "2026-10-02T20:04:24Z"),
                  ("DE", "2026-10-02T20:04:24Z"), ("CT", "2026-10-02T20:04:24Z"), ("MD", "2026-10-01T00:00:00Z"), ("MD", "2026-10-01T00:00:00Z"), ("FL", null), ("FL", null),
                  ("RI", Week), ("VT", Week), ("WV", "rosters 713 " + Week), ("AL", null), ("MI", null), ("CA", null)])),
             ("state_medicaid", "NY 2026-10-02T20:04:24Z | CA 2026-10-12T20:02:55.387406")],
            await db.QueryAsync<(string, string)>("SELECT source, version FROM reference_data WHERE source NOT IN ('oig_cia', 'fema_structures', 'address_building', 'address_type') ORDER BY source"));
        Assert.StartsWith("3 agreements ", await db.ScalarAsync<string>("SELECT version FROM reference_data WHERE source = 'oig_cia'"));
        // FEMA structures (item 18): the listing's newest date and a hash of its files (new addresses don't reload it, speed item 4).
        Assert.Matches("^USA Structures 2024-12-06 [0-9a-f]{12}$", await db.ScalarAsync<string>("SELECT version FROM reference_data WHERE source = 'fema_structures'"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM reference_data WHERE source = 'address_type'")); // labelled after the load
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND (table_name LIKE '%\\_staging' " +
            "OR table_name LIKE 'cc\\_%\\_old' OR table_name LIKE 'cms\\_%\\_old' OR table_name LIKE 'medicare\\_%\\_old' OR table_name LIKE 'county\\_%\\_old' OR table_name LIKE 'open\\_payments\\_%\\_old' OR table_name IN ('oig_exclusion_old', 'medicare_opt_out_old', 'medicare_order_referring_old'))"));
        Assert.Empty(Directory.GetFiles(Path.Combine(folder, "datasets"))); // downloads are deleted after loading
    }

    [Fact]
    public async Task Care_compare_and_facility_datasets_load_and_summarize()
    {
        var db = (await Shared.Value).Db;

        // Medicare enrollment (step 1.5): the newest extract, Latin-1 read as such, one row per enrollment, no row without an NPI.
        Assert.Equal(
            [("1000000004", "I20200101000002", "5000000004", null), ("1000000038", "O20200101000001", "9000000001", "SMITH, JONES & CO, LLC")],
            await db.QueryAsync<(string, string, string, string?)>("SELECT npi, enrollment_id, pac_id, org_name FROM medicare_enrollment ORDER BY npi"));
        Assert.Equal([("9000000001", "1000000004", "Reassignment", "Smith, Jones & Co, Llc"), ("9000000001", "1000000012", "Physician Assistant", "Smith, Jones & Co, Llc")],
            await db.QueryAsync<(string, string, string, string)>("SELECT group_pac_id, npi, record_type, group_name FROM medicare_reassignment ORDER BY npi"));
        Assert.Contains("PPEF_Enrollment_Extract_2026.07.17.csv", await db.ScalarAsync<string>("SELECT version FROM reference_data WHERE source = 'medicare_enrollment'"));

        // State Medicaid lists (Phase 3): New York's grouped export and California's file, one row per NPI and state (the earliest
        // enrollment; California's specialty, else its provider type), no row without an NPI.
        Assert.Equal(
            [("1000000004", "NY", new DateTime(1979, 2, 1), "PHYSICIAN"), ("1000000012", "CA", new DateTime(2002, 4, 1), "Family Nurse Practitioner"),
             ("1000000012", "NY", new DateTime(2007, 10, 26), "NURSE PRACTITIONER"), ("1000000038", "CA", new DateTime(2019, 12, 5), "CLINICS")],
            await db.QueryAsync<(string, string, DateTime, string)>("SELECT npi, state, enrolled_since, provider_type FROM state_medicaid ORDER BY npi, state"));

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
        Assert.Equal([("330045", "1000000038", "hospital"), ("331501", "1000000038", "hospice"), ("331936", "1000000020", "fqhc"),
                ("335001", "1000000046", "nursing_home"), ("337001", "1000000038", "home_health"), ("353982", "1000000053", "rhc")],
            await db.QueryAsync<(string, string, string)>("SELECT ccn, npi, kind FROM cms_facility_npi ORDER BY ccn"));

        // Signs of current practice for the record-quality label (step 3.1): one row per NPI, rows without an NPI dropped.
        Assert.Equal([("1000000038", "CENTER FOR COMMUNITY ALTERNATIVES,", new DateTime(2024, 5, 1))],
            await db.QueryAsync<(string, string, DateTime)>("SELECT npi, name, effective_date FROM cms_otp_provider"));
        Assert.Equal([("1000000004", (DateTime?)new DateTime(2027, 12, 31)), ("1000000038", null)],
            await db.QueryAsync<(string, DateTime?)>("SELECT npi, due_date FROM medicare_revalidation ORDER BY npi")); // the latest (adjusted) date
        Assert.Equal([("1000000038", (short)2024, (int?)238, (int?)244), ("1000000046", (short)2024, null, 15)],
            await db.QueryAsync<(string, short, int?, int?)>("SELECT npi, data_year, beneficiaries, claims FROM medicare_dme_supplier ORDER BY npi"));

        // Provider of Services: both parts, the iQIES row winning (337001 is certified there); 07 = a change of status, kept as such.
        Assert.Equal(
            [("330045", (sbyte)1, "00", (DateTime?)null, "11795"), ("330777", (sbyte)0, "01", new DateTime(2025, 3, 1), "11201"),
             ("330888", (sbyte)0, "07", new DateTime(2020, 1, 1), "11201"), ("331501", (sbyte)0, null, new DateTime(2024, 6, 30), "11763"),
             ("337001", (sbyte)1, null, null, "11788")],
            await db.QueryAsync<(string, sbyte, string?, DateTime?, string)>(
                "SELECT ccn, active, termination_code, termination_date, zip5 FROM cms_pos_facility ORDER BY ccn"));
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
              ('1000000020', 1, 'LEE', 'ANN', 'LEE, ANN'), ('1000000038', 1, 'NUÑEZ', 'ELENA', 'NUÑEZ, ELENA'), ('1000000046', 1, 'OTHER', 'PAT', 'OTHER, PAT'),
              ('1000000053', 1, 'BROWN', 'KIM', 'BROWN, KIM'), ('1000000061', 1, 'SINGH', 'JOSEPH', 'SINGH, JOSEPH'), ('1000000079', 1, 'PATEL', 'RAJ', 'PATEL, RAJ'),
              ('1000000087', 1, 'JONES', 'AMY', 'JONES, AMY'), ('1000000095', 1, 'ACOSTA', 'GILBERTO', 'ACOSTA, GILBERTO'),
              ('1000000103', 1, 'WHITE', 'SAM', 'WHITE, SAM'), ('1000000111', 1, 'GREEN', 'LEE', 'GREEN, LEE'), ('1000000129', 1, 'BLACK', 'RON', 'BLACK, RON'),
              ('1000000137', 1, 'HILL', 'DANA', 'HILL, DANA'), ('1000000145', 1, 'STONE', 'AVA', 'STONE, AVA'),
              ('1000000152', 1, 'GARRETT', 'MICHAEL', 'GARRETT, MICHAEL'), ('1000000160', 1, 'ADAMS', 'KATELYN', 'ADAMS, KATELYN');
            INSERT INTO provider_taxonomy (npi, slot, taxonomy_code, is_primary, license_no, license_state) VALUES
              ('1000000004', 1, '207Q00000X', 1, 'MD-174744', 'NY'), ('1000000012', 1, '207Q00000X', 1, 'N1234', 'TX'),
              ('1000000020', 1, '207Q00000X', 1, 'MD00012345', 'WA'), ('1000000020', 2, '207Q00000X', 0, '036.098765', 'IL'),
              ('1000000038', 1, '207Q00000X', 1, 'DR.0042345', 'CO'), ('1000000046', 1, '207Q00000X', 1, '1234', 'TX'),
              ('1000000053', 1, '207Q00000X', 1, '0024514', 'DE'), ('1000000061', 1, '207Q00000X', 1, '050192', 'CT'),
              ('1000000079', 1, '207Q00000X', 1, 'D12345', 'MD'), ('1000000087', 1, '363A00000X', 1, 'C1234', 'MD'),
              ('1000000095', 1, '207Q00000X', 1, 'ME80978', 'FL'), ('1000000103', 1, '207Q00000X', 1, 'MD05878', 'RI'),
              ('1000000111', 1, '207Q00000X', 1, '10818', 'VT'), ('1000000129', 1, '207Q00000X', 1, '16132', 'WV'),
              ('1000000137', 1, '207Q00000X', 1, 'A64406', 'CA'), ('1000000145', 1, '207Q00000X', 1, '35457', 'CA'),
              ('1000000152', 1, '207Q00000X', 1, 'MD.801', 'AL'), ('1000000160', 1, '106H00000X', 1, '4151001126', 'MI');
            """);
        // Florida's files are dropped by the owner into the state files folder (PROF_ALL inside its zip).
        var florida = Directory.CreateDirectory(Path.Combine(_folder, "state-files", "FL")).FullName;
        using (var zip = ZipFile.Open(Path.Combine(florida, "PROF_ALL.zip"), ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(Fixtures.Path("datasets/state_fl_prof_all.txt"), "dbdumps/ldms/PROF_ALL.txt");
        }

        File.Copy(Fixtures.Path("datasets/state_fl_dxe004dd.txt"), Path.Combine(florida, "dxe004dd.txt"));

        // Alabama's roster as the browser names it; Michigan's list downloaded twice (counted once); California's snapshots in one zip
        // (the newest is read; Windows only, with the Access database driver).
        var alabama = Directory.CreateDirectory(Path.Combine(_folder, "state-files", "AL")).FullName;
        File.Copy(Fixtures.Path("datasets/state_al_roster.csv"), Path.Combine(alabama, "roster-2026-10-12-001538.csv"));
        var michigan = Directory.CreateDirectory(Path.Combine(_folder, "state-files", "MI")).FullName;
        var list = XlsxWriter.Write(
        [
            ["Last Name", "First Name", "Middle Name", "Suffix", "Facility Name", "Profession", "Type", "Specialities", "License No", "Status", "Issue Date", "Expiration"],
            [null, null, null, null, "Sparkline Solutions LLC", "Personnel Agency", "Agency A", null, "6102003894", "Active", 46248.0, 47344.0],
            ["Adams", "Katelyn", null, null, null, "Marriage and Family Therapy", "Marriage and Fam Ther Ed Ltd", null, "4151001126", "Active", 45246.0, 46342.0],
        ]);
        File.WriteAllBytes(Path.Combine(michigan, "BPL_EXT_LicPubList_REC_MUL_SSRS.xlsx"), list);
        File.WriteAllBytes(Path.Combine(michigan, "BPL_EXT_LicPubList_REC_MUL_SSRS (1).xlsx"), list);
        var california = OperatingSystem.IsWindows();
        if (california)
        {
            var folder = Directory.CreateDirectory(Path.Combine(_folder, "state-files", "CA")).FullName;
            using var snapshot = new MemoryStream();
            using (var inner = new ZipArchive(snapshot, ZipArchiveMode.Create, leaveOpen: true))
            {
                inner.CreateEntryFromFile(Fixtures.Path("datasets/state_ca_mbc.accdb"), "MBCPhysicianAndSurgeonInformation-PUBLIC.accdb");
            }

            using var outer = ZipFile.Open(Path.Combine(folder, "MBC Physician and Surgeon Access Database.zip"), ZipArchiveMode.Create);
            foreach (var date in new[] { "20260922", "20260929" })
            {
                var entry = outer.CreateEntry($"MBC Physician and Surgeon Access Database/MBCPhysicianAndSurgeonInformation-PUBLIC-{date}.zip");
                using var to = entry.Open();
                to.Write(date == "20260929" ? snapshot.ToArray() : [0x50, 0x4b, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]); // the older one: an empty zip
            }
        }

        Assert.True(await Loader(db, new FakeSources(), new FakeClock()).RefreshAsync(force: false, only: "state_licenses", _ct));

        // O'BRIEN = "OBrien" is not equal (the apostrophe), but the second row ("O'BRIEN") is; GARCIA's N1234 matches, and the
        // other TX provider's "1234" doesn't (name); the placeholder "000000" and "Someone Else" never match. DE's "C1-0024514"
        // matches NPPES "0024514" by its last digits; CT's full name "JOSEPH U SINGH" matches SINGH, "MARIA SINGHAL" doesn't;
        // MD's letterhead lines are skipped; FL's pipe rows load (a stray " included), the row with an extra field is left out.
        Assert.Equal(
            [
                ("1000000004", "NY", "action", (DateTime?)new DateTime(2019, 1, 15)), ("1000000012", "TX", "license", new DateTime(2010, 1, 1)),
                ("1000000020", "IL", "license", new DateTime(2024, 5, 1)), ("1000000020", "WA", "license", null),
                ("1000000038", "CO", "license", new DateTime(2021, 7, 11)), ("1000000038", "CO", "license", new DateTime(2023, 10, 9)),
                ("1000000053", "DE", "license", null), ("1000000053", "DE", "action", new DateTime(2025, 3, 4)),
                ("1000000061", "CT", "license", null), ("1000000079", "MD", "license", null), ("1000000087", "MD", "license", null),
                ("1000000095", "FL", "license", null), ("1000000095", "FL", "action", new DateTime(2026, 9, 25)),
                ("1000000103", "RI", "license", null), ("1000000111", "VT", "license", null), ("1000000129", "WV", "license", null),
                .. (california
                    ? new[]
                    {
                        ("1000000137", "CA", "license", (DateTime?)null), ("1000000137", "CA", "license", new DateTime(2020, 4, 10)),
                        ("1000000137", "CA", "license", new DateTime(2025, 5, 21)), ("1000000145", "CA", "license", null),
                    }
                    : []),
                ("1000000152", "AL", "license", null), ("1000000160", "MI", "license", null),
            ],
            await db.QueryAsync<(string, string, string, DateTime?)>(
                "SELECT npi, state, kind, action_date FROM provider_state_license ORDER BY npi, state, action_date"));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN ('state_license_raw_staging', 'provider_state_license_new')"));

        // Each state's version is kept, and every matched NPI is queued for the record-quality label.
        var states = await db.QueryAsync<string>("SELECT DISTINCT state FROM provider_state_license ORDER BY state");
        Assert.Equal(states, await db.QueryAsync<string>("SELECT state FROM state_license_version WHERE rows_loaded > 0 ORDER BY state"));
        Assert.Equal(await db.QueryAsync<string>("SELECT DISTINCT npi FROM provider_state_license ORDER BY npi"),
            await db.QueryAsync<string>("SELECT npi FROM quality_pending ORDER BY npi"));

        // Reloaded with the same files: every state is unchanged and skipped.
        await db.ExecuteAsync("DELETE FROM quality_pending");
        var loaded = await db.QueryAsync<(string, DateTime)>("SELECT state, loaded_at FROM state_license_version ORDER BY state");
        Assert.True(await Loader(db, new FakeSources(), new FakeClock()).RefreshAsync(force: true, only: "state_licenses", _ct));
        Assert.Equal(loaded, await db.QueryAsync<(string, DateTime)>("SELECT state, loaded_at FROM state_license_version ORDER BY state"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM quality_pending"));

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
        var fl = Assert.Single((await details.GetAsync("1000000095", _ct))!.StateLicenses);
        Assert.Equal(("Clear/Active", "Y", "AC Filed", new DateOnly(2028, 1, 31)), (fl.Status, fl.Discipline, fl.Actions.Single().Action, fl.ExpirationDate));
        var ct = Assert.Single((await details.GetAsync("1000000061", _ct))!.StateLicenses);
        Assert.Equal(("Physician/Surgeon", "ACTIVE"), (ct.LicenseType, ct.Status));
        Assert.Equal(("D0012345", "A", "N"), (await details.GetAsync("1000000079", _ct))!.StateLicenses.Select(l => (l.LicenseNumber, l.Status, l.Discipline)).Single());

        // Lists assembled from the boards' pages: RI's physician download (a { } and a comma in the address), VT's feed ("042." dropped
        // from the type; the action column), WV's roster workbook (active only; the type names the roster and the sheet).
        var ri = Assert.Single((await details.GetAsync("1000000103", _ct))!.StateLicenses);
        Assert.Equal(("MD05878", "Allopathic Physician (MD)", "Active Probation", new DateOnly(2028, 6, 30)), (ri.LicenseNumber, ri.LicenseType, ri.Status, ri.ExpirationDate));
        var vt = Assert.Single((await details.GetAsync("1000000111", _ct))!.StateLicenses);
        Assert.Equal(("Physician", "INACTIVE - DECEASED", "Stipulation and Consent Order"), (vt.LicenseType, vt.Status, vt.Actions.Single().Action));
        var wv = Assert.Single((await details.GetAsync("1000000129", _ct))!.StateLicenses);
        Assert.Equal(("16132", "Medical Doctor: Sheet1", "Active", new DateOnly(2028, 6, 30)), (wv.LicenseNumber, wv.LicenseType, wv.Status, wv.ExpirationDate));

        // Dropped files: AL's roster (a comma in the medical school; the public-actions flag), MI's list (Excel serial dates).
        var al = Assert.Single((await details.GetAsync("1000000152", _ct))!.StateLicenses);
        Assert.Equal(("801", "MD FULL UNRESTRICTED MD", "ACTIVE", "YES", new DateOnly(2027, 12, 31)), (al.LicenseNumber, al.LicenseType, al.Status, al.Discipline, al.ExpirationDate));
        var mi = Assert.Single((await details.GetAsync("1000000160", _ct))!.StateLicenses);
        Assert.Equal(("Marriage and Family Therapy: Marriage and Fam Ther Ed Ltd", "Active", new DateOnly(2026, 11, 16)), (mi.LicenseType, mi.Status, mi.ExpirationDate));
        if (california)
        {
            // CA: the status from the board's codes, retired over Current; the public actions, newest first, with their details.
            var hill = Assert.Single((await details.GetAsync("1000000137", _ct))!.StateLicenses);
            Assert.Equal(("A64406", "Physician and surgeon (A)", "Current"), (hill.LicenseNumber, hill.LicenseType, hill.Status));
            Assert.Equal(["Citation issued, fine $1,000", "PUBLIC REPRIMAND, WITH 'QUOTES', AND {BRACES}."], hill.Actions.Select(a => a.Action));
            Assert.Equal("Case 8002016026076", hill.Actions[1].Description);
            Assert.Equal("Retired", Assert.Single((await details.GetAsync("1000000145", _ct))!.StateLicenses).Status);
        }
    }

    // The version of the lists the loader assembles (Rhode Island, Vermont, West Virginia): the ISO week.
    private static string Week => $"week {System.Globalization.ISOWeek.GetYear(DateTime.UtcNow)}-{System.Globalization.ISOWeek.GetWeekOfYear(DateTime.UtcNow):00}";

    [Fact]
    public async Task Quality_and_outcome_datasets_load()
    {
        var db = (await Shared.Value).Db;

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

    // The older Medicare years of the fixture catalog: Part B 2023 (2024 is the newest year).
    private static string MedicareHistoryVersion()
    {
        using var stream = File.OpenRead(Fixtures.Path("datasets/cms_catalog_sample.json"));
        var files = MedicareHistorySource.Files(CmsCatalog.Parse(stream));
        Assert.Equal([('B', 2023)], files.Select(f => (f.Part.Code, f.Year)));
        return MedicareHistorySource.Version(files);
    }

    [Fact]
    public async Task Medicare_activity_loads_with_its_data_year_and_top_services()
    {
        var db = (await Shared.Value).Db;

        // Older years (Phase 3): 2023's Part B file, with a Windows-1252 name in it (only the NPI and the patients are read); a
        // suppressed count stays NULL.
        Assert.Equal([((short)2023, "1000000004", (sbyte)1, (int?)198), ((short)2023, "1000000020", (sbyte)1, null)],
            await db.QueryAsync<(short, string, sbyte, int?)>("SELECT data_year, npi, parts, part_b_beneficiaries FROM medicare_activity_year ORDER BY npi"));
        Assert.Equal([("B", (short)2023, "/files/MUP_PHY_D23_Prov.csv", 2L)],
            await db.QueryAsync<(string, short, string, long)>("SELECT part, data_year, version, rows_loaded FROM medicare_history_version"));

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
        var db = (await Shared.Value).Db;

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
        var db = (await Shared.Value).Db;

        // The NPI-less teaching hospital payment is left out; amounts sum exactly to the cent.
        Assert.Equal([("1000000012", (short)2025, 2563.35, 5, 4), ("1000000046", (short)2025, 35.5, 1, 1)],
            await db.QueryAsync<(string, short, double, int, int)>("SELECT npi, program_year, total_amount, records, payers FROM open_payments_summary ORDER BY npi"));
        Assert.Equal([("Consulting Fee", 2500.0, 1), ("Food and Beverage", 63.35, 4)], await db.QueryAsync<(string, double, int)>(
            "SELECT nature, amount, records FROM open_payments_nature WHERE npi = '1000000012' ORDER BY nature"));
        Assert.Equal([("Medtronic USA Inc.", 2500.0, "100000000002"), ("Pfizer, Inc.", 40.0, "100000000001"), ("AbbVie Inc.", 12.25, "100000000004")],
            await db.QueryAsync<(string, double, string)>("SELECT payer, amount, company_id FROM open_payments_payer WHERE npi = '1000000012' ORDER BY payer_rank"));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'open_payments_raw_staging'"));
    }

    [Fact]
    public async Task Open_payments_history_sums_profiles_per_year_and_keeps_the_top_companies()
    {
        var db = (await Shared.Value).Db;

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
    public async Task Company_pages_summarize_each_company()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ExecuteAsync(
            """
            INSERT INTO taxonomy_codes (Taxonomy_Code, `Grouping`, Classification, Specialization, Display_Name, Section, Nucc_Version) VALUES
              ('207RC0000X', 'Allopathic & Osteopathic Physicians', 'Internal Medicine', 'Cardiovascular Disease', 'Cardiologist', 'Individual', '261'),
              ('363L00000X', 'Physician Assistants & Advanced Practice Nursing Providers', 'Nurse Practitioner', NULL, 'Nurse Practitioner', 'Individual', '261');
            INSERT INTO provider (npi, entity_type, last_name, first_name, sort_name, primary_taxonomy_code) VALUES
              ('1000000012', 1, 'BAKER', 'AL', 'BAKER, AL', '207RC0000X'), ('1000000046', 1, 'NUÑEZ', 'ELENA', 'NUÑEZ, ELENA', '363L00000X');
            INSERT INTO provider_location (npi, is_primary, address1, city, state, zip5) VALUES ('1000000012', 1, '1 MAIN ST', 'ALBANY', 'NY', '12207');
            INSERT INTO provider_credential (npi, ord, credential) VALUES ('1000000012', 1, 'MD'), ('1000000012', 2, 'PhD');
            """);
        Assert.True(await Loader(db, new FakeSources(), new FakeClock()).RefreshAsync(force: false, only: null, _ct));

        var companies = new CompanyService(db.ConnectionString);
        // Every company in either file: the profile's name, else the newest year's (Smith & Nephew has no profile); the "ALL" row is left out.
        var pfizer = (await companies.GetAsync("100000000001", _ct))!;
        Assert.Equal(("Pfizer, Inc.", "NY", 11040.5, 111000.0, 2024, 2025, 1), (pfizer.Name, pfizer.State, pfizer.General, pfizer.Research,
            pfizer.FirstYear, pfizer.LastYear, pfizer.Providers));
        Assert.Equal(["PFIZER INC"], pfizer.OtherNames); // its own name isn't repeated
        Assert.Equal([2025, 2024], pfizer.Years.Select(y => y.Year));
        // 2025 natures and products over every recipient, the teaching hospital included; "ELIQUIS" and " Eliquis " are one product.
        Assert.Equal(2025, pfizer.DetailYear);
        Assert.Equal([("Royalty or License", 10000.0), ("Food and Beverage", 40.0)], pfizer.ByNature.Select(n => (n.Nature, n.Amount)));
        // Every product a payment names counts (payment 1001 names ELIQUIS and IBRANCE), each linked by its slug.
        Assert.Equal([("IBRANCE", 10018.57, 2, "ibrance"), ("ELIQUIS", 40.0, 2, "eliquis")], pfizer.TopProducts.Select(p => (p.Name, p.Amount, p.Records, p.Slug)));
        Assert.Equal(("Drug", "Cardiology"), (pfizer.TopProducts[1].Kind, pfizer.TopProducts[1].Category));
        // Over all years: the active providers it paid, by specialty, with their credentials and city.
        var baker = Assert.Single(pfizer.TopProviders);
        Assert.Equal(("1000000012", "BAKER, AL", "MD, PhD", "Internal Medicine", "ALBANY", 1040.0, 1000.0), (baker.Npi, baker.Name, baker.Credential,
            baker.Specialty, baker.City, baker.Total, baker.Research));
        Assert.Equal([("Internal Medicine", 1, 1040.0)], pfizer.TopSpecialties.Select(s => (s.Specialty, s.Providers, s.Amount)));
        Assert.Equal("https://openpaymentsdata.cms.gov/company/100000000001", pfizer.OpenPaymentsUrl);
        Assert.Empty(pfizer.SimilarNames); // no other company has "PFIZER" in its name

        var smith = (await companies.GetAsync("100000000009", _ct))!;
        Assert.Equal(("Smith & Nephew, Inc.", "TN", 2025), (smith.Name, smith.State, smith.FirstYear));
        Assert.Null(await companies.GetAsync("999", _ct));

        // The list: largest payments (general + research) first; a name matches any part of a name or another name.
        var all = await companies.SearchAsync(null, 1, 50, _ct);
        Assert.Equal(["100000000001", "100000000009"], all.Items.Take(2).Select(c => c.Id));
        Assert.Equal(5, all.TotalCount); // Pfizer, Medtronic, Acme, Smith & Nephew, SNAP Diagnostics
        Assert.Equal(["100000000001"], (await companies.SearchAsync("fizer inc", 1, 50, _ct)).Items.Select(c => c.Id));
        Assert.Equal(["100000000009"], (await companies.SearchAsync("nephew", 1, 50, _ct)).Items.Select(c => c.Id));
        Assert.Equal(7000.0, (await companies.SearchAsync("acme", 1, 50, _ct)).Items.Single().OwnershipValue);
        Assert.Empty((await companies.SearchAsync("100%", 1, 50, _ct)).Items); // wildcards are literal

        // Public records, matched by name key: FDA recalls ("Pfizer Inc." = "Pfizer, Inc."; the duplicate report is loaded once per file;
        // "Medtronic, L.L.C." is not "Medtronic USA Inc."), SEC tickers and OIG agreements (one entity of "SNAP Diagnostics, LLC and Gil Raviv").
        var recalls = pfizer.Recalls!;
        Assert.Equal((2, 2, 0, 2), (recalls.Total, recalls.ClassI, recalls.ClassII, recalls.Ongoing));
        Assert.Equal(["Pfizer Inc."], recalls.Firms);
        Assert.Equal([("D-0001-2026", "Devices"), ("D-0001-2026", "Drugs")], recalls.Latest.Select(r => (r.RecallNumber, r.ProductType)).Order());
        Assert.Equal(new DateOnly(2026, 8, 20), recalls.Latest[0].Initiated);
        Assert.Equal(1, (await companies.GetAsync("100000000002", _ct))!.Recalls!.Total / 2); // the MiniMed recall, in both fixture files
        Assert.Equal([(78003, "PFE", "NYSE")], pfizer.SecListings.Select(l => (l.Cik, l.Ticker, l.Exchange)));
        Assert.Equal("https://www.sec.gov/cgi-bin/browse-edgar?action=getcompany&CIK=0000078003", pfizer.SecListings[0].EdgarUrl);
        Assert.False(pfizer.SecListings[0].IsParent);
        // The hand-made parent list is loaded with the registrants; a listed subsidiary shows its parent (here a test row).
        Assert.Equal(SecCompanySource.ReadParents().Count, await db.ScalarAsync<long>("SELECT COUNT(*) FROM company_parent"));
        await db.ExecuteAsync("INSERT INTO company_parent (company_id, parent_cik, note) VALUES ('100000000002', 59478, 'test subsidiary')");
        var medtronic = Assert.Single((await companies.GetAsync("100000000002", _ct))!.SecListings);
        Assert.Equal(("LLY", true, "test subsidiary"), (medtronic.Ticker, medtronic.IsParent, medtronic.Note));
        Assert.Empty(pfizer.IntegrityAgreements);
        var snap = Assert.Single((await companies.GetAsync("100000000010", _ct))!.IntegrityAgreements);
        Assert.Equal(("Suspended", (DateOnly?)new DateOnly(2026, 10, 1)), (snap.Status, snap.StatusDate));
        Assert.Equal(3L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM oig_cia"));

        // Product pages (item 19, part 1): "ELIQUIS" and " Eliquis " are one product named by its most paid spelling.
        var products = new ProductService(db.ConnectionString);
        var eliquis = (await products.GetAsync("eliquis", _ct))!;
        Assert.Equal(("Eliquis", "Drug", "Cardiology", 2025, 40.0, 2, 1, 1), (eliquis.Name, eliquis.Kind, eliquis.Category, eliquis.Year, eliquis.Amount,
            eliquis.Records, eliquis.CompanyCount, eliquis.Providers));
        Assert.Equal([("100000000001", "Pfizer, Inc.", 40.0)], eliquis.Companies.Select(c => (c.CompanyId, c.Name, c.Amount)));
        Assert.Equal([("Food and Beverage", 40.0)], eliquis.ByNature.Select(n => (n.Nature, n.Amount)));
        Assert.Equal([("1000000012", "BAKER, AL", "MD, PhD", 40.0, 2)], eliquis.TopProviders.Select(r => (r.Npi, r.Name, r.Credential, r.Amount, r.Records)));

        // The Companies & products page (owner, 2026-10-12): totals agree with the lists and the exports hold every row.
        using var industry = new IndustryService(db.ConnectionString, companies, products);
        var overview = await industry.GetOverviewAsync(_ct);
        var allCompanies = await companies.SearchAsync(null, 1, CompanyService.MaxPageSize, _ct);
        var allProducts = await products.SearchAsync(null, null, 1, ProductService.MaxPageSize, _ct);
        Assert.Equal((allCompanies.TotalCount, allProducts.TotalCount), (overview.Companies, overview.Products));
        Assert.Equal(allCompanies.Items.Sum(c => c.Payments), overview.Paid, 2);
        Assert.Equal(allCompanies.Items.Take(IndustryService.TopCount).Select(c => c.Id), overview.TopCompanies.Select(c => c.Id));
        Assert.Equal(allProducts.Items.Take(IndustryService.TopCount).Select(p => p.Slug), overview.TopProducts.Select(p => p.Slug));
        Assert.Equal(allProducts.Items.Count, overview.ProductKinds.Sum(k => k.Products));
        Assert.Equal([2024, 2025], overview.Years.Select(y => y.Year));
        Assert.Equal((2024, 2025, 2025), (overview.FirstYear, overview.LastYear, overview.ProductYear));
        Assert.Equal(await db.ScalarAsync<long>("SELECT COUNT(*) FROM open_payments_summary"), overview.ProvidersPaid);
        Assert.Same(overview, await industry.GetOverviewAsync(_ct)); // cached

        string Csv(Func<Stream, Task<long>> write)
        {
            using var stream = new MemoryStream();
            write(stream).GetAwaiter().GetResult();
            return System.Text.Encoding.UTF8.GetString(stream.ToArray()).TrimStart('\uFEFF');
        }

        var companyCsv = Csv(s => IndustryCsv.WriteCompaniesAsync(industry.AllCompaniesAsync(_ct), s, "https://site.test", _ct)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(string.Join(',', IndustryCsv.CompanyHeader), companyCsv[0]);
        Assert.Equal(allCompanies.TotalCount + 1, companyCsv.Length);
        Assert.Contains(companyCsv, l => l.StartsWith("100000000001,\"Pfizer, Inc.\",", StringComparison.Ordinal) && l.EndsWith(",https://site.test/company/100000000001", StringComparison.Ordinal));
        var productCsv = Csv(s => IndustryCsv.WriteProductsAsync(industry.AllProductsAsync(_ct), s, "https://site.test", _ct)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(allProducts.TotalCount + 1, productCsv.Length);
        Assert.Contains(productCsv, l => l.StartsWith("Eliquis,Drug,Cardiology,", StringComparison.Ordinal) && l.EndsWith(",https://site.test/product/eliquis", StringComparison.Ordinal));
        Assert.Equal([("Internal Medicine", 1, 40.0)], eliquis.TopSpecialties.Select(x => (x.Specialty, x.Providers, x.Amount)));
        // The teaching hospital's payment counts for the product, not for a provider; research counts every recipient.
        var ibrance = (await products.GetAsync("ibrance", _ct))!;
        Assert.Equal((10018.57, 2, 1), (ibrance.Amount, ibrance.Records, ibrance.Providers));
        Assert.Equal((76000.0, 3, 2), (ibrance.Research!.Amount, ibrance.Research.Records, ibrance.Research.Studies));
        var paloma = ibrance.Research.TopStudies[0];
        Assert.Equal(("NCT01740427", 75000.0, 2, "Pfizer, Inc.", "https://clinicaltrials.gov/study/NCT01740427"),
            (paloma.NctId, paloma.Amount, paloma.Records, paloma.CompanyName, paloma.ClinicalTrialsUrl));
        Assert.Equal(1000.0, eliquis.Research!.Amount);
        Assert.Equal("0169-4132-12", (await products.GetAsync("ozempic", _ct))!.Ndc);
        Assert.Equal("00763000636251", (await products.GetAsync("minimed-780g", _ct))!.DeviceId);
        Assert.Null(await products.GetAsync("no-such-product", _ct));

        // Part 2, what it is: Eliquis by its NDC 0003-0893-21 (directory 0003-0893) and its application. That listing has no
        // FDA annotations, so the drug class comes from another listing of the generic name and the label from a listing under
        // the same application (the repackager's). The repackager isn't another maker; the ANDA generic is.
        var drug = eliquis.Drug!;
        Assert.Equal(("0003-0893", "ELIQUIS", "apixaban", "APIXABAN 5 mg/1", "Factor Xa Inhibitor", "BRISTOL MYERS SQUIBB", (DateOnly?)new DateOnly(2012, 12, 28), 1, 1),
            (drug.ProductNdc, drug.BrandName, drug.GenericName, drug.ActiveIngredients, drug.PharmClasses, drug.Sponsor, drug.ApprovalDate, drug.OtherMakers, drug.GenericMakers));
        Assert.StartsWith("WARNING: (A) PREMATURE DISCONTINUATION", drug.BoxedWarning, StringComparison.Ordinal);
        Assert.Equal("https://dailymed.nlm.nih.gov/dailymed/lookup.cfm?setid=e9481622-7cc6-418a-acb6-c5450daae9b0", drug.DailyMedUrl);
        Assert.EndsWith("ApplNo=202155", drug.DrugsAtFdaUrl, StringComparison.Ordinal);
        Assert.Equal("ndc", drug.MatchedBy);
        Assert.Null(eliquis.Device);
        // Ozempic's reported NDC 0169-4132 isn't listed, so it is matched by brand name (listing 0169-4130); its label is found by
        // the label's own product NDCs (no set ID in the directory); it has no boxed warning.
        var ozempic = (await products.GetAsync("ozempic", _ct))!.Drug!;
        Assert.Equal(("name", "0169-4130", "adec4fd2-6858-4c99-91d4-531f5f2a2d79", (string?)null, 0),
            (ozempic.MatchedBy, ozempic.ProductNdc, ozempic.LabelSetId, ozempic.BoxedWarning, ozempic.OtherMakers));
        Assert.StartsWith("OZEMPIC is indicated", ozempic.Indications, StringComparison.Ordinal);
        // MiniMed by its device identifier: GUDID facts and its decisions (the PMA original, not the supplement; the 510(k)).
        var pump = (await products.GetAsync("minimed-780g", _ct))!.Device!;
        Assert.Equal(("MiniMed 780G", "Medtronic MiniMed, Inc.", "QFG", "3", true, false), (pump.BrandName, pump.Company, pump.ProductCode, pump.DeviceClass, pump.IsRx, pump.Implantable));
        Assert.Equal([("P160017", "PMA", (DateOnly?)new DateOnly(2016, 9, 28)), ("K000001", "510(k)", new DateOnly(2000, 2, 1))],
            pump.Premarket.Select(m => (m.Number, m.Kind, m.DecisionDate)));
        Assert.Equal("https://www.accessdata.fda.gov/scripts/cdrh/cfdocs/cfpma/pma.cfm?id=P160017", pump.Premarket[0].Url);
        Assert.Null((await products.GetAsync("guardian-4-sensor", _ct))!.Device);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM fda_drug_label WHERE set_id = 'unrelated-label'"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM fda_device"));
        Assert.Equal(4L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM fda_ndc_product"));

        // Part 3, public data. Spending by brand name: Part D's "Overall" rows (not the manufacturer's), Medicaid; Part B summed over
        // HCPCS codes, with CMS's "*" dropped.
        // A brand listed by form ("Eliquis Starter Pack") counts too.
        Assert.Equal([("Part D", 2024, 1200000.0, (double?)320), ("Part D", 2024, 5000.0, 9), ("Part D", 2023, 1000000.5, 300), ("Medicaid", 2024, 250000.0, null)],
            eliquis.Spending.Select(x => (x.Program, x.Year, x.Spending, x.Beneficiaries)));
        Assert.Equal(20.0, eliquis.Spending[0].PerUnit);
        var ibranceSpend = Assert.Single((await products.GetAsync("ibrance", _ct))!.Spending);
        Assert.Equal(("Part B", "Ibrance", "Palbociclib", 8000.0, (double?)6), (ibranceSpend.Program, ibranceSpend.BrandName, ibranceSpend.GenericName,
            ibranceSpend.Spending, ibranceSpend.Beneficiaries));
        Assert.Empty((await products.GetAsync("minimed-780g", _ct))!.Spending);
        // NADAC: the newest price of each package of the listing (0003-0893); 0003-0894 is another listing.
        Assert.Equal([("00003089321", 9.32611, "Brand")], eliquis.Prices.Select(x => (x.Ndc, x.PerUnit, x.Kind)));
        // Shortages: Ozempic by its listing's NDC; Eliquis has none.
        var ozempicPage = (await products.GetAsync("ozempic", _ct))!;
        Assert.Equal([("Current", "Limited Availability")], ozempicPage.Shortages.Select(x => (x.Status, x.Availability)));
        Assert.Empty(eliquis.Shortages);
        // Recalls: a drug by name in any firm's recall; a device only in recalls by a company paying for it.
        Assert.Equal(["D-0100-2025"], eliquis.Recalls!.Latest.Select(x => x.RecallNumber).Distinct());
        var pumpRecalls = (await products.GetAsync("minimed-780g", _ct))!.Recalls!;
        Assert.Equal(["Z-0200-2024"], pumpRecalls.Latest.Select(x => x.RecallNumber).Distinct());
        // Adverse events (FAERS by the FDA brand name; MAUDE by the GUDID brand) and trials (by the generic name), asked once and cached.
        Assert.Equal(("drug", "ELIQUIS", 140, 100), (eliquis.AdverseEvents!.Kind, eliquis.AdverseEvents.QueryName, eliquis.AdverseEvents.Reports, eliquis.AdverseEvents.Serious));
        var pumpEvents = (await products.GetAsync("minimed-780g", _ct))!.AdverseEvents!;
        Assert.Equal(("device", 10, 1, 2, 7), (pumpEvents.Kind, pumpEvents.Reports, pumpEvents.Deaths, pumpEvents.Injuries, pumpEvents.Malfunctions));
        Assert.Equal(0, ozempicPage.AdverseEvents!.Reports); // asked, nothing found
        Assert.Null((await products.GetAsync("guardian-4-sensor", _ct))!.AdverseEvents); // no device record, so not asked
        Assert.Null(ibrance.AdverseEvents); // openFDA kept failing (500): skipped, asked again on the next run
        Assert.Equal(("apixaban", 482, 71), (eliquis.Trials!.QueryName, eliquis.Trials.Studies, eliquis.Trials.Recruiting));
        Assert.Equal("https://clinicaltrials.gov/search?intr=apixaban", eliquis.Trials.SearchUrl);

        // Part 4, prescribing overlap: Eliquis' Part D brands (also its form "Starter Pack"; the generic's rows are left out), all its
        // prescribers, and the active providers paid for it who prescribed it.
        var rx = eliquis.Prescribing!;
        Assert.Equal(["Eliquis", "Eliquis Starter Pack"], rx.Brands);
        Assert.Equal((2024, 2025, 3, 205L, 1, 1, 125L), (rx.Year, rx.PaymentYear, rx.Prescribers, rx.Claims, rx.PaidProviders, rx.PaidPrescribers, rx.PaidClaims));
        Assert.Equal(125.0 / 205, rx.PaidClaimShare!.Value, 6);
        var prescribers = (await products.PrescribersAsync("eliquis", "claims", 1, 50, _ct))!;
        var baker2 = Assert.Single(prescribers.Items);
        Assert.Equal(("1000000012", "BAKER, AL", 40.0, 2, 125, 62500.5, (int?)40), (baker2.Npi, baker2.Name, baker2.Paid, baker2.Payments, baker2.Claims,
            baker2.DrugCost, baker2.Beneficiaries));
        Assert.Equal(1, prescribers.TotalCount);
        Assert.Equal(["1000000012"], await products.AllPrescribersAsync("eliquis", _ct).Select(x => x.Npi).ToListAsync(_ct));
        Assert.Equal((1, 15L, 1, 15L), ((await products.GetAsync("ozempic", _ct))!.Prescribing is { } oz ? (oz.Prescribers, oz.Claims, oz.PaidPrescribers, oz.PaidClaims) : default));
        Assert.Null(ibrance.Prescribing); // no Part D brand
        Assert.Null((await products.GetAsync("minimed-780g", _ct))!.Prescribing); // a device
        Assert.Null(await products.PrescribersAsync("minimed-780g", null, 1, 50, _ct));
        // Only drugs and biologicals keep their (product, NPI) pairs.
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM op_product_npi WHERE slug IN ('minimed-780g', 'guardian-4-sensor', 'pico')"));
        var list = await products.SearchAsync(null, null, 1, 50, _ct);
        Assert.Equal(["ibrance", "guardian-4-sensor", "minimed-780g", "eliquis", "pico", "ozempic"], list.Items.Select(p => p.Slug));
        Assert.Equal(["guardian-4-sensor", "minimed-780g", "pico"], (await products.SearchAsync(null, "Device", 1, 50, _ct)).Items.Select(p => p.Slug));
        Assert.Equal(["eliquis"], (await products.SearchAsync("liqu", null, 1, 50, _ct)).Items.Select(p => p.Slug));

        // The provider page links its payers and top companies to the company pages.
        var detail = (await new ProviderDetailService(db.ConnectionString).GetAsync("1000000012", _ct))!;
        Assert.Equal("100000000002", detail.IndustryPayments!.TopPayers[0].CompanyId);
        Assert.Equal("100000000002", detail.PaymentHistory!.TopCompanies[0].CompanyId);
        // …and its top products (tied amounts by slug) to the product pages.
        Assert.Equal(["guardian-4-sensor", "minimed-780g", "eliquis", "ibrance", "ozempic"], detail.IndustryPayments.TopProducts.Select(p => p.Slug));
        // …with the provider's Medicare Part D claims of each drug that has a Part D brand.
        Assert.Equal([null, null, 125, null, 15], detail.IndustryPayments.TopProducts.Select(p => p.MedicareClaims));
        Assert.Equal(("Guardian 4 Sensor", "Device", 2500.0), (detail.IndustryPayments.TopProducts[0].Name, detail.IndustryPayments.TopProducts[0].Kind,
            detail.IndustryPayments.TopProducts[0].Amount));
    }

    [Fact]
    public async Task Facility_owners_ownership_changes_and_inspections_load_and_read_back()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ExecuteAsync(
            """
            INSERT INTO provider (npi, entity_type, org_name, sort_name, primary_taxonomy_code) VALUES ('1000000061', 2, 'SAMPLE HEALTH HOLDINGS LLC', 'SAMPLE HEALTH HOLDINGS LLC', NULL);
            INSERT INTO medicare_enrollment (npi, enrollment_id, pac_id, org_name) VALUES ('1000000061', 'O20990101000001', '0244144871', 'SAMPLE HEALTH HOLDINGS LLC');
            INSERT INTO directory_facility (ccn, type, name, city, state, active, billing_npis) VALUES
              ('330045', 'hospital', 'GOOD SAMARITAN HOSPITAL', 'WEST ISLIP', 'NY', 1, 1), ('335001', 'nursing_home', 'SMITHTOWN CENTER FOR REHAB', 'SMITHTOWN', 'NY', 1, 1);
            """);
        var server = new FakeSources();
        var clock = new FakeClock();
        foreach (var source in new[] { "cms_facility_enrollments", "cms_facility_owners", "cms_ownership_changes", "cc_nursing_home_inspections" })
        {
            Assert.True(await Loader(db, server, clock).RefreshAsync(force: false, only: source, _ct), source);
        }

        // Enrollments by ID with their associate IDs; the hospice without a CCN kept.
        Assert.Equal([("O20020101000001", "hospital", "330045", "1234567891"), ("O20020101000005", "hospice", null, "1234567895"),
                ("O20020813000023", "rhc", "353982", "0042127045")],
            (await db.QueryAsync<(string, string, string?, string)>("SELECT enrollment_id, kind, ccn, associate_id FROM cms_facility_enrollment ORDER BY enrollment_id"))
                .Where(e => e.Item1 is "O20020101000001" or "O20020101000005" or "O20020813000023"));

        // Owners: Windows-1252 read as such, leading zeros restored, names put together, owner kinds kept, the row without a role dropped.
        Assert.Equal(14L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM facility_owner"));
        Assert.Equal(("SAMPLE HOSPITAL PARTNERS, LP", "SAMPLE PARTNERS \u2013 NY", 100.0, "private_equity,for_profit", new DateTime(2026, 8, 31)),
            (await db.QueryAsync<(string, string, double, string, DateTime)>(
                "SELECT name, dba_name, percentage, owner_kinds, period_end FROM facility_owner WHERE enrollment_id = 'O20020101000001' AND owner_id = '0446239073'")).Single());
        Assert.Equal(("JANE Q EXAMPLE", "I", "BOARD CHAIR", new DateTime(1995, 7, 7)), (await db.QueryAsync<(string, string, string, DateTime)>(
            "SELECT name, owner_type, title, association_date FROM facility_owner WHERE owner_id = '0749197705'")).Single());
        Assert.Equal(("0446168215", new DateTime(2022, 1, 1), "non_profit,corporation"), (await db.QueryAsync<(string, DateTime, string)>(
            "SELECT owner_id, association_date, owner_kinds FROM facility_owner WHERE enrollment_id = 'O20020101000003' AND role_code = '43'")).Single());
        Assert.Equal("private_equity,parent_company,trust", await db.ScalarAsync<string>(
            "SELECT owner_kinds FROM facility_owner WHERE enrollment_id = 'O20020101000002' AND owner_id = '0446239073'"));
        // One row per owner: listed for three enrollments, with an ownership interest in each.
        Assert.Equal((3, 3), (await db.QueryAsync<(int, int)>("SELECT facilities, owned FROM facility_owner_entity WHERE owner_id = '0244144871'")).Single());

        // Ownership changes: the sellers' owners in an ownership or control role only (the officer is left out, the buyer's owners too).
        Assert.Equal([("330045", "330045", "CH", new DateTime(2018, 6, 1)), ("330045", "330888", "AM", new DateTime(2025, 12, 28)), ("335001", "335001", "CH", new DateTime(2023, 10, 11))],
            await db.QueryAsync<(string, string, string, DateTime)>("SELECT buyer_ccn, seller_ccn, chow_type_code, effective_date FROM facility_ownership_change ORDER BY buyer_ccn, effective_date"));
        Assert.Equal([("O20000101000099", "0555555555", "34"), ("O20111230000151", "0666666666", "43")],
            await db.QueryAsync<(string, string, string)>("SELECT enrollment_id, owner_id, role_code FROM facility_ownership_change_owner ORDER BY enrollment_id"));

        // Inspections: counts by cycle and scope and severity, every survey, the ten most serious and ten newest citations, the tags once.
        Assert.Equal([("fire", 1, "F", 1, 0), ("fire", 2, "C", 1, 0), ("health", 1, "D", 1, 0), ("health", 1, "J", 1, 1), ("health", 2, "E", 1, 0), ("health", 3, "G", 1, 1)],
            await db.QueryAsync<(string, int, string, int, int)>(
                "SELECT kind, cycle, scope_severity, deficiencies, complaint FROM nh_deficiency_count WHERE ccn = '335001' ORDER BY kind, cycle, scope_severity"));
        Assert.Equal(11L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM nh_citation WHERE ccn = '335003'")); // the K and the ten newest of twelve D
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM nh_citation WHERE ccn = '335003' AND survey_date < '2025-03-01' AND scope_severity = 'D'"));
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM nh_deficiency_tag WHERE tag = '0689' OR tag = '0345'"));

        var ownership = new OwnershipService(db.ConnectionString);
        var hospital = (await ownership.GetFacilityOwnershipAsync("330045", _ct))!;
        Assert.Equal(("O20020101000001", "GOOD SAMARITAN HOSPITAL", "1234567891", new DateOnly(2026, 8, 31)),
            (hospital.EnrollmentId, hospital.LegalName, hospital.AssociateId, hospital.OwnersAsOf));
        // Owners first, the largest stake first; then control; officers last.
        Assert.Equal([("0446239073", "owner", 100.0), ("0244144871", "owner", 80.0), ("0244144871", "control", null), ("0749197705", "officer", null)],
            hospital.Owners.Select(o => (o.OwnerId, o.RoleGroup, o.Percentage)));
        Assert.Equal(["private_equity", "for_profit"], hospital.Owners[0].Kinds);
        Assert.Equal(("Acquisition or merger", "330888", "OLD NAME HOSPITAL, INC."), (hospital.Changes[0].Type, hospital.Changes[0].SellerCcn, hospital.Changes[0].SellerName));
        var before = hospital.Changes[1].PreviousOwners.Single();
        Assert.Equal(("0555555555", "OLD SAMARITAN PARENT INC", "Direct owner (5% or more)", 100.0), (before.OwnerId, before.Name, before.Role, before.Percentage));

        var holdings = (await ownership.GetOwnerAsync("244144871", _ct))!; // leading zero optional
        Assert.Equal(("0244144871", true, 3, 3), (holdings.Owner.Id, holdings.Owner.IsOrganization, holdings.Owner.Facilities, holdings.Owner.Owned));
        Assert.Equal([("330045", "GOOD SAMARITAN HOSPITAL", "hospital"), ("335001", "SMITHTOWN CENTER FOR REHAB", "nursing_home"), (null, "NOT ENROLLED HOSPITAL", "hospital")],
            holdings.Facilities.Select(f => (f.Ccn, f.Name, f.Type)).OrderBy(f => f.Ccn ?? "z", StringComparer.Ordinal));
        Assert.Equal(["1000000061"], holdings.Npis.Select(n => n.Npi)); // by its associate ID = PAC ID, not by name
        var former = (await ownership.GetOwnerAsync("0555555555", _ct))!; // only a previous owner
        Assert.Equal((0, "OLD SAMARITAN PARENT INC"), (former.Owner.Facilities, former.Owner.Name));
        Assert.Equal(new DateOnly(2018, 6, 1), Assert.Single(former.Changes).EffectiveDate);
        Assert.Null(await ownership.GetOwnerAsync("9999999999", _ct));

        var search = await ownership.SearchOwnersAsync("sample", OwnershipService.TypeOrganization, "private_equity", 1, 50, _ct);
        Assert.Equal(["0446239073"], search.Items.Select(o => o.Id));
        Assert.Equal("0244144871", (await ownership.SearchOwnersAsync(null, null, null, 1, 1, _ct)).Items[0].Id); // owning the most first

        var inspections = (await ownership.GetInspectionsAsync("335001", _ct))!;
        Assert.Equal((new DateOnly(2026, 9, 30), 182968.0, 1), (inspections.UpdatedOn, inspections.FinesTotal, inspections.PaymentDenials));
        Assert.Equal(("F0689", "J", true), (inspections.Citations[0].Tag, inspections.Citations[0].ScopeSeverity, inspections.Citations[0].Complaint));
        Assert.StartsWith("Ensure that a nursing home area", inspections.Citations[0].Description, StringComparison.Ordinal);
        Assert.Equal(5, inspections.Surveys.Count); // health on three dates, fire safety on two
        Assert.Null(await ownership.GetInspectionsAsync("330045", _ct));

        // Unchanged a day later: the versions are probed, no file is downloaded again and the rows stay.
        clock.Advance(TimeSpan.FromDays(1));
        server.Requests.Clear();
        foreach (var source in new[] { "cms_facility_owners", "cms_ownership_changes", "cc_nursing_home_inspections" })
        {
            Assert.True(await Loader(db, server, clock).RefreshAsync(force: false, only: source, _ct), source);
        }

        Assert.DoesNotContain(server.Requests, r => r.Contains("/files/", StringComparison.Ordinal));
        Assert.Equal(14L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM facility_owner"));
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
        // The sources check side by side (speed item 7), so the order varies; the catalog is still fetched once.
        Assert.Equal(
            new[]
            {
                "HEAD https://oig.test/UPDATED.csv", "https://cms.test/data.json", "https://pdc.test/items/mj5m-pzi6", "https://pdc.test/items/27ea-46a8",
                "https://pdc.test/items/xubh-q36u", "https://pdc.test/items/4pq5-n9py", "https://pdc.test/items/dgck-syfz", "https://pdc.test/items/6jpm-sxkc",
                "https://pdc.test/items/yc9t-dgbk", "https://pdc.test/items/gxki-hrr8", "https://fda.test/download.json", "https://fda.test/download.json",
                "https://pdc.test/items/r5ix-sfxw", "https://pdc.test/items/ifjz-ge4w", "https://pdc.test/items/g6vv-u9sr",
            }.Order(StringComparer.Ordinal),
            server.Requests.Order(StringComparer.Ordinal)); // HRSA, Census, Open Payments and most FDA files are checked weekly; FDA's shortage list and adverse event export dates daily

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

    private DatasetLoader Loader(TestDatabase db, FakeSources server, FakeClock clock) => Loader(db, server, clock, _folder);

    private static DatasetLoader Loader(TestDatabase db, FakeSources server, FakeClock clock, string folder) =>
        new(new LoaderOptions
            {
                WorkFolder = folder,
                LogFolder = folder,
                StateLicenseFilesFolder = Path.Combine(folder, "state-files"),
                DownloadAttempts = 1,
                CmsCatalogUrl = "https://cms.test/data.json",
                ProviderDataMetastoreUrl = "https://pdc.test/items/",
                LeieUrl = "https://oig.test/UPDATED.csv",
                HrsaHpsaUrlTemplate = "https://hrsa.test/BCD_HPSA_FCT_DET_{discipline}.csv",
                CensusPopulationBaseUrl = "https://census.test/popest/",
                OpenPaymentsCatalogUrl = "https://op.test/items",
                FdaDownloadIndexUrl = "https://fda.test/download.json",
                SecCompanyTickersUrl = "https://sec.test/company_tickers_exchange.json",
                SecUserAgent = "getnpidata-tests ci@example.test",
                OigCiaUrl = "https://oig.test/browse-cias/",
                MedicaidCatalogUrl = "https://medicaid.test/items",
                MediCalPackageUrl = "https://chhs.test/package_show",
                OpenFdaApiUrl = "https://openfda.test/",
                ClinicalTrialsApiUrl = "https://ct.test/api/v2/studies",
                FemaStructuresListingUrl = "https://fema.test/list",
                FemaStructuresBaseUrl = "https://fema.test/files/",
                ApiRequestDelay = TimeSpan.Zero,
                OpenFdaApiKey = "test-key", // adverse event counts asked several at a time
                OpenFdaKeyedRequestInterval = TimeSpan.Zero,
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
            ["https://pdc.test/items/r5ix-sfxw"] = ("2026-09-30", "NH_HealthCitations_Sep2026.csv"),
            ["https://pdc.test/items/ifjz-ge4w"] = ("2026-09-30", "NH_FireSafetyCitations_Sep2026.csv"),
            ["https://pdc.test/items/g6vv-u9sr"] = ("2026-09-30", "NH_Penalties_Sep2026.csv"),
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests)
            {
                // The sources run side by side.
                Requests.Add(request.Method == HttpMethod.Head ? "HEAD " + url : url);
            }
            if (ProviderData.TryGetValue(url, out var entry))
            {
                var json = $$"""{"modified":"{{entry.Modified}}","distribution":[{"downloadURL":"https://pdc.test/files/{{entry.File}}","mediaType":"text/csv"}]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }

            if (url.StartsWith("https://fda.test/", StringComparison.Ordinal) && url.EndsWith(".zip", StringComparison.Ordinal))
            {
                // openFDA bulk files are zipped JSON; the drug and device files share one fixture.
                using var zipped = new MemoryStream();
                using (var zip = new System.IO.Compression.ZipArchive(zipped, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                {
                    // The part 2 files (ndc, drugsfda, label, udi, 510k, pma) have their own fixtures.
                    var endpoint = new[] { "ndc", "drugsfda", "label", "udi", "510k", "pma", "shortages" }.FirstOrDefault(e => url.Contains($"-{e}-", StringComparison.Ordinal));
                    zip.CreateEntryFromFile(Fixtures.Path(endpoint is null ? "datasets/fda_enforcement_sample.json" : $"datasets/fda_{endpoint}_sample.json"),
                        "records-0001-of-0001.json");
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipped.ToArray()) });
            }

            // openFDA counts and ClinicalTrials.gov totals (part 3): a few known answers, nothing for everything else.
            if (url.StartsWith("https://openfda.test/", StringComparison.Ordinal) || url.StartsWith("https://ct.test/", StringComparison.Ordinal))
            {
                if (url.Contains("medicinalproduct:%22IBRANCE%22", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)); // openFDA fails now and then
                }

                var answer = url switch
                {
                    _ when url.Contains("medicinalproduct:%22ELIQUIS%22", StringComparison.Ordinal) => """{"results":[{"term":1,"count":100},{"term":2,"count":40}]}""",
                    _ when url.Contains("brand_name:%22MiniMed%20780G%22", StringComparison.Ordinal) =>
                        """{"results":[{"term":"Malfunction","count":7},{"term":"Injury","count":2},{"term":"Death","count":1}]}""",
                    _ when url.Contains("query.intr=apixaban", StringComparison.Ordinal) && url.Contains("RECRUITING", StringComparison.Ordinal) => """{"totalCount":71,"studies":[]}""",
                    _ when url.Contains("query.intr=apixaban", StringComparison.Ordinal) => """{"totalCount":482,"studies":[]}""",
                    _ when url.StartsWith("https://ct.test/", StringComparison.Ordinal) => """{"totalCount":0,"studies":[]}""",
                    _ => null,
                };
                return Task.FromResult(answer is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":{"code":"NOT_FOUND"}}""") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) });
            }

            if (url.StartsWith("https://oig.test/browse-cias/?page=", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><body>No more agreements.</body></html>") });
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

            // Rhode Island: one POST per profession (only Physician has rows here); Vermont's feed (one page); West Virginia's rosters.
            if (url == RhodeIslandDataset.DownloadUrl.AbsoluteUri)
            {
                var form = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                var physicians = File.ReadAllText(Fixtures.Path("datasets/state_ri_physician.csv"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(form.Contains("prof=Physician&", StringComparison.Ordinal) ? physicians : physicians[..(physicians.IndexOf('\n') + 1)]),
                });
            }

            if (url.StartsWith(VermontDataset.FeedUrl + "?", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(url.Contains("start=0&", StringComparison.Ordinal)
                        ? File.ReadAllText(Fixtures.Path("datasets/state_vt_feed.json"))
                        : """{"draw":1,"recordsTotal":2,"recordsFiltered":2,"data":[]}"""),
                });
            }

            if (url == "https://wvbom.wv.gov/download_resource.asp?id=713")
            {
                var roster = XlsxWriter.Write(
                [
                    ["MEDICAL DOCTORS (MDs) WHO HOLD AN ACTIVE STATUS LICENSE", null, null, null, null, null],
                    ["First Name", "Middle Name", "Last Name", "Suffix", "License Number", "License Expiration Date"],
                    ["Ron", null, "Black", null, "16132", "6/30/2028"],
                    [null, null, null, null, null, null],
                ]);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(roster) });
            }

            string? fixture = url switch
            {
                "https://wvbom.wv.gov/Rosters.asp" => "datasets/state_wv_rosters.html",
                "https://oig.test/UPDATED.csv" when LeieAvailable => "datasets/leie_sample.csv",
                "https://cms.test/data.json" => "datasets/cms_catalog_sample.json",
                "https://cms.test/files/DSD_PTD_DY24.csv" => "datasets/cms_part_d_spending_sample.csv",
                "https://cms.test/files/DSD_PTB_DY24.csv" => "datasets/cms_part_b_spending_sample.csv",
                "https://cms.test/files/DSD_MCD_DY24.csv" => "datasets/cms_medicaid_spending_sample.csv",
                "https://cms.test/files/MUP_DPR_DY24_NPIBN.csv" => "datasets/part_d_by_provider_and_drug_sample.csv",
                "https://medicaid.test/items" => "datasets/medicaid_catalog_sample.json",
                "https://chhs.test/package_show" => "datasets/medi_cal_package_sample.json",
                "https://chhs.test/medi-cal-ffs-provider-listing.csv" => "datasets/medi_cal_sample.csv",
                "https://medicaid.test/nadac-national-average-drug-acquisition-cost-10-07-2026.csv" => "datasets/nadac_sample.csv",
                "https://cms.test/files/OptOut_August2026.csv" => "datasets/optout_sample.csv",
                "https://cms.test/files/OrderReferring_20261008.csv" => "datasets/orderref_sample.csv",
                "https://cms.test/files/Hospital_Enrollments_2026.07.31.csv" => "datasets/hospital_enrollments_sample.csv",
                "https://cms.test/files/PPEF_Enrollment_Extract_2026.07.17.csv" => "datasets/ppef_sample.csv",
                "https://cms.test/files/revalidation_clinic_group_practice_reassignment_pa_xref.csv" => "datasets/reassignment_sample.csv",
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
                "https://cms.test/files/FQHC_Enrollments_2026.07.17.csv" => "datasets/fqhc_enrollments_sample.csv",
                "https://cms.test/files/RHC_Enrollments_2026.07.17.csv" => "datasets/rhc_enrollments_sample.csv",
                "https://cms.test/files/Hospital_All_Owners_2026.07.31_update.csv" => "datasets/hospital_all_owners_sample.csv",
                "https://cms.test/files/SNF_All_Owners_2026.07.31_update.csv" => "datasets/snf_all_owners_sample.csv",
                "https://cms.test/files/HHA_All_Owners_2026.07.17.csv" => "datasets/hha_all_owners_sample.csv",
                "https://cms.test/files/Hospice_All_Owners_2026.07.17.csv" => "datasets/hospice_all_owners_sample.csv",
                "https://cms.test/files/FQHC_All_Owners_2026.07.17.csv" => "datasets/fqhc_all_owners_sample.csv",
                "https://cms.test/files/RHC_All_Owners_2026.07.17.csv" => "datasets/rhc_all_owners_sample.csv",
                "https://cms.test/files/Hospital_CHOW_2026.07.17.csv" => "datasets/hospital_chow_sample.csv",
                "https://cms.test/files/Hospital_CHOW_Owners_2026.07.17.csv" => "datasets/hospital_chow_owners_sample.csv",
                "https://cms.test/files/SNF_CHOW_2026.07.17.csv" => "datasets/snf_chow_sample.csv",
                "https://cms.test/files/SNF_CHOW_Owners_2026.07.17.csv" => "datasets/snf_chow_owners_sample.csv",
                "https://pdc.test/files/NH_HealthCitations_Sep2026.csv" => "datasets/nh_health_deficiencies_sample.csv",
                "https://pdc.test/files/NH_FireSafetyCitations_Sep2026.csv" => "datasets/nh_fire_safety_deficiencies_sample.csv",
                "https://pdc.test/files/NH_Penalties_Sep2026.csv" => "datasets/nh_penalties_sample.csv",
                "https://cms.test/files/OPIOID_TREATMENT_PROGRAM_PROVIDERS_10052026.csv" => "datasets/otp_sample.csv",
                "https://cms.test/files/revalidation_base.csv" => "datasets/revalidation_sample.csv",
                "https://cms.test/files/mup_dme_ry26_p05_v10_dy24_supr.csv" => "datasets/dme_supplier_sample.csv",
                "https://cms.test/files/Hospital_and_other.DATA.Q2_2026.csv" => "datasets/pos_qies_sample.csv",
                "https://cms.test/files/POS_iQIES.DATA.Q2_2026.csv" => "datasets/pos_iqies_sample.csv",
                "https://cms.test/files/MUP_PHY_D24_Prov.csv" => "datasets/physician_by_provider_sample.csv",
                "https://cms.test/files/MUP_PHY_D23_Prov.csv" => "datasets/physician_by_provider_2023_sample.csv",
                "https://cms.test/files/MUP_PHY_D24_Prov_Svc.csv" => "datasets/physician_by_service_sample.csv",
                "https://cms.test/files/mup_dpr_dy24_npi.csv" => "datasets/part_d_by_provider_sample.csv",
                "https://hrsa.test/BCD_HPSA_FCT_DET_PC.csv" => "datasets/hpsa_PC_sample.csv",
                "https://hrsa.test/BCD_HPSA_FCT_DET_DH.csv" => "datasets/hpsa_DH_sample.csv",
                "https://hrsa.test/BCD_HPSA_FCT_DET_MH.csv" => "datasets/hpsa_MH_sample.csv",
                "https://census.test/popest/" => "datasets/census_popest_listing.html",
                "https://fda.test/download.json" => "datasets/fda_download_index_sample.json",
                "https://sec.test/company_tickers_exchange.json" => "datasets/sec_company_tickers_sample.json",
                "https://oig.test/browse-cias/" => "datasets/oig_cia_page_sample.html",
                "https://www.mbp.state.md.us/forms/doctor_list_revised.csv" => "datasets/state_md_doctors.csv",
                "https://www.mbp.state.md.us/forms/allied_health_list.csv" => "datasets/state_md_allied.csv",
                "https://op.test/items" => "datasets/open_payments_catalog_sample.json",
                "https://op.test/PGYR2025_P06302026/OP_DTL_GNRL_PGYR2025_P06302026_06032026.csv" => "datasets/open_payments_sample.csv",
                "https://op.test/PGYR2025_P06302026/OP_DTL_RSRCH_PGYR2025_P06302026_06032026.csv" => "datasets/open_payments_research_sample.csv",
                "https://op.test/SMRY_P06302026/PBLCTN_PHYSN_NON_PHYSN_PRCTNR_SMRY_P06302026_06032026.csv" => "datasets/open_payments_years_sample.csv",
                "https://op.test/SMRY_P06302026/PBLCTN_SMRY_BY_CR_BY_AMGPO_PGYRall_P06302026_06032026.csv" => "datasets/open_payments_companies_sample.csv",
                "https://op.test/SMRY_P06302026/PBLCTN_RPTG_ORG_PRFL_SRCH_P06302026_06032026.csv" => "datasets/op_company_profile_sample.csv",
                "https://op.test/SMRY_P06302026/PBLCTN_RPTG_ORG_SMRY_P06302026_06032026.csv" => "datasets/op_company_years_sample.csv",
                "https://census.test/popest/2020-2025/counties/totals/co-est2025-alldata.csv" => "datasets/census_population_sample.csv",
                "https://fema.test/list" => "datasets/fema_listing_sample.xml",
                "https://fema.test/files/usa-structures/geoparquet/fema_part1.parquet" => "datasets/fema_part1.parquet",
                "https://fema.test/files/usa-structures/geoparquet/fema_part2.parquet" => "datasets/fema_part2.parquet",
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
            else if (url.StartsWith("https://sec.test", StringComparison.Ordinal))
            {
                content.Headers.LastModified = new DateTimeOffset(2026, 10, 9, 6, 0, 0, TimeSpan.Zero);
            }
            else if (url.StartsWith("https://www.mbp.state.md.us", StringComparison.Ordinal))
            {
                content.Headers.LastModified = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
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
