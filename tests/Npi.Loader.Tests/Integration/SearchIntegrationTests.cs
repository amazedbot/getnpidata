using Npi.Core.Search;
using Npi.Loader.Geocoding;
using Npi.Loader.Projection;
using Serilog.Core;

namespace Npi.Loader.Tests.Integration;

/// <summary>
/// Stage 3 end to end on a scratch database: load a monthly zip, add reference rows, build the
/// projection, and search through <see cref="SearchService"/>.
/// </summary>
public sealed class SearchIntegrationTests : IDisposable
{
    private const string Chiro = "111N00000X";
    private const string ChiroSports = "111NS0005X";
    private const string Dentist = "122300000X";
    private const string Pediatrics = "208000000X";

    // A: chiropractor (taxonomy slot 3) in Amityville 11701, a ZIP in both Nassau and Suffolk.
    // B: chiropractor in Brooklyn 11201 with a secondary location in Farmingdale 11735 (Suffolk).
    // C: deactivated chiropractor in Amityville: never returned.
    // D: dental organization in Brooklyn.
    // E: pediatrician in Farmingdale, name with an accent.
    private const string A = "1000000004", B = "1000000012", C = "1000000020", D = "1000000038", E = "1000000046";

    private readonly string _folder = Directory.CreateTempSubdirectory("npi-search-").FullName;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Classification_and_county_matches_any_slot_and_any_location()
    {
        await using var db = await SeededAsync();
        var search = Search(db);

        var suffolk = await search.SearchAsync(new SearchFilter { Classification = "Chiropractor", CountyFips = "36103" }, _ct);

        Assert.Equal(2, suffolk.TotalCount);
        Assert.Equal([A, B], suffolk.Items.Select(i => i.Npi).Order());
        var b = suffolk.Items.Single(i => i.Npi == B);
        Assert.Equal(("FARMINGDALE", "11735-1234", "Suffolk County"), (b.City, b.Zip, b.County)); // the matching (secondary) location
        var a = suffolk.Items.Single(i => i.Npi == A);
        Assert.Equal(("O'BRIEN, JOSÉ Q JR", "D.C.", "Chiropractor"), (a.Name, a.Credential, a.PrimarySpecialty)); // slot 1 is primary
        Assert.Equal(new DateOnly(2026, 10, 4), suffolk.DataAsOf);

        // 11701 also overlaps Nassau, so A matches Nassau too ("match any" county).
        var nassau = await search.SearchAsync(new SearchFilter { Classification = "Chiropractor", CountyFips = "36059" }, _ct);
        Assert.Equal([A], nassau.Items.Select(i => i.Npi));
        Assert.Equal("Nassau County", nassau.Items[0].County);
    }

    [Fact]
    public async Task Specialization_state_city_zip_and_radius_filters()
    {
        await using var db = await SeededAsync();
        var search = Search(db);

        Assert.Equal([B], await Npis(search, new SearchFilter { Classification = "Chiropractor", Specialization = "Sports Physician" }));
        Assert.Equal([A, B, D, E], await Npis(search, new SearchFilter { State = "ny" }));
        Assert.Equal([B, D], await Npis(search, new SearchFilter { City = "brooklyn" }));
        Assert.Equal([B, E], await Npis(search, new SearchFilter { Zip5 = "11735" }));
        Assert.Equal([A, B, E], await Npis(search, new SearchFilter { Zip5 = "11701", RadiusMiles = 10 })); // Farmingdale ~5 mi, Brooklyn ~30 mi
        Assert.Equal([A, B, D, E], await Npis(search, new SearchFilter { Zip5 = "11701", RadiusMiles = 40 }));
        Assert.Equal([D], await Npis(search, new SearchFilter { TaxonomyCode = Dentist }));

        var radius = await Assert.ThrowsAsync<SearchValidationException>(() => search.SearchAsync(new SearchFilter { Zip5 = "99999", RadiusMiles = 5 }, _ct));
        Assert.Contains("Zip5", radius.Errors.Keys);
        var unknown = await Assert.ThrowsAsync<SearchValidationException>(() => search.SearchAsync(new SearchFilter { Classification = "Wizard" }, _ct));
        Assert.Contains("Classification", unknown.Errors.Keys);
    }

    [Fact]
    public async Task Names_credentials_and_attributes()
    {
        await using var db = await SeededAsync();
        var search = Search(db);

        Assert.Equal([A], await Npis(search, new SearchFilter { LastName = "o'br" }));
        Assert.Equal([A], await Npis(search, new SearchFilter { FirstName = "jose" })); // accent-insensitive
        Assert.Equal([E], await Npis(search, new SearchFilter { LastName = "Nuñ" }));
        Assert.Equal([D], await Npis(search, new SearchFilter { OrgName = "smith, jones" }));
        Assert.Equal([B, E], await Npis(search, new SearchFilter { Credential = "md" })); // "M.D." and "MD, PhD"
        Assert.Equal([D], await Npis(search, new SearchFilter { EntityType = 2 }));
        Assert.Equal([E], await Npis(search, new SearchFilter { Gender = "f" }));
        Assert.Equal([A], await Npis(search, new SearchFilter { Npi = A }));
        Assert.Empty(await Npis(search, new SearchFilter { Npi = C })); // deactivated
        Assert.Empty(await Npis(search, new SearchFilter { LastName = "%" })); // wildcards are literal
    }

    [Fact]
    public async Task No_filter_lists_everyone_and_the_full_export_file_holds_them_by_npi()
    {
        await using var db = await SeededAsync();
        var search = Search(db);

        var everyone = await search.SearchAsync(new SearchFilter { PageSize = 2 }, _ct);
        var count = await db.ScalarAsync<long>("SELECT COUNT(*) FROM provider");
        Assert.Equal(count, everyone.TotalCount);
        Assert.Equal(["BAKER, AL", "NUÑEZ, ELENA"], everyone.Items.Select(i => i.Name)); // by name, like any list
        Assert.Equal(count, (await search.SearchAsync(new SearchFilter { Quality = null, AddressType = null }, _ct)).TotalCount);

        // The ready-made file: every active provider by NPI, from several ranges (A … E fall in different ones), one gzip file.
        var folder = Directory.CreateTempSubdirectory("npi-export-").FullName;
        try
        {
            var info = await FullExport.WriteAsync(search, folder, new DateOnly(2026, 10, 11), parts: 4, _ct);
            Assert.Equal(count, info.Rows);
            Assert.Equal(info, FullExport.Read(folder));
            var bytes = new MemoryStream();
            await using (var file = FullExport.Open(folder))
            await using (var unpacked = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress))
            {
                await unpacked.CopyToAsync(bytes, _ct);
            }

            var text = System.Text.Encoding.UTF8.GetString(bytes.ToArray()); // keeps the BOM

            // One gzip member, not several joined: browsers unpack only the first. The trailer's size is then the whole file's.
            var stored = await File.ReadAllBytesAsync(Path.Combine(folder, FullExport.FileName), _ct);
            Assert.Equal((uint)bytes.Length, BitConverter.ToUInt32(stored, stored.Length - 4));

            Assert.StartsWith("﻿" + string.Join(",", ProviderCsv.Header), text, StringComparison.Ordinal); // one BOM and header
            var expected = new MemoryStream();
            var all = new List<ProviderSummary>();
            await foreach (var item in search.SearchAllAsync(new SearchFilter { Sort = "npi" }, _ct))
            {
                all.Add(item);
            }

            await ProviderCsv.WriteAsync(all.ToAsyncEnumerable(), expected, _ct);
            Assert.Equal(System.Text.Encoding.UTF8.GetString(expected.ToArray()), text); // the same rows as the live export by NPI
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Paging_sorting_and_the_full_export_agree()
    {
        await using var db = await SeededAsync();
        var search = Search(db);

        var page1 = await search.SearchAsync(new SearchFilter { State = "NY", PageSize = 2 }, _ct);
        var page2 = await search.SearchAsync(new SearchFilter { State = "NY", PageSize = 2, Page = 2 }, _ct);
        var all = new List<ProviderSummary>();
        await foreach (var item in search.SearchAllAsync(new SearchFilter { State = "NY" }, _ct))
        {
            all.Add(item);
        }

        Assert.Equal(4, page1.TotalCount);
        Assert.Equal(["BAKER, AL", "NUÑEZ, ELENA"], page1.Items.Select(i => i.Name)); // Ñ sorts as N
        Assert.Equal(["O'BRIEN, JOSÉ Q JR", "SMITH, JONES & CO, LLC"], page2.Items.Select(i => i.Name));
        Assert.Equal(page1.Items.Concat(page2.Items).Select(i => i.Npi), all.Select(i => i.Npi));
        Assert.Equal([E, A, B, D], (await search.SearchAsync(new SearchFilter { State = "NY", Sort = "-lastUpdate" }, _ct)).Items.Select(i => i.Npi));
        // B has two NY locations; with only a state filter its primary (Brooklyn) is the one shown and sorted on.
        Assert.Equal(["BROOKLYN", "AMITYVILLE"], (await search.SearchAsync(new SearchFilter { Classification = "Chiropractor", Sort = "-city", State = "NY" }, _ct))
            .Items.Select(i => i.City));
    }

    [Fact]
    public async Task The_projection_excludes_deactivated_npis_and_hashes_child_rows()
    {
        await using var db = await SeededAsync();

        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM provider WHERE npi = @C", new { C }));
        Assert.Equal(3L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM provider_taxonomy WHERE npi = @A", new { A })); // slots 1, 2 and 3
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM provider_other_name WHERE npi = @D", new { D }));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM provider WHERE row_hash IS NULL"));
        Assert.Equal((4, "NPPES_Data_Dissemination_September_2026_V2.zip"),
            (await db.QueryAsync<(int, string)>("SELECT provider_count, monthly_file FROM data_version WHERE id = 1")).Single());

        var builder = new ProjectionBuilder(db.Database, Logger.None, 0.95);
        Assert.False(await builder.IsStaleAsync(_ct));
        var before = await db.QueryAsync<(string, byte[])>("SELECT npi, row_hash FROM provider ORDER BY npi");

        // Change only a secondary location of B, A's endpoint and D's authorized official: exactly their hashes change.
        await db.ExecuteAsync("UPDATE practice_locations SET Provider_Secondary_Practice_Location_Address_Line_1 = '2 New Rd' WHERE NPI = @B", new { B });
        await db.ExecuteAsync("UPDATE endpoints SET Endpoint = 'obrien2@direct.example.org' WHERE NPI = @A", new { A });
        await db.ExecuteAsync("UPDATE npidata SET Authorized_Official_Title_or_Position = 'CEO' WHERE NPI = @D", new { D });
        await builder.BuildAsync(_ct, full: true);
        var after = await db.QueryAsync<(string, byte[])>("SELECT npi, row_hash FROM provider ORDER BY npi");

        var changed = before.Zip(after).Where(p => !p.First.Item2.SequenceEqual(p.Second.Item2)).Select(p => p.First.Item1);
        Assert.Equal([A, B, D], changed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_projection_records_what_changed_since_the_last_one(bool incremental)
    {
        await using var db = await SeededAsync();
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM provider_change")); // the first projection has nothing to compare with

        await db.ExecuteAsync("UPDATE npidata SET Provider_Last_Name_Legal_Name = 'OBRIEN-SMITH', Provider_Business_Practice_Location_Address_Telephone_Number = '6315550000' WHERE NPI = @A", new { A });
        await db.ExecuteAsync("INSERT INTO other_names (NPI, Provider_Other_Organization_Name, Provider_Other_Organization_Name_Type_Code) VALUES (@A, 'OBRIEN CLINIC', '3')", new { A });
        await db.ExecuteAsync("UPDATE npidata SET Provider_Credential_Text = 'MD', Healthcare_Provider_Taxonomy_Code_1 = @Dentist WHERE NPI = @B", new { B, Dentist });
        await db.ExecuteAsync("UPDATE practice_locations SET Provider_Secondary_Practice_Location_Address_Line_1 = '2 New Rd' WHERE NPI = @B", new { B });
        await db.ExecuteAsync("UPDATE npidata SET Provider_First_Line_Business_Practice_Location_Address = '210 Main St' WHERE NPI = @E", new { E });
        await db.ExecuteAsync("UPDATE npidata SET Is_Deactivated = 0 WHERE NPI = @C", new { C });
        await db.ExecuteAsync("UPDATE npidata SET Is_Deactivated = 1 WHERE NPI = @D", new { D });
        if (incremental)
        {
            // As the loads would: the changed NPIs queued, only they rebuilt.
            await db.ExecuteAsync("INSERT INTO projection_pending (npi) VALUES (@A), (@B), (@C), (@D), (@E)", new { A, B, C, D, E });
            await new ProjectionBuilder(db.Database, Logger.None, 0.95) { IncrementalShare = 10 }.BuildAsync(_ct);
        }
        else
        {
            await new ProjectionBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct, full: true);
        }

        Assert.Equal(
            [
                (A, "name", "O'BRIEN, JOSÉ Q", "OBRIEN-SMITH, JOSÉ Q"), (A, "other_name_added", null, "OBRIEN CLINIC"), (A, "phone", null, "6315550000"),
                (B, "credential", "M.D.", "MD"),
                (B, "location_added", null, "2 New Rd, FARMINGDALE, NY 11735"), (B, "location_removed", "300 Conklin St, FARMINGDALE, NY 11735", null),
                (B, "specialty", ChiroSports, Dentist), (B, "taxonomy_added", null, Dentist), (B, "taxonomy_removed", ChiroSports, null),
                (C, "added", null, "CARTER"), (D, "deactivated", "SMITH, JONES & CO, LLC", null),
                (E, "address", "200 Main St, FARMINGDALE, NY 11735", "210 Main St, FARMINGDALE, NY 11735"),
            ],
            await db.QueryAsync<(string, string, string?, string?)>(
                "SELECT npi, change_type, old_value, new_value FROM provider_change ORDER BY npi, change_type"));

        var b = await new ProviderDetailService(db.ConnectionString).GetAsync(B, _ct);
        Assert.Equal(
            [
                ("credential", "M.D.", "MD"), ("location_added", null, "2 New Rd, FARMINGDALE, NY 11735"),
                ("location_removed", "300 Conklin St, FARMINGDALE, NY 11735", null), ("specialty", "Chiropractor – Sports Physician", "Dentist"),
                ("taxonomy_added", null, "Dentist"), ("taxonomy_removed", "Chiropractor – Sports Physician", null),
            ],
            b!.Changes.Select(c => (c.Type, c.OldValue, c.NewValue)).Order());
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), b.Changes[0].RecordedOn);
    }

    [Fact]
    public async Task An_incremental_projection_equals_a_full_rebuild()
    {
        await using var db = await SeededAsync();
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM projection_pending")); // the first build took C's queued flag
        var lastId = await db.ScalarAsync<long>("SELECT MAX(id) FROM provider_location");

        // A renamed, B with another secondary location and endpoint, C reactivated, D deactivated, a new NPI F (a copy of E
        // with its own number); E unchanged and not queued.
        const string F = "1000000053";
        await db.ExecuteAsync(
            """
            SET SESSION innodb_strict_mode = OFF; -- npidata's 330 TEXT columns (CLAUDE.md §6.1)
            UPDATE npidata SET Provider_Last_Name_Legal_Name = 'OBRIEN-SMITH' WHERE NPI = @A;
            UPDATE practice_locations SET Provider_Secondary_Practice_Location_Address_Line_1 = '2 New Rd' WHERE NPI = @B;
            INSERT INTO endpoints (NPI, Endpoint_Type, Endpoint) VALUES (@B, 'FHIR', 'https://b.example.org/fhir');
            UPDATE npidata SET Is_Deactivated = 0, NPI_Reactivation_Date = '02/01/2026' WHERE NPI = @C;
            UPDATE npidata SET Is_Deactivated = 1, NPI_Deactivation_Date = '10/01/2026' WHERE NPI = @D;
            CREATE TEMPORARY TABLE f SELECT * FROM npidata WHERE NPI = @E;
            UPDATE f SET NPI = @F;
            INSERT INTO npidata SELECT * FROM f;
            INSERT INTO projection_pending (npi) VALUES (@A), (@B), (@C), (@D), (@F);
            """, new { A, B, C, D, E, F });

        await new ProjectionBuilder(db.Database, Logger.None, 0.95) { IncrementalShare = 10 }.BuildAsync(_ct);
        var incremental = await ProjectionSnapshotAsync(db);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM projection_pending"));
        // Only the queued NPIs' rows were replaced (new ids after the old ones); E's stayed.
        Assert.True(await db.ScalarAsync<long>("SELECT MIN(id) FROM provider_location WHERE npi IN (@A, @B, @C, @F)", new { A, B, C, F }) > lastId);
        Assert.True(await db.ScalarAsync<long>("SELECT MAX(id) FROM provider_location WHERE npi = @E", new { E }) <= lastId);
        Assert.Equal([C, D], await db.QueryAsync<string>("SELECT npi FROM provider_change WHERE change_type IN ('added', 'deactivated') AND npi <> @F ORDER BY npi", new { F }));

        await new ProjectionBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct, full: true);
        var full = await ProjectionSnapshotAsync(db);
        foreach (var table in ProjectionBuilder.Tables)
        {
            Assert.Equal(full[table], incremental[table]);
        }

        // Nothing queued and no NPPES file since: a build only refreshes data_version.
        var before = await db.ScalarAsync<long>("SELECT MAX(id) FROM provider_location");
        await new ProjectionBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);
        Assert.Equal(before, await db.ScalarAsync<long>("SELECT MAX(id) FROM provider_location"));
        Assert.Equal(full["provider"], (await ProjectionSnapshotAsync(db))["provider"]);

        // An NPPES file loaded since that queued nothing (a monthly with no differences): still nothing rebuilt. A's raw name
        // changes unqueued, so only a full build would show it.
        await db.ExecuteAsync(
            """
            UPDATE npidata SET Provider_Last_Name_Legal_Name = 'UNQUEUED' WHERE NPI = @A;
            INSERT INTO downlog (filename, kind, status, started_at, completed_at) VALUES ('same.zip', 'Monthly', 'Completed', @at, @at);
            """, new { A, at = DateTime.UtcNow.AddMinutes(1) });
        await new ProjectionBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);
        Assert.Equal(before, await db.ScalarAsync<long>("SELECT MAX(id) FROM provider_location"));
        Assert.StartsWith("OBRIEN-SMITH", await db.ScalarAsync<string>("SELECT sort_name FROM provider WHERE npi = @A", new { A }), StringComparison.Ordinal);

        // The same load from before the queue existed (migration 089 applied after it): a full build.
        await db.ExecuteAsync("UPDATE schema_migrations SET applied_at = NOW() + INTERVAL 1 DAY WHERE version = 89");
        await new ProjectionBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);
        Assert.StartsWith("UNQUEUED", await db.ScalarAsync<string>("SELECT sort_name FROM provider WHERE npi = @A", new { A }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Columns_a_monthly_does_not_compare_never_change_the_projection()
    {
        await using var db = await SeededAsync();
        var before = await ProjectionSnapshotAsync(db);

        // Every NPPES column the monthly diff skips gets another value; a full rebuild must give the same projection.
        foreach (var table in new[] { "npidata", "other_names", "practice_locations", "endpoints" })
        {
            var columns = await db.QueryAsync<(string Name, string Type)>(
                """
                SELECT column_name, data_type FROM information_schema.columns
                WHERE table_schema = DATABASE() AND table_name = @table AND extra NOT LIKE '%GENERATED%'
                """, new { table });
            var changes = columns
                .Where(c => c.Name is not ("NPI" or "ID" or "Is_Deactivated") && !ProjectionBuilder.ReadsColumn(c.Name))
                .Select(c => c.Type switch
                {
                    "char" or "varchar" or "text" or "mediumtext" or "longtext" => $"`{c.Name}` = 'Z'",
                    "date" or "datetime" or "timestamp" => $"`{c.Name}` = '2001-01-01'",
                    _ => null,
                })
                .OfType<string>().ToList();
            if (table == "npidata")
            {
                Assert.True(changes.Count >= 20, $"only {changes.Count} unread npidata columns");
            }

            if (changes.Count > 0)
            {
                await db.ExecuteAsync($"UPDATE `{table}` SET {string.Join(", ", changes)}");
            }
        }

        await new ProjectionBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct, full: true);
        var after = await ProjectionSnapshotAsync(db);
        foreach (var table in ProjectionBuilder.Tables)
        {
            Assert.Equal(before[table], after[table]);
        }
    }

    // The tables built on the projection, each updated incrementally after an incremental projection.
    private static readonly string[] BuilderTables =
    [
        "credential_map", "provider_credential", "credential_list", "credential_search", "provider_map_credential", "address_type", "building_search",
        "specialty_code_category", "area_provider_count", "provider_map", "provider_map_specialty", "provider_quality", "quality_search",
        "area_month_stat", "area_month_event", "area_history_location",
    ];

    // sideBySide: as run builds them (speed item 13): credentials, address types and area counts at once, the credentials leaving
    // provider_map_credential to the map built after them. Otherwise one after another, each keeping its own tables current.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_builders_after_an_incremental_projection_equal_a_full_rebuild(bool sideBySide)
    {
        await using var db = await SeededAsync();
        var census = new CensusGeocoder(new HttpClient(new FakeCensus()), "https://census.test/batch", "Public_AR_Current");
        await new AddressGeocoder(db.Database, census, Logger.None, batchSize: 10, parallelism: 1) { Delays = [] }.GeocodePendingAsync(maxBatches: 0, _ct);
        // A FEMA building under every address: A's house, the others offices.
        await db.ExecuteAsync(
            """
            INSERT INTO address_building (addr_key, matched, distance_m, occ_cls, prim_occ, sqft)
            SELECT DISTINCT addr_key, 'inside', 0, IF(npi = @A, 'Residential', 'Commercial'), IF(npi = @A, 'Single Family Dwelling', 'Medical Office/Clinic'), 2000
            FROM provider_location WHERE addr_key IS NOT NULL
            """, new { A });

        // Credentials held by fewer than 2 providers are rare ("Other"), so a new one crosses that limit.
        CredentialBuilder Credentials() => new(db.Database, Logger.None, 0.95, minListed: 2);
        async Task BuildAllAsync(bool full)
        {
            if (sideBySide)
            {
                Assert.True(await BuilderSteps.RunAsync(
                [
                    new("credentials", null, c => Credentials().BuildAsync(c, full, leaveMapCells: true)),
                    new("address types", null, async c => await new AddressTypeBuilder(db.Database, Logger.None, 0.95).BuildAsync(c, full)),
                    new("areas", null, async c => await new AreaCountBuilder(db.Database, Logger.None, 0.95).BuildAsync(c, full)),
                ], parallel: 3, Logger.None, _ct));
                Assert.Equal(full ? 1L : 0L, await db.ScalarAsync<long>(CredentialBuilder.MapCellsOwedSql)); // a full build left them to the map
                await new MapBuilder(db.Database, Logger.None, 0.95) { IncrementalShare = 10 }.BuildAsync(_ct, full);
            }
            else
            {
                await Credentials().BuildAsync(_ct, full);
                await new MapBuilder(db.Database, Logger.None, 0.95) { IncrementalShare = 10 }.BuildAsync(_ct, full);
                await new AddressTypeBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct, full);
                await new AreaCountBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct, full);
            }

            await new QualityBuilder(db.Database, Logger.None, 0.95) { IncrementalShare = 10 }.BuildAsync(_ct, allowIncremental: !full);
            Assert.Equal(await ExpectedMapCellsAsync(db), await MapCellsAsync(db));
            Assert.Equal(0L, await db.ScalarAsync<long>(CredentialBuilder.MapCellsOwedSql));
            if (full)
            {
                // Side by side filled once, by the map; one after another by both (the credentials' fill replaced by the map's).
                Assert.Equal("filled by the map", await db.ScalarAsync<string>("SELECT version FROM reference_data WHERE source = @source",
                    new { source = CredentialBuilder.MapCellsSource }));
            }
            await new AreaHistoryBuilder(db.Database, Logger.None).BuildAsync(_ct, full);
        }

        await BuildAllAsync(full: true);
        string[] sources = [CredentialBuilder.Source, MapBuilder.Source, AddressTypeBuilder.Source, AreaCountBuilder.Source, QualityBuilder.Source];
        async Task<IReadOnlyList<string>> LoadedAtAsync() =>
            await db.QueryAsync<string>("SELECT CONCAT(source, ' ', loaded_at) FROM reference_data WHERE source IN @sources ORDER BY source", new { sources });
        var loadedAt = await LoadedAtAsync();
        Assert.Equal(5, loadedAt.Count);

        // A renamed, with another credential (new, so rare) and moved to E's address, updated on 10/10 (the as-of date moves);
        // B now a dentist without its second office; C reactivated; D deactivated; F a new copy of E; E's address placed again.
        const string F = "1000000053";
        await db.ExecuteAsync(
            """
            SET SESSION innodb_strict_mode = OFF; -- npidata's 330 TEXT columns (CLAUDE.md §6.1)
            UPDATE npidata SET Provider_Last_Name_Legal_Name = 'OBRIEN-SMITH', Provider_Credential_Text = 'D.C., LAC',
              Provider_First_Line_Business_Practice_Location_Address = '200 Main St', Provider_Business_Practice_Location_Address_City_Name = 'FARMINGDALE',
              Provider_Business_Practice_Location_Address_Postal_Code = '11735', Last_Update_Date = '10/10/2026' WHERE NPI = @A;
            UPDATE npidata SET Healthcare_Provider_Taxonomy_Code_1 = @Dentist WHERE NPI = @B;
            DELETE FROM practice_locations WHERE NPI = @B;
            UPDATE npidata SET Is_Deactivated = 0, NPI_Reactivation_Date = '02/01/2026' WHERE NPI = @C;
            UPDATE npidata SET Is_Deactivated = 1, NPI_Deactivation_Date = '10/01/2026' WHERE NPI = @D;
            CREATE TEMPORARY TABLE f SELECT * FROM npidata WHERE NPI = @E;
            UPDATE f SET NPI = @F;
            INSERT INTO npidata SELECT * FROM f;
            INSERT INTO projection_pending (npi) VALUES (@A), (@B), (@C), (@D), (@F);
            INSERT INTO address_point (addr_key, source, lat, lon, matched, `release`, matched_at)
            SELECT addr_key, 'address', 40.7331, -73.4452, '200 MAIN ST', '2026-09-23.1', UTC_TIMESTAMP() FROM provider_location WHERE npi = @E;
            """, new { A, B, C, D, E, F, Dentist });

        await new ProjectionBuilder(db.Database, Logger.None, 0.95) { IncrementalShare = 10 }.BuildAsync(_ct);
        Assert.True(await Credentials().IsStaleAsync(_ct));
        await BuildAllAsync(full: false);
        Assert.Equal(loadedAt, await LoadedAtAsync()); // every builder took its incremental path
        foreach (var table in new[] { "builder_pending", "builder_old_location", "builder_old_taxonomy", "address_pending", "quality_pending" })
        {
            Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT COUNT(*) FROM {table}"));
        }

        Assert.Equal("Other", await db.ScalarAsync<string>("SELECT credential FROM provider_credential WHERE npi = @A AND ord = 100", new { A }));
        var incremental = await ProjectionSnapshotAsync(db, BuilderTables);

        await new ProjectionBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct, full: true);
        await BuildAllAsync(full: true);
        var full = await ProjectionSnapshotAsync(db, BuilderTables);
        foreach (var table in BuilderTables)
        {
            Assert.Equal(full[table], incremental[table]);
        }
    }

    // provider_map_credential as ProjectionSnapshotAsync writes it, and as it should be: every credential of a provider at each of
    // its map points' cells.
    private static async Task<IReadOnlyList<string>> MapCellsAsync(TestDatabase db) =>
        (await ProjectionSnapshotAsync(db, [CredentialBuilder.MapCellsSource]))[CredentialBuilder.MapCellsSource];

    private static Task<IReadOnlyList<string>> ExpectedMapCellsAsync(TestDatabase db) => db.QueryAsync<string>(
        """
        SELECT CONCAT_WS('|', HEX(x.credential), HEX(x.cell), HEX(x.npi)) AS r
        FROM (SELECT DISTINCT c.credential, CAST(FLOOR((m.lat + 90) * 10) * 3600 + FLOOR((m.lon + 180) * 10) AS SIGNED) AS cell, m.npi
              FROM provider_map m JOIN provider_credential c ON c.npi = m.npi) x
        ORDER BY r
        """);

    [Fact]
    public async Task Map_credential_cells_are_kept_by_whichever_builder_runs_last()
    {
        await using var db = await SeededAsync();
        var census = new CensusGeocoder(new HttpClient(new FakeCensus()), "https://census.test/batch", "Public_AR_Current");
        await new AddressGeocoder(db.Database, census, Logger.None, batchSize: 10, parallelism: 1) { Delays = [] }.GeocodePendingAsync(maxBatches: 0, _ct);
        // No row check: the steps below keep changing one provider's credential, so a full build has fewer raw values than the last.
        var credentials = new CredentialBuilder(db.Database, Logger.None, 0);
        var map = new MapBuilder(db.Database, Logger.None, 0) { IncrementalShare = 10 };
        async Task<string?> FilledByAsync() =>
            await db.ScalarAsync<string?>("SELECT version FROM reference_data WHERE source = @source", new { source = CredentialBuilder.MapCellsSource });
        async Task AssertCurrentAsync(string filledBy)
        {
            Assert.Equal(await ExpectedMapCellsAsync(db), await MapCellsAsync(db));
            Assert.Equal(0L, await db.ScalarAsync<long>(CredentialBuilder.MapCellsOwedSql));
            Assert.Equal(filledBy, await FilledByAsync());
        }

        // The map before any credentials, then the credentials command alone: it fills the cells itself.
        await map.BuildAsync(_ct, full: true);
        Assert.Empty(await MapCellsAsync(db));
        await credentials.BuildAsync(_ct, full: true);
        Assert.NotEmpty(await MapCellsAsync(db));
        await AssertCurrentAsync("filled by the credentials");
        Assert.False(await map.IsStaleAsync(_ct));

        // A full credential build that leaves them to the map (run): owed, untouched; a map update then fills them on their own.
        var before = await MapCellsAsync(db);
        await db.ExecuteAsync("UPDATE provider SET credential = 'D.O.' WHERE npi = @B", new { B });
        await credentials.BuildAsync(_ct, full: true, leaveMapCells: true);
        Assert.Equal(1L, await db.ScalarAsync<long>(CredentialBuilder.MapCellsOwedSql));
        Assert.Null(await FilledByAsync());
        Assert.Equal(before, await MapCellsAsync(db));
        Assert.True(await map.IsStaleAsync(_ct));
        await map.BuildAsync(_ct);
        await AssertCurrentAsync("filled on its own");
        Assert.False(await map.IsStaleAsync(_ct));

        // The same before a full map build: filled once, with the new points.
        await db.ExecuteAsync("UPDATE provider SET credential = 'D.C.' WHERE npi = @B", new { B });
        await credentials.BuildAsync(_ct, full: true, leaveMapCells: true);
        await map.BuildAsync(_ct, full: true);
        await AssertCurrentAsync("filled by the map");

        // A map update after geocoding alone (E's address placed at its building): the moved providers' cells follow.
        await db.ExecuteAsync(
            """
            INSERT INTO address_point (addr_key, source, lat, lon, matched, `release`, matched_at)
            SELECT addr_key, 'address', 40.9331, -73.1452, '200 MAIN ST', '2026-09-23.1',
                   (SELECT checked_at FROM reference_data WHERE source = 'provider_map') + INTERVAL 1 SECOND -- after the map's last update, whatever the clock's rounding
            FROM provider_location WHERE npi = @E
            """, new { E });
        Assert.True(await map.IsStaleAsync(_ct));
        await map.BuildAsync(_ct);
        await AssertCurrentAsync("filled by the map");

        // Incremental credentials with no map after them (project): the batch's cells replaced at the points the map has.
        var loadedAt = await db.ScalarAsync<DateTime>("SELECT loaded_at FROM reference_data WHERE source = @source", new { source = CredentialBuilder.Source });
        await db.ExecuteAsync(
            "UPDATE provider SET credential = 'N.P.' WHERE npi = @A; INSERT INTO builder_pending (builder, npi) VALUES (@builder, @A)",
            new { A, builder = BuilderPending.Credentials });
        await credentials.BuildAsync(_ct);
        Assert.Equal(loadedAt, await db.ScalarAsync<DateTime>("SELECT loaded_at FROM reference_data WHERE source = @source", new { source = CredentialBuilder.Source }));
        await AssertCurrentAsync("filled by the map");

        // Incremental credentials before the map (run): no cells for the NPIs the map is about to update; the map adds them.
        await db.ExecuteAsync(
            "UPDATE provider SET credential = 'P.A.' WHERE npi = @A; INSERT INTO builder_pending (builder, npi) VALUES (@credentials, @A), (@map, @A)",
            new { A, credentials = BuilderPending.Credentials, map = BuilderPending.Map });
        await credentials.BuildAsync(_ct, leaveMapCells: true);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM provider_map_credential WHERE npi = @A", new { A }));
        await map.BuildAsync(_ct);
        await AssertCurrentAsync("filled by the map");
        Assert.NotEqual(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM provider_map_credential WHERE npi = @A", new { A }));

        // Incremental credentials while the cells are owed and no map follows: refilled in full at the end.
        await credentials.BuildAsync(_ct, full: true, leaveMapCells: true);
        await db.ExecuteAsync(
            "UPDATE provider SET credential = 'R.N.' WHERE npi = @A; INSERT INTO builder_pending (builder, npi) VALUES (@builder, @A)",
            new { A, builder = BuilderPending.Credentials });
        await credentials.BuildAsync(_ct);
        await AssertCurrentAsync("filled on its own");
    }

    // Speed item 17: a map update of more than MapBuilder.IncrementalShare of the providers (the queued NPIs plus those at addresses
    // geocoded or placed since) is a full build instead; either way the tables equal a full build's, and the follow-up state too.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_large_map_update_is_a_full_build_with_the_same_result(bool large)
    {
        await using var db = await SeededAsync();
        var census = new CensusGeocoder(new HttpClient(new FakeCensus()), "https://census.test/batch", "Public_AR_Current");
        await new AddressGeocoder(db.Database, census, Logger.None, batchSize: 10, parallelism: 1) { Delays = [] }.GeocodePendingAsync(maxBatches: 0, _ct);
        var credentials = new CredentialBuilder(db.Database, Logger.None, 0.95);
        await credentials.BuildAsync(_ct, full: true);
        await new MapBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct, full: true);

        // A queued; E's address placed at its building after the map's last update; the credentials rebuilt in full, leaving their
        // map cells to the map (as in run). A marker load time shows whether the map was built in full.
        await db.ExecuteAsync(
            """
            INSERT INTO builder_pending (builder, npi) VALUES (@map, @A);
            UPDATE address_geocode SET geocoded_at = geocoded_at - INTERVAL 1 DAY; -- well before the map (DATETIME rounds to the second)
            INSERT INTO address_point (addr_key, source, lat, lon, matched, `release`, matched_at)
            SELECT addr_key, 'address', 40.9331, -73.1452, '200 MAIN ST', '2026-09-23.1',
                   (SELECT checked_at FROM reference_data WHERE source = 'provider_map') -- placed at the last update counts too
            FROM provider_location WHERE npi = @E;
            UPDATE reference_data SET loaded_at = '2099-01-01' WHERE source = 'provider_map';
            """, new { map = BuilderPending.Map, A, E });
        await credentials.BuildAsync(_ct, full: true, leaveMapCells: true);
        await db.ExecuteAsync("DELETE FROM quality_pending");
        var checkedAt = await db.ScalarAsync<DateTime>("SELECT checked_at FROM reference_data WHERE source = 'provider_map'");

        // The batch: A, plus every other provider at E's address. The limit sits just above it, or just below.
        var atAddress = await db.ScalarAsync<long>(
            """
            SELECT COUNT(DISTINCT l.npi) FROM provider_location l
            JOIN (SELECT addr_key FROM provider_location WHERE npi = @E) a ON a.addr_key = l.addr_key WHERE l.npi <> @A
            """, new { A, E });
        Assert.True(atAddress >= 1);
        var providers = await db.ScalarAsync<long>("SELECT COUNT(*) FROM provider");
        var map = new MapBuilder(db.Database, Logger.None, 0.95) { IncrementalShare = (1 + atAddress + (large ? -0.5 : 0.5)) / providers };
        Assert.True(await map.IsStaleAsync(_ct));
        await map.BuildAsync(_ct);

        Assert.Equal(large, await db.ScalarAsync<DateTime>("SELECT loaded_at FROM reference_data WHERE source = 'provider_map'") != new DateTime(2099, 1, 1));
        Assert.True(await db.ScalarAsync<DateTime>("SELECT checked_at FROM reference_data WHERE source = 'provider_map'") >= checkedAt);
        Assert.False(await map.IsStaleAsync(_ct));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM builder_pending WHERE builder = @map", new { map = BuilderPending.Map }));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'map_batch'"));
        // An update queues its providers for the record quality; a full build makes it relabel everyone instead.
        Assert.Equal(large ? 0L : 1 + atAddress, await db.ScalarAsync<long>("SELECT COUNT(*) FROM quality_pending"));
        Assert.Equal(await ExpectedMapCellsAsync(db), await MapCellsAsync(db));
        Assert.Equal(0L, await db.ScalarAsync<long>(CredentialBuilder.MapCellsOwedSql));
        Assert.Equal(large ? "filled by the map" : "filled on its own",
            await db.ScalarAsync<string>("SELECT version FROM reference_data WHERE source = @source", new { source = CredentialBuilder.MapCellsSource }));
        string[] tables = ["provider_map", "provider_map_specialty", "provider_map_credential"];
        var built = await ProjectionSnapshotAsync(db, tables);
        Assert.Equal([(40.9331, -73.1452)], await db.QueryAsync<(double, double)>("SELECT lat, lon FROM provider_map WHERE npi = @E", new { E }));

        await new MapBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct, full: true);
        var full = await ProjectionSnapshotAsync(db, tables);
        foreach (var table in tables)
        {
            Assert.Equal(full[table], built[table]);
        }
    }

    // A point saved just after a map build is placed by the next update. The build kept its end in C# as checked_at, which
    // DATETIME rounded up to the next second when its fraction was .5 or more, while the point's matched_at (MySQL's
    // UTC_TIMESTAMP()) dropped the fraction: the point looked older than the build, so the update skipped it and kept the Census
    // point (a full rebuild didn't; the builders test failed on CI once). The build starts just past the middle of a second so it
    // ends there too, in a database this small.
    [Fact]
    public async Task A_point_placed_right_after_a_map_build_is_placed_by_the_next_update()
    {
        await using var db = await SeededAsync();
        var census = new CensusGeocoder(new HttpClient(new FakeCensus()), "https://census.test/batch", "Public_AR_Current");
        await new AddressGeocoder(db.Database, census, Logger.None, batchSize: 10, parallelism: 1) { Delays = [] }.GeocodePendingAsync(maxBatches: 0, _ct);
        await new CredentialBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct, full: true);
        var map = new MapBuilder(db.Database, Logger.None, 0.95) { IncrementalShare = 10 };

        foreach (var lon in new[] { -73.1452, -73.1552 })
        {
            while (await db.ScalarAsync<double>("SELECT MICROSECOND(UTC_TIMESTAMP(6)) / 1e6") is < 0.55 or > 0.65)
            {
                await Task.Delay(10, _ct);
            }

            await map.BuildAsync(_ct, full: true);
            await db.ExecuteAsync(
                """
                DELETE FROM address_point;
                INSERT INTO address_point (addr_key, source, lat, lon, matched, `release`, matched_at)
                SELECT addr_key, 'address', 40.9331, @lon, '200 MAIN ST', '2026-09-23.1', UTC_TIMESTAMP() FROM provider_location WHERE npi = @E;
                """, new { E, lon });
            Assert.True(await db.ScalarAsync<DateTime>("SELECT checked_at FROM reference_data WHERE source = 'provider_map'")
                <= await db.ScalarAsync<DateTime>("SELECT MAX(matched_at) FROM address_point"));
            Assert.True(await map.IsStaleAsync(_ct));

            await map.BuildAsync(_ct);
            Assert.Equal([(40.9331, lon)], await db.QueryAsync<(double, double)>("SELECT lat, lon FROM provider_map WHERE npi = @E", new { E }));
        }
    }

    // Every row of each projection table (or the given ones) as text (all columns but AUTO_INCREMENT ids), sorted.
    private static async Task<Dictionary<string, IReadOnlyList<string>>> ProjectionSnapshotAsync(TestDatabase db, IEnumerable<string>? tables = null)
    {
        var snapshot = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var table in tables ?? ProjectionBuilder.Tables)
        {
            var columns = await db.QueryAsync<string>(
                """
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = DATABASE() AND table_name = @table AND extra NOT LIKE '%auto_increment%' ORDER BY ordinal_position
                """, new { table });
            snapshot[table] = await db.QueryAsync<string>(
                $"SELECT CONCAT_WS('|', {string.Join(", ", columns.Select(c => $"IFNULL(HEX(`{c}`), '-')"))}) AS r FROM `{table}` ORDER BY r");
        }

        return snapshot;
    }

    [Fact]
    public async Task Groups_facilities_and_an_organizations_clinicians()
    {
        await using var db = await SeededAsync();
        // D (a dental organization) is enrolled as group 9000000001; A and B bill through it, C (deactivated) too; E is listed in Care
        // Compare only. A is also affiliated with hospital 330045, which bills under D.
        await db.ExecuteAsync(
            """
            INSERT INTO medicare_enrollment (npi, enrollment_id, pac_id, provider_type, state, org_name) VALUES
              (@D, 'O20200101000001', '9000000001', 'PART B SUPPLIER - CLINIC/GROUP PRACTICE', 'NY', 'SMITH JONES AND CO LLC');
            INSERT INTO medicare_reassignment (group_pac_id, group_enrollment_id, group_name, group_state, record_type, npi, specialty) VALUES
              ('9000000001', 'O20200101000001', 'Smith Jones And Co Llc', 'NY', 'Reassignment', @A, 'Chiropractic'),
              ('9000000001', 'O20200101000001', 'Smith Jones And Co Llc', 'NY', 'Reassignment', @B, 'Chiropractic'),
              ('9000000001', 'O20200101000001', 'Smith Jones And Co Llc', 'NY', 'Reassignment', @C, 'Chiropractic');
            INSERT INTO cc_group (npi, org_pac_id, group_name, members, accepts_assignment) VALUES (@E, '9000000001', 'SMITH JONES & CO', 4, 1);
            INSERT INTO cc_facility_affiliation (npi, facility_type, ccn) VALUES (@A, 'Hospital', '330045');
            INSERT INTO cms_hospital (ccn, name, address, city, state, zip, hospital_type, emergency_services, overall_rating) VALUES
              ('330045', 'GOOD SAMARITAN HOSPITAL', '1000 MONTAUK HWY', 'WEST ISLIP', 'NY', '11795', 'Acute Care Hospitals', 1, 3);
            INSERT INTO cms_facility_npi (ccn, npi, kind) VALUES ('330045', @D, 'hospital');
            """, new { A, B, C, D, E });
        var organizations = new OrganizationService(db.ConnectionString);

        var group = (await organizations.GetGroupAsync("9000000001", 1, _ct))!;
        Assert.Equal(("SMITH JONES & CO", "NY", true), (group.Name, group.State, group.AcceptsMedicare));   // Care Compare's name first
        Assert.Equal([B, E, A], group.Clinicians.Page.Select(c => c.Npi));                                // active only, by name: BAKER, NUÑEZ, O'BRIEN
        Assert.Equal(3, group.Clinicians.Total);
        Assert.Contains(group.Clinicians.BySpecialty, s => s is { Specialty: "Chiropractor", Clinicians: 2 });
        Assert.Equal([D], group.Organizations.Select(o => o.Npi));
        Assert.Null(await organizations.GetGroupAsync("9999999999", 1, _ct));
        Assert.Null(await organizations.GetGroupAsync("12", 1, _ct));

        Assert.Equal([new OrganizationGroup("9000000001", "SMITH JONES & CO", 3)], await organizations.GetGroupsForNpiAsync(D, _ct));
        Assert.Equal(["9000000001"], (await organizations.GetGroupsForClinicianAsync(A, _ct)).Select(g => g.PacId));
        Assert.Equal([B, E, A], await organizations.GetClinicianNpisAsync("9000000001", facility: false, _ct));

        var hospital = (await organizations.GetFacilityAsync("330045", 1, _ct))!;
        Assert.Equal(("hospital", "GOOD SAMARITAN HOSPITAL", "1000 MONTAUK HWY", "11795"), (hospital.Facility.Kind, hospital.Facility.Name, hospital.Address, hospital.Zip));
        Assert.Equal([A], hospital.Clinicians.Page.Select(c => c.Npi));
        Assert.Equal([D], hospital.BillingNpis.Select(o => o.Npi));
        Assert.Null(await organizations.GetFacilityAsync("999999", 1, _ct));
        Assert.Equal([A], await organizations.GetClinicianNpisAsync("330045", facility: true, _ct));
    }

    [Fact]
    public async Task Facility_and_group_directories()
    {
        await using var db = await SeededAsync();
        // Facilities: a hospital in POS and Care Compare, an FQHC (POS only, billing under D and the deactivated C), a closed hospital,
        // and Care Compare listings without a POS row. Groups: 9000000001 (A, B, E; C is deactivated), 9000000003 (A, Care Compare
        // only) and 9000000002 (only C: left out). D is the only organization NPI.
        await db.ExecuteAsync(
            """
            INSERT INTO cms_pos_facility (ccn, name, category, address, city, state, zip5, active, termination_code, termination_date) VALUES
              ('330045', 'GOOD SAMARITAN HOSP', '01', '1000 MONTAUK HWY', 'WEST ISLIP', 'NY', '11795', 1, '00', NULL),
              ('331850', 'BROOKLYN COMMUNITY HEALTH', '20', '5 ATLANTIC AVE', 'BROOKLYN', 'NY', '11201', 1, NULL, NULL),
              ('330888', 'OLD NAME HOSPITAL', '01', '10 COURT ST', 'BROOKLYN', 'NY', '11201', 0, '01', '2020-01-01');
            INSERT INTO cms_hospital (ccn, name, address, city, state, zip, hospital_type, emergency_services, overall_rating) VALUES
              ('330045', 'GOOD SAMARITAN HOSPITAL', '1000 MONTAUK HWY', 'WEST ISLIP', 'NY', '11795', 'Acute Care Hospitals', 1, 3);
            INSERT INTO cms_nursing_home (ccn, name, city, state, zip, certified_beds, overall_rating) VALUES ('335002', 'SHORE VIEW CARE', 'AMITYVILLE', 'NY', '11701', 120, 2);
            INSERT INTO cms_home_health (ccn, name, city, state, zip, quality_rating) VALUES ('337001', 'HOME CARE OF LONG ISLAND', 'HAUPPAUGE', 'NY', '11788', 4.5);
            INSERT INTO cms_hospice (ccn, name, city, state, zip, family_rating) VALUES ('331501', 'GOOD SHEPHERD HOSPICE', 'MEDFORD', 'NY', '11763', NULL);
            INSERT INTO cms_facility_npi (ccn, npi, kind) VALUES ('330045', @D, 'hospital'), ('331850', @D, 'fqhc'), ('331850', @C, 'fqhc');

            INSERT INTO medicare_enrollment (npi, enrollment_id, pac_id, provider_type, state, org_name) VALUES
              (@D, 'O20200101000001', '9000000001', 'PART B SUPPLIER - CLINIC/GROUP PRACTICE', 'NY', 'SMITH JONES AND CO LLC'),
              (@A, 'I20200101000001', '1111111111', 'CHIROPRACTIC', 'NY', NULL);
            INSERT INTO medicare_reassignment (group_pac_id, group_enrollment_id, group_name, group_state, record_type, npi, specialty) VALUES
              ('9000000001', 'O20200101000001', 'Smith Jones And Co Llc', 'NY', 'Reassignment', @A, 'Chiropractic'),
              ('9000000001', 'O20200101000001', 'Smith Jones And Co Llc', 'NY', 'Reassignment', @B, 'Chiropractic'),
              ('9000000001', 'O20200101000001', 'Smith Jones And Co Llc', 'NY', 'Reassignment', @C, 'Chiropractic'),
              ('9000000002', 'O20200101000002', 'Tiny Group', 'NY', 'Reassignment', @C, 'Chiropractic');
            INSERT INTO cc_group (npi, org_pac_id, group_name, members, accepts_assignment) VALUES
              (@E, '9000000001', 'SMITH JONES & CO', 4, 1), (@A, '9000000003', 'ISLAND SPINE', 1, 0);
            INSERT INTO cc_mips (npi, program_year, source, org_pac_id, facility_name, final_score) VALUES
              (@A, 2024, 'group', '9000000001', 'SMITH JONES', 88.5), (@B, 2024, 'group', '9000000001', 'SMITH JONES', 88.5),
              (@A, 2024, 'individual', NULL, NULL, 70);
            INSERT INTO provider_quality (npi, label, reasons, activity) VALUES (@D, 2, 0, 0);
            INSERT INTO medicare_utilization (npi, data_year, payment_amount, beneficiaries) VALUES (@D, 2024, 12345.5, 40), (@A, 2024, 999, 10);
            INSERT INTO facility_ownership_change (kind, chow_type_code, effective_date, buyer_ccn, seller_ccn) VALUES
              ('hospital', 'CH', '2018-06-01', '330045', '330045'), ('hospital', 'CH', @changed, '330045', '330045'),
              ('snf', 'CH', '2017-01-01', '335002', '335002');
            INSERT INTO nh_penalty (ccn, penalty_date, penalty_type, fine_amount, denial_start, denial_days) VALUES
              ('335002', @fined, 'Fine', 182968, NULL, NULL), ('335002', @fined, 'Payment Denial', NULL, @fined, 30);
            """, new { A, B, C, D, E, changed = DateTime.UtcNow.Date.AddMonths(-10), fined = DateTime.UtcNow.Date.AddMonths(-3) });

        // Before the loader's first build the site says the lists are being prepared (and never aggregates the sources itself).
        using (var early = new FacilityDirectory(db.ConnectionString))
        using (var earlyGroups = new OrganizationDirectory(db.ConnectionString))
        {
            Assert.False(await early.IsReadyAsync(_ct));
            Assert.False(await earlyGroups.IsReadyAsync(_ct));
            await Assert.ThrowsAsync<DirectoryNotReadyException>(() => early.SearchAsync(new FacilityFilter(), _ct));
            await Assert.ThrowsAsync<DirectoryNotReadyException>(() => earlyGroups.GetOverviewAsync(_ct));
        }

        var builder = new DirectoryBuilder(db.Database, Logger.None, 0.95);
        Assert.True(await builder.IsStaleAsync(_ct));
        var built = await builder.BuildAsync(_ct);
        Assert.Equal((6L, 2L, 1L, 1L), (built["directory_facility"], built["directory_group"], built["directory_org_count"], built["directory_org_billing"]));
        Assert.False(await builder.IsStaleAsync(_ct));

        // The table holds exactly what merging the sources gives (what the site computed in memory before the table existed).
        await using (var connection = await db.Database.OpenAsync(_ct))
        {
            using var fromTable = new FacilityDirectory(db.ConnectionString);
            var all = new List<FacilitySummary>();
            await foreach (var f in fromTable.AllAsync(new FacilityFilter { Status = "all" }, _ct))
            {
                all.Add(f);
            }

            Assert.Equal((await FacilityDirectory.ReadSourcesAsync(connection, _ct)).OrderBy(f => f.Ccn, StringComparer.Ordinal), all.OrderBy(f => f.Ccn, StringComparer.Ordinal));
        }

        using var facilities = new FacilityDirectory(db.ConnectionString);
        var active = await facilities.SearchAsync(new FacilityFilter(), _ct);
        Assert.Equal(["331850", "330045", "331501", "337001", "335002"], active.Items.Select(f => f.Ccn));
        var fqhc = active.Items[0];
        Assert.Equal((FacilityTypes.Fqhc, 1, 40.694), (fqhc.Type, fqhc.BillingNpis, fqhc.Latitude));        // C is deactivated
        var hospital = active.Items[1];
        Assert.Equal(("GOOD SAMARITAN HOSPITAL", "Acute care hospital", 3.0, (double?)null), (hospital.Name, hospital.Kind, hospital.Rating, hospital.Latitude));
        Assert.Equal((120, 2.0), (active.Items[4].Beds, active.Items[4].Rating));
        Assert.Equal(4.5, active.Items[3].Rating);
        // The newest change of ownership and the penalties per CCN, for the list's filters.
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow.Date.AddMonths(-10)), hospital.LastOwnershipChange);
        Assert.Equal((2, 182968.0, DateOnly.FromDateTime(DateTime.UtcNow.Date.AddMonths(-3)), new DateOnly(2017, 1, 1)),
            (active.Items[4].Penalties, active.Items[4].Fines, active.Items[4].LastPenalty, active.Items[4].LastOwnershipChange));
        Assert.Equal(["330045"], (await facilities.SearchAsync(new FacilityFilter { OwnershipChangedWithinYears = 1 }, _ct)).Items.Select(f => f.Ccn));
        Assert.Equal(["335002"], (await facilities.SearchAsync(new FacilityFilter { HasPenalties = true }, _ct)).Items.Select(f => f.Ccn));
        var closed = Assert.Single((await facilities.SearchAsync(new FacilityFilter { Status = "closed" }, _ct)).Items);
        Assert.Equal(("330888", new DateOnly(2020, 1, 1), "Closed or merged"), (closed.Ccn, closed.TerminationDate, closed.TerminationReason));
        var overview = await facilities.GetOverviewAsync(_ct);
        Assert.Equal((5, 1), (overview.Active, overview.Closed));
        Assert.Equal([0, 1, 0, 0, 0], overview.Ratings.Single(r => r.Type == FacilityTypes.NursingHome).ByStars);

        using var organizations = new OrganizationDirectory(db.ConnectionString);
        var groups = await organizations.SearchGroupsAsync(new GroupFilter(), _ct);
        Assert.Equal([new GroupSummary("9000000001", "SMITH JONES & CO", "NY", 3, 1, true, 88.5, 2024), new GroupSummary("9000000003", "ISLAND SPINE", null, 1, 0, false, null, null)],
            groups.Items);
        Assert.Equal(["9000000001", "9000000003"], (await organizations.SearchGroupsAsync(new GroupFilter { Q = A }, _ct)).Items.Select(g => g.PacId));
        Assert.Equal(["9000000003"], (await organizations.SearchGroupsAsync(new GroupFilter { Q = "spine" }, _ct)).Items.Select(g => g.PacId));
        var orgs = await organizations.GetOverviewAsync(_ct);
        Assert.Equal((2, 1, 1), (orgs.Groups, orgs.GroupsWithMips, orgs.Organizations));
        Assert.Equal(("Dentists", 1, 1), orgs.Categories.Select(c => (c.Category, c.Organizations, c.Check)).Single());
        Assert.Equal(["NY"], orgs.States);
        Assert.Equal((1, 12345.5, 2024), (orgs.BillingOrganizations, orgs.BillingTotal, orgs.BillingYear));
        Assert.Equal((D, "SMITH, JONES & CO, LLC", "BROOKLYN"), orgs.TopBilling.Select(b => (b.Npi, b.Name, b.City)).Single());
        Assert.Empty(await organizations.GetCategoriesAsync("CA", _ct));

        // Facility pages for facilities only the Provider of Services file lists.
        var service = new OrganizationService(db.ConnectionString);
        var page = (await service.GetFacilityAsync("331850", 1, _ct))!;
        Assert.True(page.CertificationOnly);
        Assert.Equal(("fqhc", "BROOKLYN COMMUNITY HEALTH", "5 ATLANTIC AVE", "11201", 40.694), (page.Facility.Kind, page.Facility.Name, page.Address, page.Zip, page.Latitude));
        Assert.Equal([D], page.BillingNpis.Select(n => n.Npi));
        Assert.Equal(new FacilityCertification(true, null, null), page.Certification);
        var gone = (await service.GetFacilityAsync("330888", 1, _ct))!;
        Assert.Equal(("hospital", "Short-term hospital"), (gone.Facility.Kind, gone.Facility.Type));
        Assert.Equal(new FacilityCertification(false, new DateOnly(2020, 1, 1), "Closed or merged"), gone.Certification);
        var listed = (await service.GetFacilityAsync("330045", 1, _ct))!;
        Assert.False(listed.CertificationOnly);
        Assert.Equal(new FacilityCertification(true, null, null), listed.Certification);
    }

    [Fact]
    public async Task Dialysis_surgery_rehabilitation_and_long_term_care_facilities_and_hospital_measures()
    {
        await using var db = await SeededAsync();
        // Step 3.6 part 2: a dialysis facility (billing under D in the dialysis facility report), a surgery center (under D and under no
        // valid NPI, with fewer cases), a hospital rehabilitation unit and a long-term care hospital, and a hospital with measures and
        // Medicare volume. A is affiliated with the dialysis facility and the long-term care hospital.
        await db.ExecuteAsync(
            """
            INSERT INTO cms_dialysis (ccn, name, address, city, state, zip, phone, ownership, chain, stations, in_center, peritoneal, home_hemodialysis,
                                      five_star, five_star_period, survey_rating, survival, survival_period, hospitalization, hospitalization_period) VALUES
              ('332301', 'BROOKLYN KIDNEY CENTER', '1 MAIN ST', 'BROOKLYN', 'NY', '11201', '(718) 555-0100', 'Profit', 'DaVita', 28, 1, 0, 1,
               4, '01Jan2021-31Dec2024', 3, 'Better than Expected', '01Jan2021-31Dec2024', 'Worse than Expected', '01Jan2024-31Dec2024');
            INSERT INTO cms_dialysis_npi (ccn, npi) VALUES ('332301', @D);
            INSERT INTO cms_asc (ccn, npi, name, city, state, zip, data_year, asc9_rate, asc12_cases, asc12_category, asc12_rate, asc17_cases, asc17_category,
                                 asc17_rate, asc18_category) VALUES
              ('33C0001234', @D, 'ISLAND SURGERY CENTER', 'HAUPPAUGE', 'NY', '11788', 2024, 71.67, 1269, 'No Different Than the National Rate', 11.9,
               145, 'Better Than the National Rate', 1.6, 'Number of Cases Too Small'),
              ('33C0001234', '', 'ISLAND SURGERY CENTER', 'HAUPPAUGE', 'NY', '11788', 2024, 50, 20, 'No Different Than the National Rate', 14.2, NULL, NULL,
               NULL, NULL),
              ('NATION', '', 'National', NULL, NULL, NULL, 2024, 76.95, NULL, NULL, 9.9, NULL, NULL, 2.4, NULL);
            INSERT INTO cms_post_acute (ccn, kind, name, address, city, state, zip, phone, ownership, certified_since, beds) VALUES
              ('33T045', 'rehab', 'SOUTH SHORE REHABILITATION UNIT', '1000 MONTAUK HWY', 'WEST ISLIP', 'NY', '11795', '(631) 555-0102', 'Non-profit', '1983-10-01', NULL),
              ('332001', 'ltch', 'LONG ISLAND SPECIALTY HOSPITAL', '30 MAIN ST', 'MINEOLA', 'NY', '11501', '(516) 555-0104', 'For profit', '1993-10-01', 40);
            INSERT INTO cms_post_acute_measure (ccn, measure_id, score, compared, cases, start_date, end_date) VALUES
              ('33T045', 'I_017_01', 9.88, 'No Different than the National Rate', 1458, '2023-10-01', '2025-09-30'),
              ('33T045', 'I_006_01', NULL, 'Not Available', NULL, '2024-10-01', '2025-09-30'),
              ('332001', 'L_019_01', 1.04, NULL, 210, '2022-10-01', '2024-09-30'),
              ('NATION', 'I_017_01', 9.43, NULL, NULL, '2023-10-01', '2025-09-30'), ('NATION', 'L_019_01', 1, NULL, NULL, '2022-10-01', '2024-09-30');
            INSERT INTO cms_hospital (ccn, name, address, city, state, zip, hospital_type, emergency_services, overall_rating) VALUES
              ('330045', 'GOOD SAMARITAN HOSPITAL', '1000 MONTAUK HWY', 'WEST ISLIP', 'NY', '11795', 'Acute Care Hospitals', 1, 3);
            INSERT INTO cms_facility_npi (ccn, npi, kind) VALUES ('330045', @D, 'hospital');
            INSERT INTO cms_hospital_measure (ccn, measure_id, score, compared, cases, start_date, end_date) VALUES
              ('330045', 'MORT_30_AMI', 13.5, 'No Different Than the National Rate', 234, '2022-07-01', '2025-06-30'),
              ('330045', 'MORT_30_HF', NULL, 'Not Available', NULL, '2022-07-01', '2025-06-30'),
              ('330045', 'SEP_1', 60, NULL, 285, '2024-10-01', '2025-09-30'),
              ('NATION', 'MORT_30_AMI', 11.9, NULL, NULL, '2022-07-01', '2025-06-30'), ('NATION', 'MSPB_1', 1, NULL, NULL, '2024-01-01', '2024-12-31');
            INSERT INTO cms_hospital_volume (ccn, inpatient_year, discharges, beneficiaries, covered_days, medicare_payments, total_payments, outpatient_year,
                                             outpatient_services, outpatient_payments) VALUES
              ('330045', 2024, 4299, 3177, 23739, 52228590, 65257547, 2024, 230, 281012.5);
            INSERT INTO cms_hospital_service (ccn, setting, service_rank, code, description, cases, beneficiaries, avg_medicare_payment) VALUES
              ('330045', 'I', 1, '470', 'MAJOR HIP AND KNEE JOINT REPLACEMENT', 300, NULL, 12000.5), ('330045', 'I', 2, '871', 'SEPTICEMIA', 250, NULL, 13500.25),
              ('330045', 'O', 1, '5072', 'Level 2 Excision/ Biopsy/ Incision and Drainage', 188, 175, 1080.5);
            INSERT INTO cc_clinician (npi, primary_specialty, accepts_assignment, telehealth) VALUES (@A, 'CHIROPRACTIC', 1, 0);
            INSERT INTO cc_facility_affiliation (npi, facility_type, ccn) VALUES (@A, 'Dialysis facility', '332301'), (@A, 'Long-term care hospital', '332001');
            """, new { A, D });

        // The directory: each new type is a type of its own, with the dialysis five-star rating and the NPIs the report and the file name.
        var built = await new DirectoryBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);
        Assert.Equal(5L, built["directory_facility"]);
        using var facilities = new FacilityDirectory(db.ConnectionString);
        var listed = (await facilities.SearchAsync(new FacilityFilter(), _ct)).Items;
        Assert.Equal(
            [("332301", FacilityTypes.Dialysis, (string?)null, (double?)4, 1, (int?)null), ("330045", FacilityTypes.Hospital, "Acute care hospital", 3, 1, null),
             ("33C0001234", FacilityTypes.SurgeryCenter, null, null, 1, null), ("332001", FacilityTypes.LongTermCare, null, null, 0, 40),
             ("33T045", FacilityTypes.Rehab, "Rehabilitation unit of a hospital", null, 0, null)],
            listed.Select(f => (f.Ccn, f.Type, f.Kind, f.Rating, f.BillingNpis, f.Beds)));
        Assert.Equal(["332301"], (await facilities.SearchAsync(new FacilityFilter { Type = FacilityTypes.Dialysis }, _ct)).Items.Select(f => f.Ccn));
        Assert.Equal(("Profit", "(718) 555-0100", "1 MAIN ST"), (listed[0].Ownership, listed[0].Phone, listed[0].Address));
        Assert.Contains((await facilities.GetOverviewAsync(_ct)).Ratings, r => r.Type == FacilityTypes.Dialysis && r.ByStars[3] == 1);

        var service = new OrganizationService(db.ConnectionString);
        var dialysis = (await service.GetFacilityAsync("332301", 1, _ct))!;
        Assert.Equal((FacilityTypes.Dialysis, "BROOKLYN KIDNEY CENTER", "1 MAIN ST", "11201", (int?)4, (int?)3),
            (dialysis.Facility.Kind, dialysis.Facility.Name, dialysis.Address, dialysis.Zip, dialysis.Facility.OverallRating, dialysis.Facility.PatientSurveyRating));
        Assert.Equal(new DialysisFacts(28, "Profit", "DaVita", true, false, true, 4, new DateOnly(2021, 1, 1), new DateOnly(2024, 12, 31), 3), dialysis.Dialysis);
        Assert.Equal([("hospitalization", MeasureComparisons.Worse), ("survival", MeasureComparisons.Better)], dialysis.Measures.Select(m => (m.Id, m.Comparison)));
        Assert.Equal([D], dialysis.BillingNpis.Select(n => n.Npi));
        Assert.Equal([A], dialysis.Clinicians.Page.Select(c => c.Npi));
        Assert.Null(dialysis.MedicareVolume);

        // The surgery center: the row with the most cases, against the national rates; "too few cases" is a comparison of its own.
        var asc = (await service.GetFacilityAsync("33C0001234", 1, _ct))!;
        Assert.Equal((FacilityTypes.SurgeryCenter, "ISLAND SURGERY CENTER", "11788"), (asc.Facility.Kind, asc.Facility.Name, asc.Zip));
        Assert.Equal([D], asc.BillingNpis.Select(n => n.Npi));
        var asc12 = asc.Measures.Single(m => m.Id == "ASC_12");
        Assert.Equal(((double?)11.9, (double?)9.9, MeasureComparisons.Same, (int?)1269, new DateOnly(2022, 1, 1), new DateOnly(2024, 12, 31)),
            (asc12.Value, asc12.National, asc12.Comparison, asc12.Cases, asc12.From!.Value, asc12.To!.Value));
        Assert.Equal(MeasureComparisons.Better, asc.Measures.Single(m => m.Id == "ASC_17").Comparison);
        Assert.Equal(MeasureComparisons.TooFew, asc.Measures.Single(m => m.Id == "ASC_18").Comparison);
        Assert.Equal(((double?)71.67, (double?)76.95), asc.Measures.Where(m => m.Id == "ASC_9").Select(m => (m.Value, m.National)).Single());
        Assert.DoesNotContain(asc.Measures, m => m.Id is "ASC_13" or "ASC_19");

        // Rehabilitation and long-term care: their own measures (a measure with neither a value nor a comparison is left out).
        var rehab = (await service.GetFacilityAsync("33T045", 1, _ct))!;
        Assert.Equal((FacilityTypes.Rehab, new DateOnly(1983, 10, 1)), (rehab.Facility.Kind, rehab.CertifiedSince));
        var readmissions = Assert.Single(rehab.Measures);
        Assert.Equal(("I_017_01", (double?)9.88, (double?)9.43, MeasureComparisons.Same, (int?)1458, new DateOnly(2025, 9, 30)),
            (readmissions.Id, readmissions.Value, readmissions.National, readmissions.Comparison, readmissions.Cases, readmissions.To!.Value));
        var ltch = (await service.GetFacilityAsync("332001", 1, _ct))!;
        Assert.Equal((FacilityTypes.LongTermCare, (int?)40, "MINEOLA"), (ltch.Facility.Kind, ltch.Facility.CertifiedBeds, ltch.Facility.City));
        Assert.Equal([("L_019_01", (double?)1.04, (double?)1)], ltch.Measures.Select(m => (m.Id, m.Value, m.National)));

        // The hospital: its measures with the national values, and its Medicare volume.
        var hospital = (await service.GetFacilityAsync("330045", 1, _ct))!;
        Assert.Equal([("MORT_30_AMI", (double?)13.5, (double?)11.9, (int?)234), ("SEP_1", 60, null, 285)],
            hospital.Measures.Select(m => (m.Id, m.Value, m.National, m.Cases)));
        var volume = hospital.MedicareVolume!;
        Assert.Equal(((int?)4299, (int?)3177, (int?)230, (double?)281012.5), (volume.Discharges, volume.Beneficiaries, volume.OutpatientServices, volume.OutpatientPayments));
        Assert.Equal(["470", "871"], volume.TopInpatient.Select(s => s.Code));
        Assert.Equal(("5072", 188, (int?)175), volume.TopOutpatient.Select(s => (s.Code, s.Cases, s.Beneficiaries)).Single());

        // The provider pages: D's facilities include the dialysis facility and the surgery center; A's affiliations name them.
        var details = new ProviderDetailService(db.ConnectionString);
        var billing = (await details.GetAsync(D, _ct))!.Facilities;
        Assert.Equal([(FacilityTypes.SurgeryCenter, "33C0001234", "ISLAND SURGERY CENTER"), (FacilityTypes.Dialysis, "332301", "BROOKLYN KIDNEY CENTER"),
                (FacilityTypes.Hospital, "330045", "GOOD SAMARITAN HOSPITAL")],
            billing.Select(f => (f.Kind, f.Ccn, f.Name)));
        Assert.Equal(((int?)4, (int?)3, "Profit"), billing.Where(f => f.Kind == FacilityTypes.Dialysis).Select(f => (f.OverallRating, f.PatientSurveyRating, f.Ownership)).Single());
        Assert.Equal(
            [new FacilityAffiliation("Dialysis facility", "332301", "BROOKLYN KIDNEY CENTER", "BROOKLYN", "NY", 4, D),
             new FacilityAffiliation("Long-term care hospital", "332001", "LONG ISLAND SPECIALTY HOSPITAL", "MINEOLA", "NY", null, null)],
            (await details.GetAsync(A, _ct))!.CareCompare!.Facilities);

        // The patient survey of a surgery center and a dialysis facility's quality incentive score (owner, 2026-10-15).
        await db.ExecuteAsync(
            """
            INSERT INTO cms_asc_survey (ccn, professional, communication, recommend, rating, surveys, response_rate, start_date, end_date) VALUES
              ('33C0001234', 96, NULL, 92, 89, 217, 27, '2024-10-01', '2025-09-30'), ('NATION', 97, 92, 87, 89, 1150253, 25, '2024-10-01', '2025-09-30');
            INSERT INTO cms_dialysis_qip (ccn, payment_year, total_score, state_average, national_average, payment_reduction) VALUES
              ('332301', 2026, 39, 55, 54, 1.5);
            """);
        var surveyed = (await service.GetFacilityAsync("33C0001234", 1, _ct))!.Measures.Where(m => m.Group == FacilityMeasures.PatientExperience).ToList();
        // In CMS's order; a question without the facility's share is left out.
        Assert.Equal([("OAS_PROFESSIONAL", (double?)96, (double?)97, (int?)217, (string?)null), ("OAS_RATING", 89, 89, 217, null), ("OAS_RECOMMEND", 92, 87, 217, null)],
            surveyed.Select(m => (m.Id, m.Value, m.National, m.Cases, m.Comparison)));
        Assert.Equal(new DialysisQualityIncentive(2026, 39, 55, 54, 1.5), (await service.GetFacilityAsync("332301", 1, _ct))!.QualityIncentive);
        Assert.Null(hospital.QualityIncentive);

        // The record quality counts the dialysis report's and the surgery center file's NPIs as holding a certified facility.
        await db.ExecuteAsync("DELETE FROM cms_facility_npi WHERE npi = @D", new { D });
        await new AreaCountBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);
        await new QualityBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct, allowIncremental: false);
        Assert.Equal((int)QualityActivity.CertifiedFacility, await db.ScalarAsync<int>(
            $"SELECT activity & {(int)QualityActivity.CertifiedFacility} FROM provider_quality WHERE npi = @D", new { D }));
        await db.ExecuteAsync("DELETE FROM cms_dialysis_npi; DELETE FROM cms_asc WHERE npi = @D", new { D });
        await new QualityBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct, allowIncremental: false);
        Assert.Equal(0, await db.ScalarAsync<int>(
            $"SELECT activity & {(int)QualityActivity.CertifiedFacility} FROM provider_quality WHERE npi = @D", new { D }));
    }

    [Fact]
    public async Task Area_counts_and_pages()
    {
        await using var db = await SeededAsync();
        // Suffolk (36103): A in Amityville 11701 (also in Nassau), B's second office and E in Farmingdale 11735. B and D are in Brooklyn (Kings).
        await db.ExecuteAsync(
            """
            INSERT INTO county_population (county_fips, state_fips, county_code, population, year) VALUES
              ('36103', '36', '103', 1500000, 2025), ('36059', '36', '059', 1400000, 2025), ('36047', '36', '047', 2600000, 2025);
            INSERT INTO county_shortage (county_fips, discipline, whole_county, hpsa_count, max_score) VALUES ('36103', 'MH', 0, 2, 14);
            INSERT INTO cms_hospital (ccn, name, address, city, state, zip, hospital_type, emergency_services, overall_rating) VALUES
              ('330045', 'GOOD SAMARITAN HOSPITAL', '1000 MONTAUK HWY', 'WEST ISLIP', 'NY', '11701', 'Acute Care Hospitals', 1, 3);
            """);
        await new AreaCountBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);
        const string chiro = "Chiropractic & complementary care", doctors = "Doctors (MD, DO)";
        Assert.Equal(
            [("county", "36047", "", "", 2), ("county", "36059", "", "", 1), ("county", "36103", "", "", 3), ("state", "NY", "", "", 4), ("us", "US", "", "", 4)],
            await db.QueryAsync<(string, string, string, string, int)>(
                "SELECT area_type, area_id, category, subcategory, providers FROM area_provider_count WHERE category = '' ORDER BY area_type, area_id"));
        Assert.Equal(2, await db.ScalarAsync<int>(
            "SELECT providers FROM area_provider_count WHERE area_type = 'county' AND area_id = '36103' AND category = @chiro AND subcategory = 'Chiropractors'",
            new { chiro }));
        Assert.Equal(2, await db.ScalarAsync<int>( // A (a pediatrics taxonomy besides chiropractic) and E
            "SELECT providers FROM area_provider_count WHERE area_type = 'county' AND area_id = '36103' AND category = @doctors AND subcategory = ''",
            new { doctors }));

        var areas = new AreaProfileService(db.ConnectionString);
        var suffolk = (await areas.GetAsync("NY", "36103", chiro, "Chiropractors", _ct))!;
        Assert.Equal(("Suffolk County", "NY", 1_500_000L, 2), (suffolk.Name, suffolk.State, suffolk.Population, suffolk.Selected.Providers));
        Assert.Equal(2 * 100_000.0 / 1_500_000, suffolk.Selected.Per100k!.Value, 6);
        Assert.Equal(2 * 100_000.0 / 5_500_000, suffolk.ParentPer100k!.Value, 6);      // NY: the same two chiropractors, for 5.5M people
        Assert.Equal(["Amityville", "Farmingdale"], suffolk.Cities.Select(c => c.City).Order());
        Assert.Equal(("Mental health", 2), (suffolk.Shortages.Single().DisciplineName, suffolk.Shortages.Single().ShortageAreas));
        Assert.Equal(["330045"], suffolk.Hospitals.Select(h => h.Ccn));
        Assert.Empty(suffolk.Children);

        var ny = (await areas.GetAsync("ny", null, null, null, _ct))!;
        Assert.Equal(("state", "New York", 4), (ny.Type, ny.Name, ny.Selected.Providers));
        Assert.Equal(["Kings County", "Nassau County", "Suffolk County"], ny.Children.Select(c => c.Name));
        Assert.Equal(["Mental health"], ny.Children.Single(c => c.Id == "36103").Shortages);
        Assert.Contains(ny.Specialties, s => s.Category == chiro && s.Providers == 2);

        var us = (await areas.GetAsync(null, null, null, null, _ct))!;
        Assert.Equal(("us", 4), (us.Type, us.Selected.Providers));
        Assert.Contains(us.Children, c => c.Id == "NY" && c.Providers == 4);

        Assert.Null(await areas.GetAsync("ZZ", null, null, null, _ct));
        Assert.Null(await areas.GetAsync("CA", "36103", null, null, _ct));  // the county isn't in that state
        Assert.Null(await areas.GetAsync("NY", "123", null, null, _ct));
    }

    [Fact]
    public async Task A_deactivated_npi_has_only_its_status()
    {
        await using var db = await SeededAsync();
        var details = new ProviderDetailService(db.ConnectionString);
        Assert.Null(await details.GetAsync(C, _ct));
        Assert.Equal((true, new DateOnly(2025, 1, 1)), await details.GetDeactivationAsync(C, _ct));
        Assert.Equal((false, null as DateOnly?), await details.GetDeactivationAsync(A, _ct));      // active
        Assert.Equal((false, null as DateOnly?), await details.GetDeactivationAsync("1234567893", _ct)); // unknown
        Assert.Equal((false, null as DateOnly?), await details.GetDeactivationAsync("12", _ct));

        // The table is part of the projection: a reactivated NPI leaves it at the next build.
        await db.ExecuteAsync("UPDATE npidata SET Is_Deactivated = 0 WHERE NPI = @C", new { C });
        await new ProjectionBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct, full: true);
        Assert.Equal((false, null as DateOnly?), await details.GetDeactivationAsync(C, _ct));
    }

    [Fact]
    public async Task Similar_names_find_misspellings_and_organization_words_anywhere()
    {
        await using var db = await SeededAsync();
        var search = Search(db);

        Assert.Empty(await Npis(search, new SearchFilter { LastName = "obrian" }));                                   // prefix: no
        Assert.Equal([A], await Npis(search, new SearchFilter { LastName = "obrian", NameMatch = "similar" }));       // O'BRIEN by sound
        Assert.Equal([E], await Npis(search, new SearchFilter { LastName = "Nunes", NameMatch = "similar" }));        // NUÑEZ, accent folded
        Assert.Equal([B], await Npis(search, new SearchFilter { LastName = "bakr", FirstName = "al", NameMatch = "similar" }));
        Assert.Equal([A], await Npis(search, new SearchFilter { FirstName = "josie", NameMatch = "similar", State = "NY" }));  // JOSÉ
        Assert.Equal([E], await Npis(search, new SearchFilter { LastName = "nu", NameMatch = "similar" }));           // a prefix still matches

        Assert.Empty(await Npis(search, new SearchFilter { OrgName = "jones" }));
        Assert.Equal([D], await Npis(search, new SearchFilter { OrgName = "jones smith", NameMatch = "similar" }));  // words anywhere, any order
        Assert.Equal([D], await Npis(search, new SearchFilter { OrgName = "dental", NameMatch = "similar" }));       // the other name "SJC DENTAL"
        Assert.Equal([D], await Npis(search, new SearchFilter { OrgName = "smi", NameMatch = "similar", Classification = "Dentist", State = "NY" }));
        Assert.Empty(await Npis(search, new SearchFilter { OrgName = "jones dentist", NameMatch = "similar" }));

        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM provider_org_name WHERE npi = @D", new { D }));

        // The ranking distance as MySQL computes it: swapped letters cost less than a wrong one; case and accents don't count.
        foreach (var (name, expected) in new[] { ("SMIHT", 0L), ("SMITH", 2L), ("smidt", 3L), ("SMOOT", 6L), ("SMÍHT", 0L) })
        {
            Assert.Equal((name, expected), (name, await db.ScalarAsync<long>(
                $"SELECT {NameSearch.DistanceSql("CAST(@name AS CHAR CHARACTER SET utf8mb4) COLLATE utf8mb4_0900_ai_ci", "@typed", 5)}",
                new { name, typed = "Smiht" })));
        }
    }

    [Fact]
    public async Task New_and_recently_updated_filters()
    {
        await using var db = await SeededAsync();
        var search = Search(db);
        await db.ExecuteAsync("UPDATE provider SET enumeration_date = UTC_DATE() - INTERVAL 10 DAY, last_update_date = UTC_DATE() - INTERVAL 10 DAY WHERE npi = @E", new { E });
        await db.ExecuteAsync("UPDATE provider SET last_update_date = UTC_DATE() - INTERVAL 3 DAY WHERE npi = @A", new { A });

        Assert.Equal([E], await Npis(search, new SearchFilter { NewWithinDays = 30 }));                // drives
        Assert.Equal([E], await Npis(search, new SearchFilter { NewWithinDays = 30, CountyFips = "36103" }));
        Assert.Empty(await Npis(search, new SearchFilter { NewWithinDays = 5 }));
        Assert.Equal([E], await Npis(search, new SearchFilter { NewWithinDays = 3650, State = "NY" }));  // checked per candidate
        Assert.Equal([A], await Npis(search, new SearchFilter { UpdatedWithinDays = 7 }));
        Assert.Equal([A], await Npis(search, new SearchFilter { UpdatedWithinDays = 7, Classification = "Chiropractor" }));
        Assert.Empty(await Npis(search, new SearchFilter { UpdatedWithinDays = 7, NewWithinDays = 30 }));
    }

    [Fact]
    public async Task Detail_shows_registration_details_identifiers_and_endpoints()
    {
        await using var db = await SeededAsync();
        var details = new ProviderDetailService(db.ConnectionString);

        var d = await details.GetAsync(D, _ct);
        Assert.NotNull(d?.Profile);
        Assert.Equal(new AuthorizedOfficial("DR. PAT SMITH", "DDS", "OWNER, PRESIDENT", "7185550100"), d.Profile.AuthorizedOfficial);
        Assert.Equal("SMITH HOLDINGS INC", d.Profile.ParentOrganization);
        Assert.True(d.Profile.IsOrganizationSubpart);
        Assert.Equal(new MailingAddress("PO BOX 12", null, "BROOKLYN", "NY", "11201-0012", "US", null, "7185550199"), d.Profile.MailingAddress);
        Assert.Equal([new ProviderIdentifier("MCD-123", "05", "Medicaid", "NY", null), new ProviderIdentifier("X9", "01", "Other", null, "BLUE PLAN")],
            d.Identifiers);
        Assert.Empty(d.Endpoints);

        var a = await details.GetAsync(A, _ct);
        Assert.Equal([new ProviderEndpoint("DIRECT", "Direct Messaging Address", "obrien@direct.example.org", "Direct address", null, null,
            "O'BRIEN CHIROPRACTIC, PC", "AMITYVILLE", "NY")], a!.Endpoints);
        Assert.Null(a.Profile!.AuthorizedOfficial); // individuals have none
        Assert.Empty(a.Identifiers);

        // The deactivated NPI's endpoint never reaches the projection.
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM provider_endpoint WHERE npi = @C", new { C }));
    }

    [Fact]
    public async Task Compliance_filters_badges_and_details()
    {
        await using var db = await SeededAsync();
        await db.ExecuteAsync(
            """
            INSERT INTO oig_exclusion (npi, business_name, exclusion_type, exclusion_date) VALUES
              (@A, NULL, '1128b4', '2025-01-15'), (@C, NULL, '1128a1', '2020-01-01'), (NULL, 'NO NPI LLC', '1128a1', '2021-01-01');
            INSERT INTO medicare_opt_out (npi, specialty, effective_date, end_date, can_order_refer) VALUES
              (@B, 'Chiropractic', '2024-01-30', '2099-01-30', 1), (@E, 'Pediatrics', '2018-01-01', '2020-01-01', 0);
            INSERT INTO medicare_order_referring (npi, part_b, dme, hha, pmd, hospice) VALUES
              (@A, 1, 1, 0, 0, 0), (@B, 0, 0, 0, 0, 0), (@E, 1, 0, 0, 0, 0), (@E, 0, 0, 0, 0, 1);
            """, new { A, B, C, E });
        var search = Search(db);

        // Exclusions are matched by NPI; the deactivated C is never returned, and the NPI-less entry matches nobody.
        Assert.Equal([A], await Npis(search, new SearchFilter { Excluded = true }));
        Assert.Equal([A], await Npis(search, new SearchFilter { Excluded = true, State = "NY", Classification = "Chiropractor" }));
        Assert.Equal([B], await Npis(search, new SearchFilter { Excluded = false, Classification = "Chiropractor" }));

        // Only active opt-outs count (E's ended in 2020).
        Assert.Equal([B], await Npis(search, new SearchFilter { OptedOut = true }));
        Assert.Equal([A, D, E], await Npis(search, new SearchFilter { OptedOut = false, State = "NY" }));

        // Order/refer: eligible in any program.
        Assert.Equal([A, E], await Npis(search, new SearchFilter { OrderRefer = true, State = "NY" }));
        Assert.Equal([B, D], await Npis(search, new SearchFilter { OrderRefer = false, State = "NY" }));

        var flags = (await search.SearchAsync(new SearchFilter { State = "NY" }, _ct)).Items.ToDictionary(i => i.Npi, i => i.Flags);
        Assert.Equal(new ProviderFlags(true, false, true, false, false, false), flags[A]);
        Assert.Equal(new ProviderFlags(false, true, false, false, false, false), flags[B]);
        Assert.Equal(ProviderFlags.None, flags[D]);
        Assert.Equal(new ProviderFlags(false, false, true, false, false, false), flags[E]);

        var csvFlags = new Dictionary<string, ProviderFlags>();
        await foreach (var item in search.SearchAllAsync(new SearchFilter { State = "NY" }, _ct))
        {
            csvFlags[item.Npi] = item.Flags;
        }

        Assert.Equal(flags, csvFlags);

        var details = new ProviderDetailService(db.ConnectionString);
        var a = (await details.GetAsync(A, _ct))!.Compliance;
        Assert.Equal([new OigExclusion("1128b4", "License revocation, suspension or surrender", new DateOnly(2025, 1, 15), null, null, null, null)], a.Exclusions);
        Assert.Equal(new MedicareOrderReferring(true, true, false, false, false), a.OrderReferring);
        Assert.Null(a.OptOut);

        var b = (await details.GetAsync(B, _ct))!.Compliance;
        Assert.Equal(new MedicareOptOut("Chiropractic", new DateOnly(2024, 1, 30), new DateOnly(2099, 1, 30), true, true), b.OptOut);
        Assert.Empty(b.Exclusions);

        var e = (await details.GetAsync(E, _ct))!.Compliance;
        Assert.False(e.OptOut!.Active);
        Assert.Equal(new MedicareOrderReferring(true, false, false, false, true), e.OrderReferring); // two rows merged

        Assert.Null((await details.GetAsync(D, _ct))!.Compliance.OrderReferring);
    }

    [Fact]
    public async Task Care_compare_filters_badges_and_facility_details()
    {
        await using var db = await SeededAsync();
        var year = DateTime.UtcNow.Year;
        await db.ExecuteAsync(
            """
            INSERT INTO cc_clinician (npi, medical_school, graduation_year, primary_specialty, accepts_assignment, telehealth) VALUES
              (@A, 'NEW YORK CHIROPRACTIC COLLEGE', @old, 'CHIROPRACTIC', 1, 1), (@B, 'OTHER', @recent, 'CHIROPRACTIC', 0, 1), (@E, NULL, NULL, 'PEDIATRICS', 1, 0);
            INSERT INTO cc_group (npi, org_pac_id, group_name, members, accepts_assignment) VALUES (@A, '1234567890', 'ISLAND SPINE, PLLC', 12, 1);
            INSERT INTO cc_facility_affiliation (npi, facility_type, ccn) VALUES (@A, 'Hospital', '330045'), (@A, 'Home health agency', '337002'), (@B, 'Hospital', '330045');
            INSERT INTO cms_hospital (ccn, name, city, state, hospital_type, ownership, emergency_services, overall_rating) VALUES
              ('330045', 'GOOD SAMARITAN HOSPITAL', 'WEST ISLIP', 'NY', 'Acute Care Hospitals', 'Voluntary non-profit - Church', 1, 3);
            INSERT INTO cms_facility_npi (ccn, npi, kind) VALUES ('330045', @D, 'hospital'), ('337002', @D, 'home_health');
            UPDATE cms_hospital SET mort_measures = 8, mort_better = 1, mort_worse = 0, safety_measures = 7, safety_better = 3, safety_worse = 1,
              readm_measures = 11, readm_better = 1, readm_worse = 2 WHERE ccn = '330045';
            INSERT INTO cms_hospital_survey (ccn, star_rating, surveys, response_rate) VALUES ('330045', 3, 1392, 17);
            INSERT INTO cms_home_health (ccn, name, city, state, quality_rating) VALUES ('337002', 'ISLAND HOME CARE', 'BAY SHORE', 'NY', 3.5);
            INSERT INTO cc_mips (npi, program_year, source, facility_name, quality_score, pi_score, ia_score, cost_score, final_score) VALUES
              (@A, 2024, 'individual', NULL, 85.87, NULL, 40, 76.41, 87.27), (@A, 2024, 'group', 'ISLAND SPINE PLLC', 100, 100, 40, NULL, 100);
            """, new { A, B, D, E, old = year - 25, recent = year - 3 });
        var search = Search(db);

        Assert.Equal([A, E], await Npis(search, new SearchFilter { AcceptsAssignment = true }));
        Assert.Equal([B, D], await Npis(search, new SearchFilter { AcceptsAssignment = false, State = "NY" }));
        Assert.Equal([A, B], await Npis(search, new SearchFilter { Telehealth = true, State = "NY" }));
        Assert.Equal([A], await Npis(search, new SearchFilter { MinYears = 20, Classification = "Chiropractor" }));
        Assert.Equal([A, B], await Npis(search, new SearchFilter { MinYears = 2, Classification = "Chiropractor" }));

        var flags = (await search.SearchAsync(new SearchFilter { State = "NY" }, _ct)).Items.ToDictionary(i => i.Npi, i => i.Flags);
        Assert.Equal(new ProviderFlags(false, false, false, true, true, false), flags[A]);
        Assert.Equal(new ProviderFlags(false, false, false, false, true, false), flags[B]);

        var details = new ProviderDetailService(db.ConnectionString);
        var a = (await details.GetAsync(A, _ct))!.CareCompare!;
        Assert.Equal(("NEW YORK CHIROPRACTIC COLLEGE", year - 25, true, true), (a.MedicalSchool, a.GraduationYear, a.AcceptsMedicareAssignment, a.OffersTelehealth));
        Assert.Equal([new GroupPractice("1234567890", "ISLAND SPINE, PLLC", 12, true, null, null)], a.GroupPractices);
        Assert.Equal(
            [new FacilityAffiliation("Home health agency", "337002", "ISLAND HOME CARE", "BAY SHORE", "NY", null, D),
             new FacilityAffiliation("Hospital", "330045", "GOOD SAMARITAN HOSPITAL", "WEST ISLIP", "NY", 3, D)],
            a.Facilities);
        Assert.Null((await details.GetAsync(D, _ct))!.CareCompare);

        // The hospital's own NPI shows its Care Compare facilities, with the clinicians affiliated with them, outcomes and survey stars (item 14).
        var facilities = (await details.GetAsync(D, _ct))!.Facilities;
        var hospital = facilities.Single(f => f.Kind == "hospital");
        Assert.Equal(("330045", "GOOD SAMARITAN HOSPITAL", true, 3, 2, 3), (hospital.Ccn, hospital.Name, hospital.EmergencyServices,
            hospital.OverallRating, hospital.AffiliatedClinicians, hospital.PatientSurveyRating));
        Assert.Equal(new HospitalOutcomes(new OutcomeCounts(8, 1, 0), new OutcomeCounts(7, 3, 1), new OutcomeCounts(11, 1, 2)), hospital.Outcomes);
        var homeHealth = facilities.Single(f => f.Kind == "home_health");
        Assert.Equal(("ISLAND HOME CARE", 3.5, (HospitalOutcomes?)null), (homeHealth.Name, homeHealth.QualityOfCareRating, homeHealth.Outcomes));

        // MIPS: best score first.
        Assert.Equal(
            [new MipsScore(2024, "group", "ISLAND SPINE PLLC", 100, 100, 100, 40, null), new MipsScore(2024, "individual", null, 87.27, 85.87, null, 40, 76.41)],
            (await details.GetAsync(A, _ct))!.MipsScores);
        Assert.Empty((await details.GetAsync(B, _ct))!.MipsScores);
    }

    [Fact]
    public async Task Medicare_activity_filter_badge_and_details()
    {
        await using var db = await SeededAsync();
        await db.ExecuteAsync(
            """
            INSERT INTO medicare_utilization (npi, data_year, provider_type, participating, distinct_services, beneficiaries, services, allowed_amount,
              payment_amount, avg_risk_score) VALUES (@A, 2024, 'Chiropractic', 1, 4, 212, 1840, 40000.5, 31234.56, 0.91);
            INSERT INTO medicare_top_service (npi, service_rank, data_year, hcpcs, description, is_drug, place_of_service, beneficiaries, services, avg_payment)
              VALUES (@A, 1, 2024, '98940', 'Chiropractic manipulative treatment', 0, 'O', 150, 900, 28.12), (@A, 2, 2024, '98941', 'CMT 3-4 regions', 0, 'F', NULL, 600, 35);
            INSERT INTO medicare_part_d (npi, data_year, prescriber_type, claims, drug_cost, beneficiaries, brand_claims, generic_claims, opioid_claims, opioid_rate)
              VALUES (@E, 2024, 'Pediatrics', 15, 123.4, NULL, NULL, 15, NULL, NULL);
            """, new { A, E });
        var search = Search(db);

        Assert.Equal([A, E], await Npis(search, new SearchFilter { MedicareActive = true }));
        Assert.Equal([B, D], await Npis(search, new SearchFilter { MedicareActive = false, State = "NY" }));
        Assert.True((await search.SearchAsync(new SearchFilter { Npi = A }, _ct)).Items.Single().Flags.BilledMedicare);

        var details = new ProviderDetailService(db.ConnectionString);
        var a = (await details.GetAsync(A, _ct))!;
        Assert.Equal((2024, "Chiropractic", true, 212, 31234.56), (a.MedicareServices!.Year, a.MedicareServices.ProviderType, a.MedicareServices.Participating,
            a.MedicareServices.Beneficiaries, a.MedicareServices.PaymentAmount));
        Assert.Equal([new MedicareService("98940", "Chiropractic manipulative treatment", false, "Office", 150, 900, 28.12),
                      new MedicareService("98941", "CMT 3-4 regions", false, "Facility", null, 600, 35)], a.MedicareServices.TopServices);
        Assert.Null(a.MedicarePrescribing);

        var e = (await details.GetAsync(E, _ct))!;
        Assert.Null(e.MedicareServices);
        Assert.Equal(new MedicarePrescribing(2024, "Pediatrics", 15, 123.4, null, null, 15, null, null, null), e.MedicarePrescribing);
    }

    [Fact]
    public async Task Shortage_filter_and_county_facts()
    {
        await using var db = await SeededAsync();
        await db.ExecuteAsync(
            """
            INSERT INTO county_shortage (county_fips, discipline, whole_county, hpsa_count, max_score) VALUES
              ('36103', 'PC', 0, 1, 14), ('36103', 'DH', 1, 1, NULL), ('36047', 'MH', 0, 2, 19);
            INSERT INTO county_population (county_fips, state_fips, county_code, population, year) VALUES ('36103', '36', '103', 1530000, 2025);
            """);
        var search = Search(db);

        // A in Amityville (11701: Nassau and Suffolk), B's secondary location and E in Farmingdale (Suffolk); B and D in Brooklyn (Kings).
        Assert.Equal([A, B, E], await Npis(search, new SearchFilter { Shortage = "primaryCare" }));
        Assert.Equal([B, D], await Npis(search, new SearchFilter { Shortage = "mentalHealth" }));
        Assert.Equal([A, B], await Npis(search, new SearchFilter { Shortage = "dental", Classification = "Chiropractor" }));
        var error = await Assert.ThrowsAsync<SearchValidationException>(() => search.SearchAsync(new SearchFilter { Shortage = "vision" }, _ct));
        Assert.Contains(nameof(SearchFilter.Shortage), error.Errors.Keys);

        var areas = new AreaService(db.ConnectionString);
        var suffolk = await areas.GetCountyAsync("36103", _ct);
        Assert.Equal(("Suffolk County", "NY", (int?)1530000, (int?)2025), (suffolk!.Name, suffolk.State, suffolk.Population, suffolk.PopulationYear));
        Assert.Equal([new CountyShortage("PC", "Primary care", false, 1, 14), new CountyShortage("DH", "Dental", true, 1, null)], suffolk.Shortages);
        Assert.Null((await areas.GetCountyAsync("36047", _ct))!.Population);
        Assert.Null(await areas.GetCountyAsync("99999", _ct));
    }

    [Fact]
    public async Task Industry_payments_detail()
    {
        await using var db = await SeededAsync();
        await db.ExecuteAsync(
            """
            INSERT INTO open_payments_summary (npi, program_year, total_amount, records, payers) VALUES (@B, 2025, 2563.35, 5, 4);
            INSERT INTO open_payments_nature (npi, nature, amount, records) VALUES (@B, 'Consulting Fee', 2500, 1), (@B, 'Food and Beverage', 63.35, 4);
            INSERT INTO open_payments_payer (npi, payer_rank, payer, amount, records) VALUES (@B, 1, 'Medtronic USA Inc.', 2500, 1), (@B, 2, 'Pfizer, Inc.', 40, 2);
            """, new { B });
        var details = new ProviderDetailService(db.ConnectionString);

        var b = (await details.GetAsync(B, _ct))!.IndustryPayments!;
        Assert.Equal((2025, 2563.35, 5, 4), (b.Year, b.TotalAmount, b.Records, b.Payers));
        Assert.Equal([new IndustryPaymentKind("Consulting Fee", 2500, 1), new IndustryPaymentKind("Food and Beverage", 63.35, 4)], b.ByNature);
        Assert.Equal([new IndustryPayer("Medtronic USA Inc.", 2500, 1), new IndustryPayer("Pfizer, Inc.", 40, 2)], b.TopPayers);
        Assert.Null((await details.GetAsync(A, _ct))!.IndustryPayments);

        // Every program year (item 13), newest first, and the top companies over all years. A provider with history but no
        // payments in the newest year still gets it.
        await db.ExecuteAsync(
            """
            INSERT INTO open_payments_year VALUES (@B, 2024, 100.5, 3, 0, 0, 0, 0, 0, 0, 0), (@B, 2025, 2563.35, 5, 1000, 1, 50000, 3, 7000, 7500, 1),
                                                  (@A, 2021, 12, 1, 0, 0, 0, 0, 0, 0, 0);
            INSERT INTO open_payments_company VALUES (@B, 1, 'Medtronic USA Inc.', 52500, 2500, 0, 50000, 0, 4, '100000000002'), (@B, 2, 'Acme Devices LLC', 7000, 0, 0, 0, 7000, 1, NULL);
            """, new { A, B });
        var history = (await details.GetAsync(B, _ct))!.PaymentHistory!;
        Assert.Equal([new IndustryPaymentYear(2025, 2563.35, 5, 1000, 1, 50000, 3, 7000, 7500, 1), new IndustryPaymentYear(2024, 100.5, 3, 0, 0, 0, 0, 0, 0, 0)],
            history.Years);
        Assert.Equal([new IndustryPaymentCompany("Medtronic USA Inc.", 52500, 2500, 0, 50000, 0, 4, "100000000002"), new IndustryPaymentCompany("Acme Devices LLC", 7000, 0, 0, 0, 7000, 1)],
            history.TopCompanies);
        var a = (await details.GetAsync(A, _ct))!;
        Assert.Null(a.IndustryPayments);
        Assert.Equal([2021], a.PaymentHistory!.Years.Select(y => y.Year));
        Assert.Null((await details.GetAsync(D, _ct))!.PaymentHistory);
    }

    [Fact]
    public async Task Credentials_are_standardized_for_display_search_and_the_dropdown()
    {
        await using var db = await SeededAsync();
        // Listed: credentials held by 2+ providers (MD); DC and PhD are rarer, so A and E are also under "Other".
        await new CredentialBuilder(db.Database, Logger.None, 0.95, minProvidersForKnown: 1, minListed: 2).BuildAsync(_ct);
        Assert.Equal([(A, "DC"), (A, "Other"), (B, "MD"), (E, "MD"), (E, "PhD"), (E, "Other")],
            await db.QueryAsync<(string, string)>("SELECT npi, credential FROM provider_credential ORDER BY npi, ord"));

        var catalog = new CredentialCatalog(db.ConnectionString, minProviders: 2);
        Assert.Equal([new CredentialInfo("MD", 2), new CredentialInfo("Other", 2)], await catalog.GetAllAsync(_ct)); // "Other" last

        var search = new SearchService(db.ConnectionString, new TaxonomyCatalog(db.ConnectionString), catalog);
        Assert.Equal([B, E], await Npis(search, new SearchFilter { Credential = "M.D." }));   // exact: MD, whatever was typed
        Assert.Equal([A, E], await Npis(search, new SearchFilter { Credential = "Other" }));
        Assert.Equal([A, E], await Npis(search, new SearchFilter { Credential = "other", State = "NY" }));
        Assert.Equal([B, E], await Npis(search, new SearchFilter { Credential = "M" }));      // not a credential: prefix of the raw text

        // The grid, CSV and detail page show the standard spelling.
        var e = (await search.SearchAsync(new SearchFilter { Npi = E }, _ct)).Items.Single();
        Assert.Equal("MD, PhD", e.Credential);
        Assert.Equal("DC", (await new ProviderDetailService(db.ConnectionString).GetAsync(A, _ct))?.Credential);
    }

    [Fact]
    public async Task The_city_dropdown_lists_a_state_or_county_s_cities()
    {
        await using var db = await SeededAsync();
        // A misspelled city isn't listed (only the names USPS uses for ZIPs with practice locations are).
        await db.ExecuteAsync($"INSERT INTO provider_location (npi, is_primary, address1, city, state, zip5) VALUES ('{B}', 0, '9 Elm St', 'AMITYVILE', 'NY', '11701')");
        var cities = new CityCatalog(db.ConnectionString);

        Assert.Equal(["Amityville", "Brooklyn", "Farmingdale"], await cities.GetCitiesAsync("ny", null, _ct));
        Assert.Equal(["Amityville", "Farmingdale"], await cities.GetCitiesAsync("NY", "36103", _ct)); // Suffolk County's ZIPs
        Assert.Empty(await cities.GetCitiesAsync("ZZ", null, _ct));

        // The dropdown's value matches the city as written (case-insensitive) or a ZIP USPS calls Amityville: B wrote AMITYVILE.
        Assert.Equal([A, B], await Npis(Search(db), new SearchFilter { State = "NY", City = "Amityville" }));
        Assert.Equal([A], await Npis(Search(db), new SearchFilter { Classification = "Chiropractor", State = "NY", City = "Amityville" })); // provider_search (B's added row is in it only after the next projection)
        Assert.Equal([B], await Npis(Search(db), new SearchFilter { City = "AMITYVILE" })); // a typed name still matches as written
    }

    [Fact]
    public async Task Typed_searches_and_suggestions_use_the_loaded_specialties_and_places()
    {
        await using var db = await SeededAsync();
        var vocabularies = new SearchVocabularyCatalog(db.ConnectionString, new TaxonomyCatalog(db.ConnectionString));
        var vocabulary = await vocabularies.GetAsync(_ct);

        var list = QueryInterpreter.Interpret("chiropractors in suffolk county ny", vocabulary);
        Assert.Equal((QueryKind.List, "Chiropractic & complementary care", "Chiropractors", "36103"),
            (list.Kind, list.Filter.Category, list.Filter.Subcategory, list.Filter.CountyFips));
        Assert.Equal([A, B], await Npis(Search(db), list.Filter));                     // B through its Farmingdale office
        var city = QueryInterpreter.Interpret("pediatricians in farmingdale", vocabulary);
        Assert.Equal(("Doctors (MD, DO)", "Children & teens (pediatrics)", "Farmingdale", "NY"),
            (city.Filter.Category, city.Filter.Subcategory, city.Filter.City, city.Filter.State));
        Assert.Equal([E], await Npis(Search(db), city.Filter));
        var nucc = QueryInterpreter.Interpret("sports chiropractor in suffolk county", vocabulary);
        Assert.Equal(("Chiropractor", "Sports Physician"), (nucc.Filter.Classification, nucc.Filter.Specialization));

        // The plain categories search like the NUCC filters; together both must match; results say the specialty in plain words.
        var search = Search(db);
        Assert.Equal([D], await Npis(search, new SearchFilter { Category = "Dentists" }));
        Assert.Equal([A, B], await Npis(search, new SearchFilter { Category = "chiropractic & complementary care", State = "NY" }));
        Assert.Empty(await Npis(search, new SearchFilter { Category = "Dentists", Classification = "Chiropractor" }));
        var page = await search.SearchAsync(new SearchFilter { Category = "Chiropractic & complementary care", Subcategory = "Chiropractors", Sort = "npi" }, _ct);
        Assert.All(page.Items, p => Assert.Equal(("Chiropractic & complementary care", "Chiropractors", "Chiropractor"), (p.Category, p.Subcategory, p.PlainSpecialty)));
        var unknown = await Assert.ThrowsAsync<SearchValidationException>(() => search.SearchAsync(new SearchFilter { Category = "Dentists", Subcategory = "Cardiology" }, _ct));
        Assert.True(unknown.Errors.ContainsKey(nameof(SearchFilter.Subcategory)));

        var suggestions = new SuggestService(db.ConnectionString, vocabularies);
        var names = await suggestions.SuggestAsync("o'brien", _ct);
        Assert.Equal(("provider", "/provider/" + A), (names[0].Kind, names[0].Url));
        Assert.Equal("O'BRIEN, JOSÉ Q", names[0].Label);
        Assert.Contains(await suggestions.SuggestAsync("chiro", _ct), s => s is { Kind: "specialty", Label: "Chiropractors", Text: "Chiropractors in " });
        Assert.Contains(await suggestions.SuggestAsync("dentists in suff", _ct), s => s is { Kind: "place", Text: "dentists in Suffolk County, NY" });
    }

    [Fact]
    public async Task Bulk_lookup_keeps_request_order_and_reports_each_npi()
    {
        await using var db = await SeededAsync();
        await db.ExecuteAsync(
            """
            INSERT INTO oig_exclusion (npi, business_name, exclusion_type, exclusion_date) VALUES (@A, NULL, '1128b4', '2025-01-15'), (@A, NULL, '1128b4', '2023-05-01');
            INSERT INTO medicare_opt_out (npi, specialty, effective_date, end_date, can_order_refer) VALUES (@B, 'Chiropractic', '2024-01-30', '2099-01-30', 1);
            """, new { A, B });
        var search = Search(db);

        var rows = await search.LookupAsync([E, C, "1234567890", A, E, " ", "1234567893", B], _ct);

        Assert.Equal(
            [(E, LookupStatus.Found), (C, LookupStatus.Deactivated), ("1234567890", LookupStatus.Invalid), (A, LookupStatus.Found),
                ("1234567893", LookupStatus.NotFound), (B, LookupStatus.Found)],
            rows.Select(r => (r.Npi, r.Status)));
        Assert.Equal("NUÑEZ, ELENA", rows[0].Provider!.Name);
        Assert.Equal(new DateOnly(2025, 1, 1), rows[1].DeactivationDate); // docs/PRODUCT_PLAN.md decision 2
        Assert.Equal("AMITYVILLE", rows[3].Provider!.City); // the primary location: a lookup has no location filter
        Assert.Equal((new DateOnly(2023, 5, 1), null as DateOnly?), (rows[3].ExclusionDate, rows[3].OptOutEndDate)); // the earliest exclusion
        Assert.Equal((null as DateOnly?, new DateOnly(2099, 1, 30)), (rows[5].ExclusionDate, rows[5].OptOutEndDate));

        using var csv = new MemoryStream();
        await ProviderCsv.WriteLookupAsync(rows.ToAsyncEnumerable(), csv, _ct);
        var lines = System.Text.Encoding.UTF8.GetString(csv.ToArray()).TrimStart('\uFEFF').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Requested NPI,Lookup Status,Deactivation Date,OIG Exclusion Date,Medicare Opt-Out End Date,NPI,Entity Type,Name", lines[0],
            StringComparison.Ordinal);
        Assert.StartsWith($"{E},Active,,,,{E},Individual,", lines[1], StringComparison.Ordinal);
        Assert.Equal($"{C},Deactivated,2025-01-01,,", lines[2]);
        Assert.Equal("1234567890,Invalid NPI,,,", lines[3]);
        Assert.StartsWith($"{A},Active,,2023-05-01,,{A},", lines[4], StringComparison.Ordinal);
        Assert.Equal("1234567893,Not found,,,", lines[5]);

        // The Excel download has the same columns.
        using var xlsx = new MemoryStream();
        Assert.Equal(6, await ProviderXlsx.WriteLookupAsync(rows.ToAsyncEnumerable(), xlsx, _ct));
    }

    [Fact]
    public async Task Addresses_are_geocoded_once_and_the_map_search_finds_providers_by_area()
    {
        await using var db = await SeededAsync();
        var census = new FakeCensus();
        var geocoder = new AddressGeocoder(db.Database, new CensusGeocoder(new HttpClient(census), "https://census.test/batch", "Public_AR_Current"),
            Logger.None, batchSize: 2, parallelism: 2) { Delays = [] };

        // Five street addresses (B has two); C is deactivated, so not in the projection. Batches of 2, two at a time.
        // Suffolk County first (11701, 11735: A, B's Farmingdale office, E), then the rest (Brooklyn).
        Assert.Equal(new GeocodeOutcome(Pending: 3, Geocoded: 3, Matched: 2, Ok: true), await geocoder.GeocodePendingAsync(maxBatches: 0, _ct, countyFips: "36103"));
        Assert.Equal(new GeocodeOutcome(Pending: 2, Geocoded: 2, Matched: 0, Ok: true), await geocoder.GeocodePendingAsync(maxBatches: 0, _ct));
        Assert.Equal(3, census.Requests);
        Assert.Equal([("Match", 2L), ("No_Match", 2L), ("Tie", 1L)],
            await db.QueryAsync<(string, long)>("SELECT status, COUNT(*) FROM address_geocode GROUP BY status ORDER BY status"));
        Assert.Equal(new GeocodeOutcome(0, 0, 0, true), await geocoder.GeocodePendingAsync(maxBatches: 0, _ct)); // nothing is sent twice

        // Overture placed D's address at a building (it beats the Census answer and the ZIP centroid).
        await db.ExecuteAsync($"INSERT INTO address_point (addr_key, source, lat, lon, matched, `release`, matched_at) " +
            $"SELECT addr_key, 'place', 40.6905, -73.9950, '5 ATLANTIC AVE', '2026-09-23.1', NOW() FROM provider_location WHERE npi = '{D}'");

        var map = new MapBuilder(db.Database, Logger.None, 0.95);
        Assert.True(await map.IsStaleAsync(_ct));
        Assert.Equal(5L, await map.BuildAsync(_ct));
        Assert.False(await map.IsStaleAsync(_ct));
        Assert.Equal([(A, (sbyte)0, "census"), (B, (sbyte)1, "zip"), (B, (sbyte)1, "zip"), (D, (sbyte)0, "place"), (E, (sbyte)0, "census")],
            await db.QueryAsync<(string, sbyte, string)>("SELECT npi, approximate, source FROM provider_map ORDER BY npi, approximate"));
        // Each point under each of its specialties and grid cells: A (chiropractor + pediatrics), B (sports chiropractor at two addresses), D, E.
        Assert.Equal(6L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM provider_map_specialty"));

        var search = Search(db);
        var longIsland = MapBounds.Parse("-74.1,40.5,-73.3,40.9")!;
        var all = await search.SearchAreaAsync(new SearchFilter(), longIsland, _ct);
        Assert.Equal(5, all.Items.Count);
        Assert.False(all.Truncated);
        Assert.Equal(all.Items.OrderBy(i => i.DistanceMiles).Select(i => i.Provider.Npi), all.Items.Select(i => i.Provider.Npi)); // nearest the centre first

        // B appears at both of its addresses, each with that address.
        Assert.Equal(["10 Court St", "300 Conklin St"], all.Items.Where(i => i.Provider.Npi == B).Select(i => i.Provider.Address1).Order());
        var a = Assert.Single(all.Items, i => i.Provider.Npi == A);
        Assert.Equal((40.6790, -73.4150, false), (a.Lat, a.Lon, a.Approximate)); // the geocoded point, not the ZIP centroid

        // Filters work as on the search page; the location filters are replaced by the area.
        var chiros = await search.SearchAreaAsync(new SearchFilter { Classification = "Chiropractor", State = "CA" }, longIsland, _ct);
        Assert.Equal([A, B, B], chiros.Items.Select(i => i.Provider.Npi).Order());

        var amityville = await search.SearchAreaAsync(new SearchFilter(), MapBounds.Parse("-73.43,40.67,-73.40,40.69")!, _ct);
        Assert.Equal([A], amityville.Items.Select(i => i.Provider.Npi));

        var tooLarge = await Assert.ThrowsAsync<SearchValidationException>(() => search.SearchAreaAsync(new SearchFilter(), MapBounds.Parse("-100,30,-70,45")!, _ct));
        Assert.Contains("bbox", tooLarge.Errors.Keys);

        // The same area as a list filter (owner, 2026-10-14): every match, not only the pins; each provider once, at a location inside it.
        var listed = await search.SearchAsync(new SearchFilter { Bbox = "-74.1,40.5,-73.3,40.9" }, _ct);
        Assert.Equal([A, B, D, E], listed.Items.Select(i => i.Npi).Order());
        Assert.Equal(4, listed.TotalCount);
        var listedChiros = await search.SearchAsync(new SearchFilter { Classification = "Chiropractor", Bbox = "-74.1,40.5,-73.3,40.9" }, _ct);
        Assert.Equal([A, B], listedChiros.Items.Select(i => i.Npi).Order());
        Assert.Equal([A], await search.SearchAllAsync(new SearchFilter { Bbox = "-73.43,40.67,-73.40,40.69" }, _ct).Select(i => i.Npi).ToListAsync(_ct));
        Assert.Empty((await search.SearchAsync(new SearchFilter { State = "CA", Bbox = "-73.43,40.67,-73.40,40.69" }, _ct)).Items); // both must hold
        Assert.Empty((await search.SearchAsync(new SearchFilter { Classification = "Chiropractor", Bbox = "-73.39,40.67,-73.30,40.69" }, _ct)).Items);

        // "View on map" opens on the area of the search's location filters.
        var maps = new MapService(db.ConnectionString);
        var county = await maps.GetStartAreaAsync(new SearchFilter { CountyFips = "36103" }, _ct);
        Assert.NotNull(county);
        Assert.InRange(40.682177, county.South, county.North);
        Assert.Null(await maps.GetStartAreaAsync(new SearchFilter { Classification = "Chiropractor" }, _ct));

        // Find care (docs/PRODUCT_PLAN.md step 1.8): nearest first, widening until enough are found (here all 50 miles).
        await db.ExecuteAsync(
            """
            INSERT INTO cc_facility_affiliation (npi, facility_type, ccn) VALUES (@E, 'Hospital', '330045');
            INSERT INTO cms_hospital (ccn, name, address, city, state, zip, hospital_type, emergency_services, overall_rating) VALUES
              ('330045', 'GOOD SAMARITAN HOSPITAL', '1000 MONTAUK HWY', 'WEST ISLIP', 'NY', '11795', 'Acute Care Hospitals', 1, 3);
            """, new { E });
        var care = new CareFinder(db.ConnectionString, search);
        var children = CareFinder.Resolve("children", null)!;
        var near = (await care.FindAsync(new CareRequest(children, "11701"), _ct))!;
        Assert.Equal([A, E], near.Items.Select(r => r.Nearest.Provider.Npi)); // A (pediatrics too) in Amityville, E in Farmingdale
        Assert.Equal(50, near.RadiusMiles);
        Assert.True(near.Items[0].Nearest.DistanceMiles < near.Items[1].Nearest.DistanceMiles);
        Assert.Equal((40.682177, -73.414596), (near.CenterLat, near.CenterLon)); // one placed address in 11701: the Census centre
        var withHospitals = await care.WithHospitalsAsync(near.Items, _ct);
        Assert.Equal([null, "GOOD SAMARITAN HOSPITAL"], withHospitals.Select(r => r.Hospital));

        var back = (await care.FindAsync(new CareRequest(CareFinder.Resolve("back", null)!, "11701"), _ct))!;
        Assert.Equal([A, B], back.Items.Select(r => r.Nearest.Provider.Npi)); // chiropractors; B once, at its nearer (Farmingdale) office
        Assert.Equal("300 Conklin St", back.Items[1].Nearest.Provider.Address1);
        Assert.Empty((await care.FindAsync(new CareRequest(CareFinder.Resolve("dentist", null)!, "11701"), _ct))!.Items); // D is an organization
        Assert.Null(await care.FindAsync(new CareRequest(children, "99999"), _ct)); // no Census centre, no addresses
        Assert.Equal("11735", await care.NearestZipAsync(40.731, -73.441, _ct));
    }

    [Fact]
    public async Task Practice_addresses_are_labelled_from_FEMA_buildings_and_signs_of_a_business()
    {
        await using var db = await SeededAsync();
        var census = new CensusGeocoder(new HttpClient(new FakeCensus()), "https://census.test/batch", "Public_AR_Current");
        await new AddressGeocoder(db.Database, census, Logger.None, batchSize: 10, parallelism: 1) { Delays = [] }.GeocodePendingAsync(maxBatches: 0, _ct);
        await db.ExecuteAsync($"INSERT INTO address_point (addr_key, source, lat, lon, matched, `release`, matched_at) " +
            $"SELECT addr_key, 'place', 40.6905, -73.9950, '5 ATLANTIC AVE', '2026-09-23.1', NOW() FROM provider_location WHERE npi = '{D}'");
        await new MapBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);

        // Nothing to label before FEMA structures are loaded.
        var labels = new AddressTypeBuilder(db.Database, Logger.None, 0.95);
        Assert.False(await labels.IsStaleAsync(_ct));

        var options = new LoaderOptions
        {
            WorkFolder = _folder, LogFolder = _folder, DownloadAttempts = 1,
            FemaStructuresListingUrl = "https://fema.test/list", FemaStructuresBaseUrl = "https://fema.test/files/",
        };
        var context = new Npi.Loader.Datasets.DatasetContext(options, db.Database, new HttpClient(new FakeFema()), Logger.None);
        var source = new Npi.Loader.Datasets.FemaStructureSource();
        var release = await source.FindLatestAsync(context, _ct);
        Assert.StartsWith("USA Structures 2024-12-06 ", release.Version, StringComparison.Ordinal);

        // A, D and E have points (B's two addresses are only at their ZIP centroids, so they aren't looked up).
        Assert.Equal(3L, await source.LoadAsync(context, release, _ct));
        Assert.Equal([("census", "inside", "Single Family Dwelling"), ("place", "inside", "Multi - Family Dwelling"), ("census", "near", "Medical Office/Clinic")],
            await db.QueryAsync<(string, string, string)>(
                "SELECT m.source, b.matched, b.prim_occ FROM address_building b JOIN provider_map m ON m.addr_key = b.addr_key ORDER BY m.npi"));
        Assert.Equal(17, await db.ScalarAsync<int>("SELECT b.distance_m FROM address_building b WHERE b.matched = 'near'"));
        Assert.False(File.Exists(Path.Combine(_folder, "datasets", "fema", "fema_part1.parquet"))); // deleted after use

        // A: a house. D: an apartment building, but an Overture place has its address → commercial. E: the clinic next to its
        // street point. B: not placed on a building → unknown.
        Assert.Equal([(A, "residential"), (B, "unknown"), (B, "unknown"), (D, "commercial"), (E, "commercial")],
            await db.QueryAsync<(string, string)>(
                "SELECT l.npi, t.building_type FROM provider_location l JOIN address_type t ON t.addr_key = l.addr_key ORDER BY l.npi, t.building_type"));
        Assert.False(await labels.IsStaleAsync(_ct));

        var search = Search(db);
        Assert.Equal([A], await Npis(search, new SearchFilter { State = "NY", AddressType = "residential" }));
        Assert.Equal([D, E], await Npis(search, new SearchFilter { State = "NY", AddressType = "commercial" }));
        Assert.Equal([B], await Npis(search, new SearchFilter { State = "NY", AddressType = "unknown" }));
        Assert.Equal([A], await Npis(search, new SearchFilter { Classification = "Chiropractor", State = "NY", AddressType = "residential" }));
        Assert.Empty(await Npis(search, new SearchFilter { Classification = "Chiropractor", CountyFips = "36047", AddressType = "residential" }));

        var page = await search.SearchAsync(new SearchFilter { State = "NY", AddressType = "commercial" }, _ct);
        Assert.All(page.Items, i => Assert.Equal("commercial", i.AddressType));

        var details = new ProviderDetailService(db.ConnectionString);
        var location = Assert.Single((await details.GetAsync(D, _ct))!.Locations);
        Assert.Equal("commercial", location.BuildingType);
        Assert.Equal("FEMA USA Structures: Multi - Family Dwelling, 40,000 sq ft; a business is listed at this address (Overture Maps)", location.BuildingBasis);
        Assert.Equal("FEMA USA Structures: Medical Office/Clinic, 5,200 sq ft, the nearest building (about 17 m)",
            (await details.GetAsync(E, _ct))!.Locations[0].BuildingBasis);

        // The map checks the building at each pin.
        var area = await search.SearchAreaAsync(new SearchFilter { AddressType = "residential" }, MapBounds.Parse("-74.1,40.5,-73.3,40.9")!, _ct);
        Assert.Equal([A], area.Items.Select(i => i.Provider.Npi));
        Assert.Equal("residential", area.Items[0].Provider.AddressType);

        // Speed item 4: between releases only new or moved addresses are looked up, in the files in place. Nothing is due
        // until the map changes.
        var full = await ProjectionSnapshotAsync(db, ["address_building", "address_type", "building_search"]);
        var files = Path.Combine(_folder, "fema-files", "usa-structures", "geoparquet");
        Directory.CreateDirectory(files);
        foreach (var part in new[] { "fema_part1.parquet", "fema_part2.parquet" })
        {
            File.Copy(Fixtures.Path("datasets/" + part), Path.Combine(files, part));
        }

        options.FemaStructuresReadBaseUrl = Path.Combine(_folder, "fema-files") + Path.DirectorySeparatorChar;
        var http = new HttpClient(new FakeFema());
        Assert.Equal(0L, await Npi.Loader.Datasets.FemaStructureSource.MatchNewAsync(options, db.Database, http, Logger.None, _ct));

        // E's address is new (no building yet, labelled unknown meanwhile), A's point moved since its match, and an address
        // that is no longer on the map keeps a row.
        await db.ExecuteAsync(
            """
            DELETE b FROM address_building b JOIN provider_location l ON l.addr_key = b.addr_key WHERE l.npi = @E;
            UPDATE address_building b JOIN provider_location l ON l.addr_key = b.addr_key SET b.lat = b.lat + 0.01 WHERE l.npi = @A;
            INSERT INTO address_building (addr_key, matched, lat, lon) VALUES (REPEAT('f', 32), 'none', 40.0, -73.0);
            """, new { A, E });
        await labels.BuildAsync(_ct, full: true);
        Assert.Equal("unknown", await db.ScalarAsync<string>(
            "SELECT t.building_type FROM address_type t JOIN provider_location l ON l.addr_key = t.addr_key WHERE l.npi = @E", new { E }));
        await db.ExecuteAsync("UPDATE reference_data SET checked_at = checked_at + INTERVAL 1 MINUTE WHERE source = 'provider_map'");
        Assert.Equal(2L, await Npi.Loader.Datasets.FemaStructureSource.MatchNewAsync(options, db.Database, http, Logger.None, _ct));
        var incremental = await ProjectionSnapshotAsync(db, ["address_building", "address_type", "building_search"]);
        Assert.Equal(full["address_building"], incremental["address_building"]);
        Assert.Equal(full["address_type"], incremental["address_type"]);
        Assert.Equal(full["building_search"], incremental["building_search"]);
        Assert.Equal(0L, await Npi.Loader.Datasets.FemaStructureSource.MatchNewAsync(options, db.Database, http, Logger.None, _ct)); // done until the map changes again
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name LIKE 'fema\\_%'"));
    }

    [Fact]
    public async Task Overture_places_every_address_for_a_new_release_and_only_new_addresses_between_releases()
    {
        await using var db = await SeededAsync();
        // A one-state Overture release in Overture's layout: A's and B's Farmingdale address points, a place at D's address,
        // and an address point far away with D's house number and street.
        var root = Path.Combine(_folder, "overture").Replace('\\', '/') + "/";
        var addresses = $"{root}2026-09-23.1/theme=addresses/type=address";
        var places = $"{root}2026-09-23.1/theme=places/type=place";
        Directory.CreateDirectory(addresses);
        Directory.CreateDirectory(places);
        using (var duck = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:"))
        {
            duck.Open();
            using var command = duck.CreateCommand();
            command.CommandText =
                $$"""
                COPY (SELECT * FROM (VALUES
                  ('US', '1', 'BROADWAY', '11701', 'AMITYVILLE', {'xmin': -73.4151, 'ymin': 40.6791, 'xmax': -73.4151, 'ymax': 40.6791}),
                  ('US', '300', 'CONKLIN STREET', '11735', 'FARMINGDALE', {'xmin': -73.4410, 'ymin': 40.7310, 'xmax': -73.4410, 'ymax': 40.7310}),
                  ('US', '5', 'ATLANTIC AVE', '90012', 'LOS ANGELES', {'xmin': -118.2400, 'ymin': 34.0600, 'xmax': -118.2400, 'ymax': 34.0600}))
                  t(country, number, street, postcode, postal_city, bbox)) TO '{{addresses}}/part-0.parquet' (FORMAT parquet);
                COPY (SELECT {'primary': 'ATLANTIC SPINE CARE'} AS names,
                             [{'freeform': '5 ATLANTIC AVE', 'postcode': '11201', 'locality': 'BROOKLYN'}] AS addresses,
                             {'xmin': -73.9950, 'ymin': 40.6905, 'xmax': -73.9950, 'ymax': 40.6905} AS bbox,
                             0.9 AS confidence, 'open' AS operating_status,
                             [{'dataset': 'meta', 'update_time': '2026-05-01T00:00:00.000Z'}, {'dataset': 'Overture', 'update_time': '2026-09-17T22:52:12Z'}] AS sources)
                  TO '{{places}}/part-0.parquet' (FORMAT parquet);
                """;
            command.ExecuteNonQuery();
        }

        var options = new LoaderOptions { WorkFolder = _folder, LogFolder = _folder, OvertureReleasesUrl = "https://overture.test/list", OvertureBaseUrl = root };
        var matcher = new OvertureMatcher(db.Database, new HttpClient(new FakeOverture()), Logger.None, options);
        const string points = "SELECT p.addr_key, p.source, p.lat, p.lon, p.matched, p.`release` FROM address_point p ORDER BY p.addr_key";
        const string listings = "SELECT addr_key, name, operating_status, updated, `release` FROM address_place ORDER BY addr_key, name";

        // No full pass recorded: every state (NY here) is placed. A by its address point, B's Farmingdale office by its
        // ("CONKLIN STREET" = "Conklin St"), D by the place listed at its address; E and B's Brooklyn office aren't in Overture.
        var full = await matcher.RefreshAsync(_ct);
        Assert.Equal(new OvertureRefresh("2026-09-23.1", true, 5, 3), full);
        Assert.Equal(["address", "address", "place"], (await db.QueryAsync<string>("SELECT source FROM address_point ORDER BY source")).ToArray());
        Assert.Equal(("ATLANTIC SPINE CARE", "meta 2026-05-01", new DateTime(2026, 5, 1)), // Overture's own row is no source
            (await db.QueryAsync<(string, string, DateTime)>("SELECT name, sources, updated FROM address_place")).Single());
        Assert.Equal(5L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM overture_checked WHERE `release` = '2026-09-23.1'"));
        Assert.Equal("2026-09-23.1", await db.ScalarAsync<string>("SELECT version FROM reference_data WHERE source = 'overture_full'"));
        Assert.Null(await matcher.RefreshAsync(_ct)); // the same release, and the projection hasn't changed since

        // A's and D's addresses are new to Overture (as after a weekly); one pass over the release places them as the full pass did.
        var fullPoints = await db.QueryAsync<(string, string, double, double, string, string)>(points);
        var fullListings = await db.QueryAsync<(string, string, string, DateTime, string)>(listings);
        await db.ExecuteAsync(
            """
            DELETE x FROM address_point x JOIN provider_location l ON l.addr_key = x.addr_key WHERE l.npi IN (@A, @D);
            DELETE x FROM address_place x JOIN provider_location l ON l.addr_key = x.addr_key WHERE l.npi IN (@A, @D);
            DELETE x FROM overture_checked x JOIN provider_location l ON l.addr_key = x.addr_key WHERE l.npi IN (@A, @D);
            UPDATE reference_data SET checked_at = (SELECT projected_at FROM data_version WHERE id = 1) - INTERVAL 1 MINUTE WHERE source = 'overture';
            """, new { A, D });
        var loadedAt = await db.ScalarAsync<DateTime>("SELECT loaded_at FROM reference_data WHERE source = 'overture'");
        Assert.Equal(new OvertureRefresh("2026-09-23.1", false, 2, 2), await matcher.RefreshAsync(_ct));
        Assert.Equal(fullPoints, await db.QueryAsync<(string, string, double, double, string, string)>(points));
        Assert.Equal(fullListings, await db.QueryAsync<(string, string, string, DateTime, string)>(listings));
        Assert.Equal(5L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM overture_checked"));
        // Their providers are queued for the labels; only checked_at moved, so the record quality doesn't relabel everyone.
        Assert.Equal([A, D], (await db.QueryAsync<string>("SELECT npi FROM builder_pending WHERE builder = 'address_types' ORDER BY npi")).ToArray());
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM quality_pending WHERE npi IN (@A, @D)", new { A, D }));
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM address_pending"));
        Assert.Equal(loadedAt, await db.ScalarAsync<DateTime>("SELECT loaded_at FROM reference_data WHERE source = 'overture'"));
        Assert.Null(await matcher.RefreshAsync(_ct));
    }

    /// <summary>The S3 listing of Overture release folders: one release.</summary>
    private sealed class FakeOverture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.ToString() == "https://overture.test/list"
                ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("<ListBucketResult><CommonPrefixes><Prefix>release/2026-09-23.1/</Prefix></CommonPrefixes></ListBucketResult>"),
                })
                : throw new InvalidOperationException($"Unexpected request {request.RequestUri}");
    }

    [Fact]
    public async Task A_suite_or_floor_in_the_address_is_found_as_a_whole_word()
    {
        // The word boundaries must reach MySQL as \\b: a plain C# string sent a backspace and nothing matched (2026-10-13).
        await using var db = await TestDatabase.CreateAsync();
        var suites = await db.QueryAsync<long>(
            $"""
            SELECT {AddressTypeBuilder.SuiteSql("l")} FROM (
              SELECT 1 AS n, '100 Main St Ste 200' AS address1, NULL AS address2 UNION ALL
              SELECT 2, '5 Main St', 'Floor 3' UNION ALL
              SELECT 3, '1 Broadway', 'Suite 12B' UNION ALL
              SELECT 4, '1 Stevens Rd', NULL UNION ALL
              SELECT 5, '9 Florence Ave', 'Rear') l
            ORDER BY l.n
            """);
        Assert.Equal([1L, 1L, 1L, 0L, 0L], suites.ToArray());
    }

    [Fact]
    public async Task Records_are_labelled_current_check_or_stale()
    {
        await using var db = await SeededAsync();
        await new AreaCountBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct); // specialty_code_category
        var quality = new QualityBuilder(db.Database, Logger.None, 0.95) { IncrementalShare = 10 };
        Assert.True(await quality.IsStaleAsync(_ct));

        // A: not updated since 2012, no sign of practice. B: Care Compare lists another profession, and practices only in Los Angeles
        // (25+ miles from its Brooklyn and Farmingdale addresses). D: a PO box,
        // not on the map, no phone. E: Care Compare agrees, mapped, a phone, updated this month. (As of 2026-10-04, E's update.)
        await db.ExecuteAsync(
            """
            UPDATE provider SET last_update_date = '2012-01-01' WHERE npi = @A;
            UPDATE provider SET last_update_date = '2019-01-01' WHERE npi = @B;
            UPDATE provider_location SET phone = '6315550100' WHERE npi IN (@A, @B, @E) AND is_primary = 1;
            UPDATE provider_location SET address1 = 'P.O. Box 5' WHERE npi = @D;
            INSERT INTO provider_map (npi, addr_key, lat, lon, approximate, pt, source)
              SELECT npi, addr_key, 40.7, -73.4, 0, ST_SRID(POINT(-73.4, 40.7), 0), 'address' FROM provider_location WHERE npi IN (@A, @B, @E) AND is_primary = 1;
            INSERT INTO cc_clinician (npi, primary_specialty, accepts_assignment, telehealth) VALUES
              (@B, 'NURSE PRACTITIONER', 0, 0), (@E, 'PEDIATRIC MEDICINE', 1, 0);
            INSERT INTO zip_centroid (zip5, lat, lon) VALUES ('90012', 34.0614, -118.2385);
            INSERT INTO cc_clinician_location (npi, zip5, address1, city, state) VALUES
              (@B, '90012', '1 CIVIC CTR', 'LOS ANGELES', 'CA'), (@E, '11735', '200 MAIN ST', 'FARMINGDALE', 'NY');
            """, new { A, B, D, E });
        Assert.Equal(4L, await quality.BuildAsync(_ct));
        Assert.False(await quality.IsStaleAsync(_ct));

        var rows = (await db.QueryAsync<(string, sbyte, int, int)>("SELECT npi, label, reasons, activity FROM provider_quality ORDER BY npi"))
            .ToDictionary(r => r.Item1, r => (Label: (QualityLabel)r.Item2, Reasons: (QualityReason)r.Item3, Activity: (QualityActivity)r.Item4));
        Assert.Equal((QualityLabel.Stale, QualityReason.NotUpdated5Years | QualityReason.NotUpdated10Years | QualityReason.NoActivity, QualityActivity.None),
            rows[A]);
        Assert.Equal((QualityLabel.Check, QualityReason.NotUpdated5Years | QualityReason.CareCompareAddress | QualityReason.CareCompareSpecialty,
            QualityActivity.CareCompare), rows[B]);
        Assert.Equal((QualityLabel.Check, QualityReason.NoActivity | QualityReason.NotMapped | QualityReason.PoBox | QualityReason.NoPhone, QualityActivity.None),
            rows[D]);
        Assert.Equal((QualityLabel.Current, QualityReason.None, QualityActivity.CareCompare), rows[E]);
        Assert.All(rows.Values, r => Assert.Equal(RecordQuality.Classify(r.Reasons), r.Label)); // the SQL label = the C# rule

        // Search: the filter, and the label on each result.
        var search = Search(db);
        Assert.Equal([A], await Npis(search, new SearchFilter { State = "NY", Quality = "stale" }));
        Assert.Equal([B, D, E], await Npis(search, new SearchFilter { State = "NY", Quality = "notStale" }));
        Assert.Equal([E], await Npis(search, new SearchFilter { State = "NY", Quality = "Current" }));
        Assert.Equal([B, D], await Npis(search, new SearchFilter { State = "NY", Quality = "check" }));
        var page = await search.SearchAsync(new SearchFilter { State = "NY" }, _ct);
        Assert.Equal(["Not updated in 10+ years", "No sign of current practice"], page.Items.Single(i => i.Npi == A).Quality!.ReasonTexts);
        Assert.Equal(["listed in Medicare's Care Compare"], page.Items.Single(i => i.Npi == E).Quality!.Signs);

        // The profile explains it, with what Care Compare lists.
        var detail = (await new ProviderDetailService(db.ConnectionString).GetAsync(B, _ct))!;
        Assert.Equal(QualityLabel.Check, detail.Quality!.Label);
        var notes = RecordQuality.Explain(detail.Quality, detail.LastUpdateDate, new DateOnly(2026, 10, 12), true, detail.QualityCareCompare);
        Assert.Contains(new QualityNote("bad", "Medicare's Care Compare lists practices only elsewhere (25+ miles from every address here): " +
                                               "1 CIVIC CTR, LOS ANGELES, CA 90012"), notes);
        Assert.Contains(new QualityNote("bad", "Medicare's Care Compare lists another profession (Nurse Practitioner)"), notes);
        Assert.Contains(new QualityNote("warn", "Not updated in NPPES for 7 years (last update January 2019)"), notes);

        // More signs: E billed Medicare for equipment, B has a MIPS score. D (back at its street address) is listed on the map only as
        // permanently closed, and a certified facility of its name at the address was terminated (an ownership change, 07, wouldn't count).
        await db.ExecuteAsync(
            """
            UPDATE provider_location SET address1 = '5 Atlantic Ave' WHERE npi = @D;
            INSERT INTO medicare_dme_supplier (npi, data_year, beneficiaries, claims) VALUES (@E, 2024, 20, 25);
            INSERT INTO state_medicaid (npi, state, enrolled_since, provider_type) VALUES (@E, 'NY', '2007-10-26', 'PHYSICIAN');
            INSERT INTO cc_mips (npi, program_year, source, final_score) VALUES (@B, 2024, 'individual', 90);
            INSERT INTO address_place (addr_key, name, operating_status, updated, `release`)
              SELECT addr_key, 'Smith Jones Dental', 'permanently_closed', '2025-03-01', '2026-09-23.1' FROM provider_location WHERE npi = @D;
            INSERT INTO cms_pos_facility (ccn, name, category, address, city, state, zip5, active, termination_code, termination_date) VALUES
              ('330777', 'SMITH JONES DENTAL CLINIC', '21', '5 ATLANTIC AVENUE', 'BROOKLYN', 'NY', '11201', 0, '01', '2025-03-01'),
              ('330778', 'BAKER CLINIC', '21', '10 COURT ST', 'BROOKLYN', 'NY', '11201', 0, '07', '2025-03-01');
            """, new { B, D, E });
        await quality.BuildAsync(_ct, allowIncremental: false);
        rows = (await db.QueryAsync<(string, sbyte, int, int)>("SELECT npi, label, reasons, activity FROM provider_quality ORDER BY npi"))
            .ToDictionary(r => r.Item1, r => (Label: (QualityLabel)r.Item2, Reasons: (QualityReason)r.Item3, Activity: (QualityActivity)r.Item4));
        Assert.Equal((QualityLabel.Stale, QualityReason.NoActivity | QualityReason.NotMapped | QualityReason.NoPhone | QualityReason.FacilityClosed
            | QualityReason.ListedClosed, QualityActivity.None), rows[D]);
        Assert.Equal(QualityActivity.CareCompare | QualityActivity.MipsScore, rows[B].Activity);
        Assert.Equal(QualityActivity.CareCompare | QualityActivity.EquipmentBilling | QualityActivity.MedicaidEnrollment, rows[E].Activity);
        Assert.Equal(["Medicare-certified facility closed", "Listed as permanently closed"],
            RecordQuality.ShortReasons(rows[D].Reasons).Where(t => t.Contains("closed", StringComparison.Ordinal)));

        // A certified facility still active at the address (a hospital whose old unit closed): no closure there.
        await db.ExecuteAsync(
            "INSERT INTO cms_pos_facility (ccn, name, category, address, city, state, zip5, active) " +
            "VALUES ('330999', 'BROOKLYN GENERAL HOSPITAL', '01', '5 Atlantic Ave', 'BROOKLYN', 'NY', '11201', 1)");
        await quality.BuildAsync(_ct, allowIncremental: false);
        var reasons = (QualityReason)await db.ScalarAsync<int>("SELECT reasons FROM provider_quality WHERE npi = @D", new { D });
        Assert.Equal(QualityReason.ListedClosed, reasons & (QualityReason.FacilityClosed | QualityReason.ListedClosed));

        // Only state licenses changed: the queued NPIs are relabelled and nobody else. A gets a current license (no longer stale);
        // B's MIPS score goes, but B isn't queued, so it keeps its label until a full build.
        await db.ExecuteAsync(
            """
            INSERT INTO provider_state_license (npi, state, kind, source, license_number, status, expiration_date)
              VALUES (@A, 'NY', 'license', 'ny_test', '123', 'ACTIVE', '2028-01-31');
            INSERT INTO quality_pending (npi) VALUES (@A);
            DELETE FROM cc_mips WHERE npi = @B;
            INSERT INTO reference_data (source, version, source_url, rows_loaded, loaded_at, checked_at) VALUES ('state_licenses', 'v2', '', 1, @now, @now) AS new
              ON DUPLICATE KEY UPDATE loaded_at = new.loaded_at;
            UPDATE reference_data SET loaded_at = @before WHERE source = 'provider_quality';
            UPDATE data_version SET projected_at = @older;
            UPDATE reference_data SET loaded_at = @older WHERE source NOT IN ('provider_quality', 'state_licenses');
            """, new { A, B, now = DateTime.UtcNow, before = DateTime.UtcNow.AddMinutes(-1), older = DateTime.UtcNow.AddMinutes(-2) });
        Assert.True(await quality.IsStaleAsync(_ct));
        Assert.Equal(4L, await quality.BuildAsync(_ct));
        Assert.False(await quality.IsStaleAsync(_ct));
        var a = (await db.QueryAsync<(sbyte, int, int)>("SELECT label, reasons, activity FROM provider_quality WHERE npi = @A", new { A })).Single();
        Assert.Equal(QualityActivity.CurrentLicense, (QualityActivity)a.Item3);
        Assert.NotEqual(QualityLabel.Stale, (QualityLabel)a.Item1);
        Assert.Equal(RecordQuality.Classify((QualityReason)a.Item2), (QualityLabel)a.Item1);
        Assert.Equal(QualityActivity.CareCompare | QualityActivity.MipsScore,
            (QualityActivity)await db.ScalarAsync<int>("SELECT activity FROM provider_quality WHERE npi = @B", new { B }));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM quality_pending"));
        // The search rows follow A's new label: none left under the old one.
        Assert.Equal([a.Item1], await db.QueryAsync<sbyte>("SELECT DISTINCT label FROM quality_search WHERE npi = @A", new { A }));
        Assert.DoesNotContain(A, await Npis(search, new SearchFilter { State = "NY", Quality = "stale" }));

        // Nothing queued and nothing reloaded: nothing to do (B keeps its MIPS sign); a full build drops it.
        Assert.False(await quality.IsStaleAsync(_ct));
        await quality.BuildAsync(_ct);
        Assert.Equal(QualityActivity.CareCompare | QualityActivity.MipsScore,
            (QualityActivity)await db.ScalarAsync<int>("SELECT activity FROM provider_quality WHERE npi = @B", new { B }));
        await quality.BuildAsync(_ct, allowIncremental: false);
        Assert.Equal(QualityActivity.CareCompare,
            (QualityActivity)await db.ScalarAsync<int>("SELECT activity FROM provider_quality WHERE npi = @B", new { B }));

        // An open listing counts only when a source that keeps up with the business backs it (owner, 2026-10-15): D's name listed
        // open on a business registration (BrightQuery) alone is no sign of operation; on Meta it is, but a record 10+ years old with
        // only a listing is to check, not current.
        async Task<(QualityReason Reasons, QualityActivity Activity)> DAsync()
        {
            await quality.BuildAsync(_ct, allowIncremental: false);
            var d = (await db.QueryAsync<(int, int)>("SELECT reasons, activity FROM provider_quality WHERE npi = @D", new { D })).Single();
            return ((QualityReason)d.Item1, (QualityActivity)d.Item2);
        }

        await db.ExecuteAsync(
            """
            UPDATE address_place SET operating_status = 'open', sources = 'BrightQuery 2026-09-17' WHERE name = 'Smith Jones Dental';
            UPDATE provider SET last_update_date = '2012-05-01' WHERE npi = @D;
            """, new { D });
        var registered = await DAsync();
        Assert.Equal(QualityActivity.None, registered.Activity);
        Assert.Equal(QualityReason.NoActivity | QualityReason.NotUpdated10Years, registered.Reasons & (QualityReason.NoActivity | QualityReason.NotUpdated10Years | QualityReason.ListedClosed));
        await db.ExecuteAsync("UPDATE address_place SET sources = 'BrightQuery 2026-09-17;meta 2026-09-14' WHERE name = 'Smith Jones Dental'");
        var listed = await DAsync();
        Assert.Equal(QualityActivity.MapListing, listed.Activity);
        Assert.Equal(QualityReason.ListingOnly, listed.Reasons & (QualityReason.ListingOnly | QualityReason.NoActivity));
        Assert.Contains("Only a map listing shows it open", RecordQuality.ShortReasons(listed.Reasons));
        await db.ExecuteAsync("UPDATE provider SET last_update_date = '2024-05-01' WHERE npi = @D", new { D });
        Assert.Equal(QualityReason.None, (await DAsync()).Reasons & QualityReason.ListingOnly); // a recent record keeps the listing as its sign
    }

    [Fact]
    public async Task A_dataset_reload_relabels_only_the_providers_whose_rows_changed()
    {
        await using var db = await SeededAsync();
        await new AreaCountBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct); // specialty_code_category
        await db.ExecuteAsync("INSERT INTO medicare_order_referring (npi, part_b) VALUES (@A, 1), (@B, 1)", new { A, B });
        var quality = new QualityBuilder(db.Database, Logger.None, 0.95) { IncrementalShare = 10 };
        await quality.BuildAsync(_ct, allowIncremental: false);
        const string loadedAt = "SELECT CAST(loaded_at AS CHAR) FROM reference_data WHERE source = 'provider_quality'";
        var before = await db.ScalarAsync<string>(loadedAt);

        // The order-and-refer list reloads: A left it, E joined, B's row is the same (in another order, with another id).
        await using (var connection = await db.Database.OpenAsync(_ct))
        {
            await Npi.Loader.Datasets.TableSwap.ReplaceAsync(connection, ["medicare_order_referring"], 0, () => Npi.Loader.Db.Database.ExecuteAsync(connection,
                "INSERT INTO medicare_order_referring_staging (npi, part_b) VALUES (@E, 1), (@B, 1)", _ct, param: new { B, E }), _ct);
        }

        Assert.Equal([A, E], await db.QueryAsync<string>("SELECT npi FROM quality_pending ORDER BY npi"));
        await db.ExecuteAsync(
            """
            INSERT INTO reference_data (source, version, source_url, rows_loaded, loaded_at, checked_at) VALUES ('cms_order_referring', 'v2', '', 2, @now, @now) AS new
              ON DUPLICATE KEY UPDATE loaded_at = new.loaded_at
            """, new { now = DateTime.UtcNow });
        Assert.True(await quality.IsStaleAsync(_ct));
        await quality.BuildAsync(_ct);
        Assert.Equal(before, await db.ScalarAsync<string>(loadedAt)); // relabelled the queue only
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM quality_pending"));
        Assert.False(await quality.IsStaleAsync(_ct));
        var incremental = await ProjectionSnapshotAsync(db, ["provider_quality", "quality_search"]);

        await quality.BuildAsync(_ct, allowIncremental: false);
        var full = await ProjectionSnapshotAsync(db, ["provider_quality", "quality_search"]);
        Assert.Equal(full["provider_quality"], incremental["provider_quality"]);
        Assert.Equal(full["quality_search"], incremental["quality_search"]);
        Assert.Equal(QualityActivity.OrderRefer,
            (QualityActivity)await db.ScalarAsync<int>("SELECT activity FROM provider_quality WHERE npi = @E", new { E }) & QualityActivity.OrderRefer);
    }

    [Fact]
    public async Task Older_medicare_years_load_once_and_a_changed_year_relabels_only_its_changed_providers()
    {
        await using var db = await SeededAsync();
        await new AreaCountBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct); // specialty_code_category
        // A, B and E otherwise look current: updated this year, mapped, with a phone. The newest data year is 2024 (B prescribed then).
        await db.ExecuteAsync(
            """
            UPDATE provider SET last_update_date = '2026-01-01' WHERE npi IN (@A, @B, @E);
            UPDATE provider_location SET phone = '6315550100' WHERE npi IN (@A, @B, @E) AND is_primary = 1;
            INSERT INTO provider_map (npi, addr_key, lat, lon, approximate, pt, source)
              SELECT npi, addr_key, 40.7, -73.4, 0, ST_SRID(POINT(-73.4, 40.7), 0), 'address' FROM provider_location WHERE npi IN (@A, @B, @E) AND is_primary = 1;
            INSERT INTO medicare_part_d (npi, data_year, claims) VALUES (@B, 2024, 300);
            INSERT INTO state_medicaid (npi, state, enrolled_since, provider_type) VALUES (@E, 'NY', '2007-10-26', 'PHYSICIAN');
            INSERT INTO reference_data (source, version, source_url, rows_loaded, loaded_at, checked_at) VALUES
              ('cms_part_d_by_provider', '2024-12-31 mup_dpr_dy24_npi.csv', '', 1, UTC_TIMESTAMP(), UTC_TIMESTAMP());
            """, new { A, B, E });
        var quality = new QualityBuilder(db.Database, Logger.None, 0.95) { IncrementalShare = 10 };
        await quality.BuildAsync(_ct, allowIncremental: false);
        Assert.Equal((sbyte)QualityLabel.Current, await db.ScalarAsync<sbyte>("SELECT label FROM provider_quality WHERE npi = @A", new { A }));
        const string loadedAt = "SELECT CAST(loaded_at AS CHAR) FROM reference_data WHERE source = 'provider_quality'";
        var labelledAt = await db.ScalarAsync<string>(loadedAt);

        // Part B 2014 in CMS's old column names, 2015 and 2016 in today's; Part D 2016; 2024 is each part's newest year (not history).
        var server = new FakeMedicareCms();
        server.Files["https://cms.test/files/2025-11/a/MUP_PHY_D14_Prov.csv"] = $"npi,nppes_provider_last_org_name,total_services,total_unique_benes\n{E},NUNEZ,120,40\n";
        server.Files["https://cms.test/files/2025-11/b/MUP_PHY_D15_Prov.csv"] = $"Rndrng_NPI,Rndrng_Prvdr_Last_Org_Name,Tot_Benes,Tot_Srvcs\n{A},OBRIEN,12,30\n1999999993,GONE,15,40\n";
        server.Files["https://cms.test/files/2025-11/c/MUP_PHY_D16_Prov.csv"] = $"Rndrng_NPI,Rndrng_Prvdr_Last_Org_Name,Tot_Benes,Tot_Srvcs\n{A},OBRIEN,15,31\n";
        server.Files["https://cms.test/files/2026-08/d/mup_dpr_dy16_npi.csv"] = $"Prscrbr_NPI,Prscrbr_Last_Org_Name,Tot_Clms,Tot_Benes\n{B},BAKER,100,\n{A},OBRIEN,20,\n";
        server.Releases.AddRange(
        [
            (Npi.Loader.Datasets.MedicareHistoryPart.PartB.Title, 2014, "https://cms.test/files/2025-11/a/MUP_PHY_D14_Prov.csv", "2025-03-26"),
            (Npi.Loader.Datasets.MedicareHistoryPart.PartB.Title, 2015, "https://cms.test/files/2025-11/b/MUP_PHY_D15_Prov.csv", "2025-03-26"),
            (Npi.Loader.Datasets.MedicareHistoryPart.PartB.Title, 2016, "https://cms.test/files/2025-11/c/MUP_PHY_D16_Prov.csv", "2025-03-26"),
            (Npi.Loader.Datasets.MedicareHistoryPart.PartB.Title, 2024, "https://cms.test/files/2026-05/e/MUP_PHY_D24_Prov.csv", "2026-05-21"),
            (Npi.Loader.Datasets.MedicareHistoryPart.PartD.Title, 2016, "https://cms.test/files/2026-08/d/mup_dpr_dy16_npi.csv", "2026-08-03"),
            (Npi.Loader.Datasets.MedicareHistoryPart.PartD.Title, 2024, "https://cms.test/files/2026-08/f/mup_dpr_dy24_npi.csv", "2026-08-03"),
        ]);
        var clock = new SteppedClock();
        Npi.Loader.Datasets.DatasetLoader History() => new(
            new LoaderOptions { WorkFolder = _folder, LogFolder = _folder, DownloadAttempts = 1, CmsCatalogUrl = "https://cms.test/data.json" },
            db.Database, new HttpClient(server), Logger.None, [new Npi.Loader.Datasets.MedicareHistorySource()], clock);

        // The first load reads every older year, once, and queues the providers in them.
        Assert.True(await History().RefreshAsync(force: false, only: null, _ct));
        Assert.Equal(["https://cms.test/data.json", "https://cms.test/files/2025-11/a/MUP_PHY_D14_Prov.csv", "https://cms.test/files/2025-11/b/MUP_PHY_D15_Prov.csv",
                      "https://cms.test/files/2025-11/c/MUP_PHY_D16_Prov.csv", "https://cms.test/files/2026-08/d/mup_dpr_dy16_npi.csv"], server.Requests);
        const string rows = "SELECT data_year, npi, parts, part_b_beneficiaries, part_d_claims FROM medicare_activity_year ORDER BY data_year, npi";
        Assert.Equal(
            [((short)2014, E, (sbyte)1, (int?)40, (int?)null), ((short)2015, A, (sbyte)1, 12, null), ((short)2015, "1999999993", (sbyte)1, 15, null),
             ((short)2016, A, (sbyte)3, 15, 20), ((short)2016, B, (sbyte)2, null, 100)],
            await db.QueryAsync<(short, string, sbyte, int?, int?)>(rows));
        Assert.Equal([A, B, E, "1999999993"], await db.QueryAsync<string>("SELECT npi FROM quality_pending ORDER BY npi"));
        Assert.Matches("^2014-2016 [0-9a-f]{12}$", await db.ScalarAsync<string>("SELECT version FROM reference_data WHERE source = 'medicare_history'"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "datasets"))); // downloads are deleted after loading

        // Relabelled: A billed Medicare until 2016 and shows nothing current, so it needs a check; E stopped too but is enrolled in
        // Medicaid (a note only); B prescribed in 2024 (not stopped).
        Assert.True(await quality.IsStaleAsync(_ct));
        await quality.BuildAsync(_ct);
        Assert.Equal(labelledAt, await db.ScalarAsync<string>(loadedAt)); // the queue only
        var labels = (await db.QueryAsync<(string, sbyte, int)>("SELECT npi, label, reasons FROM provider_quality WHERE npi IN (@A, @B, @E) ORDER BY npi", new { A, B, E }))
            .ToDictionary(r => r.Item1, r => (Label: (QualityLabel)r.Item2, Reasons: (QualityReason)r.Item3));
        Assert.Equal((QualityLabel.Check, QualityReason.NoActivity | QualityReason.MedicareStopped), labels[A]);
        Assert.Equal((QualityLabel.Current, QualityReason.None), labels[B]);
        Assert.Equal((QualityLabel.Current, QualityReason.MedicareStopped), labels[E]);
        Assert.All(labels.Values, l => Assert.Equal(RecordQuality.Classify(l.Reasons), l.Label)); // the SQL label = the C# rule

        // The profile: every year since the first one loaded, the newest from the newest-year tables, and the note.
        var a = (await new ProviderDetailService(db.ConnectionString).GetAsync(A, _ct))!;
        Assert.Equal((2014, 2024, true), (a.MedicareHistory!.FirstDataYear, a.MedicareHistory.NewestDataYear, a.MedicareHistory.Stopped));
        Assert.Equal([new MedicareHistoryYear(2015, true, false, 12, null), new MedicareHistoryYear(2016, true, true, 15, 20)], a.MedicareHistory.Years);
        Assert.Contains(new QualityNote("bad", "Last billed Medicare in 2016 (Part B and Part D); none in the CMS data for 2017–2024"),
            RecordQuality.Explain(a.Quality!, a.LastUpdateDate, new DateOnly(2026, 10, 12), true, medicare: a.MedicareHistory));
        var b = (await new ProviderDetailService(db.ConnectionString).GetAsync(B, _ct))!;
        Assert.Equal([new MedicareHistoryYear(2016, false, true, null, 100), new MedicareHistoryYear(2024, false, true, null, 300)], b.MedicareHistory!.Years);
        Assert.False(b.MedicareHistory.Stopped);
        Assert.Null((await new ProviderDetailService(db.ConnectionString).GetAsync(D, _ct))!.MedicareHistory);

        // Nothing changed: even a forced reload asks only for the catalog.
        server.Requests.Clear();
        Assert.True(await History().RefreshAsync(force: true, only: null, _ct));
        Assert.Equal(["https://cms.test/data.json"], server.Requests);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM quality_pending"));

        // CMS republishes 2015 in a new folder: A the same, E added. Only that file is fetched and only E is queued.
        server.Releases[1] = (Npi.Loader.Datasets.MedicareHistoryPart.PartB.Title, 2015, "https://cms.test/files/2026-12/g/MUP_PHY_D15_Prov.csv", "2026-12-01");
        server.Files["https://cms.test/files/2026-12/g/MUP_PHY_D15_Prov.csv"] = $"Rndrng_NPI,Rndrng_Prvdr_Last_Org_Name,Tot_Benes,Tot_Srvcs\n{A},OBRIEN,12,30\n1999999993,GONE,15,40\n{E},NUNEZ,11,12\n";
        server.Requests.Clear();
        clock.Advance(TimeSpan.FromDays(1));
        Assert.True(await History().RefreshAsync(force: false, only: null, _ct));
        Assert.Equal(["https://cms.test/data.json", "https://cms.test/files/2026-12/g/MUP_PHY_D15_Prov.csv"], server.Requests);
        Assert.Equal([E], await db.QueryAsync<string>("SELECT npi FROM quality_pending"));
        Assert.Equal(((sbyte)1, (int?)11), (await db.QueryAsync<(sbyte, int?)>(
            "SELECT parts, part_b_beneficiaries FROM medicare_activity_year WHERE data_year = 2015 AND npi = @E", new { E })).Single());
        Assert.Equal("/files/2026-12/g/MUP_PHY_D15_Prov.csv", await db.ScalarAsync<string>(
            "SELECT version FROM medicare_history_version WHERE part = 'B' AND data_year = 2015"));
        Assert.True(await quality.IsStaleAsync(_ct));
        await quality.BuildAsync(_ct);
        Assert.Equal(labelledAt, await db.ScalarAsync<string>(loadedAt));
        var incremental = await ProjectionSnapshotAsync(db, ["provider_quality", "quality_search"]);
        await quality.BuildAsync(_ct, allowIncremental: false);
        var full = await ProjectionSnapshotAsync(db, ["provider_quality", "quality_search"]);
        Assert.Equal(full["provider_quality"], incremental["provider_quality"]);
        Assert.Equal(full["quality_search"], incremental["quality_search"]);

        // A year with far fewer NPIs than before is refused (≥ 95% check), and a file in an unknown layout fails clearly; both keep the data.
        server.Releases[2] = (Npi.Loader.Datasets.MedicareHistoryPart.PartB.Title, 2016, "https://cms.test/files/2026-12/h/MUP_PHY_D16_Prov.csv", "2026-12-01");
        server.Files["https://cms.test/files/2026-12/h/MUP_PHY_D16_Prov.csv"] = "Rndrng_NPI,Tot_Benes\n";
        server.Releases[4] = (Npi.Loader.Datasets.MedicareHistoryPart.PartD.Title, 2016, "https://cms.test/files/2026-12/i/mup_dpr_dy16_npi.csv", "2026-12-01");
        server.Files["https://cms.test/files/2026-12/i/mup_dpr_dy16_npi.csv"] = $"Prescriber_NPI,Total_Claims\n{B},100\n";
        var before = await db.QueryAsync<(short, string, sbyte, int?, int?)>(rows);
        Assert.False(await History().RefreshAsync(force: true, only: null, _ct));
        Assert.Equal(before, await db.QueryAsync<(short, string, sbyte, int?, int?)>(rows));
        Assert.Equal("/files/2025-11/c/MUP_PHY_D16_Prov.csv", await db.ScalarAsync<string>(
            "SELECT version FROM medicare_history_version WHERE part = 'B' AND data_year = 2016"));
    }

    [Fact]
    public async Task Medicaid_enrollment_and_billing_count_for_two_years_and_a_reload_relabels_only_its_changed_providers()
    {
        await using var db = await SeededAsync();
        await new AreaCountBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct); // specialty_code_category
        // A, B, D and E: not updated in 10+ years, so with no sign of practice they're stale. The data is as of 2026-10-11, so a
        // Medicaid period or year counts when it ended on or after 2024-10-11 (the T-MSIS files end in December 2024).
        await db.ExecuteAsync(
            """
            UPDATE provider SET last_update_date = '2012-01-01' WHERE npi IN (@A, @B, @D, @E);
            UPDATE data_version SET as_of_date = '2026-10-11' WHERE id = 1;
            INSERT INTO medicaid_data_period (source, first_month, last_month) VALUES
              ('medicaid_enrollment_segments', '2018-01-01', '2024-12-01'), ('medicaid_spending', '2018-01-01', '2024-12-01');
            INSERT INTO medicaid_enrollment_period (npi, state, period, enrolled_from, enrolled_until, ended_status) VALUES
              (@A, 'NY', 1, '2024-08-01', NULL, NULL),
              (@A, 'OH', 1, '2015-03-01', '2019-06-30', 'TERM - LICENSE EXPIRED'),
              (@B, 'CA', 1, NULL, '2023-05-31', NULL),
              (@E, 'TX', 1, '2018-01-01', '2024-11-30', 'TERM - VOLUNTARY TERMINATION');
            INSERT INTO medicaid_billing_year (npi, data_year, claim_lines, paid, billing_lines, billing_paid, servicing_lines, servicing_paid) VALUES
              (@A, 2021, 14, 700, 14, 700, 14, 700), (@B, 2024, 40, 1250.50, 0, 0, 40, 1250.50), (@D, 2019, 12, 300, 12, 300, 0, 0);
            INSERT INTO medicaid_top_procedure (npi, procedure_rank, data_year, hcpcs, description, claim_lines, paid) VALUES
              (@B, 1, 2024, '99213', 'Established patient office visit', 40, 1250.50);
            """, new { A, B, D, E });
        var quality = new QualityBuilder(db.Database, Logger.None, 0.95) { IncrementalShare = 10 };
        await quality.BuildAsync(_ct, allowIncremental: false);
        async Task<Dictionary<string, (QualityLabel Label, QualityReason Reasons, QualityActivity Activity)>> LabelsAsync() =>
            (await db.QueryAsync<(string, sbyte, int, int)>("SELECT npi, label, reasons, activity FROM provider_quality ORDER BY npi"))
            .ToDictionary(r => r.Item1, r => (Label: (QualityLabel)r.Item2, Reasons: (QualityReason)r.Item3, Activity: (QualityActivity)r.Item4));
        var labels = await LabelsAsync();
        // A: still enrolled in NY at the end of the data; E: enrolled in TX until November 2024; B: billed in 2024 (its CA period ended
        // in 2023, too long ago); D: billed only in 2019, so still stale.
        Assert.Equal(QualityActivity.MedicaidEnrollment, labels[A].Activity);
        Assert.Equal(QualityActivity.MedicaidBilling, labels[B].Activity);
        Assert.Equal(QualityActivity.MedicaidEnrollment, labels[E].Activity);
        Assert.Equal((QualityLabel.Stale, QualityActivity.None), (labels[D].Label, labels[D].Activity));
        Assert.All(new[] { A, B, E }, n => Assert.Equal(QualityReason.None, labels[n].Reasons & QualityReason.NoActivity));
        Assert.All(labels.Values, l => Assert.Equal(RecordQuality.Classify(l.Reasons), l.Label)); // the SQL label = the C# rule
        Assert.Equal(["billed Medicaid"], RecordQuality.SignTexts(labels[B].Activity));

        // The profile: enrollment periods by state with why one ended, billing by year, the newest year's procedures; D billed once.
        var a = (await new ProviderDetailService(db.ConnectionString).GetAsync(A, _ct))!.MedicaidHistory!;
        Assert.Equal((new DateOnly(2018, 1, 1), new DateOnly(2024, 12, 31), 2018, 2024), (a.EnrollmentFrom!.Value, a.EnrollmentThrough!.Value, a.FirstDataYear!.Value, a.NewestDataYear!.Value));
        Assert.Equal([new MedicaidEnrollmentPeriod("NY", new DateOnly(2024, 8, 1), null, null),
                      new MedicaidEnrollmentPeriod("OH", new DateOnly(2015, 3, 1), new DateOnly(2019, 6, 30), "TERM - LICENSE EXPIRED")], a.Enrollments);
        Assert.Equal("license expired", a.Enrollments[1].EndedReason);
        Assert.Equal([new MedicaidBillingYear(2021, 14, 700, 14, 700, 14, 700)], a.Years);
        Assert.True(a.Stopped);
        var b = (await new ProviderDetailService(db.ConnectionString).GetAsync(B, _ct))!.MedicaidHistory!;
        Assert.Equal([new MedicaidProcedure("99213", "Established patient office visit", 40, 1250.50)], b.TopProcedures);
        Assert.Equal((2024, false), (b.TopProceduresYear!.Value, b.Stopped));
        Assert.Empty((await new ProviderDetailService(db.ConnectionString).GetAsync(D, _ct))!.MedicaidHistory!.Enrollments);
        Assert.Contains("billed Medicaid", (await new ProviderDetailService(db.ConnectionString).GetAsync(B, _ct))!.Quality!.Signs);

        // The spending file reloads: B's 2024 is the same, D billed again in 2024. Only D is queued and relabelled.
        await using (var connection = await db.Database.OpenAsync(_ct))
        {
            await Npi.Loader.Datasets.TableSwap.ReplaceAsync(connection, ["medicaid_billing_year"], 0, () => Npi.Loader.Db.Database.ExecuteAsync(connection,
                """
                INSERT INTO medicaid_billing_year_staging (npi, data_year, claim_lines, paid, billing_lines, billing_paid, servicing_lines, servicing_paid) VALUES
                  (@D, 2024, 12, 360, 12, 360, 0, 0), (@D, 2019, 12, 300, 12, 300, 0, 0), (@A, 2021, 14, 700, 14, 700, 14, 700), (@B, 2024, 40, 1250.50, 0, 0, 40, 1250.50)
                """, _ct, param: new { A, B, D }), _ct);
        }

        Assert.Equal([D], await db.QueryAsync<string>("SELECT npi FROM quality_pending ORDER BY npi"));
        await db.ExecuteAsync(
            """
            INSERT INTO reference_data (source, version, source_url, rows_loaded, loaded_at, checked_at) VALUES ('medicaid_spending', 'v2', '', 4, @now, @now) AS new
              ON DUPLICATE KEY UPDATE loaded_at = new.loaded_at
            """, new { now = DateTime.UtcNow });
        const string loadedAt = "SELECT CAST(loaded_at AS CHAR) FROM reference_data WHERE source = 'provider_quality'";
        var labelledAt = await db.ScalarAsync<string>(loadedAt);
        Assert.True(await quality.IsStaleAsync(_ct));
        await quality.BuildAsync(_ct);
        Assert.Equal(labelledAt, await db.ScalarAsync<string>(loadedAt)); // the queue only
        Assert.Equal(QualityActivity.MedicaidBilling, (await LabelsAsync())[D].Activity);

        // The as-of date moves past two years after the data's end: the Medicaid signs expire, and the providers they cleared are
        // queued and relabelled like a full build would.
        await db.ExecuteAsync("UPDATE data_version SET as_of_date = '2027-01-05' WHERE id = 1");
        await quality.BuildAsync(_ct);
        Assert.Equal(labelledAt, await db.ScalarAsync<string>(loadedAt));
        var incremental = await ProjectionSnapshotAsync(db, ["provider_quality", "quality_search"]);
        var expired = await LabelsAsync();
        Assert.All(new[] { A, B, D, E }, n => Assert.Equal((QualityLabel.Stale, QualityActivity.None), (expired[n].Label, expired[n].Activity)));
        await quality.BuildAsync(_ct, allowIncremental: false);
        var full = await ProjectionSnapshotAsync(db, ["provider_quality", "quality_search"]);
        Assert.Equal(full["provider_quality"], incremental["provider_quality"]);
        Assert.Equal(full["quality_search"], incremental["quality_search"]);
    }

    [Fact]
    public async Task The_label_sql_matches_the_csharp_rule_for_every_reason_combination()
    {
        // LabelSql run by MySQL for all 2^16 values of the low 16 reason bits (every reason there is, MedicareStopped = 32768 included).
        await using var db = await TestDatabase.CreateAsync();
        var labels = await db.QueryAsync<(long Reasons, long Label)>(
            $"""
            SELECT CAST(n.r AS SIGNED), CAST({RecordQuality.LabelSql("n.r")} AS SIGNED) FROM (
              SELECT a.d + 16 * b.d + 256 * c.d + 4096 * e.d AS r
              FROM (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7
                    UNION ALL SELECT 8 UNION ALL SELECT 9 UNION ALL SELECT 10 UNION ALL SELECT 11 UNION ALL SELECT 12 UNION ALL SELECT 13 UNION ALL SELECT 14 UNION ALL SELECT 15) a
              CROSS JOIN (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7
                    UNION ALL SELECT 8 UNION ALL SELECT 9 UNION ALL SELECT 10 UNION ALL SELECT 11 UNION ALL SELECT 12 UNION ALL SELECT 13 UNION ALL SELECT 14 UNION ALL SELECT 15) b
              CROSS JOIN (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7
                    UNION ALL SELECT 8 UNION ALL SELECT 9 UNION ALL SELECT 10 UNION ALL SELECT 11 UNION ALL SELECT 12 UNION ALL SELECT 13 UNION ALL SELECT 14 UNION ALL SELECT 15) c
              CROSS JOIN (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7
                    UNION ALL SELECT 8 UNION ALL SELECT 9 UNION ALL SELECT 10 UNION ALL SELECT 11 UNION ALL SELECT 12 UNION ALL SELECT 13 UNION ALL SELECT 14 UNION ALL SELECT 15) e
            ) n
            """);
        Assert.Equal(65536, labels.Count());
        Assert.All(labels, l => Assert.Equal((long)RecordQuality.Classify((QualityReason)l.Reasons), l.Label));
    }

    /// <summary>A data.cms.gov catalog with the given yearly releases, and their CSV files.</summary>
    private sealed class FakeMedicareCms : HttpMessageHandler
    {
        public List<(string Title, int Year, string Url, string Modified)> Releases { get; } = [];

        public Dictionary<string, string> Files { get; } = [];

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            var body = url == "https://cms.test/data.json" ? MedicareHistoryTests.CatalogJson(Releases) : Files.GetValueOrDefault(url);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    /// <summary>A clock the test moves forward (past the sources' check interval).</summary>
    private sealed class SteppedClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>The Source Cooperative listing of the FEMA USA Structures files and the two fixture files.</summary>
    private sealed class FakeFema : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            HttpContent content = url == "https://fema.test/list"
                ? new ByteArrayContent(File.ReadAllBytes(Fixtures.Path("datasets/fema_listing_sample.xml")))
                : url.StartsWith("https://fema.test/files/usa-structures/geoparquet/", StringComparison.Ordinal)
                    ? new ByteArrayContent(File.ReadAllBytes(Fixtures.Path("datasets/" + url[(url.LastIndexOf('/') + 1)..])))
                    : throw new InvalidOperationException($"Unexpected request {url}");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>The Census batch geocoder: 1 Broadway and 200 Main St match, 300 Conklin St is a tie, the rest don't match.</summary>
    private sealed class FakeCensus : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var answer = new System.Text.StringBuilder();
            foreach (var line in body.Split("\r\n").Where(l => l.Length > 0 && char.IsAsciiDigit(l[0])))
            {
                var parts = line.Split(',');
                var (id, street) = (parts[0], parts[1]);
                answer.AppendLine(street switch
                {
                    "1 Broadway" => $"\"{id}\",\"{street}\",\"Match\",\"Exact\",\"1 BROADWAY, AMITYVILLE, NY, 11701\",\"-73.4150,40.6790\",\"1\",\"L\"",
                    "200 Main St" => $"\"{id}\",\"{street}\",\"Match\",\"Non_Exact\",\"200 MAIN ST, FARMINGDALE, NY, 11735\",\"-73.4450,40.7330\",\"2\",\"R\"",
                    "300 Conklin St" => $"\"{id}\",\"{street}\",\"Tie\"",
                    _ => $"\"{id}\",\"{street}\",\"No_Match\"",
                });
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(answer.ToString()) };
        }
    }

    private static SearchService Search(TestDatabase db) => new(db.ConnectionString, new TaxonomyCatalog(db.ConnectionString));

    private async Task<string[]> Npis(SearchService search, SearchFilter filter) =>
        (await search.SearchAsync(filter, _ct)).Items.Select(i => i.Npi).Order().ToArray();

    private async Task<TestDatabase> SeededAsync()
    {
        var db = await TestDatabase.CreateAsync();
        await db.ExecuteAsync(
            """
            INSERT INTO taxonomy_codes (Taxonomy_Code, `Grouping`, Classification, Specialization, Display_Name, Section, Nucc_Version) VALUES
              ('111N00000X', 'Chiropractic Providers', 'Chiropractor', NULL, 'Chiropractor', 'Individual', '261'),
              ('111NS0005X', 'Chiropractic Providers', 'Chiropractor', 'Sports Physician', 'Sports Chiropractor', 'Individual', '261'),
              ('122300000X', 'Dental Providers', 'Dentist', NULL, 'Dentist', 'Individual', '261'),
              ('208000000X', 'Allopathic & Osteopathic Physicians', 'Pediatrics', NULL, 'Pediatrician', 'Individual', '261');
            INSERT INTO county (county_fips, state, county_name, source) VALUES
              ('36103', 'NY', 'Suffolk County', 'census-gazetteer'), ('36059', 'NY', 'Nassau County', 'census-gazetteer'),
              ('36047', 'NY', 'Kings County', 'census-gazetteer');
            INSERT INTO zip_county (zip5, county_fips, res_ratio, bus_ratio, oth_ratio, tot_ratio, usps_city, usps_state, year, quarter) VALUES
              ('11701', '36103', 0.9, 0.9, 0.9, 0.9, 'AMITYVILLE', 'NY', 2026, 2), ('11701', '36059', 0.1, 0.1, 0.1, 0.1, 'AMITYVILLE', 'NY', 2026, 2),
              ('11735', '36103', 1, 1, 1, 1, 'FARMINGDALE', 'NY', 2026, 2), ('11201', '36047', 1, 1, 1, 1, 'BROOKLYN', 'NY', 2026, 2);
            INSERT INTO zip_centroid (zip5, lat, lon) VALUES
              ('11701', 40.682177, -73.414596), ('11735', 40.730000, -73.440000), ('11201', 40.694000, -73.990000);
            """);

        var zip = new NppesZipBuilder()
            .Provider(A, ("Entity_Type_Code", "1"), ("Provider_Last_Name_Legal_Name", "O'BRIEN"), ("Provider_First_Name", "JOSÉ"),
                ("Provider_Middle_Name", "Q"), ("Provider_Name_Suffix_Text", "JR"), ("Provider_Credential_Text", "D.C."), ("Provider_Gender_Code", "M"),
                ("Healthcare_Provider_Taxonomy_Code_1", Chiro), ("Healthcare_Provider_Primary_Taxonomy_Switch_1", "Y"),
                ("Healthcare_Provider_Taxonomy_Code_2", Pediatrics), ("Healthcare_Provider_Taxonomy_Code_3", Chiro),
                ("Provider_First_Line_Business_Practice_Location_Address", "1 Broadway"),
                ("Provider_Business_Practice_Location_Address_City_Name", "AMITYVILLE"), ("Provider_Business_Practice_Location_Address_State_Name", "NY"),
                ("Provider_Business_Practice_Location_Address_Postal_Code", "117014321"), ("Provider_Business_Practice_Location_Address_Country_Code", "US"),
                ("Provider_Enumeration_Date", "05/23/2005"), ("Last_Update_Date", "09/01/2026"))
            .Provider(B, ("Entity_Type_Code", "1"), ("Provider_Last_Name_Legal_Name", "BAKER"), ("Provider_First_Name", "AL"), ("Provider_Credential_Text", "M.D."),
                ("Provider_Gender_Code", "M"),
                ("Healthcare_Provider_Taxonomy_Code_1", ChiroSports), ("Healthcare_Provider_Primary_Taxonomy_Switch_1", "Y"),
                ("Provider_First_Line_Business_Practice_Location_Address", "10 Court St"),
                ("Provider_Business_Practice_Location_Address_City_Name", "BROOKLYN"), ("Provider_Business_Practice_Location_Address_State_Name", "NY"),
                ("Provider_Business_Practice_Location_Address_Postal_Code", "11201"), ("Last_Update_Date", "08/01/2026"))
            .Provider(C, ("Entity_Type_Code", "1"), ("Provider_Last_Name_Legal_Name", "CARTER"),
                ("Healthcare_Provider_Taxonomy_Code_1", Chiro), ("Provider_Business_Practice_Location_Address_Postal_Code", "11701"),
                ("Provider_Business_Practice_Location_Address_State_Name", "NY"), ("NPI_Deactivation_Date", "01/01/2025"))
            .Provider(D, ("Entity_Type_Code", "2"), ("Provider_Organization_Name_Legal_Business_Name", "SMITH, JONES & CO, LLC"),
                ("Authorized_Official_Name_Prefix_Text", "DR."), ("Authorized_Official_First_Name", "PAT"), ("Authorized_Official_Last_Name", "SMITH"),
                ("Authorized_Official_Credential_Text", "DDS"), ("Authorized_Official_Title_or_Position", "OWNER, PRESIDENT"),
                ("Authorized_Official_Telephone_Number", "7185550100"),
                ("Is_Organization_Subpart", "Y"), ("Parent_Organization_LBN", "SMITH HOLDINGS INC"), ("Parent_Organization_TIN", "<UNAVAIL>"),
                ("Provider_First_Line_Business_Mailing_Address", "PO BOX 12"), ("Provider_Business_Mailing_Address_City_Name", "BROOKLYN"),
                ("Provider_Business_Mailing_Address_State_Name", "NY"), ("Provider_Business_Mailing_Address_Postal_Code", "112010012"),
                ("Provider_Business_Mailing_Address_Country_Code", "US"), ("Provider_Business_Mailing_Address_Fax_Number", "7185550199"),
                ("Other_Provider_Identifier_1", "MCD-123"), ("Other_Provider_Identifier_Type_Code_1", "05"), ("Other_Provider_Identifier_State_1", "NY"),
                ("Other_Provider_Identifier_2", "X9"), ("Other_Provider_Identifier_Type_Code_2", "01"), ("Other_Provider_Identifier_Issuer_2", "BLUE PLAN"),
                ("Healthcare_Provider_Taxonomy_Code_1", Dentist), ("Healthcare_Provider_Primary_Taxonomy_Switch_1", "Y"),
                ("Provider_First_Line_Business_Practice_Location_Address", "5 Atlantic Ave"),
                ("Provider_Business_Practice_Location_Address_City_Name", "BROOKLYN"), ("Provider_Business_Practice_Location_Address_State_Name", "NY"),
                ("Provider_Business_Practice_Location_Address_Postal_Code", "112010001"), ("Last_Update_Date", "07/01/2026"))
            .Provider(E, ("Entity_Type_Code", "1"), ("Provider_Last_Name_Legal_Name", "NUÑEZ"), ("Provider_First_Name", "ELENA"),
                ("Provider_Credential_Text", "MD, PhD"), ("Provider_Gender_Code", "F"),
                ("Healthcare_Provider_Taxonomy_Code_1", Pediatrics), ("Healthcare_Provider_Primary_Taxonomy_Switch_1", "Y"),
                ("Provider_First_Line_Business_Practice_Location_Address", "200 Main St"),
                ("Provider_Business_Practice_Location_Address_City_Name", "FARMINGDALE"), ("Provider_Business_Practice_Location_Address_State_Name", "NY"),
                ("Provider_Business_Practice_Location_Address_Postal_Code", "11735"), ("Last_Update_Date", "10/04/2026"))
            .Location(B, "300 Conklin St", "", "FARMINGDALE", "NY", "117351234", "US", "5165550100")
            .OtherName(D, "SJC DENTAL", "3", "01/01/2020")
            .Endpoint(A, "DIRECT", "obrien@direct.example.org", "Direct address", "O'BRIEN CHIROPRACTIC, PC", "AMITYVILLE", "NY")
            .Endpoint(C, "DIRECT", "deactivated@direct.example.org")
            .Write(_folder, "NPPES_Data_Dissemination_September_2026_V2.zip");

        using var http = new HttpClient();
        Assert.Equal(0, await new LoaderApp(new LoaderOptions { WorkFolder = _folder, LogFolder = _folder }, db.Database, http, Logger.None)
            .LoadFileAsync(zip, _ct));
        await new ProjectionBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);
        return db;
    }
}
