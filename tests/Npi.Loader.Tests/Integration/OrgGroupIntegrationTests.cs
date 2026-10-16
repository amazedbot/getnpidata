using Npi.Core.Search;
using Npi.Loader.Projection;
using Serilog.Core;

namespace Npi.Loader.Tests.Integration;

/// <summary>
/// Combined organization profiles (docs/PRODUCT_PLAN.md step 3.8) on a scratch database: the groups built from the projection
/// and the CMS tables, the record quality's "another NPI of this organization is current", one row per organization in the search,
/// and the combined profile.
/// </summary>
public sealed class OrgGroupIntegrationTests
{
    // A hospital with its own CCN and Medicare enrollment (the main NPI), a second NPI of the same name at the same address with no
    // sign of practice (a duplicate), its psychiatric unit by the unit's CCN, and a pharmacy enrolled under the hospital's PAC ID in
    // the same ZIP code. Not grouped: the same name at another address in the same ZIP code (and city), an imaging center under the
    // same PAC ID in another ZIP code, two drugstores naming the same parent tax ID in two ZIP codes.
    private const string Hospital = "1000000101", Duplicate = "1000000119", Unit = "1000000127", Pharmacy = "1000000135";
    private const string SameNameElsewhere = "1000000143", Store1 = "1000000150", Store2 = "1000000168", Imaging = "1000000176";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static async Task SeedAsync(TestDatabase db) =>
        await db.ExecuteAsync(
            """
            INSERT INTO data_version (id, as_of_date, provider_count, projected_at) VALUES (1, '2026-10-11', 9, UTC_TIMESTAMP() - INTERVAL 1 HOUR);
            INSERT INTO provider (npi, entity_type, org_name, sort_name, primary_taxonomy_code, phone, enumeration_date, last_update_date) VALUES
              (@Hospital, 2, 'SAINT EXAMPLE HOSPITAL', 'SAINT EXAMPLE HOSPITAL', '282N00000X', '7185550100', '2006-01-01', '2024-05-01'),
              (@Duplicate, 2, 'Saint Example Hospital', 'SAINT EXAMPLE HOSPITAL', '282N00000X', '7185550100', '2007-01-01', '2023-01-01'),
              (@Unit, 2, 'SAINT EXAMPLE PSYCHIATRIC UNIT', 'SAINT EXAMPLE PSYCHIATRIC UNIT', '273R00000X', '7185550100', '2007-01-01', '2024-01-01'),
              (@Pharmacy, 2, 'EXAMPLE OUTPATIENT PHARMACY LLC', 'EXAMPLE OUTPATIENT PHARMACY LLC', '3336C0003X', '7185550101', '2010-01-01', '2025-01-01'),
              (@SameNameElsewhere, 2, 'SAINT EXAMPLE HOSPITAL', 'SAINT EXAMPLE HOSPITAL', '282N00000X', '7185550102', '2008-01-01', '2024-01-01'),
              (@Store1, 2, 'DRUGSTORE 101', 'DRUGSTORE 101', '3336C0003X', '7185550103', '2012-01-01', '2025-01-01'),
              (@Store2, 2, 'DRUGSTORE 102', 'DRUGSTORE 102', '3336C0003X', '2125550104', '2012-01-01', '2025-01-01'),
              (@Imaging, 2, 'EXAMPLE IMAGING CENTER', 'EXAMPLE IMAGING CENTER', '261QR0200X', '2125550105', '2015-01-01', '2025-01-01'),
              ('1000000184', 1, NULL, 'BAKER, ANN', '207R00000X', '7185550106', '2015-01-01', '2025-01-01');
            UPDATE provider SET last_name = 'BAKER', first_name = 'ANN', org_name = NULL WHERE npi = '1000000184';
            INSERT INTO provider_taxonomy (npi, slot, taxonomy_code, is_primary)
              SELECT npi, 1, primary_taxonomy_code, 1 FROM provider;
            INSERT INTO provider_location (npi, is_primary, address1, city, state, zip5, country_code, phone) VALUES
              (@Hospital, 1, '100 MAIN ST', 'RICHMOND HILL', 'NY', '11418', 'US', '7185550100'),
              (@Duplicate, 1, '100 Main Street', 'RICHMOND HILL', 'NY', '11418', 'US', '7185550100'),
              (@Unit, 1, '100 MAIN ST FL 3', 'RICHMOND HILL', 'NY', '11418', 'US', '7185550100'),
              (@Pharmacy, 1, '5 OAK AVE', 'RICHMOND HILL', 'NY', '11418', 'US', '7185550101'),
              (@SameNameElsewhere, 1, '200 ELM ST', 'RICHMOND HILL', 'NY', '11418', 'US', '7185550102'),
              (@Store1, 1, '10 MARKET ST', 'RICHMOND HILL', 'NY', '11418', 'US', '7185550103'),
              (@Store2, 1, '10 BROADWAY', 'NEW YORK', 'NY', '10001', 'US', '2125550104'),
              (@Imaging, 1, '5 FIFTH AVE', 'NEW YORK', 'NY', '10001', 'US', '2125550105'),
              ('1000000184', 1, '100 MAIN ST', 'RICHMOND HILL', 'NY', '11418', 'US', '7185550106');
            -- Every address placed at its building, so no record is "not mapped".
            INSERT INTO provider_map (npi, addr_key, lat, lon, approximate, pt, source)
              SELECT npi, addr_key, 40.7, -73.8, 0, POINT(-73.8, 40.7), 'address' FROM provider_location;
            INSERT INTO cms_facility_npi (ccn, npi, kind) VALUES ('330014', @Hospital, 'hospital'), ('33S014', @Unit, 'hospital');
            INSERT INTO cms_hospital (ccn, name, address, city, state, zip, hospital_type, emergency_services, overall_rating) VALUES
              ('330014', 'SAINT EXAMPLE HOSPITAL', '100 MAIN ST', 'RICHMOND HILL', 'NY', '11418', 'Acute Care Hospitals', 1, 3);
            INSERT INTO medicare_enrollment (npi, enrollment_id, pac_id, provider_type, state, org_name) VALUES
              (@Hospital, 'O20200101000001', '9000000001', 'PART A PROVIDER - HOSPITAL', 'NY', 'SAINT EXAMPLE HOSPITAL'),
              (@Pharmacy, 'O20200101000002', '9000000001', 'PART B SUPPLIER - PHARMACY', 'NY', 'EXAMPLE OUTPATIENT PHARMACY LLC'),
              (@Imaging, 'O20200101000003', '9000000001', 'PART B SUPPLIER - CLINIC/GROUP PRACTICE', 'NY', 'EXAMPLE IMAGING CENTER');
            INSERT INTO provider_profile (npi, is_subpart) VALUES (@Store1, 1), (@Store2, 1);
            INSERT INTO npidata (NPI, Parent_Organization_TIN) VALUES (@Store1, '12-3456789'), (@Store2, '123456789');
            """, new { Hospital, Duplicate, Unit, Pharmacy, SameNameElsewhere, Store1, Store2, Imaging });

    // A group of four losing one is under the 95% row check (a whole table of 190,000 never is).
    private static OrgGroupBuilder Builder(TestDatabase db) => new(db.Database, Logger.None, 0.5);

    private static QualityBuilder Quality(TestDatabase db) => new(db.Database, Logger.None, 0.95) { IncrementalShare = 10 };

    [Fact]
    public async Task Groups_by_name_and_address_and_by_cms_ties_in_one_zip_code_only()
    {
        await using var db = await TestDatabase.CreateAsync();
        await SeedAsync(db);
        var builder = Builder(db);
        Assert.True(await builder.IsStaleAsync(_ct));
        Assert.Equal(4L, await builder.BuildAsync(_ct));

        Assert.Equal([(Hospital, Hospital, 4, (sbyte)1, "330014", (short)(OrganizationTie.NameAddress | OrganizationTie.Ccn | OrganizationTie.MedicareEnrollment)),
                (Duplicate, Hospital, 4, (sbyte)0, null, (short)OrganizationTie.NameAddress),
                (Unit, Hospital, 4, (sbyte)0, "33S014", (short)OrganizationTie.Ccn),
                (Pharmacy, Hospital, 4, (sbyte)0, null, (short)OrganizationTie.MedicareEnrollment)],
            await db.QueryAsync<(string, string, int, sbyte, string?, short)>(
                "SELECT npi, main_npi, members, is_main, ccn, joined_by FROM org_group_member ORDER BY npi"));
        Assert.Equal(3L, await db.ScalarAsync<long>("SELECT total FROM search_total WHERE name = 'org_group:members'"));

        // Built once: not stale until the projection or a CCN or enrollment table changes.
        Assert.False(await builder.IsStaleAsync(_ct));
        await db.ExecuteAsync("UPDATE data_version SET projected_at = UTC_TIMESTAMP() + INTERVAL 1 MINUTE");
        Assert.True(await builder.IsStaleAsync(_ct));
        await db.ExecuteAsync("UPDATE data_version SET projected_at = UTC_TIMESTAMP() - INTERVAL 1 HOUR");
        await db.ExecuteAsync("INSERT INTO reference_data (source, version, source_url, rows_loaded, loaded_at, checked_at) VALUES " +
                              "('medicare_enrollment', 'v2', 'x', 3, UTC_TIMESTAMP() + INTERVAL 1 MINUTE, UTC_TIMESTAMP())");
        Assert.True(await builder.IsStaleAsync(_ct));

        // The combined profile from any NPI of it: the main NPI first, roles in plain words, the facilities and the Medicare group of
        // all of them; a lone NPI has none.
        var groups = new OrganizationGroupService(db.ConnectionString);
        Assert.Equal(Hospital, await groups.GetMainNpiAsync(Pharmacy, _ct));
        Assert.Null(await groups.GetMainNpiAsync(SameNameElsewhere, _ct));
        Assert.Null(await groups.GetAsync(Store1, _ct));
        Assert.Null(await groups.GetAsync("12", _ct));
        Assert.Equal(new ProviderOrganization(Hospital, "SAINT EXAMPLE HOSPITAL", 4), await groups.GetMembershipAsync(Unit, _ct));
        Assert.Equal([Hospital, Unit], (await groups.GetMembershipsAsync([Hospital, Unit, Store1], _ct)).Keys.Order(StringComparer.Ordinal));

        var details = new ProviderDetailService(db.ConnectionString);
        var profile = (await groups.GetCombinedAsync(Duplicate, details, _ct))!;
        Assert.Equal((Hospital, "SAINT EXAMPLE HOSPITAL"), (profile.MainNpi, profile.Name));
        Assert.Equal([(Hospital, "Main NPI: Hospital (CCN 330014)"), (Unit, "Psychiatric unit (CCN 33S014)"),
                (Pharmacy, "Retail pharmacy"), (Duplicate, "Possible duplicate registration")],
            profile.Npis.Select(m => (m.Npi, m.Role)));
        Assert.Equal("same name and address, shared Medicare-certified facility (CCN), same Medicare enrollment (PAC ID)", profile.Npis[0].JoinedBy);
        Assert.Equal(["330014"], profile.Facilities.Select(f => f.Ccn)); // the unit has no listing of its own
        Assert.Equal(["9000000001"], profile.Groups.Select(g => g.PacId));
        Assert.Equal(4, profile.Locations.Count); // 100 Main St, 100 Main Street, 100 Main St Fl 3 (as written) and 5 Oak Ave
        Assert.Equal(Hospital, profile.Main?.Npi);

        // The provider record names its organization.
        Assert.Equal(new ProviderOrganization(Hospital, "SAINT EXAMPLE HOSPITAL", 4), (await details.GetAsync(Pharmacy, _ct))!.Organization);
        Assert.Null((await details.GetAsync(Store1, _ct))!.Organization);
    }

    [Fact]
    public async Task A_member_without_signs_of_practice_is_stale_when_another_npi_is_current()
    {
        await using var db = await TestDatabase.CreateAsync();
        await SeedAsync(db);
        await Builder(db).BuildAsync(_ct);
        Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT COUNT(*) FROM {QualityBuilder.Pending}")); // no labels yet: nothing to queue
        await Quality(db).BuildAsync(_ct, allowIncremental: false);

        var labels = (await db.QueryAsync<(string Npi, sbyte Label, int Reasons, string? Other)>("SELECT npi, label, reasons, other_npi FROM provider_quality"))
            .ToDictionary(r => r.Npi);
        Assert.Equal(((sbyte)QualityLabel.Stale, Hospital), (labels[Duplicate].Label, labels[Duplicate].Other));
        Assert.NotEqual(0, labels[Duplicate].Reasons & (int)QualityReason.OtherNpiCurrent);
        foreach (var npi in new[] { Hospital, Unit, Pharmacy, SameNameElsewhere, Store1 })
        {
            Assert.Equal(0, labels[npi].Reasons & (int)QualityReason.OtherNpiCurrent);
            Assert.Null(labels[npi].Other);
        }

        Assert.Equal((sbyte)QualityLabel.Current, labels[Hospital].Label);
        var duplicate = (await new ProviderDetailService(db.ConnectionString).GetAsync(Duplicate, _ct))!;
        Assert.Equal(Hospital, duplicate.Quality?.OtherCurrentNpi);
        Assert.Contains($"{RecordQuality.OtherNpiCurrentText}: {Hospital}", duplicate.Quality!.ReasonTexts);

        // A member issued or updated in the last two years is spared (owner, 2026-10-16): updated a year before the as-of date, it
        // keeps its own label; three years before, it is flagged again.
        async Task<(sbyte Label, int Other)> DuplicateAsync() => (await db.QueryAsync<(sbyte, int)>(
            $"SELECT label, reasons & {(int)QualityReason.OtherNpiCurrent} FROM provider_quality WHERE npi = @Duplicate", new { Duplicate })).Single();
        await db.ExecuteAsync("UPDATE provider SET last_update_date = '2025-10-11' WHERE npi = @Duplicate", new { Duplicate });
        await Quality(db).BuildAsync(_ct, allowIncremental: false);
        Assert.Equal(((sbyte)QualityLabel.Current, 0), await DuplicateAsync());
        await db.ExecuteAsync("UPDATE provider SET last_update_date = '2023-10-11', enumeration_date = '2025-10-11' WHERE npi = @Duplicate", new { Duplicate });
        await Quality(db).BuildAsync(_ct, allowIncremental: false);
        Assert.Equal(((sbyte)QualityLabel.Current, 0), await DuplicateAsync()); // issued a year ago
        await db.ExecuteAsync("UPDATE provider SET enumeration_date = '2007-01-01' WHERE npi = @Duplicate", new { Duplicate });
        await Quality(db).BuildAsync(_ct, allowIncremental: false);
        Assert.Equal(((sbyte)QualityLabel.Stale, (int)QualityReason.OtherNpiCurrent), await DuplicateAsync());

        // The duplicate moves away: the group changes, its NPIs are queued, and the incremental relabel drops the reason.
        await db.ExecuteAsync("UPDATE provider_location SET address1 = '300 ELM ST' WHERE npi = @Duplicate", new { Duplicate });
        await Builder(db).BuildAsync(_ct);
        Assert.Equal([Hospital, Duplicate, Unit, Pharmacy], await db.QueryAsync<string>($"SELECT npi FROM {QualityBuilder.Pending} ORDER BY npi"));
        await Quality(db).BuildAsync(_ct);
        Assert.Equal((0, (string?)null), (await db.QueryAsync<(int, string?)>(
            $"SELECT reasons & {(int)QualityReason.OtherNpiCurrent}, other_npi FROM provider_quality WHERE npi = @Duplicate", new { Duplicate })).Single());
        Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT COUNT(*) FROM {QualityBuilder.Pending}"));

        // Built again with nothing changed: nothing queued.
        await Builder(db).BuildAsync(_ct);
        Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT COUNT(*) FROM {QualityBuilder.Pending}"));
    }

    [Fact]
    public async Task The_search_lists_one_row_per_organization_unless_every_npi_is_asked_for()
    {
        await using var db = await TestDatabase.CreateAsync();
        await SeedAsync(db);
        await Builder(db).BuildAsync(_ct);
        var search = new SearchService(db.ConnectionString, new TaxonomyCatalog(db.ConnectionString));

        // Organizations in New York: the hospital's three other NPIs are left out (it matches too) and it says it has four.
        var collapsed = await search.SearchAsync(new SearchFilter { State = "NY", EntityType = 2 }, _ct);
        Assert.Equal([Hospital, SameNameElsewhere, Store1, Store2, Imaging], collapsed.Items.Select(i => i.Npi).Order(StringComparer.Ordinal));
        Assert.Equal(5, collapsed.TotalCount);
        Assert.Equal(4, collapsed.Items.Single(i => i.Npi == Hospital).OrganizationNpis);
        Assert.Null(collapsed.Items.Single(i => i.Npi == Store1).OrganizationNpis);

        var all = await search.SearchAsync(new SearchFilter { State = "NY", EntityType = 2, AllNpis = true }, _ct);
        Assert.Equal(8, all.TotalCount);
        Assert.Equal(4, all.Items.Single(i => i.Npi == Pharmacy).OrganizationNpis);

        // A member is listed when its main NPI doesn't match: organizations named "Example ..." in New York.
        var named = await search.SearchAsync(new SearchFilter { State = "NY", OrgName = "example" }, _ct);
        Assert.Equal([Pharmacy, Imaging], named.Items.Select(i => i.Npi).Order(StringComparer.Ordinal));

        // Everyone (no filter): the projection's count less the hidden members; the page agrees.
        await db.ExecuteAsync("UPDATE data_version SET provider_count = (SELECT COUNT(*) FROM provider)");
        var everyone = await search.SearchAsync(new SearchFilter(), _ct);
        Assert.Equal(6, everyone.TotalCount);
        Assert.Equal(6, everyone.Items.Count);

        // A person's search isn't collapsed; the CSV export lists every NPI.
        Assert.Single((await search.SearchAsync(new SearchFilter { State = "NY", LastName = "baker" }, _ct)).Items);
        Assert.Equal(9, await search.SearchAllAsync(new SearchFilter(), _ct).CountAsync(_ct));

        // When the main NPI doesn't match but several members do, the organization is listed once, as its first matching member by
        // NPI: the hospital moved to New Jersey, its duplicate and unit (and the same name elsewhere, another organization) in New York.
        await db.ExecuteAsync("UPDATE provider_location SET state = 'NJ' WHERE npi = @Hospital", new { Hospital });
        var withoutMain = await search.SearchAsync(new SearchFilter { State = "NY", OrgName = "saint example" }, _ct);
        Assert.Equal([Duplicate, SameNameElsewhere], withoutMain.Items.Select(i => i.Npi).Order(StringComparer.Ordinal));
        Assert.Equal(2, withoutMain.TotalCount);
        Assert.Equal(4, withoutMain.Items.Single(i => i.Npi == Duplicate).OrganizationNpis);
        Assert.Equal(3, (await search.SearchAsync(new SearchFilter { State = "NY", OrgName = "saint example", AllNpis = true }, _ct)).TotalCount);
    }
}
