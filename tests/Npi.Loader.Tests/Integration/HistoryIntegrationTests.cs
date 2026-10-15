using Npi.Core.Search;
using Npi.Loader.History;
using Npi.Loader.Projection;
using Serilog.Core;

namespace Npi.Loader.Tests.Integration;

/// <summary>
/// The NPPES history (docs/PRODUCT_PLAN.md step 3.4; migration 103) on scratch databases: three tiny archived files in order
/// give the expected versions and area figures (basis 3), a second pass changes nothing, a run stopped by a broken file goes
/// on from it to the same tables, the lists behind the counts match them, and step 3.3's builder keeps the archive's months.
/// </summary>
public sealed class HistoryIntegrationTests : IDisposable
{
    private const string Chiro = "111N00000X", Dentist = "122300000X";
    private const string Suffolk = "36103", Kings = "36047";
    private const string A = "1000000004", B = "1000000012", C = "1000000020", D = "1000000038", E = "1000000046";

    private static readonly DateOnly Sep2011 = new(2011, 9, 1), Oct2011 = new(2011, 10, 1), Nov2011 = new(2011, 11, 1), Dec2012 = new(2012, 12, 1);

    private readonly string _folder = Directory.CreateTempSubdirectory("npi-history-").FullName;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // September 2011 (314 columns): A chiropractor and C dentist in Suffolk, B dentist in Brooklyn (issued that month).
    // November 2011 (October has no file): A moved to Brooklyn, B's primary taxonomy is now chiropractic, C dropped out
    // (deactivated, inferred), D (issued in October, Suffolk) and E (November, Brooklyn) are new.
    // December 2012 (329 columns, deactivated NPIs listed): C listed with its date, D deactivated.
    private IReadOnlyList<(HistoryFile File, string Zip)> Files(string name, bool brokenSecond = false)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_folder, name)).FullName;
        var first = new HistoryFiles()
            .Active(A, "NY", "11735", "05/23/2005", "05/23/2005", [Chiro])
            .Active(B, "NY", "11201", "09/05/2011", "09/05/2011", [Dentist])
            .Active(C, "NY", "11735", "01/01/2008", "01/01/2008", [Dentist])
            .Zip(folder, "NPPES_Data_Dissemination_Sept_2011.zip", HistoryFiles.Layout.Y2011, new DateOnly(2011, 9, 11));
        var second = Path.Combine(folder, "NPPES_Data_Dissemination_Nov_2011.zip");
        if (brokenSecond)
        {
            File.WriteAllText(second, "not a zip");
        }
        else
        {
            new HistoryFiles()
                .Active(A, "NY", "11201", "05/23/2005", "10/01/2011", [Chiro])
                .Active(B, "NY", "11201", "09/05/2011", "11/01/2011", [Dentist, Chiro], primarySlot: 2)
                .Active(D, "NY", "11735", "10/20/2011", "10/20/2011", [Chiro])
                .Active(E, "NY", "11201", "11/02/2011", "11/02/2011", [Dentist])
                .Zip(folder, Path.GetFileName(second), HistoryFiles.Layout.Y2011, new DateOnly(2011, 11, 13), subfolder: "20111113_0101");
        }

        var third = new HistoryFiles()
            .Active(A, "NY", "11201", "05/23/2005", "10/01/2011", [Chiro])
            .Active(B, "NY", "11201", "09/05/2011", "11/01/2011", [Dentist, Chiro], primarySlot: 2)
            .Deactivated(C, "10/15/2011")
            .Deactivated(D, "12/01/2012")
            .Active(E, "NY", "11201", "11/02/2011", "11/02/2011", [Dentist])
            .Zip(folder, "NPPES_Data_Dissemination_Dec_2012.zip", HistoryFiles.Layout.Y2016, new DateOnly(2012, 12, 9));
        HistoryFile F(DateOnly month, string zip) => new(month, Path.GetFileName(zip), new Uri("https://example.test/" + Path.GetFileName(zip)), "nber");
        return [(F(Sep2011, first), first), (F(Nov2011, second), second), (F(Dec2012, third), third)];
    }

    private HistoryBuilder Builder(TestDatabase db, string name) =>
        new(new LoaderOptions { WorkFolder = Path.Combine(_folder, name, "work"), LogFolder = Path.Combine(_folder, name), HistoryKeepZips = true },
            db.Database, new HttpClient(), Logger.None);

    private async Task<bool> ProcessAsync(TestDatabase db, string name, IReadOnlyList<(HistoryFile File, string Zip)> files) =>
        await Builder(db, name).ProcessFilesAsync(files.Select(f => f.File).ToList(), f => Task.FromResult(files.Single(x => x.File.Period == f.Period).Zip), _ct);

    private static async Task<TestDatabase> SeededAsync()
    {
        var db = await TestDatabase.CreateAsync();
        await db.ExecuteAsync(
            """
            INSERT INTO county (county_fips, state, county_name, source) VALUES
              ('36103', 'NY', 'Suffolk County', 'census-gazetteer'), ('36047', 'NY', 'Kings County', 'census-gazetteer');
            INSERT INTO zip_county (zip5, county_fips, res_ratio, bus_ratio, oth_ratio, tot_ratio, usps_city, usps_state, year, quarter) VALUES
              ('11735', '36103', 1, 1, 1, 1, 'FARMINGDALE', 'NY', 2026, 2), ('11201', '36047', 1, 1, 1, 1, 'BROOKLYN', 'NY', 2026, 2);
            """);
        return db;
    }

    private static Task<IReadOnlyList<(uint, DateTime, DateTime?, sbyte, string?, string?)>> VersionsAsync(TestDatabase db) =>
        db.QueryAsync<(uint, DateTime, DateTime?, sbyte, string?, string?)>(
            "SELECT npi, from_period, to_period, status, zip5, primary_taxonomy FROM npi_history ORDER BY npi, from_period");

    private static Task<IReadOnlyList<(string, string, string, DateTime, int?, int?, int?, int?, int?, int?)>> StatsAsync(TestDatabase db) =>
        db.QueryAsync<(string, string, string, DateTime, int?, int?, int?, int?, int?, int?)>(
            """
            SELECT area_type, area_id, category, month, providers, new_providers, deactivated, moved_in, moved_out, specialty_changes
            FROM area_month_stat WHERE basis = 3 ORDER BY area_type, area_id, category, month
            """);

    private static async Task<int?> StatAsync(TestDatabase db, string type, string id, string category, DateOnly month, string column) =>
        await db.ScalarAsync<int?>($"SELECT {column} FROM area_month_stat WHERE area_type = @type AND area_id = @id AND category = @category AND month = @month",
            new { type, id, category, month = month.ToDateTime(TimeOnly.MinValue) });

    [Fact]
    public async Task Three_files_in_order_give_the_versions_and_the_area_figures()
    {
        await using var db = await SeededAsync();
        Assert.True(await ProcessAsync(db, "order", Files("order")));

        DateTime P(int y, int m, int d) => new(y, m, d);
        Assert.Equal(
        [
            (1000000004u, P(2011, 9, 11), P(2011, 11, 13), (sbyte)0, "11735", Chiro), (1000000004u, P(2011, 11, 13), null, (sbyte)0, "11201", Chiro),
            (1000000012u, P(2011, 9, 11), P(2011, 11, 13), (sbyte)0, "11201", Dentist), (1000000012u, P(2011, 11, 13), null, (sbyte)0, "11201", Chiro),
            (1000000020u, P(2011, 9, 11), P(2011, 11, 13), (sbyte)0, "11735", Dentist), (1000000020u, P(2011, 11, 13), P(2012, 12, 9), (sbyte)2, null, null),
            (1000000020u, P(2012, 12, 9), null, (sbyte)1, null, null),
            (1000000038u, P(2011, 11, 13), P(2012, 12, 9), (sbyte)0, "11735", Chiro), (1000000038u, P(2012, 12, 9), null, (sbyte)1, null, null),
            (1000000046u, P(2011, 11, 13), null, (sbyte)0, "11201", Dentist),
        ], await VersionsAsync(db));
        Assert.Equal(["Completed", "Completed", "Completed"], await db.QueryAsync<string>("SELECT status FROM history_period WHERE stats_done = 1 ORDER BY period"));
        Assert.Equal((Sep2011.ToDateTime(TimeOnly.MinValue), Dec2012.ToDateTime(TimeOnly.MinValue)),
            (await db.QueryAsync<(DateTime, DateTime)>("SELECT archive_from, archive_to FROM area_history_meta WHERE id = 1")).Single());

        // September: three providers; B is new (issued that month).
        Assert.Equal(3, await StatAsync(db, "us", "US", "", Sep2011, "providers"));
        Assert.Equal((2, 1), (await StatAsync(db, "county", Suffolk, "", Sep2011, "providers"), await StatAsync(db, "county", Kings, "", Sep2011, "providers")));
        Assert.Equal(1, await StatAsync(db, "county", Kings, "", Sep2011, "new_providers"));
        Assert.Equal(1, await StatAsync(db, "county", Suffolk, "Dentists", Sep2011, "providers"));
        // October has no file: D's NPI, issued then, still counts in its month; no provider count.
        Assert.Equal((1, (int?)null), (await StatAsync(db, "county", Suffolk, "", Oct2011, "new_providers"), await StatAsync(db, "county", Suffolk, "", Oct2011, "providers")));
        Assert.Equal(1, await StatAsync(db, "county", Suffolk, "Chiropractic & complementary care", Oct2011, "new_providers"));
        // November: A moved from Suffolk to Kings, B changed specialty, C (gone) deactivated in Suffolk, E is new.
        Assert.Equal((1, 3), (await StatAsync(db, "county", Suffolk, "", Nov2011, "providers"), await StatAsync(db, "county", Kings, "", Nov2011, "providers")));
        Assert.Equal((1, 1), (await StatAsync(db, "county", Kings, "", Nov2011, "moved_in"), await StatAsync(db, "county", Suffolk, "", Nov2011, "moved_out")));
        Assert.Equal((0, 0), (await StatAsync(db, "state", "NY", "", Nov2011, "moved_in"), await StatAsync(db, "state", "NY", "", Nov2011, "moved_out")));
        Assert.Equal((1, 1, 1), (await StatAsync(db, "county", Suffolk, "", Nov2011, "deactivated"), await StatAsync(db, "us", "US", "", Nov2011, "deactivated"),
            await StatAsync(db, "county", Kings, "", Nov2011, "specialty_changes")));
        Assert.Equal(1, await StatAsync(db, "county", Kings, "", Nov2011, "new_providers"));
        Assert.Null(await StatAsync(db, "us", "US", "", Nov2011, "moved_in"));
        Assert.Equal(2, await StatAsync(db, "county", Kings, "Chiropractic & complementary care", Nov2011, "providers"));   // A and B
        // December 2012: D deactivated in Suffolk; C's listed date is no new deactivation.
        Assert.Equal((0, 1, 1), (await StatAsync(db, "county", Suffolk, "", Dec2012, "providers"), await StatAsync(db, "county", Suffolk, "", Dec2012, "deactivated"),
            await StatAsync(db, "us", "US", "", Dec2012, "deactivated")));

        // The site: the archive's months, with the lists behind the counts.
        var service = new AreaHistoryService(db.ConnectionString);
        var suffolk = (await service.GetAsync("NY", Suffolk, null, _ct))!;
        Assert.Equal((Sep2011, Dec2012), (suffolk.Coverage.ArchiveFrom, suffolk.Coverage.ArchiveTo));
        Assert.Equal(16, suffolk.Months.Count);
        Assert.All(suffolk.Months, m => Assert.Equal(AreaHistoryMath.Archive, m.Basis));
        var nov = suffolk.Months[2];
        Assert.Equal((1, 1, 0, 1), (nov.Providers, nov.Deactivated, nov.MovedIn, nov.MovedOut));
        Assert.Null(nov.NetChange);                                                              // October has no count
        Assert.Null(suffolk.Months[1].MovedOut);                                                 // October: no file
        Assert.Equal([A], await service.GetChangeNpisAsync("NY", Suffolk, AreaChangeKinds.MovedOut, Nov2011, null, _ct));
        Assert.Equal([A], await service.GetChangeNpisAsync("NY", Kings, AreaChangeKinds.MovedIn, Nov2011, null, _ct));
        Assert.Equal([C], await service.GetChangeNpisAsync("NY", Suffolk, AreaChangeKinds.Deactivated, Nov2011, null, _ct));
        Assert.Equal([C], await service.GetChangeNpisAsync(null, null, AreaChangeKinds.Deactivated, Nov2011, null, _ct));
        Assert.Equal([D], await service.GetChangeNpisAsync("NY", Suffolk, AreaChangeKinds.New, Oct2011, null, _ct));
        Assert.Equal([D], await service.GetChangeNpisAsync("NY", Suffolk, AreaChangeKinds.New, Oct2011, "Chiropractic & complementary care", _ct));
        Assert.Empty(await service.GetChangeNpisAsync("NY", Suffolk, AreaChangeKinds.New, Oct2011, "Dentists", _ct));
        Assert.Equal([B], await service.GetChangeNpisAsync("NY", null, AreaChangeKinds.Specialty, Nov2011, null, _ct));
        Assert.Equal([D], await service.GetChangeNpisAsync("NY", Suffolk, AreaChangeKinds.Deactivated, Dec2012, null, _ct));
        var page = (await service.GetChangesAsync("NY", Kings, AreaChangeKinds.New, Nov2011, null, 1, _ct))!;
        Assert.Equal((1, E), (page.Total, page.Entries.Single().Npi));

        // The profile's "Over the years" and Verify's "changed since" (October 2011 recorded as without a file, as a run does).
        await db.ExecuteAsync("INSERT INTO history_period (period, status) VALUES ('2011-10-01', 'Missing')");
        await db.ExecuteAsync(
            "INSERT INTO provider_change (npi, detected_at, change_type, old_value, new_value) VALUES (@A, '2026-10-14 03:00:00', 'taxonomy_added', NULL, @Dentist)",
            new { A, Dentist });
        await using (var connection = new MySqlConnector.MySqlConnection(db.ConnectionString))
        {
            await connection.OpenAsync(_ct);
            var a = (await ProviderHistoryReader.ReadAsync(connection, A, _ct))!;
            Assert.Equal((Sep2011, Dec2012, new DateOnly(2011, 9, 11), true), (a.ArchiveFrom, a.ArchiveTo, a.FirstListed, a.InFirstFile));
            Assert.Equal(2, a.Versions.Count);
            Assert.Equal(["Primary practice location: Farmingdale, NY 11735 → Brooklyn, NY 11201", "Updated in NPPES on October 1, 2011"],
                a.Versions[0].Changes.Select(x => x.Text));
            Assert.Equal([Oct2011], a.Versions[0].MonthsWithoutFileBefore);
            Assert.Equal(("CMS NPPES monthly file (historical copy via NBER)", new DateOnly(2011, 11, 13)), (a.Versions[0].Source, a.Versions[1].ListedTo));
            Assert.StartsWith("In the archive's first file", a.Versions[1].Changes.Single().Text, StringComparison.Ordinal);
            var c = (await ProviderHistoryReader.ReadAsync(connection, C, _ct))!;
            Assert.Equal(("11735", "Farmingdale, NY 11735"), (c.LastActive!.Practice!.Zip5, c.LastActive.Practice.ToString()));
            Assert.Equal([ProviderHistoryReader.Deactivated, ProviderHistoryReader.NotListed, ProviderHistoryReader.Active], c.Versions.Select(v => v.Status));
            Assert.Null(await ProviderHistoryReader.ReadAsync(connection, "1234567893", _ct));

            var since = await ProviderHistoryReader.ChangesSinceAsync(connection, [A, B, C, D, E, "1234567893"], new DateOnly(2011, 12, 1), _ct);
            Assert.Equal([A, C, D], since.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(["Listed again as deactivated on October 15, 2011"], since[C].Select(x => x.Text));
            Assert.Equal([(new DateOnly(2012, 12, 9), HistoryChangeKinds.Deactivated, "Deactivated on December 1, 2012")], since[D].Select(x => (x.On, x.Kind, x.Text)));
            Assert.StartsWith("Specialty added: ", Assert.Single(since[A]).Text, StringComparison.Ordinal);       // recorded since October 2026
            Assert.Equal([B, E], (await ProviderHistoryReader.ChangesSinceAsync(connection, [B, E], new DateOnly(2011, 10, 1), _ct)).Keys.Order(StringComparer.Ordinal));
        }

        // A second pass: nothing to do, nothing changes.
        var versions = await VersionsAsync(db);
        var stats = await StatsAsync(db);
        Assert.True(await ProcessAsync(db, "order", Files("order-again")));
        Assert.Equal(versions, await VersionsAsync(db));
        Assert.Equal(stats, await StatsAsync(db));
    }

    private static readonly DateOnly Aug2018 = new(2018, 8, 1), Sep2018 = new(2018, 9, 1), Oct2018 = new(2018, 10, 1);

    // August 2018 (no pl_pfile): A in Suffolk, B in Brooklyn. September (the first pl_pfile): A also practises in Brooklyn.
    // October: A drops Brooklyn, B adds Suffolk.
    private IReadOnlyList<(HistoryFile File, string Zip)> LocationFiles(string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_folder, name)).FullName;
        HistoryFiles Both() => new HistoryFiles()
            .Active(A, "NY", "11735", "05/23/2005", "05/23/2005", [Chiro])
            .Active(B, "NY", "11201", "05/23/2005", "05/23/2005", [Dentist]);
        var aug = Both().Zip(folder, "NPPES_Data_Dissemination_August_2018.zip", HistoryFiles.Layout.Y2016, new DateOnly(2018, 8, 12), pfile: true);
        var sep = Both().Secondary(A, "NY", "11201")
            .Zip(folder, "NPPES_Data_Dissemination_September_2018.zip", HistoryFiles.Layout.Y2016, new DateOnly(2018, 9, 9), pfile: true, locations: true);
        var oct = Both().Secondary(B, "NY", "117351234")
            .Zip(folder, "NPPES_Data_Dissemination_October_2018.zip", HistoryFiles.Layout.Y2016, new DateOnly(2018, 10, 7), pfile: true, locations: true);
        HistoryFile F(DateOnly month, string zip) => new(month, Path.GetFileName(zip), new Uri("https://example.test/" + Path.GetFileName(zip)), "cms");
        return [(F(Aug2018, aug), aug), (F(Sep2018, sep), sep), (F(Oct2018, oct), oct)];
    }

    [Fact]
    public async Task Secondary_locations_count_from_the_first_file_that_lists_them()
    {
        await using var db = await SeededAsync();
        var files = LocationFiles("locations");
        // Two files, then a new run for the third: the stored location sets are read back.
        Assert.True(await ProcessAsync(db, "locations", files.Take(2).ToList()));
        Assert.True(await ProcessAsync(db, "locations", files));

        Assert.Equal(5L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM npi_history"));
        Assert.Equal([((sbyte)0, (int?)null), ((sbyte)1, 1), ((sbyte)1, 1)],
            await db.QueryAsync<(sbyte, int?)>("SELECT locations, secondary_rows FROM history_period ORDER BY period"));
        Assert.Equal(Sep2018.ToDateTime(TimeOnly.MinValue), await db.ScalarAsync<DateTime?>("SELECT any_location_from FROM area_history_meta WHERE id = 1"));
        Assert.Equal(["1|NY|11201", "2|NY|11735"], await db.QueryAsync<string>("SELECT CONCAT_WS('|', set_id, state, zip5) FROM npi_history_location_set ORDER BY set_id"));

        // August: by primary location. September: A counts in Kings too, but that's the new definition, not a move.
        Assert.Equal((1, 1), (await StatAsync(db, "county", Suffolk, "", Aug2018, "providers"), await StatAsync(db, "county", Kings, "", Aug2018, "providers")));
        Assert.Equal((1, 2, 0), (await StatAsync(db, "county", Suffolk, "", Sep2018, "providers"), await StatAsync(db, "county", Kings, "", Sep2018, "providers"),
            await StatAsync(db, "county", Kings, "", Sep2018, "moved_in")));
        Assert.Equal(2, await StatAsync(db, "county", Kings, "Chiropractic & complementary care", Sep2018, "providers") + await StatAsync(db, "county", Kings, "Dentists", Sep2018, "providers"));
        // October: A left Kings, B came to Suffolk; both stay in NY and the US.
        Assert.Equal((2, 1), (await StatAsync(db, "county", Suffolk, "", Oct2018, "providers"), await StatAsync(db, "county", Kings, "", Oct2018, "providers")));
        Assert.Equal((1, 1), (await StatAsync(db, "county", Kings, "", Oct2018, "moved_out"), await StatAsync(db, "county", Suffolk, "", Oct2018, "moved_in")));
        Assert.Equal((0, 0, 2), (await StatAsync(db, "state", "NY", "", Oct2018, "moved_in"), await StatAsync(db, "state", "NY", "", Oct2018, "moved_out"),
            await StatAsync(db, "us", "US", "", Oct2018, "providers")));

        var service = new AreaHistoryService(db.ConnectionString);
        var kings = (await service.GetAsync("NY", Kings, null, _ct))!;
        Assert.Equal((Sep2018, Sep2018), (kings.Coverage.AnyLocationFrom, kings.Coverage.DefinitionStep()));
        Assert.Equal([(int?)null, null, -1], kings.Months.Select(m => m.NetChange).ToList());      // none across the definition step
        Assert.Empty(await service.GetChangeNpisAsync("NY", Kings, AreaChangeKinds.MovedIn, Sep2018, null, _ct));
        Assert.Equal([A], await service.GetChangeNpisAsync("NY", Kings, AreaChangeKinds.MovedOut, Oct2018, null, _ct));
        Assert.Equal([B], await service.GetChangeNpisAsync("NY", Suffolk, AreaChangeKinds.MovedIn, Oct2018, null, _ct));
    }

    [Fact]
    public async Task A_file_read_as_history_counts_its_providers_as_the_area_counts_do()
    {
        await using var db = await SeededAsync();
        static (string, string)[] At(string state, string zip, string country = "US") =>
        [
            ("Provider_First_Line_Business_Practice_Location_Address", "1 Main St"), ("Provider_Business_Practice_Location_Address_City_Name", "TOWN"),
            ("Provider_Business_Practice_Location_Address_State_Name", state), ("Provider_Business_Practice_Location_Address_Postal_Code", zip),
            ("Provider_Business_Practice_Location_Address_Country_Code", country),
        ];
        static (string, string)[] Doing(string code, string enumeration = "05/23/2005") =>
        [
            ("Entity_Type_Code", "1"), ("Provider_Last_Name_Legal_Name", "TESTER"), ("Healthcare_Provider_Taxonomy_Code_1", code),
            ("Healthcare_Provider_Primary_Taxonomy_Switch_1", "Y"), ("Provider_Enumeration_Date", enumeration), ("Last_Update_Date", "01/02/2026"),
        ];
        var folder = Directory.CreateDirectory(Path.Combine(_folder, "join")).FullName;
        var zip = new NppesZipBuilder()
            .Provider(A, [.. Doing(Chiro), .. At("NY", "11735")])
            .Provider(B, [.. Doing(Dentist), .. At("NY", "11201")])
            .Provider(C, [.. Doing(Chiro), .. At("ONTARIO", "K1A0B1", "CA")])
            .Provider(D, [.. Doing(Dentist), .. At("NJ", "070011234")])
            .Provider(E, [("Entity_Type_Code", "1"), ("Healthcare_Provider_Taxonomy_Code_1", Chiro), ("NPI_Deactivation_Date", "01/01/2025"), .. At("NY", "11735")])
            .Location(A, "2 Court St", "", "BROOKLYN", "NY", "11201", "US")
            .Location(A, "3 Court St", "", "BROOKLYN", "NY", "112011234", "US")
            .Location(B, "4 Elm St", "", "NEWARK", "NJ", "07102", "US")
            .Location(C, "5 Bay St", "", "TOWN", "NY", "11735", "US")
            .Write(folder, "NPPES_Data_Dissemination_January_2026_V2.zip");

        // Loaded as a monthly and counted by the projection and the area counts (what the recorded series takes each month) …
        using var http = new HttpClient();
        Assert.Equal(0, await new LoaderApp(new LoaderOptions { WorkFolder = folder, LogFolder = folder }, db.Database, http, TestLog.Warnings).LoadFileAsync(zip, _ct));
        await new ProjectionBuilder(db.Database, TestLog.Warnings, 0.95).BuildAsync(_ct);
        await new AreaCountBuilder(db.Database, TestLog.Warnings, 0.95).BuildAsync(_ct);

        // … and read as the archive's month: the same providers in every area and category.
        var january = new HistoryFile(new DateOnly(2026, 1, 1), Path.GetFileName(zip), new Uri("https://example.test/" + Path.GetFileName(zip)), "cms");
        Assert.True(await Builder(db, "join").ProcessFilesAsync([january], _ => Task.FromResult(zip), _ct));
        var counted = await db.QueryAsync<string>(
            "SELECT CONCAT_WS('|', area_type, area_id, category, providers) FROM area_provider_count WHERE subcategory = '' AND providers > 0 ORDER BY 1");
        var archived = await db.QueryAsync<string>(
            """
            SELECT CONCAT_WS('|', area_type, area_id, category, providers) FROM area_month_stat
            WHERE basis = 3 AND month = '2026-01-01' AND subcategory = '' AND providers > 0 ORDER BY 1
            """);
        Assert.Contains("county|36047||2", counted);                                             // A (secondary) and B in Brooklyn
        Assert.Contains("us|US||4", counted);                                                    // C's Ontario address counts in the US
        Assert.Equal(counted, archived);
    }

    [Fact]
    public async Task A_run_stopped_by_a_broken_file_goes_on_from_it_to_the_same_tables()
    {
        await using var straight = await SeededAsync();
        await using var resumed = await SeededAsync();
        Assert.True(await ProcessAsync(straight, "straight", Files("straight")));

        Assert.False(await ProcessAsync(resumed, "resumed", Files("broken", brokenSecond: true)));
        Assert.Equal([("Completed", (sbyte)1), ("Failed", (sbyte)0)],
            await resumed.QueryAsync<(string, sbyte)>("SELECT status, stats_done FROM history_period ORDER BY period"));
        Assert.Equal(3L, await resumed.ScalarAsync<long>("SELECT COUNT(*) FROM npi_history"));

        // The next run reads the open versions back and goes on with November.
        Assert.True(await ProcessAsync(resumed, "resumed", Files("resumed")));
        Assert.Equal(await VersionsAsync(straight), await VersionsAsync(resumed));
        Assert.Equal(await StatsAsync(straight), await StatsAsync(resumed));
        Assert.Equal(
            await straight.QueryAsync<(string, string, string, int)>("SELECT area_type, area_id, category, providers FROM history_area_count ORDER BY 1, 2, 3"),
            await resumed.QueryAsync<(string, string, string, int)>("SELECT area_type, area_id, category, providers FROM history_area_count ORDER BY 1, 2, 3"));
    }
}
