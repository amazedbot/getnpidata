using System.Text;
using Npi.Core.Search;

namespace Npi.Core.Tests;

/// <summary>The Facilities &amp; hospitals and Groups &amp; organizations lists (docs/PRODUCT_PLAN.md step 3.6), over rows in memory.</summary>
public class DirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("330045", FacilityTypes.Hospital, "Short-term hospital")]
    [InlineData("331301", FacilityTypes.Hospital, "Critical access hospital")]
    [InlineData("332001", FacilityTypes.Hospital, "Long-term care hospital")]
    [InlineData("333025", FacilityTypes.Hospital, "Rehabilitation hospital")]
    [InlineData("333300", FacilityTypes.Hospital, "Children's hospital")]
    [InlineData("334001", FacilityTypes.Hospital, "Psychiatric hospital")]
    [InlineData("331000", FacilityTypes.Fqhc, null)]
    [InlineData("331850", FacilityTypes.Fqhc, null)]
    [InlineData("331501", FacilityTypes.Hospice, null)]
    [InlineData("332301", FacilityTypes.Dialysis, null)]
    [InlineData("333501", FacilityTypes.Dialysis, null)]
    [InlineData("333401", FacilityTypes.Rhc, null)]
    [InlineData("338501", FacilityTypes.Rhc, null)]
    [InlineData("335001", FacilityTypes.NursingHome, null)]
    [InlineData("337001", FacilityTypes.HomeHealth, null)]
    [InlineData("339001", FacilityTypes.HomeHealth, null)]
    [InlineData("333101", FacilityTypes.HomeHealth, null)]
    [InlineData("339801", FacilityTypes.Other, "Transplant center")]
    [InlineData("331401", FacilityTypes.Other, "Community mental health center")]
    [InlineData("33E001", FacilityTypes.NursingHome, "Medicaid nursing facility")]
    [InlineData("33S045", FacilityTypes.Other, "Hospital unit")]
    [InlineData("33C0001234", FacilityTypes.SurgeryCenter, null)]
    [InlineData("12", FacilityTypes.Other, null)]
    public void A_ccn_says_what_kind_of_facility_it_is(string ccn, string type, string? kind) =>
        Assert.Equal((type, kind), FacilityTypes.FromCcn(ccn));

    [Fact]
    public void Plain_words_for_hospital_types_and_closures()
    {
        Assert.Equal("Acute care hospital", FacilityTypes.HospitalKind("Acute Care Hospitals"));
        Assert.Equal("Psychiatric hospital", FacilityTypes.HospitalKind("Psychiatric"));
        Assert.Null(FacilityTypes.HospitalKind(" "));
        Assert.Equal("Status change, such as a new owner", FacilityTypes.TerminationReason("07"));
        Assert.Null(FacilityTypes.TerminationReason("00"));
        Assert.Equal("CHILDRENS HOSPITAL ST MARY", FacilityDirectory.Normalize("Children's Hospital - St. Mary"));
    }

    private static IReadOnlyList<FacilitySummary> Facilities() => FacilityDirectory.Merge(
        [
            new("330045", "GOOD SAMARITAN HOSP", "1000 MONTAUK HWY", "WEST ISLIP", "NY", "11795", true, "00", null),
            new("331850", "HEALTHY COMMUNITY CENTER", "5 MAIN ST", "BROOKLYN", "NY", "11201", true, null, null),
            new("333401", "VALLEY RURAL CLINIC", "1 FARM RD", "ALBANY", "NY", "12201", true, null, null),
            new("330888", "OLD NAME HOSPITAL", "10 COURT ST", "BROOKLYN", "NY", "11201", false, "07", new DateOnly(2020, 1, 1)),
            new("335001", "ISLAND NURSING", "2 SHORE RD", "BABYLON", "NY", "11702", false, "01", new DateOnly(2024, 6, 30)),
            new("050001", "PACIFIC MEDICAL CENTER", "1 OCEAN AVE", "LOS ANGELES", "CA", "90001", true, null, null),
            new("999999", null, null, null, "NY", null, true, null, null), // no name: left out
        ],
        [
            new(FacilityTypes.Hospital, "330045", "GOOD SAMARITAN HOSPITAL", "1000 MONTAUK HWY", "WEST ISLIP", "NY", "117951234", "6315550100",
                "Acute Care Hospitals", "Voluntary non-profit", null, 3),
            new(FacilityTypes.Hospital, "050001", "PACIFIC MEDICAL CENTER", null, "LOS ANGELES", "CA", "90001", null, "Acute Care Hospitals", null, null, 5),
            new(FacilityTypes.NursingHome, "335002", "SHORE VIEW CARE", "3 SHORE RD", "BABYLON", "NY", "11702", null, null, "For profit", 120, 1),
            new(FacilityTypes.HomeHealth, "337001", "HOME CARE OF LONG ISLAND", null, "HAUPPAUGE", "NY", "11788", null, null, null, null, 4.5),
        ],
        [("330045", "hospital", 2), ("333401", "rhc", 1)],
        new Dictionary<string, (double Lat, double Lon)> { ["11795"] = (40.7, -73.3), ["11201"] = (40.69, -73.99) });

    [Fact]
    public void Facilities_merge_the_provider_of_services_file_with_care_compare()
    {
        var rows = Facilities().ToDictionary(r => r.Ccn);
        Assert.Equal(8, rows.Count);
        Assert.False(rows.ContainsKey("999999"));

        var hospital = rows["330045"];   // Care Compare's name, kind, ZIP, phone and rating win; its ZIP's centre
        Assert.Equal((FacilityTypes.Hospital, "Hospital", "Acute care hospital", "GOOD SAMARITAN HOSPITAL", "11795", "6315550100", 3.0, true, 2),
            (hospital.Type, hospital.TypeName, hospital.Kind, hospital.Name, hospital.Zip, hospital.Phone, hospital.Rating, hospital.Active, hospital.BillingNpis));
        Assert.Equal((40.7, -73.3), (hospital.Latitude, hospital.Longitude));

        Assert.Equal(FacilityTypes.Fqhc, rows["331850"].Type);                                       // by its number range
        Assert.Equal((FacilityTypes.Rhc, "Rural health clinic"), (rows["333401"].Type, rows["333401"].TypeName)); // by its enrollment
        Assert.Null(rows["333401"].Latitude);                                                         // no centre for its ZIP

        var closed = rows["330888"];
        Assert.Equal((false, new DateOnly(2020, 1, 1), "Status change, such as a new owner", "Short-term hospital"),
            (closed.Active, closed.TerminationDate, closed.TerminationReason, closed.Kind));
        Assert.True(rows["335002"].Active);                                                           // in Care Compare only: certified now
        Assert.Equal((120, 1.0), (rows["335002"].Beds, rows["335002"].Rating));
        Assert.Equal(4.5, rows["337001"].Rating);
    }

    [Fact]
    public async Task Facilities_filter_sort_and_page()
    {
        using var directory = new FacilityDirectory(Facilities());

        async Task<string[]> Ccns(FacilityFilter filter) => [.. (await directory.SearchAsync(filter, Ct)).Items.Select(i => i.Ccn)];

        // Active only by default, by name.
        Assert.Equal(["330045", "331850", "337001", "050001", "335002", "333401"], await Ccns(new FacilityFilter()));
        Assert.Equal(["335001", "330888"], await Ccns(new FacilityFilter { Status = "closed" }));
        Assert.Equal(8, (await directory.SearchAsync(new FacilityFilter { Status = "ALL" }, Ct)).TotalCount);

        // A CCN, a ZIP, or every word of the name or city.
        Assert.Equal(["330045"], await Ccns(new FacilityFilter { Q = "330045" }));
        Assert.Equal(["330888"], await Ccns(new FacilityFilter { Q = "330888" }));  // a CCN finds a closed one too
        Assert.Equal(["331850"], await Ccns(new FacilityFilter { Q = "11201" }));
        Assert.Equal(["330045"], await Ccns(new FacilityFilter { Q = "samaritan west islip" }));
        Assert.Equal(["335002"], await Ccns(new FacilityFilter { Q = "babylon" }));

        Assert.Equal(["330045", "050001"], await Ccns(new FacilityFilter { Type = "HOSPITAL" }));
        Assert.Equal(["050001"], await Ccns(new FacilityFilter { Type = "hospital", State = "ca" }));
        Assert.Equal(["337001", "050001"], await Ccns(new FacilityFilter { MinRating = 4 }));
        Assert.Equal(["337001"], await Ccns(new FacilityFilter { MinRating = 4, MaxRating = 4 }));      // 4.5 counts as 4
        Assert.Equal(["335002"], await Ccns(new FacilityFilter { MaxRating = 1 }));

        // Best first, the unrated last either way.
        Assert.Equal(["050001", "337001", "330045", "335002", "331850", "333401"], await Ccns(new FacilityFilter { Sort = "-rating" }));
        Assert.Equal(["335002", "330045", "337001", "050001", "331850", "333401"], await Ccns(new FacilityFilter { Sort = "rating" }));
        Assert.Equal(["330888", "335001"], await Ccns(new FacilityFilter { Status = "closed", Sort = "closed" }));
        Assert.Equal(["335001", "330888"], await Ccns(new FacilityFilter { Status = "closed", Sort = "-closed" }));

        var page = await directory.SearchAsync(new FacilityFilter { Page = 2, PageSize = 4 }, Ct);
        Assert.Equal((6, 2), (page.TotalCount, page.Items.Count));

        var counts = await directory.CountByTypeAsync(new FacilityFilter { Type = "hospital", State = "NY", Status = "all" }, Ct);
        Assert.Equal((1, 1), counts.Where(c => c.Type == FacilityTypes.Hospital).Select(c => (c.Active, c.Closed)).Single());
        Assert.Equal((1, 1), counts.Where(c => c.Type == FacilityTypes.NursingHome).Select(c => (c.Active, c.Closed)).Single());

        await Assert.ThrowsAsync<ArgumentException>(() => directory.SearchAsync(new FacilityFilter { Type = "spa" }, Ct));
        var all = new List<string>();
        await foreach (var f in directory.AllAsync(new FacilityFilter { Type = "hospital", Status = "all", Page = 5 }, Ct))
        {
            all.Add(f.Ccn);
        }

        Assert.Equal(["330045", "330888", "050001"], all);                                            // paging ignored
    }

    [Fact]
    public async Task Facilities_filter_by_ownership_changes_and_penalties()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = FacilityDirectory.Merge(
            [
                new("330045", "GOOD SAMARITAN HOSP", null, "WEST ISLIP", "NY", "11795", true, null, null),
                new("335001", "ISLAND NURSING", null, "BABYLON", "NY", "11702", true, null, null),
                new("335003", "BAY NURSING", null, "BABYLON", "NY", "11702", true, null, null),
                new("335004", "QUIET NURSING", null, "ISLIP", "NY", "11751", true, null, null),
            ],
            [], [], new Dictionary<string, (double Lat, double Lon)>(),
            new Dictionary<string, DateOnly> { ["330045"] = today.AddMonths(-8), ["335001"] = today.AddYears(-4), ["335004"] = today.AddYears(-12) },
            new Dictionary<string, FacilityDirectory.FacilityPenalties>
            {
                ["335001"] = new(3, 182968, today.AddMonths(-5)),
                ["335003"] = new(1, null, today.AddYears(-1)),        // a payment denial, no fine
                ["335004"] = new(1, 5000, today.AddYears(-4)),        // older than the three years
            });
        Assert.Equal((3, 182968.0, today.AddMonths(-5)), rows.Where(r => r.Ccn == "335001").Select(r => (r.Penalties, r.Fines, r.LastPenalty)).Single());
        Assert.Equal((0, (double?)null, today.AddMonths(-8)), rows.Where(r => r.Ccn == "330045").Select(r => (r.Penalties, r.Fines, r.LastOwnershipChange)).Single());

        using var directory = new FacilityDirectory(rows);
        async Task<string[]> Ccns(FacilityFilter filter) => [.. (await directory.SearchAsync(filter, Ct)).Items.Select(i => i.Ccn)];

        Assert.Equal(["330045"], await Ccns(new FacilityFilter { OwnershipChangedWithinYears = 1 }));
        Assert.Equal(["330045", "335001"], await Ccns(new FacilityFilter { OwnershipChangedWithinYears = 5 }));
        Assert.Equal(["335003", "335001"], await Ccns(new FacilityFilter { HasPenalties = true }));
        Assert.Equal(4, (await directory.SearchAsync(new FacilityFilter { HasPenalties = false }, Ct)).TotalCount); // false: no filter
        Assert.Equal(["335001"], await Ccns(new FacilityFilter { HasPenalties = true, OwnershipChangedWithinYears = 5 }));

        // The largest fines and the newest changes first; those without one last either way.
        Assert.Equal(["335001", "335004", "335003", "330045"], await Ccns(new FacilityFilter { Sort = "-fines" }));
        Assert.Equal(["330045", "335001", "335004", "335003"], await Ccns(new FacilityFilter { Sort = "-ownershipChange" }));
        Assert.Equal(["335004", "335001", "330045", "335003"], await Ccns(new FacilityFilter { Sort = "ownershipchange" }));

        Assert.Equal(["ownershipChangedWithinYears"], new FacilityFilter { OwnershipChangedWithinYears = 11 }.Validate().Keys);
        Assert.Equal(["ownershipChangedWithinYears"], new FacilityFilter { OwnershipChangedWithinYears = 0 }.Validate().Keys);

        using var output = new MemoryStream();
        await FacilityCsv.WriteAsync(rows.Where(r => r.Ccn == "335001").ToAsyncEnumerable(), output, "https://example.org", Ct);
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("Last Ownership Change,Penalties (3 Years),Fines (3 Years),Page", text, StringComparison.Ordinal);
        Assert.Contains($",{today.AddYears(-4):yyyy-MM-dd},3,182968,https://example.org/facility/335001", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Facility_totals_and_ratings()
    {
        using var directory = new FacilityDirectory(Facilities());
        var o = await directory.GetOverviewAsync(Ct);
        Assert.Equal((6, 2, 2), (o.Active, o.Closed, o.States));
        Assert.Equal((2, 1), o.Types.Where(t => t.Type == FacilityTypes.Hospital).Select(t => (t.Active, t.Closed)).Single());
        var hospitals = o.Ratings.Single(r => r.Type == FacilityTypes.Hospital);
        Assert.Equal([0, 0, 1, 0, 1], hospitals.ByStars);
        Assert.Equal(0, hospitals.NotRated);
        Assert.Equal([0, 0, 0, 1, 0], o.Ratings.Single(r => r.Type == FacilityTypes.HomeHealth).ByStars);
        Assert.Equal(["hospital", "nursing_home", "home_health", "hospice"], o.Ratings.Select(r => r.Type));
    }

    [Theory]
    [InlineData("q", 101, null, null, null)]
    [InlineData("type", 0, "spa", null, null)]
    [InlineData("state", 0, null, "NYC", null)]
    [InlineData("status", 0, null, null, "open")]
    public void Bad_facility_filters_are_named(string parameter, int queryLength, string? type, string? state, string? status)
    {
        var filter = new FacilityFilter { Q = queryLength > 0 ? new string('x', queryLength) : null, Type = type, State = state, Status = status };
        Assert.Equal([parameter], filter.Validate().Keys);
    }

    [Fact]
    public void Bad_facility_numbers_and_sorts_are_named()
    {
        Assert.Equal(["minRating", "maxRating", "sort", "page", "pageSize"],
            new FacilityFilter { MinRating = 0, MaxRating = 6, Sort = "-beds", Page = 0, PageSize = 201 }.Validate().Keys);
        Assert.Equal(["maxRating"], new FacilityFilter { MinRating = 4, MaxRating = 2 }.Validate().Keys);
        Assert.Empty(new FacilityFilter { Q = "x", Type = "rhc", State = "ny", MinRating = 1, MaxRating = 5, Status = "closed", Sort = "-rating" }.Validate());
    }

    private static OrganizationDirectory Organizations() => new(
        [
            new("1000000001", "BIG HEALTH GROUP", "NY", 1200, 3, true, 91.25, 2024),
            new("1000000002", "SMALL PRACTICE LLC", "NY", 4, 1, null, null, null),
            new("1000000003", "SUNSHINE PEDIATRICS", "FL", 60, 2, false, 75, 2024),
            new("1000000004", "SOLO DOC", "FL", 1, 0, null, null, null),
        ],
        [
            ("207Q00000X", "NY", 1, 10), ("207Q00000X", "NY", 3, 4), ("207Q00000X", "FL", 2, 5), ("261QR1300X", "NY", 0, 2), (null, null, 1, 7),
            ("207Q00000X", "ZZ", 1, 1),
        ],
        [new("1111111112", "ACME LAB", "Clinical laboratory", "ALBANY", "NY", 5e6, 1000), new("2222222224", "ROAD AMBULANCE", null, null, null, 1e6, null)],
        2024,
        new Dictionary<string, IReadOnlyList<string>> { ["1234567893"] = ["1000000002", "1000000009"] });

    [Fact]
    public async Task Groups_filter_sort_and_page()
    {
        using var directory = Organizations();

        async Task<string[]> Pacs(GroupFilter filter) => [.. (await directory.SearchGroupsAsync(filter, Ct)).Items.Select(i => i.PacId)];

        Assert.Equal(["1000000001", "1000000003", "1000000002", "1000000004"], await Pacs(new GroupFilter()));   // largest first
        Assert.Equal(["1000000002", "1000000004"], await Pacs(new GroupFilter { MaxClinicians = 9 }));
        Assert.Equal(["1000000003"], await Pacs(new GroupFilter { State = "fl", MinClinicians = 2 }));
        Assert.Equal(["1000000003"], await Pacs(new GroupFilter { Q = "pediatrics sunshine" }));
        Assert.Equal(["1000000003"], await Pacs(new GroupFilter { Q = "1000000003" }));                           // a PAC ID
        Assert.Equal(["1000000002"], await Pacs(new GroupFilter { Q = "1234567893" }));                           // an NPI: the groups it bills through
        Assert.Equal(["1000000001", "1000000003", "1000000002", "1000000004"], await Pacs(new GroupFilter { Sort = "-mips" })); // scored first
        Assert.Equal(["1000000003", "1000000001", "1000000002", "1000000004"], await Pacs(new GroupFilter { Sort = "mips" }));
        Assert.Equal(["1000000001", "1000000002", "1000000004", "1000000003"], await Pacs(new GroupFilter { Sort = "name" }));
        var page = await directory.SearchGroupsAsync(new GroupFilter { Page = 2, PageSize = 3 }, Ct);
        Assert.Equal((4, 1), (page.TotalCount, page.Items.Count));

        Assert.Equal("SOLO DOC", directory.PeekGroup("1000000004")?.Name);
        Assert.Null(directory.PeekGroup("9999999999"));
        Assert.Equal(["sort", "page", "pageSize"], new GroupFilter { Sort = "size", Page = 0, PageSize = 0 }.Validate().Keys);
        Assert.Equal(["maxClinicians"], new GroupFilter { MinClinicians = 10, MaxClinicians = 2 }.Validate().Keys);
    }

    [Fact]
    public async Task Organization_totals_by_size_and_kind()
    {
        using var directory = Organizations();
        var o = await directory.GetOverviewAsync(Ct);
        Assert.Equal((4, 2, 2024), (o.Groups, o.GroupsWithMips, o.MipsYear));
        Assert.Equal([1, 1, 0, 1, 0, 1], o.Sizes.Select(s => s.Groups));
        Assert.Equal(1200L, o.Sizes[^1].Clinicians);
        Assert.Equal("1000000001", o.Largest[0].PacId);
        Assert.Equal(29, o.Organizations);
        Assert.Equal((2, 6e6, 2024), (o.BillingOrganizations, o.BillingTotal, o.BillingYear));
        Assert.Equal("ACME LAB", o.TopBilling[0].Name);
        Assert.Equal(["FL", "NY"], o.States);

        var doctors = o.Categories.Single(c => c.Category == "Doctors (MD, DO)");
        Assert.Equal((20, 11, 5, 4), (doctors.Organizations, doctors.Current, doctors.Check, doctors.Stale));
        Assert.Equal((7, 7), o.Categories.Where(c => c.Category == SpecialtyCategories.FallbackCategory).Select(c => (c.Organizations, c.Current)).Single());

        var ny = await directory.GetCategoriesAsync("ny", Ct);
        Assert.Equal((14, 10, 0, 4), ny.Where(c => c.Category == "Doctors (MD, DO)").Select(c => (c.Organizations, c.Current, c.Check, c.Stale)).Single());
        Assert.Equal((2, 0), ny.Where(c => c.Category == "Clinics & centers").Select(c => (c.Organizations, c.Current)).Single()); // not labelled yet
    }

    [Fact]
    public async Task Csv_files_link_each_row()
    {
        using var output = new MemoryStream();
        var rows = Facilities().Where(f => f.Ccn == "330888").ToAsyncEnumerable();
        Assert.Equal(1, await FacilityCsv.WriteAsync(rows, output, "https://example.org", Ct));
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.StartsWith("﻿CCN,Facility,Type", text, StringComparison.Ordinal);
        Assert.Contains("330888,OLD NAME HOSPITAL,Hospital,Short-term hospital,10 COURT ST,BROOKLYN,NY,11201,,,,,Closed,2020-01-01,\"Status change, such as a new owner\",0,,,,https://example.org/facility/330888",
            text, StringComparison.Ordinal);

        using var groups = new MemoryStream();
        using var directory = Organizations();
        Assert.Equal(1, await GroupCsv.WriteAsync(directory.AllGroupsAsync(new GroupFilter { Q = "big" }, Ct), groups, "https://example.org", Ct));
        Assert.Contains("1000000001,BIG HEALTH GROUP,NY,1200,3,Yes,91.25,2024,https://example.org/group/1000000001",
            Encoding.UTF8.GetString(groups.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lists_the_loader_has_not_built_yet_say_so()
    {
        using var facilities = new FacilityDirectory([]);
        Assert.False(await facilities.IsReadyAsync(Ct));
        await Assert.ThrowsAsync<DirectoryNotReadyException>(() => facilities.SearchAsync(new FacilityFilter(), Ct));
        await Assert.ThrowsAsync<DirectoryNotReadyException>(() => facilities.GetOverviewAsync(Ct));

        using var organizations = new OrganizationDirectory([], [], [], null, new Dictionary<string, IReadOnlyList<string>>());
        Assert.False(await organizations.IsReadyAsync(Ct));
        await Assert.ThrowsAsync<DirectoryNotReadyException>(() => organizations.SearchGroupsAsync(new GroupFilter(), Ct));
        await Assert.ThrowsAsync<DirectoryNotReadyException>(() => organizations.GetCategoriesAsync(null, Ct));
        Assert.Null(organizations.PeekGroup("1000000001"));

        using var ready = Organizations();
        Assert.True(await ready.IsReadyAsync(Ct));
    }

    [Fact]
    public async Task A_snapshot_is_built_once_and_peeked_without_waiting()
    {
        var builds = 0;
        using var cache = new SnapshotCache<string>(_ => Task.FromResult("v1"), _ => Task.FromResult("built " + ++builds));
        Assert.Null(cache.Peek());
        Assert.Equal("built 1", await cache.GetAsync(Ct));
        Assert.Equal("built 1", await cache.GetAsync(Ct));
        Assert.Equal("built 1", cache.Peek());
        Assert.Equal(1, builds);
    }
}
