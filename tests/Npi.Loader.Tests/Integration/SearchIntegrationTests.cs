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
        await builder.BuildAsync(_ct);
        var after = await db.QueryAsync<(string, byte[])>("SELECT npi, row_hash FROM provider ORDER BY npi");

        var changed = before.Zip(after).Where(p => !p.First.Item2.SequenceEqual(p.Second.Item2)).Select(p => p.First.Item1);
        Assert.Equal([A, B, D], changed);
    }

    [Fact]
    public async Task Each_projection_records_what_changed_since_the_last_one()
    {
        await using var db = await SeededAsync();
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM provider_change")); // the first projection has nothing to compare with

        await db.ExecuteAsync("UPDATE npidata SET Provider_Last_Name_Legal_Name = 'OBRIEN-SMITH' WHERE NPI = @A", new { A });
        await db.ExecuteAsync("UPDATE npidata SET Provider_Credential_Text = 'MD', Healthcare_Provider_Taxonomy_Code_1 = @Dentist WHERE NPI = @B", new { B, Dentist });
        await db.ExecuteAsync("UPDATE npidata SET Provider_First_Line_Business_Practice_Location_Address = '210 Main St' WHERE NPI = @E", new { E });
        await db.ExecuteAsync("UPDATE npidata SET Is_Deactivated = 0 WHERE NPI = @C", new { C });
        await db.ExecuteAsync("UPDATE npidata SET Is_Deactivated = 1 WHERE NPI = @D", new { D });
        await new ProjectionBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);

        Assert.Equal(
            [
                (A, "name", "O'BRIEN, JOSÉ Q", "OBRIEN-SMITH, JOSÉ Q"), (B, "credential", "M.D.", "MD"), (B, "specialty", ChiroSports, Dentist),
                (C, "added", null, "CARTER"), (D, "deactivated", "SMITH, JONES & CO, LLC", null),
                (E, "address", "200 Main St, FARMINGDALE, NY 11735", "210 Main St, FARMINGDALE, NY 11735"),
            ],
            await db.QueryAsync<(string, string, string?, string?)>(
                "SELECT npi, change_type, old_value, new_value FROM provider_change ORDER BY npi, change_type"));

        var b = await new ProviderDetailService(db.ConnectionString).GetAsync(B, _ct);
        Assert.Equal([("credential", "M.D.", "MD"), ("specialty", "Chiropractor – Sports Physician", "Dentist")],
            b!.Changes.Select(c => (c.Type, c.OldValue, c.NewValue)).Order());
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), b.Changes[0].RecordedOn);
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
        await new ProjectionBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct);
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
        Assert.EndsWith("; 0+ addresses", release.Version, StringComparison.Ordinal); // 5 geocoded addresses, in steps of 50,000

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
    }

    [Fact]
    public async Task Records_are_labelled_current_check_or_stale()
    {
        await using var db = await SeededAsync();
        await new AreaCountBuilder(db.Database, Logger.None, 0.95).BuildAsync(_ct); // specialty_code_category
        var quality = new QualityBuilder(db.Database, Logger.None, 0.95);
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

        var rows = (await db.QueryAsync<(string, sbyte, int, short)>("SELECT npi, label, reasons, activity FROM provider_quality ORDER BY npi"))
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
            INSERT INTO cc_mips (npi, program_year, source, final_score) VALUES (@B, 2024, 'individual', 90);
            INSERT INTO address_place (addr_key, name, operating_status, updated, `release`)
              SELECT addr_key, 'Smith Jones Dental', 'permanently_closed', '2025-03-01', '2026-09-23.1' FROM provider_location WHERE npi = @D;
            INSERT INTO cms_pos_facility (ccn, name, category, address, city, state, zip5, active, termination_code, termination_date) VALUES
              ('330777', 'SMITH JONES DENTAL CLINIC', '21', '5 ATLANTIC AVENUE', 'BROOKLYN', 'NY', '11201', 0, '01', '2025-03-01'),
              ('330778', 'BAKER CLINIC', '21', '10 COURT ST', 'BROOKLYN', 'NY', '11201', 0, '07', '2025-03-01');
            """, new { B, D, E });
        await quality.BuildAsync(_ct);
        rows = (await db.QueryAsync<(string, sbyte, int, short)>("SELECT npi, label, reasons, activity FROM provider_quality ORDER BY npi"))
            .ToDictionary(r => r.Item1, r => (Label: (QualityLabel)r.Item2, Reasons: (QualityReason)r.Item3, Activity: (QualityActivity)r.Item4));
        Assert.Equal((QualityLabel.Stale, QualityReason.NoActivity | QualityReason.NotMapped | QualityReason.NoPhone | QualityReason.FacilityClosed
            | QualityReason.ListedClosed, QualityActivity.None), rows[D]);
        Assert.Equal(QualityActivity.CareCompare | QualityActivity.MipsScore, rows[B].Activity);
        Assert.Equal(QualityActivity.CareCompare | QualityActivity.EquipmentBilling, rows[E].Activity);
        Assert.Equal(["Medicare-certified facility closed", "Listed as permanently closed"],
            RecordQuality.ShortReasons(rows[D].Reasons).Where(t => t.Contains("closed", StringComparison.Ordinal)));

        // A certified facility still active at the address (a hospital whose old unit closed): no closure there.
        await db.ExecuteAsync(
            "INSERT INTO cms_pos_facility (ccn, name, category, address, city, state, zip5, active) " +
            "VALUES ('330999', 'BROOKLYN GENERAL HOSPITAL', '01', '5 Atlantic Ave', 'BROOKLYN', 'NY', '11201', 1)");
        await quality.BuildAsync(_ct);
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
        var a = (await db.QueryAsync<(sbyte, int, short)>("SELECT label, reasons, activity FROM provider_quality WHERE npi = @A", new { A })).Single();
        Assert.Equal(QualityActivity.CurrentLicense, (QualityActivity)a.Item3);
        Assert.NotEqual(QualityLabel.Stale, (QualityLabel)a.Item1);
        Assert.Equal(RecordQuality.Classify((QualityReason)a.Item2), (QualityLabel)a.Item1);
        Assert.Equal(QualityActivity.CareCompare | QualityActivity.MipsScore,
            (QualityActivity)await db.ScalarAsync<short>("SELECT activity FROM provider_quality WHERE npi = @B", new { B }));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM quality_pending"));
        // The search rows follow A's new label: none left under the old one.
        Assert.Equal([a.Item1], await db.QueryAsync<sbyte>("SELECT DISTINCT label FROM quality_search WHERE npi = @A", new { A }));
        Assert.DoesNotContain(A, await Npis(search, new SearchFilter { State = "NY", Quality = "stale" }));

        // Nothing queued: a full build (B loses its MIPS sign).
        await quality.BuildAsync(_ct);
        Assert.Equal(QualityActivity.CareCompare,
            (QualityActivity)await db.ScalarAsync<short>("SELECT activity FROM provider_quality WHERE npi = @B", new { B }));
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
