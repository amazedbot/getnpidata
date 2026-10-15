using System.IO.Compression;
using Npi.Core.Search;
using Npi.Loader.Datasets;
using Npi.Loader.Projection;

namespace Npi.Loader.Tests;

/// <summary>Health systems, ACOs and HRSA health center sites (docs/PRODUCT_PLAN.md step 3.6 part 2): reading the files.</summary>
public sealed class SystemSourceTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("npi-systems-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static NamedCsv Fixture(string name) => NamedCsv.Read(Fixtures.Path("datasets/" + name));

    [Fact]
    public void Compendium_files_are_told_apart_by_their_columns_and_dated_by_their_names()
    {
        Assert.Equal(AhrqCompendium.FileKind.Systems, AhrqCompendium.Classify(Fixture("ahrq_compendium_2023_sample.csv")));
        Assert.Equal(AhrqCompendium.FileKind.Linkage, AhrqCompendium.Classify(Fixture("ahrq_hospital_linkage_2023_sample.csv")));
        Assert.Equal(AhrqCompendium.FileKind.Linkage, AhrqCompendium.Classify(Fixture("ahrq_nh_linkage_2023_sample.csv")));
        Assert.Equal(AhrqCompendium.FileKind.Linkage, AhrqCompendium.Classify(Fixture("ahrq_hhco_linkage_2023_sample.csv")));
        Assert.Equal(AhrqCompendium.FileKind.Unknown, AhrqCompendium.Classify(Fixture("aco_snf_affiliates_sample.csv")));

        Assert.Equal(2023, AhrqCompendium.YearOf("chsp-compendium-2023.csv"));
        Assert.Equal(2023, AhrqCompendium.YearOf("chsp-compendium-2023-rev.csv")); // AHRQ's revised systems file
        Assert.Equal(2022, AhrqCompendium.YearOf(@"C:\x\chsp-hospital-linkage-2022 (1).csv"));
        Assert.Null(AhrqCompendium.YearOf("chsp-compendium.csv"));
        Assert.Null(AhrqCompendium.YearOf("export-120231.csv"));
    }

    [Fact]
    public void The_systems_file_gives_ahrqs_counts()
    {
        var systems = AhrqCompendium.ReadSystems(Fixture("ahrq_compendium_2023_sample.csv"), 2023).ToDictionary(s => s.SystemId);
        Assert.Equal(3, systems.Count);

        var harbor = systems["HSI90000001"];
        Assert.Equal(("Harbor Health System", "West Islip", "NY", 2023, 1200, 400, 300, 80), (harbor.Name, harbor.City, harbor.State, harbor.DataYear,
            harbor.Physicians, harbor.PrimaryCarePhysicians, harbor.NursePractitioners, harbor.PhysicianAssistants));
        Assert.Equal((45, 3, 3, 1, 1, 812, 40210), (harbor.GroupPractices, harbor.Hospitals, harbor.AcuteHospitals, harbor.NursingHomes,
            harbor.HomeHealthAgencies, harbor.Beds, harbor.Discharges));
        Assert.Equal((true, true, true), (harbor.MajorTeaching, harbor.OffersInsurance, harbor.MedicareAdvantage));

        var prairie = systems["HSI90000002"];
        Assert.Equal(((bool?)false, (bool?)null), (prairie.OffersInsurance, prairie.MedicareAdvantage)); // blank → not known
        Assert.Equal("Prairie Care Network – Springfield", prairie.Name); // AHRQ's files are Windows-1252: the en dash is byte 0x96
        var lakes = systems["HSI90000003"];
        Assert.Equal(("Lakes & Rivers Health, Inc.", (int?)null, 2), (lakes.Name, lakes.Physicians, lakes.Hospitals));
    }

    [Fact]
    public void Linkage_files_place_facilities_in_systems_by_ccn()
    {
        var hospitals = AhrqCompendium.ReadLinkage(Fixture("ahrq_hospital_linkage_2023_sample.csv"));
        // The hospital in no system (corp parent "N/A") and the one without a CCN are left out; the one naming an unknown system is
        // the source's to drop.
        Assert.Equal(["330045", "330046", "010001", "140002", "140003"], hospitals.Select(h => h.Ccn));
        var padded = hospitals.Single(h => h.Ccn == "010001");
        Assert.Equal(("HSI90000003", FacilityTypes.Hospital, "Southern River Medical Center", "Dothan", "AL"),
            (padded.SystemId, padded.Kind, padded.Name, padded.City, padded.State));

        var homes = Assert.Single(AhrqCompendium.ReadLinkage(Fixture("ahrq_nh_linkage_2023_sample.csv")));
        Assert.Equal(("335002", FacilityTypes.NursingHome, "SHORE VIEW CARE", "BABYLON"), (homes.Ccn, homes.Kind, homes.Name, homes.City));

        // Home health, hospice and home dialysis share one file, told apart by home_health_care_org_type.
        Assert.Equal([("337001", FacilityTypes.HomeHealth, "HARBOR HOME CARE"), ("331501", FacilityTypes.Hospice, "HARBOR HOSPICE"),
                ("242301", FacilityTypes.Dialysis, "LAKES HOME DIALYSIS")],
            AhrqCompendium.ReadLinkage(Fixture("ahrq_hhco_linkage_2023_sample.csv")).Select(h => (h.Ccn, h.Kind, h.Name)));

        Assert.Equal("33C0001234", AhrqCompendium.Ccn(" 33c0001234 "));
        Assert.Null(AhrqCompendium.Ccn("1234"));
        Assert.Null(AhrqCompendium.Ccn("33-0045"));
    }

    [Fact]
    public void The_newest_year_with_a_systems_file_is_chosen_with_its_linkage_files()
    {
        Assert.Null(HealthSystemSource.Choose(Path.Combine(_folder, "missing")));
        Assert.Null(HealthSystemSource.Choose(_folder));

        File.Copy(Fixtures.Path("datasets/ahrq_compendium_2023_sample.csv"), Path.Combine(_folder, "chsp-compendium-2023-rev.csv"));
        File.Copy(Fixtures.Path("datasets/ahrq_hospital_linkage_2023_sample.csv"), Path.Combine(_folder, "chsp-hospital-linkage-2023.csv"));
        File.Copy(Fixtures.Path("datasets/ahrq_hhco_linkage_2023_sample.csv"), Path.Combine(_folder, "chsp-hhco-linkage-2023.csv"));
        File.Copy(Fixtures.Path("datasets/ahrq_nh_linkage_2023_sample.csv"), Path.Combine(_folder, "nursing-home-linkage.csv")); // no year: belongs to any
        File.Copy(Fixtures.Path("datasets/ahrq_compendium_2023_sample.csv"), Path.Combine(_folder, "chsp-compendium-2022.csv"));
        File.Copy(Fixtures.Path("datasets/ahrq_hospital_linkage_2023_sample.csv"), Path.Combine(_folder, "chsp-hospital-linkage-2022.csv"));
        File.WriteAllText(Path.Combine(_folder, "notes.csv"), "a,b\n1,2\n");

        var chosen = HealthSystemSource.Choose(_folder)!.Value;
        Assert.Equal(2023, chosen.Year);
        Assert.Equal("chsp-compendium-2023-rev.csv", Path.GetFileName(chosen.Systems));
        Assert.Equal(["chsp-hhco-linkage-2023.csv", "chsp-hospital-linkage-2023.csv", "nursing-home-linkage.csv"],
            chosen.Linkage.Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The owner's real downloads, when NPI_AHRQ_FOLDER (the AHRQ Compendium files) or NPI_340B_FILE (an OPAIS export) points at
    /// them; skipped otherwise (CI has neither). Read-only.
    /// </summary>
    [Fact]
    public async Task Real_downloads_are_read_when_present()
    {
        var folder = Environment.GetEnvironmentVariable("NPI_AHRQ_FOLDER");
        var export = Environment.GetEnvironmentVariable("NPI_340B_FILE");
        Assert.SkipWhen(folder is null && export is null, "NPI_AHRQ_FOLDER / NPI_340B_FILE not set");
        if (folder is not null)
        {
            var chosen = HealthSystemSource.Choose(folder)!.Value;
            var systems = AhrqCompendium.ReadSystems(NamedCsv.Read(chosen.Systems), chosen.Year);
            var known = systems.Select(s => s.SystemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var linked = chosen.Linkage.SelectMany(path => AhrqCompendium.ReadLinkage(NamedCsv.Read(path))).Where(f => known.Contains(f.SystemId)).ToList();
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"AHRQ {chosen.Year}: {systems.Count} systems; linked {string.Join(", ", linked.GroupBy(f => f.Kind).Select(g => $"{g.Key} {g.Count()}"))}");
            Assert.NotEmpty(systems);
            Assert.NotEmpty(linked);
        }

        if (export is not null)
        {
            var rows = await Opais340B.ReadAsync(export, TestContext.Current.CancellationToken);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"340B: {rows.Count} entities, {rows.Count(r => r.Participating)} participating, {rows.Count(r => r.Ccn is not null)} with a Medicare number "
                + $"({rows.Count(r => r.Participating && r.Ccn is not null)} participating), {rows.Count(r => r.Npis.Count > 0)} with NPIs "
                + $"({rows.Count(r => r.Participating && r.Npis.Count > 0)} participating), {rows.Count(r => r.ParentId340B is not null)} child sites, "
                + $"{rows.Count(r => r.SiteId is not null)} with an HRSA site ID");
            Assert.NotEmpty(rows);
        }
    }

    [Fact]
    public void Aco_files_are_read_by_their_headers_without_contacts()
    {
        var acos = AcoFiles.ReadOrganizations(Fixture("aco_organizations_sample.csv")).ToDictionary(a => a.AcoId);
        var harbor = acos["A9001"];
        Assert.Equal(("Harbor Accountable Care LLC", "NY", 3, new DateTime(2016, 1, 1), new DateTime(2024, 1, 1), "ENHANCED", true),
            (harbor.Name, harbor.ServiceArea, harbor.AgreementPeriod, harbor.InitialStartDate, harbor.CurrentStartDate, harbor.Track, harbor.HighRevenue));
        Assert.Equal(("1000 Montauk Hwy, Suite 2, West Islip, NY, 11795", "www.example.org/harbor-aco", 40.7, -73.3),
            (harbor.Address, harbor.Website, harbor.Latitude, harbor.Longitude));
        Assert.Equal(("AZ, NV", "BASIC level E", false), (acos["A9002"].ServiceArea, acos["A9002"].Track, acos["A9002"].HighRevenue));

        // "ACO_ID " (a trailing space in CMS's header) matches; a name listed twice in other case counts once.
        var participants = AcoFiles.ReadNames(Fixture("aco_participants_sample.csv"), "par_lbn");
        Assert.Equal([("A9001", "HARBOR MEDICAL GROUP PC"), ("A9001", "GOOD SAMARITAN HOSPITAL"), ("A9002", "DESERT FAMILY PRACTICE, PLLC"),
            ("A9999", "NOT AN ACO IN THE FILE LLC")], participants);
        Assert.Equal([("A9001", "SHORE VIEW CARE LLC")], AcoFiles.ReadNames(Fixture("aco_snf_affiliates_sample.csv"), "aff_lbn"));

        Assert.Equal("BASIC", AcoFiles.Track("1", "N/A", "0"));
        Assert.Null(AcoFiles.Track("0", null, "0"));
        Assert.Null(AcoFiles.Id("1234"));
        Assert.Equal("A1234", AcoFiles.Id(" a1234 "));
        Assert.Throws<InvalidDataException>(() => AcoFiles.ReadNames(Fixture("aco_snf_affiliates_sample.csv"), "par_lbn"));
    }

    [Fact]
    public void Health_center_sites_keep_service_sites_with_their_medicare_number_and_npi()
    {
        var sites = HealthCenterSiteFile.Read(Fixture("hrsa_health_center_sites_sample.csv")).ToDictionary(s => s.SiteId);
        Assert.Equal(["BPS-H80-900001", "BPS-H80-900002", "BPS-LAL-900003"], sites.Keys.Order(StringComparer.Ordinal)); // the administrative office is left out

        var main = sites["BPS-H80-900001"];
        Assert.Equal(("Healthy Community Center Main", "5 Main St", "Brooklyn", "NY", "11201", "7185550100", "www.example.org/hcc", 60.0),
            (main.Name, main.Address, main.City, main.State, main.Zip5, main.Phone, main.Website, main.HoursPerWeek));
        Assert.Equal(((string?)null, "Permanent", "Service Delivery Site", false, "H80CS90001", "HEALTHY COMMUNITY INC", "Brooklyn", "NY", "331850", (string?)null),
            (main.Setting, main.LocationType, main.SiteType, main.LookAlike, main.GrantNumber, main.HealthCenter, main.HealthCenterCity, main.HealthCenterState,
                main.Ccn, main.Npi));
        Assert.Equal((new DateTime(2006, 1, 1), "36047", 40.69, -73.99), (main.AddedDate, main.CountyFips, main.Latitude, main.Longitude));

        var school = sites["BPS-H80-900002"];
        Assert.Equal(("School", "Seasonal", "1234567893", (string?)null), (school.Setting, school.LocationType, school.Npi, school.Ccn));

        var van = sites["BPS-LAL-900003"];
        Assert.Equal((true, (string?)null, "5185550102", (string?)null), (van.LookAlike, van.Setting, van.Phone, van.Ccn)); // 10 digits aren't a CCN

        Assert.Equal("2125550199", HealthCenterSiteFile.Phone("1-212-555-0199"));
        Assert.Null(HealthCenterSiteFile.Phone("555-0199"));
    }

    [Fact]
    public async Task The_340b_daily_report_is_read_from_its_covered_entities_sheet()
    {
        // The fixture's first sheet is Shipping Addresses (340B ID, Entity Type, no NPI or Medicare column): skipped.
        var rows = (await Opais340B.ReadAsync(Fixtures.Path("datasets/hrsa_340b_daily_report_sample.xlsx"), TestContext.Current.CancellationToken))
            .ToDictionary(e => e.Id340B);
        Assert.Equal(["CH900001", "DSH140002", "DSH330045", "FQHCLA900003", "RRC050001"], rows.Keys.Order(StringComparer.Ordinal)); // the row without an ID is left out

        var hospital = rows["DSH330045"];
        Assert.Equal((1001, "DSH", true, new DateTime(2012, 1, 1), "GOOD SAMARITAN HOSPITAL", "330045", "1000 MONTAUK HWY", "WEST ISLIP", "NY", "11795", (bool?)false),
            (hospital.CeId, hospital.EntityType, hospital.Participating, hospital.StartDate, hospital.Name, hospital.Ccn, hospital.Address, hospital.City,
                hospital.State, hospital.Zip5, hospital.Rural));
        Assert.Empty(hospital.Npis); // "6315550100" has no valid check digit: a phone number, not an NPI

        Assert.Equal((new DateTime(2015, 4, 1), "H80CS90001"), (rows["CH900001"].StartDate, rows["CH900001"].GrantNumber));
        Assert.Equal(["1234567893"], rows["CH900001"].Npis);
        Assert.Equal(("BPS-LAL-900003", "MOBILE CLINIC", "12201"), (rows["FQHCLA900003"].SiteId, rows["FQHCLA900003"].SubName, rows["FQHCLA900003"].Zip5));
        var closed = rows["DSH140002"];
        Assert.Equal((false, new DateTime(2020, 3, 31), "No longer meets eligibility requirements"), (closed.Participating, closed.TerminationDate, closed.TerminationReason));
        Assert.Equal(("050001", (bool?)true), (rows["RRC050001"].Ccn, rows["RRC050001"].Rural)); // a five-digit Medicare number padded
    }

    [Fact]
    public async Task The_340b_json_export_is_streamed_from_its_zip()
    {
        // The fixture is the real export's layout (OPA_CE_DAILY_PUBLIC.JSON in a zip, HRSA's keys and their order) with made-up entities.
        var ct = TestContext.Current.CancellationToken;
        var zip = Fixtures.Path("datasets/hrsa_340b_daily_export_sample.zip");
        var rows = (await Opais340B.ReadAsync(zip, ct)).ToDictionary(e => e.Id340B);
        Assert.Equal(["CH900001", "DSH140002", "DSH330045", "DSH330045AB", "FQHCLA900003", "RRC050001"], rows.Keys.Order(StringComparer.Ordinal)); // no type: left out

        var hospital = rows["DSH330045"];
        Assert.Equal((900001, "DSH", true, new DateTime(2012, 1, 1), "GOOD SAMARITAN HOSPITAL", "330045", "1000 MONTAUK HWY", "WEST ISLIP", "NY", "11795", (bool?)false),
            (hospital.CeId, hospital.EntityType, hospital.Participating, hospital.StartDate, hospital.Name, hospital.Ccn, hospital.Address, hospital.City,
                hospital.State, hospital.Zip5, hospital.Rural));
        Assert.Empty(hospital.Npis); // its npiNumbers hold a phone number; shipping addresses and contract pharmacies aren't read
        Assert.Null(hospital.ParentId340B);

        // The child site: its parent, the parent's CCN, the four-digit ZIP padded, the sub name's escaped vertical tab trimmed.
        var child = rows["DSH330045AB"];
        Assert.Equal(("DSH330045", "330045", "INFUSION CENTER", "GREENWICH", "06830", new DateTime(2014, 7, 1)),
            (child.ParentId340B, child.Ccn, child.SubName, child.City, child.Zip5, child.StartDate));

        var center = rows["CH900001"];
        Assert.Equal(((string?)null, "H80CS90001", true, (bool?)null), (center.SiteId, center.GrantNumber, center.Participating, center.Rural)); // "Admin" is no site
        Assert.Equal(["1234567893"], center.Npis);
        Assert.Equal("BPS-LAL-900003", rows["FQHCLA900003"].SiteId); // trailing space trimmed
        var closed = rows["DSH140002"];
        Assert.Equal((false, new DateTime(2020, 3, 31), "DSH percentage below statutory minimum"), (closed.Participating, closed.TerminationDate, closed.TerminationReason));
        Assert.Equal(("HOSPITAL DE LA PEÑA", "050001", (bool?)true), (rows["RRC050001"].Name, rows["RRC050001"].Ccn, rows["RRC050001"].Rural));

        // The same JSON on its own reads the same.
        var json = Path.Combine(_folder, "OPA_CE_DAILY_PUBLIC.JSON");
        using (var archive = ZipFile.OpenRead(zip))
        {
            archive.Entries.Single().ExtractToFile(json);
        }

        Assert.Equal(rows.Values.Select(r => (r.Id340B, r.Ccn, r.SubName)).Order(), (await Opais340B.ReadAsync(json, ct)).Select(r => (r.Id340B, r.Ccn, r.SubName)).Order());

        File.WriteAllText(Path.Combine(_folder, "other.json"), """{"exportDate": "2026-10-15"}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => Opais340B.ReadAsync(Path.Combine(_folder, "other.json"), ct));
        File.WriteAllText(Path.Combine(_folder, "broken.json"), """{"coveredEntities":[{"id340B":""");
        await Assert.ThrowsAsync<InvalidDataException>(() => Opais340B.ReadAsync(Path.Combine(_folder, "broken.json"), ct));
        File.WriteAllText(Path.Combine(_folder, "export.csv"), "a,b\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => Opais340B.ReadAsync(Path.Combine(_folder, "export.csv"), ct));
        using (var empty = ZipFile.Open(Path.Combine(_folder, "empty.zip"), ZipArchiveMode.Create))
        {
            empty.CreateEntry("readme.txt");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => Opais340B.ReadAsync(Path.Combine(_folder, "empty.zip"), ct));
    }

    [Fact]
    public void The_newest_340b_export_in_the_folder_is_read()
    {
        Assert.Null(Program340BSource.Choose(Path.Combine(_folder, "missing")));
        var older = Path.Combine(_folder, "CE_Daily_Report_old.xlsx");
        var newer = Path.Combine(_folder, "340B_CoveredEntityJson_Daily_20261015_142651.zip");
        File.Copy(Fixtures.Path("datasets/hrsa_340b_daily_report_sample.xlsx"), older);
        File.Copy(Fixtures.Path("datasets/hrsa_340b_daily_export_sample.zip"), newer);
        File.WriteAllText(Path.Combine(_folder, "~$CE_Daily_Report_old.xlsx"), "Excel's lock file");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "x");
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("340B_CoveredEntityJson_Daily_20261015_142651.zip", Program340BSource.Choose(_folder)!.Name);
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("CE_Daily_Report_old.xlsx", Program340BSource.Choose(_folder)!.Name); // the newest of any kind

        Assert.Equal("050001", Opais340B.Medicare(" 50001 "));
        Assert.Equal("33C001", Opais340B.Medicare("33c001"));
        Assert.Null(Opais340B.Medicare("1234"));
        Assert.Null(Opais340B.Medicare("1234567890"));
        Assert.Equal((true, false, (bool?)null), (Opais340B.Truthy("True"), Opais340B.Truthy("no"), Opais340B.Truthy("maybe")));
        Assert.Equal((new DateTime(2015, 4, 1), new DateTime(2012, 1, 1), (DateTime?)null),
            (Opais340B.Date("4/1/2015"), Opais340B.Date("2012-01-01T00:00:00Z"), Opais340B.Date("soon")));
        Assert.Equal("340bid", Opais340B.Key("340B ID"));
        Assert.Equal(Opais340B.Key("Entity Sub-Division Name"), Opais340B.Key("entitySubDivisionName"));
    }

    [Fact]
    public void Aco_participants_link_to_organizations_with_exactly_their_legal_name()
    {
        (string, string)[] participants =
        [
            ("A9001", "HARBOR MEDICAL GROUP PC"), ("A9001", "GOOD SAMARITAN HOSPITAL"), ("A9002", "Desert Family Practice, PLLC"),
            ("A9002", "FAMILY PRACTICE ASSOCIATES PC"), ("A9002", "HARBOR MEDICAL GROUP PC"), ("A9003", "NOBODY BY THIS NAME LLC"), ("A9003", " - "),
        ];
        var organizations = new List<(string, string)>
        {
            ("1000000103", "Harbor Medical Group, P.C."), // the only one: a link
            ("1000000111", "HARBOR MEDICAL GROUP"),       // not the same name (no "PC"): no words are dropped
            ("1000000129", "DESERT FAMILY PRACTICE PLLC"), ("1000000137", "DESERT FAMILY PRACTICE, P.L.L.C."), // two: candidates
            ("1000000129", "DESERT FAMILY PRACTICE PLLC"), // the same NPI twice counts once
        };
        organizations.AddRange(Enumerable.Range(0, 6).Select(i => ($"20000000{i:00}", "Family Practice Associates, PC"))); // six: not linked

        var (links, counts) = AcoParticipantMatcher.Match(participants, organizations);
        Assert.Equal(new AcoMatchCounts(7, 2, 1, 0, 1, 3), counts);
        Assert.Equal(
            [("A9001", "HARBOR MEDICAL GROUP PC", "1000000103", 1), ("A9002", "Desert Family Practice, PLLC", "1000000129", 2),
                ("A9002", "Desert Family Practice, PLLC", "1000000137", 2), ("A9002", "HARBOR MEDICAL GROUP PC", "1000000103", 1)],
            links.Select(l => (l.AcoId, l.Name, l.Npi, l.Candidates)).Order());
        Assert.All(links, l => Assert.Equal((false, (string?)null), (l.HospitalGroup, l.GroupedBy)));

        // The fixture's participants against these organizations: the share linked once, several times, or not at all.
        var fixture = AcoFiles.ReadNames(Fixture("aco_participants_sample.csv"), "par_lbn");
        Assert.Equal(new AcoMatchCounts(4, 1, 1, 0, 0, 2), AcoParticipantMatcher.Match(fixture, organizations).Counts);
    }

    [Fact]
    public void A_hospital_and_its_own_npis_link_up_to_50_with_the_reason_they_belong_together()
    {
        (string, string)[] participants =
        [
            ("A9001", "GOOD SAMARITAN HOSPITAL"), ("A9001", "SHORE MEDICAL CENTER"), ("A9002", "ST JOSEPH HOSPITAL"), ("A9002", "VALLEY HOSPITAL"),
            ("A9003", "LAKE CLINIC PC"), ("A9003", "RIVER HOSPITAL"), ("A9004", "MERCY HOSPITAL"), ("A9004", "COUNTY MEDICAL CENTER"),
        ];
        static AcoOrganization Org(string npi, string name, bool hospital = true, string? ccn = null, string? parent = null, string? tin = null) =>
            new(npi, name) { Hospital = hospital, Ccns = ccn is null ? [] : [ccn], ParentName = parent, ParentTin = tin };

        var organizations = new List<AcoOrganization>();
        // Twelve hospital NPIs of one name, all enrolled for CCN 330045.
        organizations.AddRange(Enumerable.Range(0, 12).Select(i => Org($"30000000{i:00}", "Good Samaritan Hospital", ccn: "330045")));
        // The hospital (no parent) and seven subparts naming it as their parent.
        organizations.Add(Org("3100000000", "SHORE MEDICAL CENTER"));
        organizations.AddRange(Enumerable.Range(1, 7).Select(i => Org($"31000000{i:00}", "SHORE MEDICAL CENTER", parent: "Shore Medical Center")));
        // 51 of a name: more than 50, not linked.
        organizations.AddRange(Enumerable.Range(0, 51).Select(i => Org($"32000000{i:00}", "ST JOSEPH HOSPITAL", ccn: "330200")));
        // Seven hospital NPIs naming the same parent tax ID.
        organizations.AddRange(Enumerable.Range(0, 7).Select(i => Org($"33000000{i:00}", "VALLEY HOSPITAL", tin: "12-3456789")));
        // Six of a name with no hospital among them: the limit of 5 applies.
        organizations.AddRange(Enumerable.Range(0, 6).Select(i => Org($"34000000{i:00}", "LAKE CLINIC PC", hospital: false, parent: "LAKE CLINIC PC")));
        // Six hospitals with nothing tying them together: not linked.
        organizations.AddRange(Enumerable.Range(0, 6).Select(i => Org($"35000000{i:00}", "RIVER HOSPITAL", ccn: $"33{i:0000}", parent: $"PARENT {i}")));
        // A hospital and seven of its clinics, pharmacy and groups (not hospitals) naming it as parent organization.
        organizations.Add(Org("3600000000", "MERCY HOSPITAL", ccn: "330300"));
        organizations.AddRange(Enumerable.Range(1, 7).Select(i => Org($"36000000{i:00}", "Mercy Hospital", hospital: false, parent: "MERCY HOSPITAL")));
        // A hospital, three clinics enrolled for its CCN and three naming it: linked as the hospital's own NPIs.
        organizations.Add(Org("3700000000", "COUNTY MEDICAL CENTER", ccn: "330400"));
        organizations.AddRange(Enumerable.Range(1, 3).Select(i => Org($"37000000{i:00}", "COUNTY MEDICAL CENTER", hospital: false, ccn: "330400")));
        organizations.AddRange(Enumerable.Range(4, 3).Select(i => Org($"37000000{i:00}", "COUNTY MEDICAL CENTER", hospital: false, parent: "County Medical Center")));

        var (links, counts) = AcoParticipantMatcher.Match(participants, organizations);
        Assert.Equal(new AcoMatchCounts(8, 0, 0, 5, 3, 0), counts);
        var byName = links.GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.ToList());
        Assert.Equal(["COUNTY MEDICAL CENTER", "GOOD SAMARITAN HOSPITAL", "MERCY HOSPITAL", "SHORE MEDICAL CENTER", "VALLEY HOSPITAL"],
            byName.Keys.Order(StringComparer.Ordinal));
        Assert.All(links, l => Assert.True(l.HospitalGroup));
        Assert.Equal((12, 12, "ccn", "330045"), (byName["GOOD SAMARITAN HOSPITAL"].Count, byName["GOOD SAMARITAN HOSPITAL"][0].Candidates,
            byName["GOOD SAMARITAN HOSPITAL"][0].GroupedBy, byName["GOOD SAMARITAN HOSPITAL"][0].GroupedValue));
        Assert.All(byName["SHORE MEDICAL CENTER"], l => Assert.Equal((8, "parent", "Shore Medical Center"), (l.Candidates, l.GroupedBy, l.GroupedValue)));
        Assert.All(byName["VALLEY HOSPITAL"], l => Assert.Equal(("parent_tin", (string?)null), (l.GroupedBy, l.GroupedValue)));
        Assert.All(byName["MERCY HOSPITAL"], l => Assert.Equal((8, "parent", "MERCY HOSPITAL"), (l.Candidates, l.GroupedBy, l.GroupedValue)));
        Assert.All(byName["COUNTY MEDICAL CENTER"], l => Assert.Equal((7, "hospital", "COUNTY MEDICAL CENTER"), (l.Candidates, l.GroupedBy, l.GroupedValue)));

        // Two NPIs of a name are candidates either way; a hospital and one of its own NPIs carry the reason.
        Assert.Equal("3600000000", AcoParticipantMatcher.FamilyHospital([Org("3600000000", "M", ccn: "330300"), Org("3600000001", "M", hospital: false, ccn: "330300")])!.Npi);
        Assert.Null(AcoParticipantMatcher.FamilyHospital([Org("1", "A"), Org("2", "A", hospital: false)])); // the clinic names no parent, shares no CCN
        // A subpart of a system names the system as parent: the clinics naming the same system belong to it too.
        Assert.NotNull(AcoParticipantMatcher.FamilyHospital([Org("1", "A", parent: "SYSTEM"), Org("2", "A", hospital: false, parent: "System")]));
        Assert.Equal(("ccn", "330045"), AcoParticipantMatcher.Grouping([Org("1", "A", ccn: "330045"), Org("2", "A", ccn: "330045")]));
        Assert.Equal((null, null), AcoParticipantMatcher.Grouping([Org("1", "A", parent: "X"), Org("2", "A", parent: "Y")]));
        Assert.Equal((null, null), AcoParticipantMatcher.Grouping([Org("1", "A"), Org("2", "A"), Org("3", "A", parent: "A")])); // two without
        Assert.Equal("They share Medicare CCN 330045.", AcoNames.GroupReason("ccn", "330045"));
        Assert.Contains("names MERCY HOSPITAL as its parent organization", AcoNames.GroupReason("hospital", "MERCY HOSPITAL"), StringComparison.Ordinal);
        Assert.Null(AcoNames.GroupReason(null, null));
    }

    [Fact]
    public void A_header_ending_in_a_separator_reads_short_rows()
    {
        var csv = NamedCsv.Read(new StringReader("A,B,\r\n1,2\r\n\"x, y\",\r\n"));
        Assert.Equal(["a", "b"], csv.Headers.Order(StringComparer.Ordinal));
        var rows = csv.Rows.ToList();
        Assert.Equal(("1", "2"), (rows[0]["a"], rows[0]["B"]));
        Assert.Equal(("x, y", (string?)null, (string?)null), (rows[1]["A"], rows[1]["b"], rows[1]["missing"]));
        Assert.Equal((2, (double?)null), (NamedCsv.Read(new StringReader("n,d\n2.4,x\n")).Rows.Single().Whole("n"), NamedCsv.Read(new StringReader("n,d\n2.4,x\n")).Rows.Single().Number("d")));
    }
}
