using Npi.Core.Search;
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
        Assert.Equal(new ProviderFlags(true, false, true), flags[A]);
        Assert.Equal(new ProviderFlags(false, true, false), flags[B]);
        Assert.Equal(ProviderFlags.None, flags[D]);
        Assert.Equal(new ProviderFlags(false, false, true), flags[E]);

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
