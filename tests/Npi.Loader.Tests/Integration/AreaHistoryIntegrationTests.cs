using Npi.Core.Search;
using Npi.Loader.Projection;
using Serilog.Core;

namespace Npi.Loader.Tests.Integration;

/// <summary>
/// Areas over time (docs/PRODUCT_PLAN.md step 3.3; migration 102) on scratch databases: the first build derives the past, a
/// build after an incremental projection records moves, deactivations and changes, the full and the incremental paths give the
/// same tables, the lists behind the counts match them, and a new month freezes the one before last.
/// </summary>
public sealed class AreaHistoryIntegrationTests : IDisposable
{
    private const string Chiro = "111N00000X";
    private const string Dentist = "122300000X";
    private const string ChiroCategory = "Chiropractic & complementary care";
    private const string Dentists = "Dentists";
    private const string Suffolk = "36103", Kings = "36047", Nassau = "36059";

    // A: chiropractor in Farmingdale (Suffolk), NPI issued August 2026. B: dentist in Brooklyn (Kings), September 2026.
    // C: chiropractor in Brooklyn, issued 2005 (before the derived months). D: dentist in Hempstead (Nassau), October 2026.
    // E: deactivated on January 1, 2025. F: added later, a copy of D issued October 7.
    private const string A = "1000000004", B = "1000000012", C = "1000000020", D = "1000000038", E = "1000000046", F = "1000000053";

    private static readonly DateTime Aug = new(2026, 8, 1), Sep = new(2026, 9, 1), Oct = new(2026, 10, 1), Nov = new(2026, 11, 1), Jan2025 = new(2025, 1, 1);

    private readonly string _folder = Directory.CreateTempSubdirectory("npi-area-history-").FullName;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static AreaHistoryBuilder History(TestDatabase db) => new(db.Database, Logger.None);

    [Fact]
    public async Task The_first_build_derives_the_past_and_takes_the_month_s_counts()
    {
        await using var db = await SeededAsync("first");
        await new AreaCountBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);
        Assert.True(await History(db).IsStaleAsync(_ct));
        await History(db).BuildAsync(_ct);
        Assert.False(await History(db).IsStaleAsync(_ct));

        Assert.Equal((new DateTime(2021, 11, 1), Oct, Oct, Oct), (await db.QueryAsync<(DateTime, DateTime, DateTime, DateTime)>(
            "SELECT derived_from, tracked_from, changes_from, as_of_month FROM area_history_meta WHERE id = 1")).Single());
        // NPIs issued per month where their holders practise now; C's (2005) is before the derived months.
        Assert.Equal(
            [("county", Kings, Sep, 1), ("county", Nassau, Oct, 1), ("county", Suffolk, Aug, 1), ("state", "NY", Aug, 1), ("state", "NY", Sep, 1),
             ("state", "NY", Oct, 1), ("us", "US", Aug, 1), ("us", "US", Sep, 1), ("us", "US", Oct, 1)],
            await db.QueryAsync<(string, string, DateTime, int)>(
                "SELECT area_type, area_id, month, new_providers FROM area_month_stat WHERE category = '' AND new_providers IS NOT NULL ORDER BY area_type, area_id, month"));
        Assert.Equal(1, await Stat(db, "county", Suffolk, ChiroCategory, Aug, "new_providers"));
        Assert.Equal(1, await Stat(db, "us", "US", "", Jan2025, "deactivated"));               // E, by CMS's date
        Assert.Equal(1, await Stat(db, "us", "US", "", Jan2025, "basis"));                     // derived
        Assert.Equal(2, await Stat(db, "us", "US", "", Oct, "basis"));                         // recorded

        // The as-of month's counts: every county gets one (0 when nobody practises there), per category from the area counts.
        Assert.Equal(4, await Stat(db, "us", "US", "", Oct, "providers"));
        Assert.Equal(2, await Stat(db, "county", Kings, "", Oct, "providers"));
        Assert.Equal(1, await Stat(db, "county", Kings, Dentists, Oct, "providers"));
        Assert.Equal((2, 1, 1), (await db.QueryAsync<(int, int, int)>(
            "SELECT quality_current, quality_check, quality_stale FROM area_month_stat WHERE area_type = 'us' AND category = '' AND month = @Oct", new { Oct })).Single());
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM area_month_event"));
        Assert.Equal(4L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM area_history_location"));

        var suffolk = (await new AreaHistoryService(db.ConnectionString).GetAsync("NY", Suffolk, null, _ct))!;
        Assert.Equal(("Suffolk County", 60), (suffolk.Name, suffolk.Months.Count));            // November 2021 to October 2026
        var oct = suffolk.Months[^1];
        Assert.Equal((1, 0, 0, true), (oct.Providers!.Value, oct.MovedIn!.Value, oct.NewProviders!.Value, oct.Partial));
        Assert.Equal(1, suffolk.Months[^3].NewProviders);                                      // August
        Assert.Null(suffolk.Months[^3].MovedIn);                                               // before the recorded months
    }

    [Fact]
    public async Task Moves_deactivations_and_changes_are_recorded_and_both_paths_agree()
    {
        await using var incremental = await SeededAsync("incremental");
        await using var full = await SeededAsync("full");
        foreach (var (db, everyone) in new[] { (incremental, false), (full, true) })
        {
            await new AreaCountBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);
            await History(db).BuildAsync(_ct);
            await ChangeAsync(db);
            Assert.True(await History(db).IsStaleAsync(_ct));
            await History(db).BuildAsync(_ct, full: everyone);
            Assert.False(await History(db).IsStaleAsync(_ct));
            Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM builder_pending WHERE builder = 'area_history'"));
        }

        var now = AreaHistoryMath.MonthOf(DateTime.UtcNow).ToDateTime(TimeOnly.MinValue);   // the change log's rows count in the month they were recorded
        var db1 = incremental;
        Assert.Equal(
            new[]
            {
                ("county", Kings, Oct, "deactivated", B), ("county", Kings, Oct, "moved_in", A), ("county", Kings, now, "specialty", C),
                ("county", Nassau, now, "license", D), ("county", Suffolk, Oct, "moved_out", A), ("state", "NY", Oct, "deactivated", B),
                ("state", "NY", now, "license", D), ("state", "NY", now, "specialty", C), ("us", "US", now, "license", D), ("us", "US", now, "specialty", C),
            }.OrderBy(e => e.Item1, StringComparer.Ordinal).ThenBy(e => e.Item2, StringComparer.Ordinal).ThenBy(e => e.Item3).ThenBy(e => e.Item4, StringComparer.Ordinal),
            await db1.QueryAsync<(string, string, DateTime, string, string)>(
                "SELECT area_type, area_id, month, kind, npi FROM area_month_event ORDER BY area_type, area_id, month, kind"));

        Assert.Equal(1, await Stat(db1, "county", Suffolk, "", Oct, "moved_out"));
        Assert.Equal(0, await Stat(db1, "county", Suffolk, "", Oct, "providers"));          // A left: counted, none
        Assert.Equal((1, 1), (await Stat(db1, "county", Kings, "", Oct, "moved_in"), await Stat(db1, "county", Kings, "", Oct, "deactivated")));
        Assert.Equal(1, await Stat(db1, "us", "US", "", Oct, "deactivated"));                // B, by CMS's date
        Assert.Null(await Stat(db1, "us", "US", "", Oct, "moved_in"));                        // the US counts no moves
        Assert.Equal(2, await Stat(db1, "county", Nassau, "", Oct, "new_providers"));        // D and F
        Assert.Equal(2, await Stat(db1, "county", Nassau, Dentists, Oct, "new_providers"));
        Assert.Null(await Stat(db1, "county", Kings, "", Sep, "new_providers"));             // September is open: B is gone
        Assert.Equal(1, await Stat(db1, "county", Suffolk, "", Aug, "new_providers"));       // August is frozen: A counted where he was
        Assert.Equal(1, await Stat(db1, "us", "US", "", Jan2025, "deactivated"));
        Assert.Equal(4, await Stat(db1, "us", "US", "", Oct, "providers"));                  // A, C, D, F

        // The full path (every provider's locations compared) gives the same tables as the queued providers. (The meta rows
        // differ only in the change log position: the pool's databases keep their AUTO_INCREMENT counters.)
        foreach (var table in new[] { "area_month_stat", "area_month_event", "area_history_location" })
        {
            Assert.Equal(await RowsAsync(db1, table), await RowsAsync(full, table));
        }

        const string meta = "SELECT CONCAT_WS('|', derived_from, tracked_from, changes_from, as_of_month) FROM area_history_meta";
        Assert.Equal(await db1.QueryAsync<string>(meta), await full.QueryAsync<string>(meta));

        // Every count in an open month lists exactly the providers it counts.
        var service = new AreaHistoryService(db1.ConnectionString);
        var counts = await db1.QueryAsync<(string Type, string Id, DateTime Month, int? New, int? Deactivated, int? MovedIn, int? MovedOut, int? Specialty, int? License, int? Exclusion)>(
            """
            SELECT s.area_type, s.area_id, s.month, s.new_providers, s.deactivated, s.moved_in, s.moved_out, s.specialty_changes, s.license_changes, s.exclusion_changes
            FROM area_month_stat s WHERE s.category = '' AND s.month >= @Sep
            """, new { Sep });
        var checkedCounts = 0;
        foreach (var c in counts)
        {
            var (state, county) = c.Type switch { "county" => ("NY", c.Id), "state" => (c.Id, (string?)null), _ => ((string?)null, (string?)null) };
            foreach (var (kind, count) in new[]
                     {
                         (AreaChangeKinds.New, c.New), (AreaChangeKinds.Deactivated, c.Deactivated), (AreaChangeKinds.MovedIn, c.MovedIn),
                         (AreaChangeKinds.MovedOut, c.MovedOut), (AreaChangeKinds.Specialty, c.Specialty), (AreaChangeKinds.License, c.License),
                         (AreaChangeKinds.Exclusion, c.Exclusion),
                     })
            {
                if (!AreaHistoryService.HasKind(c.Type, kind))
                {
                    continue;
                }

                var month = DateOnly.FromDateTime(c.Month);
                var page = (await service.GetChangesAsync(state, county, kind, month, null, 1, _ct))!;
                Assert.True((count ?? 0) == page.Total, $"{c.Type} {c.Id} {month} {kind}: counted {count}, listed {page.Total}");
                Assert.Equal(page.Total, (await service.GetChangeNpisAsync(state, county, kind, month, null, _ct)).Count);
                checkedCounts += count is > 0 ? 1 : 0;
            }
        }

        Assert.True(checkedCounts >= 10, $"only {checkedCounts} counts checked");
        var gone = (await service.GetChangesAsync("NY", Kings, AreaChangeKinds.Deactivated, DateOnly.FromDateTime(Oct), null, 1, _ct))!;
        Assert.Equal((B, "BAKER, AL", new DateOnly(2026, 10, 6)), (gone.Entries[0].Npi, gone.Entries[0].Name, gone.Entries[0].DeactivationDate!.Value));
        var newDentists = (await service.GetChangesAsync("NY", Nassau, AreaChangeKinds.New, DateOnly.FromDateTime(Oct), Dentists, 1, _ct))!;
        Assert.Equal([D, F], newDentists.Entries.Select(e => e.Npi));
        Assert.Equal(0, (await service.GetChangesAsync("NY", Nassau, AreaChangeKinds.New, DateOnly.FromDateTime(Oct), ChiroCategory, 1, _ct))!.Total);

        // The pages' reads: the series and the map.
        var kings = (await service.GetAsync("NY", Kings, null, _ct))!;
        Assert.Equal((1, 1, 2), (kings.Months.Single(m => m.Month == DateOnly.FromDateTime(Oct)).MovedIn!.Value,
            kings.Months.Single(m => m.Month == DateOnly.FromDateTime(Oct)).Deactivated!.Value, kings.Months.Single(m => m.Month == DateOnly.FromDateTime(Oct)).Providers!.Value));
        var map = (await service.GetMapAsync("county", "NY", AreaMapStats.MovedIn, null, null, DateOnly.FromDateTime(Oct), _ct))!;
        Assert.Equal([(Kings, 1.0), (Nassau, 0.0), (Suffolk, 0.0)], map.Values.Select(v => (v.Id, v.Value!.Value)).OrderBy(v => v.Item1, StringComparer.Ordinal));
        var states = (await service.GetMapAsync("state", null, AreaMapStats.Providers, Dentists, null, null, _ct))!;
        Assert.Equal(3.0, states.Values.Single(v => v.Id == "NY").Value);                    // C (now a dentist), D and F; B is deactivated
        Assert.Null(await service.GetMapAsync("state", "NY", AreaMapStats.Providers, null, null, null, _ct));
    }

    [Fact]
    public async Task A_new_month_keeps_the_last_month_s_counts_and_freezes_the_one_before()
    {
        await using var db = await SeededAsync("months");
        await new AreaCountBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);
        await History(db).BuildAsync(_ct);

        // A November update moves the data's as-of month on.
        await db.ExecuteAsync(
            """
            SET SESSION innodb_strict_mode = OFF;
            UPDATE npidata SET Last_Update_Date = '11/02/2026' WHERE NPI = @D;
            INSERT INTO projection_pending (npi) VALUES (@D);
            """, new { D });
        await new ProjectionBuilder(db.Database, Logger.None, 0.95) { IncrementalShare = 10 }.BuildAsync(_ct);
        await new AreaCountBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);
        Assert.True(await History(db).IsStaleAsync(_ct));
        await History(db).BuildAsync(_ct);

        Assert.Equal(Nov, await db.ScalarAsync<DateTime>("SELECT as_of_month FROM area_history_meta WHERE id = 1"));
        Assert.Equal((1, 1), (await Stat(db, "county", Suffolk, "", Oct, "providers"), await Stat(db, "county", Suffolk, "", Nov, "providers")));
        Assert.Equal(1, await Stat(db, "county", Kings, "", Sep, "new_providers"));           // frozen as it was
        var suffolk = (await new AreaHistoryService(db.ConnectionString).GetAsync("NY", Suffolk, null, _ct))!;
        var (oct, nov) = (suffolk.Months[^2], suffolk.Months[^1]);
        Assert.Equal((false, true, 0), (oct.Partial, nov.Partial, nov.NetChange!.Value));
        Assert.Null(oct.NetChange);                                                            // September wasn't counted
    }

    // A's move to Brooklyn, B's deactivation, C's new specialty, F's new NPI and a license listed for D, then an incremental
    // projection and area count, as a weekly load and the state license reload would leave them.
    private async Task ChangeAsync(TestDatabase db)
    {
        await db.ExecuteAsync(
            """
            SET SESSION innodb_strict_mode = OFF; -- npidata's 330 TEXT columns (CLAUDE.md §6.1)
            UPDATE npidata SET Provider_First_Line_Business_Practice_Location_Address = '10 Court St', Provider_Business_Practice_Location_Address_City_Name = 'BROOKLYN',
              Provider_Business_Practice_Location_Address_Postal_Code = '11201', Last_Update_Date = '10/08/2026' WHERE NPI = @A;
            UPDATE npidata SET Is_Deactivated = 1, NPI_Deactivation_Date = '10/06/2026' WHERE NPI = @B;
            UPDATE npidata SET Healthcare_Provider_Taxonomy_Code_1 = @Dentist, Last_Update_Date = '10/07/2026' WHERE NPI = @C;
            CREATE TEMPORARY TABLE f SELECT * FROM npidata WHERE NPI = @D;
            UPDATE f SET NPI = @F, Provider_Enumeration_Date = '10/07/2026';
            INSERT INTO npidata SELECT * FROM f;
            DROP TEMPORARY TABLE f;
            INSERT INTO projection_pending (npi) VALUES (@A), (@B), (@C), (@F);
            INSERT INTO provider_change (npi, detected_at, change_type, old_value, new_value) VALUES (@D, UTC_TIMESTAMP(), 'license_added', NULL, 'NY 012345');
            """, new { A, B, C, D, F, Dentist });
        await new ProjectionBuilder(db.Database, Logger.None, 0.95) { IncrementalShare = 10 }.BuildAsync(_ct);
        await new AreaCountBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);
    }

    [Fact]
    public async Task The_archive_s_months_are_kept_and_the_recorded_series_wins_from_its_first_month()
    {
        // The NPPES archive (step 3.4, basis 3) loaded first, through October 2026, the month the recorded series will start in.
        await using var db = await SeededAsync("archive");
        var Dec2021 = new DateTime(2021, 12, 1);
        await db.ExecuteAsync(
            """
            INSERT INTO area_month_stat (area_type, area_id, category, subcategory, month, basis, providers, new_providers) VALUES
              ('us', 'US', '', '', '2021-12-01', 3, 7, 2), ('county', '36103', '', '', '2026-08-01', 3, 1, 1), ('us', 'US', '', '', '2026-10-01', 3, 9, 1);
            INSERT INTO area_history_meta (id, archive_from, archive_to) VALUES (1, '2007-11-01', '2026-10-01');
            """);
        await new AreaCountBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);
        await History(db).BuildAsync(_ct);

        // Before October the archive's figures stay and nothing is derived over them; from October the recorded ones win.
        Assert.Equal(3, await Stat(db, "us", "US", "", Dec2021, "basis"));
        Assert.Equal(7, await Stat(db, "us", "US", "", Dec2021, "providers"));
        Assert.Equal(3, await Stat(db, "county", Suffolk, "", Aug, "basis"));
        Assert.Null(await Stat(db, "county", Suffolk, ChiroCategory, Aug, "new_providers"));    // no derived row in an archive month
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM area_month_stat WHERE basis = 1"));
        Assert.Equal(2, await Stat(db, "us", "US", "", Oct, "basis"));
        Assert.Equal(4, await Stat(db, "us", "US", "", Oct, "providers"));
        Assert.Equal((new DateTime(2007, 11, 1), new DateTime(2026, 10, 1)), (await db.QueryAsync<(DateTime, DateTime)>(
            "SELECT archive_from, archive_to FROM area_history_meta WHERE id = 1")).Single());

        // The site: the archive shown up to September, then the recorded month; no net change across the join.
        var us = (await new AreaHistoryService(db.ConnectionString).GetAsync(null, null, null, _ct))!;
        Assert.Equal((new DateOnly(2007, 11, 1), new DateOnly(2026, 9, 1)), (us.Coverage.ArchiveFrom, us.Coverage.ArchiveTo));
        Assert.Equal((AreaHistoryMath.Archive, AreaHistoryMath.Recorded), (us.Months[^2].Basis, us.Months[^1].Basis));
        Assert.Null(us.Months[^1].NetChange);
        Assert.Equal(new DateOnly(2007, 11, 1), us.Months[0].Month);

        // A later build (a new month) keeps them too.
        await db.ExecuteAsync("UPDATE data_version SET as_of_date = '2026-11-03' WHERE id = 1");
        await History(db).BuildAsync(_ct);
        Assert.Equal(7, await Stat(db, "us", "US", "", Dec2021, "providers"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM area_month_stat WHERE basis = 1"));
    }

    private static Task<int?> Stat(TestDatabase db, string type, string id, string category, DateTime month, string column) =>
        db.ScalarAsync<int?>(
            $"SELECT `{column}` FROM area_month_stat WHERE area_type = @type AND area_id = @id AND category = @category AND subcategory = '' AND month = @month",
            new { type, id, category, month });

    // Every row of a table as text, sorted.
    private static async Task<IReadOnlyList<string>> RowsAsync(TestDatabase db, string table)
    {
        var columns = await db.QueryAsync<string>(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @table ORDER BY ordinal_position", new { table });
        return await db.QueryAsync<string>($"SELECT CONCAT_WS('|', {string.Join(", ", columns.Select(c => $"IFNULL(HEX(`{c}`), '-')"))}) AS r FROM `{table}` ORDER BY r");
    }

    private async Task<TestDatabase> SeededAsync(string name)
    {
        var db = await TestDatabase.CreateAsync();
        await db.ExecuteAsync(
            """
            INSERT INTO county (county_fips, state, county_name, source) VALUES
              ('36103', 'NY', 'Suffolk County', 'census-gazetteer'), ('36047', 'NY', 'Kings County', 'census-gazetteer'), ('36059', 'NY', 'Nassau County', 'census-gazetteer');
            INSERT INTO zip_county (zip5, county_fips, res_ratio, bus_ratio, oth_ratio, tot_ratio, usps_city, usps_state, year, quarter) VALUES
              ('11735', '36103', 1, 1, 1, 1, 'FARMINGDALE', 'NY', 2026, 2), ('11201', '36047', 1, 1, 1, 1, 'BROOKLYN', 'NY', 2026, 2),
              ('11550', '36059', 1, 1, 1, 1, 'HEMPSTEAD', 'NY', 2026, 2);
            """);

        static (string, string)[] Located(string street, string city, string zip) =>
        [
            ("Provider_First_Line_Business_Practice_Location_Address", street), ("Provider_Business_Practice_Location_Address_City_Name", city),
            ("Provider_Business_Practice_Location_Address_State_Name", "NY"), ("Provider_Business_Practice_Location_Address_Postal_Code", zip),
            ("Provider_Business_Practice_Location_Address_Country_Code", "US"),
        ];

        var zip = new NppesZipBuilder()
            .Provider(A, [("Entity_Type_Code", "1"), ("Provider_Last_Name_Legal_Name", "ADAMS"), ("Provider_First_Name", "ANN"),
                ("Healthcare_Provider_Taxonomy_Code_1", Chiro), ("Healthcare_Provider_Primary_Taxonomy_Switch_1", "Y"),
                ("Provider_Enumeration_Date", "08/15/2026"), ("Last_Update_Date", "09/01/2026"), .. Located("200 Main St", "FARMINGDALE", "11735")])
            .Provider(B, [("Entity_Type_Code", "1"), ("Provider_Last_Name_Legal_Name", "BAKER"), ("Provider_First_Name", "AL"),
                ("Healthcare_Provider_Taxonomy_Code_1", Dentist), ("Healthcare_Provider_Primary_Taxonomy_Switch_1", "Y"),
                ("Provider_Enumeration_Date", "09/10/2026"), ("Last_Update_Date", "09/10/2026"), .. Located("10 Court St", "BROOKLYN", "11201")])
            .Provider(C, [("Entity_Type_Code", "1"), ("Provider_Last_Name_Legal_Name", "CARTER"), ("Provider_First_Name", "CY"),
                ("Healthcare_Provider_Taxonomy_Code_1", Chiro), ("Healthcare_Provider_Primary_Taxonomy_Switch_1", "Y"),
                ("Provider_Enumeration_Date", "05/23/2005"), ("Last_Update_Date", "08/01/2026"), .. Located("5 Atlantic Ave", "BROOKLYN", "11201")])
            .Provider(D, [("Entity_Type_Code", "1"), ("Provider_Last_Name_Legal_Name", "DIAZ"), ("Provider_First_Name", "DORA"),
                ("Healthcare_Provider_Taxonomy_Code_1", Dentist), ("Healthcare_Provider_Primary_Taxonomy_Switch_1", "Y"),
                ("Provider_Enumeration_Date", "10/02/2026"), ("Last_Update_Date", "10/04/2026"), .. Located("1 Front St", "HEMPSTEAD", "11550")])
            .Provider(E, [("Entity_Type_Code", "1"), ("Provider_Last_Name_Legal_Name", "EVANS"), ("Healthcare_Provider_Taxonomy_Code_1", Chiro),
                ("NPI_Deactivation_Date", "01/01/2025"), .. Located("9 Elm St", "FARMINGDALE", "11735")])
            .Write(Directory.CreateDirectory(Path.Combine(_folder, name)).FullName, "NPPES_Data_Dissemination_October_2026_V2.zip");

        using var http = new HttpClient();
        var folder = Path.Combine(_folder, name);
        Assert.Equal(0, await new LoaderApp(new LoaderOptions { WorkFolder = folder, LogFolder = folder }, db.Database, http, Logger.None).LoadFileAsync(zip, _ct));
        await new ProjectionBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);
        // Record-quality labels as the quality builder leaves them: A and D current, B check, C stale.
        await db.ExecuteAsync(
            """
            INSERT INTO quality_search (label, state, city, zip5, npi) VALUES
              (1, 'NY', 'FARMINGDALE', '11735', @A), (2, 'NY', 'BROOKLYN', '11201', @B), (3, 'NY', 'BROOKLYN', '11201', @C), (1, 'NY', 'HEMPSTEAD', '11550', @D);
            """, new { A, B, C, D });
        return db;
    }
}
