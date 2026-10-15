using System.Net;
using System.Net.Http.Headers;
using Npi.Core.Search;
using Npi.Loader.Datasets;
using Npi.Loader.Projection;
using Serilog.Core;

namespace Npi.Loader.Tests.Integration;

/// <summary>
/// Health systems (AHRQ Compendium, from the owner's files), MSSP ACOs and HRSA health center sites (docs/PRODUCT_PLAN.md step 3.6
/// part 2): loaded from fixtures into a scratch database, then read by the facility list, the health system, ACO and health center
/// services and the facility profile.
/// </summary>
public sealed class SystemsIntegrationTests : IDisposable
{
    private const string HospitalNpi = "1000000002";
    private const string CenterNpi = "1234567893";
    private const string Clinician = "1000000001";

    private readonly string _folder = Directory.CreateTempSubdirectory("npi-systems-it-").FullName;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string AhrqFolder => Path.Combine(_folder, "ahrq-compendium");

    private DatasetLoader Loader(TestDatabase db, FakeSources server) =>
        new(new LoaderOptions
            {
                WorkFolder = _folder,
                LogFolder = _folder,
                DownloadAttempts = 1,
                CmsCatalogUrl = "https://cms.test/data.json",
                HrsaHealthCenterSitesUrl = "https://hrsa.test/Health_Center_Service_Delivery_and_LookAlike_Sites.csv",
                AhrqCompendiumFolder = AhrqFolder,
                AhrqCompendiumPageUrl = "https://ahrq.test/compendium.html",
                Hrsa340bFolder = Path.Combine(_folder, "hrsa-340b"),
                Hrsa340bPageUrl = "https://opais.test/Reports",
            },
            db.Database, new HttpClient(server), Logger.None);

    private async Task SeedAsync(TestDatabase db) =>
        await db.ExecuteAsync(
            """
            INSERT INTO provider (npi, entity_type, last_name, first_name, org_name, sort_name) VALUES
              (@HospitalNpi, 2, NULL, NULL, 'HARBOR HOSPITAL INC', 'HARBOR HOSPITAL INC'),
              (@CenterNpi, 2, NULL, NULL, 'HEALTHY COMMUNITY INC', 'HEALTHY COMMUNITY INC'),
              (@Clinician, 1, 'BAKER', 'ANN', NULL, 'BAKER, ANN'),
              -- ACO participants by name: one organization with the name, two with another, one with a shorter name (no match).
              ('1000000103', 2, NULL, NULL, 'HARBOR MEDICAL GROUP, P.C.', 'HARBOR MEDICAL GROUP, P.C.'),
              ('1000000129', 2, NULL, NULL, 'DESERT FAMILY PRACTICE PLLC', 'DESERT FAMILY PRACTICE PLLC'),
              ('1000000137', 2, NULL, NULL, 'Desert Family Practice, P.L.L.C.', 'DESERT FAMILY PRACTICE, P.L.L.C.'),
              ('1000000145', 2, NULL, NULL, 'GOOD SAMARITAN', 'GOOD SAMARITAN'),
              -- A hospital and five of its own NPIs (more than 5: linked), naming it as their parent; one is its rural health clinic.
              ('1000000200', 2, NULL, NULL, 'GOOD SAMARITAN HOSPITAL', 'GOOD SAMARITAN HOSPITAL'),
              ('1000000201', 2, NULL, NULL, 'Good Samaritan Hospital', 'GOOD SAMARITAN HOSPITAL'),
              ('1000000202', 2, NULL, NULL, 'GOOD SAMARITAN HOSPITAL', 'GOOD SAMARITAN HOSPITAL'),
              ('1000000203', 2, NULL, NULL, 'GOOD SAMARITAN HOSPITAL', 'GOOD SAMARITAN HOSPITAL'),
              ('1000000204', 2, NULL, NULL, 'GOOD SAMARITAN HOSPITAL', 'GOOD SAMARITAN HOSPITAL'),
              ('1000000205', 2, NULL, NULL, 'GOOD SAMARITAN HOSPITAL', 'GOOD SAMARITAN HOSPITAL');
            INSERT INTO provider_taxonomy (npi, slot, taxonomy_code, is_primary) VALUES
              ('1000000200', 1, '282N00000X', 1), ('1000000201', 1, '273R00000X', 1), ('1000000202', 1, '282N00000X', 1),
              ('1000000203', 1, '282N00000X', 1), ('1000000204', 1, '283X00000X', 1), ('1000000205', 1, '261QR1300X', 1);
            INSERT INTO provider_profile (npi, parent_org_name) VALUES
              ('1000000201', 'GOOD SAMARITAN HOSPITAL'), ('1000000202', 'Good Samaritan Hospital'), ('1000000203', 'GOOD SAMARITAN HOSPITAL'),
              ('1000000204', 'GOOD SAMARITAN HOSPITAL'), ('1000000205', 'GOOD SAMARITAN HOSPITAL');
            INSERT INTO cms_pos_facility (ccn, name, category, address, city, state, zip5, active, termination_code, termination_date) VALUES
              ('330045', 'GOOD SAMARITAN HOSPITAL', '01', '1000 MONTAUK HWY', 'WEST ISLIP', 'NY', '11795', 1, '00', NULL),
              ('330046', 'HARBOR SOUTH HOSPITAL', '01', '1 SHORE RD', 'BABYLON', 'NY', '11702', 1, '00', NULL),
              ('331850', 'HEALTHY COMMUNITY CENTER', '20', '5 MAIN ST', 'BROOKLYN', 'NY', '11201', 1, NULL, NULL);
            INSERT INTO cms_facility_npi (ccn, npi, kind) VALUES ('330045', @HospitalNpi, 'hospital'), ('331850', @CenterNpi, 'fqhc');
            INSERT INTO medicare_enrollment (npi, enrollment_id, pac_id, provider_type, state, org_name) VALUES
              (@HospitalNpi, 'O20200101000009', '9000000001', 'PART A PROVIDER - HOSPITAL', 'NY', 'HARBOR HOSPITAL INC'),
              ('1000000103', 'O20200101000010', '9000000001', 'CLINIC/GROUP PRACTICE', 'NY', 'HARBOR MEDICAL GROUP, P.C.');
            INSERT INTO medicare_reassignment (group_pac_id, group_enrollment_id, group_name, group_state, record_type, npi, specialty) VALUES
              ('9000000001', 'O20200101000009', 'Harbor Hospital Inc', 'NY', 'Reassignment', @Clinician, 'Internal Medicine');
            """, new { HospitalNpi, CenterNpi, Clinician });

    private void DropCompendiumFiles()
    {
        Directory.CreateDirectory(AhrqFolder);
        File.Copy(Fixtures.Path("datasets/ahrq_compendium_2023_sample.csv"), Path.Combine(AhrqFolder, "chsp-compendium-2023-rev.csv"));
        File.Copy(Fixtures.Path("datasets/ahrq_hospital_linkage_2023_sample.csv"), Path.Combine(AhrqFolder, "chsp-hospital-linkage-2023.csv"));
        File.Copy(Fixtures.Path("datasets/ahrq_nh_linkage_2023_sample.csv"), Path.Combine(AhrqFolder, "chsp-nh-linkage-2023.csv"));
        File.Copy(Fixtures.Path("datasets/ahrq_hhco_linkage_2023_sample.csv"), Path.Combine(AhrqFolder, "chsp-hhco-linkage-2023.csv"));
    }

    [Fact]
    public async Task Systems_acos_and_health_center_sites_load_and_link_by_identifiers()
    {
        await using var db = await TestDatabase.CreateAsync();
        await SeedAsync(db);
        DropCompendiumFiles();
        Directory.CreateDirectory(Path.Combine(_folder, "hrsa-340b"));
        File.Copy(Fixtures.Path("datasets/hrsa_340b_daily_export_sample.zip"), Path.Combine(_folder, "hrsa-340b", "340B_CoveredEntityJson_Daily_20261015_142651.zip"));
        var server = new FakeSources();
        foreach (var source in new[] { "ahrq_compendium", "cms_aco", "hrsa_health_center_sites", "hrsa_340b" })
        {
            Assert.True(await Loader(db, server).RefreshAsync(force: false, only: source, _ct));
        }

        // 340B (the JSON export, streamed from its zip): every entity with an ID and a type, participating or not, a child site with
        // its parent; the NPIs it lists (a phone number in npiNumbers isn't one).
        Assert.Equal([("CH900001", (sbyte)1, (string?)null, (string?)null), ("DSH140002", (sbyte)0, "140002", null), ("DSH330045", (sbyte)1, "330045", null),
                ("DSH330045AB", (sbyte)1, "330045", "DSH330045"), ("FQHCLA900003", (sbyte)1, null, null), ("RRC050001", (sbyte)1, "050001", null)],
            await db.QueryAsync<(string, sbyte, string?, string?)>("SELECT id_340b, participating, ccn, parent_id_340b FROM hrsa_340b_entity ORDER BY id_340b"));
        Assert.Equal([("CH900001", "1234567893")], await db.QueryAsync<(string, string)>("SELECT id_340b, npi FROM hrsa_340b_npi"));
        Assert.StartsWith("340B_CoveredEntityJson_Daily_20261015_142651.zip ", await db.ScalarAsync<string>("SELECT version FROM reference_data WHERE source = 'hrsa_340b'"), StringComparison.Ordinal);

        // AHRQ: 3 systems; the linkage rows of a system in the file (the padded CCN included, the unknown system's left out), the
        // nursing home, and the home health agency, hospice and home dialysis facility of the hhco file.
        Assert.Equal(3L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM health_system"));
        Assert.Equal(["010001", "140002", "242301", "330045", "330046", "331501", "335002", "337001"], await db.QueryAsync<string>("SELECT ccn FROM health_system_facility ORDER BY ccn"));
        Assert.Equal(("nursing_home", "HSI90000001"), (await db.QueryAsync<(string, string)>("SELECT kind, system_id FROM health_system_facility WHERE ccn = '335002'")).Single());
        Assert.Equal([("242301", "dialysis"), ("331501", "hospice"), ("337001", "home_health")],
            await db.QueryAsync<(string, string)>("SELECT ccn, kind FROM health_system_facility WHERE ccn IN ('242301', '331501', '337001') ORDER BY ccn"));
        Assert.Matches("^2023 \\(4 files\\) [0-9a-f]{12}$", await db.ScalarAsync<string>("SELECT version FROM reference_data WHERE source = 'ahrq_compendium'"));

        // ACOs: the 2026 release; the participant of an ACO not in the file is left out, a name listed twice counts once.
        Assert.Equal([("A9001", (short)2026), ("A9002", (short)2026)], await db.QueryAsync<(string, short)>("SELECT aco_id, program_year FROM aco ORDER BY aco_id"));
        Assert.Equal(3L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM aco_participant"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM aco_snf_affiliate"));

        // HRSA: the three service sites, not the administrative office.
        Assert.Equal(["BPS-H80-900001", "BPS-H80-900002", "BPS-LAL-900003"], await db.QueryAsync<string>("SELECT site_id FROM hrsa_health_center_site ORDER BY site_id"));

        // Unchanged files are not loaded again (within the check interval nothing is even asked).
        var loadedAt = await db.ScalarAsync<DateTime>("SELECT loaded_at FROM reference_data WHERE source = 'cms_aco'");
        server.Requests.Clear();
        Assert.True(await Loader(db, server).RefreshAsync(force: false, only: "cms_aco", _ct));
        Assert.Empty(server.Requests);
        Assert.Equal(loadedAt, await db.ScalarAsync<DateTime>("SELECT loaded_at FROM reference_data WHERE source = 'cms_aco'"));

        // The facility list: the hospital in its system, the FQHC with its health center (by Medicare number), the site matched by
        // neither an own row; the second site's NPI bills for the FQHC, so it merges there too.
        var builder = new DirectoryBuilder(db.Database, Logger.None, 0.95);
        Assert.True(await builder.IsStaleAsync(_ct));
        await builder.BuildAsync(_ct);
        using (var facilities = new FacilityDirectory(db.ConnectionString))
        {
            var harbor = await facilities.SearchAsync(new FacilityFilter { System = "HSI90000001" }, _ct);
            Assert.Equal(["330045", "330046"], harbor.Items.Select(f => f.Ccn).Order(StringComparer.Ordinal));
            Assert.All(harbor.Items, f => Assert.Equal("Harbor Health System", f.SystemName));
            var center = (await facilities.SearchAsync(new FacilityFilter { Q = "331850" }, _ct)).Items.Single();
            Assert.Equal(("HEALTHY COMMUNITY INC", "www.example.org/hcc", (string?)null), (center.HealthCenter, center.Website, center.SystemId));
            var van = (await facilities.SearchAsync(new FacilityFilter { Type = FacilityTypes.HealthCenterSite }, _ct)).Items.Single();
            Assert.Equal(("BPS-LAL-900003", FacilitySummary.SourceHrsa, "Look-alike site, mobile van", 42.65), (van.Ccn, van.Source, van.Kind, van.Latitude));

            // 340B, participating only: the hospital by its Medicare number, the FQHC by the NPI CMS enrolls for it, the site by its HRSA site ID.
            var covered = (await facilities.SearchAsync(new FacilityFilter { In340B = true }, _ct)).Items.ToDictionary(f => f.Ccn);
            Assert.Equal(["330045", "331850", "BPS-LAL-900003"], covered.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(("DSH", new DateOnly(2012, 1, 1)), (covered["330045"].Program340B, covered["330045"].Program340BSince));
            Assert.Equal(("CH", "FQHCLA"), (covered["331850"].Program340B, covered["BPS-LAL-900003"].Program340B));
        }

        // ACO participants by exact legal name: one organization, two (candidates), six hospital NPIs (a hospital and its subparts,
        // grouped by the parent they name; "GOOD SAMARITAN" is a shorter name, no match).
        Assert.Equal([("A9001", "HARBOR MEDICAL GROUP PC", "1000000103", (sbyte)1), ("A9001", "GOOD SAMARITAN HOSPITAL", "1000000200", (sbyte)6),
                ("A9001", "GOOD SAMARITAN HOSPITAL", "1000000201", (sbyte)6), ("A9001", "GOOD SAMARITAN HOSPITAL", "1000000202", (sbyte)6),
                ("A9001", "GOOD SAMARITAN HOSPITAL", "1000000203", (sbyte)6), ("A9001", "GOOD SAMARITAN HOSPITAL", "1000000204", (sbyte)6),
                ("A9001", "GOOD SAMARITAN HOSPITAL", "1000000205", (sbyte)6),
                ("A9002", "DESERT FAMILY PRACTICE, PLLC", "1000000129", (sbyte)2), ("A9002", "DESERT FAMILY PRACTICE, PLLC", "1000000137", (sbyte)2)],
            await db.QueryAsync<(string, string, string, sbyte)>("SELECT aco_id, name, npi, candidates FROM aco_participant_npi ORDER BY aco_id, npi"));
        Assert.Equal([((sbyte)0, (string?)null, (string?)null), ((sbyte)1, "parent", "GOOD SAMARITAN HOSPITAL")],
            await db.QueryAsync<(sbyte, string?, string?)>("SELECT DISTINCT hospital_group, grouped_by, grouped_value FROM aco_participant_npi ORDER BY hospital_group"));

        // Health systems: search, a system's page (facilities by type, the group through the hospital's enrollment), and the links.
        var systems = new HealthSystemService(db.ConnectionString);
        Assert.Equal(["HSI90000001"], (await systems.SearchAsync(new HealthSystemFilter { Q = "harbor islip" }, _ct)).Items.Select(s => s.SystemId));
        Assert.Equal(["HSI90000002"], (await systems.SearchAsync(new HealthSystemFilter { State = "il" }, _ct)).Items.Select(s => s.SystemId));
        Assert.Equal(["HSI90000001", "HSI90000003", "HSI90000002"], (await systems.SearchAsync(new HealthSystemFilter { Sort = "-beds" }, _ct)).Items.Select(s => s.SystemId));
        Assert.Equal(["HSI90000001", "HSI90000003", "HSI90000002"], (await systems.SearchAsync(new HealthSystemFilter(), _ct)).Items.Select(s => s.SystemId));
        Assert.Equal(1, (await systems.SearchAsync(new HealthSystemFilter { Q = "HSI90000002" }, _ct)).TotalCount);

        var detail = (await systems.GetAsync("HSI90000001", _ct))!;
        Assert.Equal(("Harbor Health System", 5, 5), (detail.System.Name, detail.System.LinkedFacilities, detail.Facilities.Count));
        Assert.Equal([(FacilityTypes.Hospital, "330045", true), (FacilityTypes.Hospital, "330046", true), (FacilityTypes.NursingHome, "335002", (bool?)null),
                (FacilityTypes.HomeHealth, "337001", null), (FacilityTypes.Hospice, "331501", null)],
            detail.Facilities.Select(f => (f.Type, f.Ccn, f.Active)));
        Assert.Equal("SHORE VIEW CARE", detail.Facilities[2].Name); // not in the facility list: the linkage file's name
        Assert.Equal(["NY"], detail.States);
        Assert.Equal([("9000000001", 1)], detail.Groups.Select(g => (g.PacId, g.Clinicians)));
        Assert.Null(await systems.GetAsync("HSI99999999", _ct));

        Assert.Equal(["HSI90000001"], (await systems.ForCcnAsync("330045", _ct)).Select(l => l.SystemId));
        Assert.Equal([("HSI90000001", "330045")], (await systems.ForNpiAsync(HospitalNpi, _ct)).Select(l => (l.SystemId, l.Ccn)));
        Assert.Equal(["HSI90000001"], (await systems.ForPacAsync("9000000001", _ct)).Select(l => l.SystemId));
        Assert.Empty(await systems.ForNpiAsync(CenterNpi, _ct));

        var overview = await systems.GetOverviewAsync(_ct);
        Assert.Equal(new NetworkOverview(3, 2023, 8, 2, 2026, 3, 3, 2), overview);

        // ACOs: by name, by a participant's name, by state in the service area; one ACO's page.
        var acos = new AcoService(db.ConnectionString);
        Assert.Equal(["A9001"], (await acos.SearchAsync(new AcoFilter { Q = "harbor care" }, _ct)).Items.Select(a => a.AcoId));
        Assert.Equal(["A9002"], (await acos.SearchAsync(new AcoFilter { Q = "desert family" }, _ct)).Items.Select(a => a.AcoId));
        Assert.Equal(["A9002"], (await acos.SearchAsync(new AcoFilter { State = "NV" }, _ct)).Items.Select(a => a.AcoId));
        Assert.Equal(["A9001"], (await acos.SearchAsync(new AcoFilter { Q = "a9001" }, _ct)).Items.Select(a => a.AcoId));
        var aco = (await acos.GetAsync("A9001", _ct))!;
        Assert.Equal(("ENHANCED", 3, new DateOnly(2016, 1, 1), 2, 1), (aco.Aco.Track, aco.Aco.AgreementPeriod, aco.Aco.InitialStartDate, aco.Aco.Participants, aco.Aco.SnfAffiliates));
        Assert.Equal([("GOOD SAMARITAN HOSPITAL", "name", 6, true, "parent"), ("HARBOR MEDICAL GROUP PC", "name", 1, false, (string?)null)],
            aco.Participants.Select(p => (p.Name, p.MatchedBy, p.Organizations.Count, p.HospitalGroup, p.GroupedBy)));
        Assert.Equal(("1000000103", "HARBOR MEDICAL GROUP, P.C."), (aco.Participants[1].Organizations[0].Npi, aco.Participants[1].Organizations[0].Name));
        Assert.Equal(["1000000129", "1000000137"], (await acos.GetAsync("A9002", _ct))!.Participants.Single().Organizations.Select(o => o.Npi).Order(StringComparer.Ordinal));
        Assert.Equal(["SHORE VIEW CARE LLC"], aco.SnfAffiliates);
        Assert.Null(await acos.GetAsync("A0000", _ct));

        // An organization's ACOs, and a group's through its organization NPIs; always a name match.
        Assert.Equal([("A9001", "Harbor Accountable Care LLC", 2026, 1, "name")],
            (await acos.ForNpiAsync("1000000103", _ct)).Select(m => (m.AcoId, m.AcoName, m.ProgramYear, m.Candidates, m.MatchedBy)));
        Assert.Equal([("A9002", 2)], (await acos.ForNpiAsync("1000000137", _ct)).Select(m => (m.AcoId, m.Candidates)));
        Assert.Equal([("A9001", 6, true, "parent", "GOOD SAMARITAN HOSPITAL")],
            (await acos.ForNpiAsync("1000000204", _ct)).Select(m => (m.AcoId, m.Candidates, m.HospitalGroup, m.GroupedBy, m.GroupedValue)));
        Assert.Equal(["A9001"], (await acos.ForPacAsync("9000000001", _ct)).Select(m => m.AcoId));
        Assert.Empty(await acos.ForNpiAsync("1000000145", _ct));

        // Health center sites: a site's page and the sites that are a CCN (by Medicare number, and by the NPI CMS enrolls for it).
        var sites = new HealthCenterService(db.ConnectionString);
        var main = (await sites.GetSiteAsync("BPS-H80-900001", _ct))!;
        Assert.Equal((true, (string?)null, 1, "BPS-H80-900002"), (main.CcnListed, main.NpiName, main.OtherSiteCount, main.OtherSites.Single().SiteId));
        Assert.Equal("HEALTHY COMMUNITY INC", (await sites.GetSiteAsync("BPS-H80-900002", _ct))!.NpiName);
        // By name: "… Center Main" (its Medicare number), then "… School Clinic" (its NPI).
        Assert.Equal(["BPS-H80-900001", "BPS-H80-900002"], (await sites.ForCcnAsync("331850", _ct)).Select(s => s.SiteId));
        Assert.Null(await sites.GetSiteAsync("BPS-NONE-1", _ct));
        Assert.Equal([("FQHCLA900003", "site")], (await sites.GetSiteAsync("BPS-LAL-900003", _ct))!.Covered340B.Select(e => (e.Id340B, e.MatchedBy)));
        Assert.Equal([("CH900001", "npi")], (await sites.GetSiteAsync("BPS-H80-900002", _ct))!.Covered340B.Select(e => (e.Id340B, e.MatchedBy)));

        // The facility profile names its system, health center sites and 340B registrations.
        var organizations = new OrganizationService(db.ConnectionString);
        var hospital = (await organizations.GetFacilityAsync("330045", 1, _ct))!;
        Assert.Equal(["Harbor Health System"], hospital.Systems.Select(s => s.Name));
        Assert.Equal([("DSH330045AB", "Disproportionate share hospital", true, new DateOnly(2014, 7, 1), "ccn"),
                ("DSH330045", "Disproportionate share hospital", true, new DateOnly(2012, 1, 1), "ccn")], // its child site shares the CCN
            hospital.Covered340B.Select(e => (e.Id340B, e.EntityTypeName, e.Participating, e.StartDate, e.MatchedBy)));
        var fqhc = (await organizations.GetFacilityAsync("331850", 1, _ct))!;
        Assert.Equal(2, fqhc.HealthCenterSites.Count);
        Assert.Equal([("CH900001", "npi")], fqhc.Covered340B.Select(e => (e.Id340B, e.MatchedBy)));
    }

    [Fact]
    public async Task Without_compendium_files_the_source_is_skipped_and_a_download_loads_it()
    {
        await using var db = await TestDatabase.CreateAsync();
        var server = new FakeSources();
        Assert.True(await Loader(db, server).RefreshAsync(force: false, only: "ahrq_compendium", _ct));
        Assert.Equal(HealthSystemSource.NoFiles, await db.ScalarAsync<string>("SELECT version FROM reference_data WHERE source = 'ahrq_compendium'"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM health_system"));

        // 340B likewise: no export in the folder, nothing loaded, no failure.
        Assert.True(await Loader(db, server).RefreshAsync(force: false, only: "hrsa_340b", _ct));
        Assert.Equal(Program340BSource.NoFiles, await db.ScalarAsync<string>("SELECT version FROM reference_data WHERE source = 'hrsa_340b'"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM hrsa_340b_entity"));

        // A systems file without a linkage file is refused, and the tables stay as they were.
        Directory.CreateDirectory(AhrqFolder);
        File.Copy(Fixtures.Path("datasets/ahrq_compendium_2023_sample.csv"), Path.Combine(AhrqFolder, "chsp-compendium-2023.csv"));
        Assert.False(await Loader(db, server).RefreshAsync(force: true, only: "ahrq_compendium", _ct));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM health_system"));

        File.Copy(Fixtures.Path("datasets/ahrq_hospital_linkage_2023_sample.csv"), Path.Combine(AhrqFolder, "chsp-hospital-linkage-2023.csv"));
        Assert.True(await Loader(db, server).RefreshAsync(force: true, only: "ahrq_compendium", _ct));
        Assert.Equal(3L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM health_system"));
        Assert.Equal(4L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM health_system_facility"));
    }

    private sealed class FakeSources : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests)
            {
                Requests.Add(request.Method == HttpMethod.Head ? "HEAD " + url : url);
            }

            var fixture = url switch
            {
                "https://cms.test/data.json" => "datasets/cms_catalog_sample.json",
                "https://cms.test/files/PY2026_Medicare_Shared_Savings_Program_Organizations.csv" => "datasets/aco_organizations_sample.csv",
                "https://cms.test/files/PY2026_Medicare_Shared_Savings_Program_Participants.csv" => "datasets/aco_participants_sample.csv",
                "https://cms.test/files/PY2026_Medicare_Shared_Savings_Program_ACO_SNF_Affiliates.csv" => "datasets/aco_snf_affiliates_sample.csv",
                "https://hrsa.test/Health_Center_Service_Delivery_and_LookAlike_Sites.csv" => "datasets/hrsa_health_center_sites_sample.csv",
                _ => null,
            };
            if (fixture is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var bytes = File.ReadAllBytes(Fixtures.Path(fixture));
            var content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : bytes);
            content.Headers.ContentLength = bytes.Length;
            content.Headers.LastModified = new DateTimeOffset(2026, 10, 15, 6, 0, 0, TimeSpan.Zero);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
