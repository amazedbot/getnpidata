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
            INSERT INTO cms_facility_npi (ccn, npi, kind) VALUES ('330045', @D, 'hospital');
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
            [new FacilityAffiliation("Home health agency", "337002", null, null, null, null, null),
             new FacilityAffiliation("Hospital", "330045", "GOOD SAMARITAN HOSPITAL", "WEST ISLIP", "NY", 3, D)],
            a.Facilities);
        Assert.Null((await details.GetAsync(D, _ct))!.CareCompare);

        // The hospital's own NPI shows its Care Compare facility, with the clinicians affiliated with it.
        var hospital = Assert.Single((await details.GetAsync(D, _ct))!.Facilities);
        Assert.Equal(("330045", "hospital", "GOOD SAMARITAN HOSPITAL", true, 3, 2), (hospital.Ccn, hospital.Kind, hospital.Name, hospital.EmergencyServices,
            hospital.OverallRating, hospital.AffiliatedClinicians));
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
    public async Task Bulk_lookup_keeps_request_order_and_reports_each_npi()
    {
        await using var db = await SeededAsync();
        var search = Search(db);

        var rows = await search.LookupAsync([E, C, "1234567890", A, E, " "], _ct);

        Assert.Equal([(E, LookupStatus.Found), (C, LookupStatus.NotFound), ("1234567890", LookupStatus.Invalid), (A, LookupStatus.Found)],
            rows.Select(r => (r.Npi, r.Status)));
        Assert.Equal("NUÑEZ, ELENA", rows[0].Provider!.Name);
        Assert.Equal("AMITYVILLE", rows[3].Provider!.City); // the primary location: a lookup has no location filter

        using var csv = new MemoryStream();
        await ProviderCsv.WriteLookupAsync(rows.ToAsyncEnumerable(), csv, _ct);
        var lines = System.Text.Encoding.UTF8.GetString(csv.ToArray()).TrimStart('\uFEFF').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Requested NPI,Lookup Status,NPI,Entity Type,Name", lines[0], StringComparison.Ordinal);
        Assert.StartsWith($"{E},Found,{E},Individual,", lines[1], StringComparison.Ordinal);
        Assert.Equal($"{C},Not found or deactivated", lines[2]);
        Assert.Equal("1234567890,Invalid NPI", lines[3]);
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
