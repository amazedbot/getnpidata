using Npi.Core.Search;

namespace Npi.Core.Tests;

/// <summary>Health systems and HRSA health center sites in the Facilities &amp; hospitals list (docs/PRODUCT_PLAN.md step 3.6 part 2).</summary>
public class SystemTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static FacilityNetworks Networks() => new(
        [new("330045", "HSI90000001", "Harbor Health System"), new("330045", "HSI90000002", "Second System"), new("999999", "HSI90000003", "Not listed")],
        [
            // By its Medicare number.
            new("BPS-H80-900001", "Healthy Community Center Main", "5 Main St", "Brooklyn", "NY", "11201", "7185550100", "www.example.org/hcc", null, "Permanent",
                false, "HEALTHY COMMUNITY INC", "331850", null, 40.69, -73.99),
            // By an NPI CMS enrolls for an FQHC's CCN.
            new("BPS-H80-900002", "Healthy Community Annex", "9 Side St", "Brooklyn", "NY", "11201", null, null, null, "Permanent", false, "OTHER CENTER INC",
                null, "1234567893", null, null),
            // By street address and ZIP (no number given).
            new("BPS-H80-900003", "Harbor FQHC", "10 Harbor Road", "Babylon", "NY", "11702", null, "harbor.example.org", null, "Permanent", false, "HARBOR CENTER",
                null, null, null, null),
            // Matched nowhere: a row of its own, at HRSA's point.
            new("BPS-LAL-900004", "Valley Mobile Clinic", "1 Farm Rd", "Albany", "NY", "12201", "5185550102", null, "School", "Mobile Van", true, "VALLEY CARE",
                null, null, 42.65, -73.75),
            // Matched nowhere and without a point: the ZIP's centre.
            new("BPS-H80-900005", "Shore Clinic", "3 Shore Rd", "West Islip", "NY", "11795", null, null, null, "Seasonal", false, "HARBOR CENTER", null, null,
                null, null),
        ],
        new Dictionary<string, string> { ["1234567893"] = "331851", ["1245319599"] = "331852" },
        [
            // By the Medicare provider number; a later start at the same facility doesn't replace the earlier one.
            new("DSH330045", "DSH", true, new DateOnly(2012, 1, 1), "330045", null, []),
            new("CAN330045", "CAN", true, new DateOnly(2018, 1, 1), "330045", null, []),
            // By the HRSA site ID of a site with a row of its own, and of a site merged into an FQHC row.
            new("FQHCLA900004", "FQHCLA", true, new DateOnly(2019, 6, 15), null, "BPS-LAL-900004", []),
            new("CH900001", "CH", true, new DateOnly(2015, 4, 1), null, "bps-h80-900001", []),
            // By an NPI CMS enrolls for a CCN.
            new("CH900003", "CH", true, new DateOnly(2016, 2, 1), null, null, ["1000000103", "1245319599"]),
            // Not participating, or matching nothing: no mark.
            new("DSH331851", "DSH", false, new DateOnly(2001, 1, 1), "331851", null, []),
            new("DSH999999", "DSH", true, new DateOnly(2001, 1, 1), "999999", null, ["1000000111"]),
        ]);

    internal static IReadOnlyList<FacilitySummary> Facilities() => FacilityDirectory.Merge(
        [
            new("330045", "GOOD SAMARITAN HOSPITAL", "1000 MONTAUK HWY", "WEST ISLIP", "NY", "11795", true, "00", null),
            new("331850", "HEALTHY COMMUNITY CENTER", "5 MAIN ST", "BROOKLYN", "NY", "11201", true, null, null),
            new("331851", "HEALTHY COMMUNITY ANNEX", "9 SIDE ST", "BROOKLYN", "NY", "11201", true, null, null),
            new("331852", "HARBOR HEALTH CENTER", "10 HARBOR ROAD", "BABYLON", "NY", "11702", true, null, null),
        ],
        [], [("331852", "fqhc", 1)], new Dictionary<string, (double Lat, double Lon)> { ["11795"] = (40.7, -73.3) }, networks: Networks());

    [Fact]
    public void Facilities_get_their_system_and_health_center_and_unmatched_sites_their_own_rows()
    {
        var rows = Facilities().ToDictionary(r => r.Ccn);
        Assert.Equal(6, rows.Count);

        var hospital = rows["330045"]; // the first system by ID when the linkage files name two
        Assert.Equal(("HSI90000001", "Harbor Health System", (string?)null, FacilitySummary.SourceCms, "/facility/330045"),
            (hospital.SystemId, hospital.SystemName, hospital.HealthCenter, hospital.Source, hospital.PagePath));

        Assert.Equal(("HEALTHY COMMUNITY INC", "www.example.org/hcc"), (rows["331850"].HealthCenter, rows["331850"].Website));
        Assert.Equal("OTHER CENTER INC", rows["331851"].HealthCenter);
        Assert.Equal(("HARBOR CENTER", "harbor.example.org"), (rows["331852"].HealthCenter, rows["331852"].Website));

        var van = rows["BPS-LAL-900004"];
        Assert.Equal((FacilityTypes.HealthCenterSite, "Health center site", "Look-alike site, school, mobile van", "Valley Mobile Clinic", FacilitySummary.SourceHrsa,
                true, 42.65, "/health-center/BPS-LAL-900004", "VALLEY CARE", "5185550102"),
            (van.Type, van.TypeName, van.Kind, van.Name, van.Source, van.Active, van.Latitude, van.PagePath, van.HealthCenter, van.Phone));
        Assert.Equal(("Health center site, seasonal", 40.7), (rows["BPS-H80-900005"].Kind, rows["BPS-H80-900005"].Latitude));
    }

    [Fact]
    public async Task The_list_filters_by_system_and_counts_health_center_sites()
    {
        using var directory = new FacilityDirectory(Facilities());
        var harbor = await directory.SearchAsync(new FacilityFilter { System = "hsi90000001" }, Ct);
        Assert.Equal(["330045"], harbor.Items.Select(f => f.Ccn));

        var sites = await directory.SearchAsync(new FacilityFilter { Type = FacilityTypes.HealthCenterSite }, Ct);
        Assert.Equal(["BPS-H80-900005", "BPS-LAL-900004"], sites.Items.Select(f => f.Ccn));
        var bySite = await directory.SearchAsync(new FacilityFilter { Q = "bps-lal-900004" }, Ct);
        Assert.Equal("Valley Mobile Clinic", Assert.Single(bySite.Items).Name);

        var overview = await directory.GetOverviewAsync(Ct);
        Assert.Equal(2, overview.Types.Single(t => t.Type == FacilityTypes.HealthCenterSite).Active);

        Assert.True(new FacilityFilter { System = "HSI00000010" }.Validate().Count == 0);
        Assert.Contains("system", new FacilityFilter { System = "HSI 1" }.Validate().Keys);
        Assert.Contains("system", new FacilityFilter { System = "1234" }.Validate().Keys);
    }

    [Fact]
    public async Task The_csv_names_the_system_and_health_center_and_links_hrsa_sites_to_their_page()
    {
        using var output = new MemoryStream();
        var rows = Facilities().Where(f => f.Ccn is "330045" or "BPS-LAL-900004").ToAsyncEnumerable();
        Assert.Equal(2, await FacilityCsv.WriteAsync(rows, output, "https://example.org", Ct));
        var text = System.Text.Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains(",Fines (3 Years),Health System,Health Center (HRSA),340B Entity Type,340B Since,Page", text, StringComparison.Ordinal);
        Assert.Contains(",0,,,,Harbor Health System,,Disproportionate share hospital,2012-01-01,https://example.org/facility/330045", text, StringComparison.Ordinal);
        Assert.Contains("BPS-LAL-900004,Valley Mobile Clinic,Health center site,", text, StringComparison.Ordinal);
        Assert.Contains(",0,,,,,VALLEY CARE,FQHC look-alike,2019-06-15,https://example.org/health-center/BPS-LAL-900004", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Participating_340B_entities_mark_their_facility_by_medicare_number_site_id_or_npi()
    {
        var rows = Facilities().ToDictionary(r => r.Ccn);
        Assert.Equal(("DSH", new DateOnly(2012, 1, 1)), (rows["330045"].Program340B, rows["330045"].Program340BSince));
        Assert.Equal(("FQHCLA", new DateOnly(2019, 6, 15)), (rows["BPS-LAL-900004"].Program340B, rows["BPS-LAL-900004"].Program340BSince));
        Assert.Equal("CH", rows["331850"].Program340B); // its HRSA site (matched by Medicare number) is the entity's site
        Assert.Equal(("CH", new DateOnly(2016, 2, 1)), (rows["331852"].Program340B, rows["331852"].Program340BSince)); // by an NPI
        Assert.Null(rows["331851"].Program340B); // its entity no longer participates
        Assert.Null(rows["BPS-H80-900005"].Program340B);

        using var directory = new FacilityDirectory(Facilities());
        Assert.Equal(["330045", "331850", "331852", "BPS-LAL-900004"],
            (await directory.SearchAsync(new FacilityFilter { In340B = true }, Ct)).Items.Select(f => f.Ccn).Order(StringComparer.Ordinal));
        Assert.Equal(["331851", "BPS-H80-900005"], (await directory.SearchAsync(new FacilityFilter { In340B = false }, Ct)).Items.Select(f => f.Ccn).Order(StringComparer.Ordinal));

        Assert.Equal("Disproportionate share hospital", Program340B.TypeName("dsh"));
        Assert.Equal("XYZ", Program340B.TypeName(" xyz "));
        Assert.Equal("340B covered entity (disproportionate share hospital, since January 1, 2012)", Program340B.Describe("DSH", new DateOnly(2012, 1, 1)));
        Assert.Equal("340B covered entity (FQHC look-alike)", Program340B.Describe("FQHCLA", null));
    }

    [Theory]
    [InlineData("Harbor Medical Group, P.C.", "HARBORMEDICALGROUPPC")]
    [InlineData("HARBOR MEDICAL GROUP PC", "HARBORMEDICALGROUPPC")]
    [InlineData("  harbor   medical-group pc ", "HARBORMEDICALGROUPPC")]
    [InlineData("Smith & Jones, LLC", "SMITHJONESLLC")]
    [InlineData("Clínica Médica", "CLÍNICAMÉDICA")]
    [InlineData(" , . ", null)]
    public void Aco_names_fold_case_spacing_and_punctuation_only(string name, string? key) => Assert.Equal(key, AcoNames.Key(name));

    [Fact]
    public void Aco_name_keys_drop_no_words()
    {
        Assert.NotEqual(AcoNames.Key("Harbor Medical Group"), AcoNames.Key("Harbor Medical Group PC"));
        Assert.NotEqual(AcoNames.Key("Smith and Jones LLC"), AcoNames.Key("Smith & Jones LLC"));
        Assert.Equal("name", new AcoParticipant("X", [new AcoParticipantMatch("1234567893", "X", null, null)]).MatchedBy);
        Assert.Null(new AcoParticipant("X", []).MatchedBy);
        Assert.Equal("name", new AcoMembership("A1", "ACO", 2026, "X", "1234567893", 1).MatchedBy);
    }

    [Fact]
    public void Health_centers_are_shown_for_the_care_they_give()
    {
        Assert.Equal(["family", "children", "dentist", "mental"], CareFinder.Needs.Where(n => n.HealthCenters).Select(n => n.Key));
        Assert.False(CareFinder.Resolve("other", "Dentists")!.HealthCenters);
        Assert.InRange(HealthCenterService.Miles(40.7128, -74.0060, 40.7306, -73.9352), 3.6, 4.0); // lower Manhattan to Brooklyn
        Assert.Equal(0, HealthCenterService.Miles(40, -73, 40, -73), 6);
    }

    [Fact]
    public void The_compendium_is_credited()
    {
        Assert.Equal("Agency for Healthcare Research and Quality (AHRQ), Compendium of U.S. Health Systems, 2023.", HealthSystemText.Credit(2023));
        Assert.Equal("Agency for Healthcare Research and Quality (AHRQ), Compendium of U.S. Health Systems.", HealthSystemText.Credit(null));
        Assert.Contains("IQVIA OneKey", HealthSystemText.Basis, StringComparison.Ordinal);
        Assert.Contains("American Hospital Association (AHA) Annual Survey", HealthSystemText.Basis, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HSI00000010", true)]
    [InlineData("hsi1", true)]
    [InlineData("HSI", false)]
    [InlineData("12345", false)]
    [InlineData("HSI00000010X", false)]
    public void System_ids(string id, bool valid) => Assert.Equal(valid, HealthSystemService.IsSystemId(id));

    [Fact]
    public void Aco_and_site_ids_and_filters()
    {
        Assert.True(AcoService.IsAcoId("A1234"));
        Assert.False(AcoService.IsAcoId("1234"));
        Assert.False(AcoService.IsAcoId("A12-34"));
        Assert.True(HealthCenterService.IsSiteId("BPS-H80-031385"));
        Assert.False(HealthCenterService.IsSiteId("BPS H80"));
        Assert.False(HealthCenterService.IsSiteId("../etc"));

        Assert.Empty(new HealthSystemFilter { Q = "harbor", State = "ny", Sort = "-beds" }.Validate());
        Assert.Equal(["state", "sort", "pageSize"], new HealthSystemFilter { State = "New York", Sort = "size", PageSize = 500 }.Validate().Keys);
        Assert.Equal(["q", "page"], new AcoFilter { Q = new string('x', 101), Page = 0 }.Validate().Keys);
    }

    [Fact]
    public void A_system_in_plain_words()
    {
        var s = new HealthSystemSummary("HSI90000001", "Harbor Health System", "WEST ISLIP", "NY", 2023, 1200, 400, 300, 80, 45, 3, 1, 1, 812, 40210, true, true, true, 5);
        Assert.Equal("Health system based in West Islip, NY with 3 hospitals, 1,200 physicians, 45 group practices (AHRQ Compendium 2023).", HealthSystemText.Summary(s));
        Assert.Equal("Health system with 1 hospital (AHRQ Compendium 2023).",
            HealthSystemText.Summary(s with { City = null, State = null, Hospitals = 1, Physicians = null, GroupPractices = null }));
    }
}
