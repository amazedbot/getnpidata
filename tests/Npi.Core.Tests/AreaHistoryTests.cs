using Npi.Core.Search;

namespace Npi.Core.Tests;

/// <summary>Areas over time (docs/PRODUCT_PLAN.md step 3.3): month buckets, the frozen months, filling a series, map requests.</summary>
public class AreaHistoryTests
{
    private static DateOnly M(int year, int month) => new(year, month, 1);

    [Fact]
    public void Months_are_bucketed_and_only_the_last_two_are_open()
    {
        Assert.Equal(M(2026, 10), AreaHistoryMath.MonthOf(new DateOnly(2026, 10, 31)));
        Assert.Equal(M(2026, 10), AreaHistoryMath.MonthOf(new DateTime(2026, 10, 1, 23, 59, 0)));
        Assert.Equal(M(2026, 9), AreaHistoryMath.OpenFrom(M(2026, 10)));
        Assert.Equal(M(2025, 12), AreaHistoryMath.OpenFrom(M(2026, 1)));
        Assert.Equal(M(2021, 11), AreaHistoryMath.DerivedFrom(M(2026, 10)));    // 60 months, the as-of month included
    }

    [Fact]
    public void An_event_in_a_frozen_month_counts_in_the_first_open_one()
    {
        var open = M(2026, 9);
        var asOf = M(2026, 10);
        Assert.Equal(M(2026, 10), AreaHistoryMath.EventMonth(new DateOnly(2026, 10, 6), open, asOf));
        Assert.Equal(M(2026, 9), AreaHistoryMath.EventMonth(new DateOnly(2026, 9, 30), open, asOf));
        Assert.Equal(M(2026, 9), AreaHistoryMath.EventMonth(new DateOnly(2025, 1, 1), open, asOf));   // a late deactivation report
        Assert.Equal(M(2026, 10), AreaHistoryMath.EventMonth(null, open, asOf));
    }

    [Theory]
    [InlineData("2026-10", 2026, 10)]
    [InlineData("2005-05", 2005, 5)]
    [InlineData("2026-13", null, null)]
    [InlineData("2026-1", null, null)]
    [InlineData("2026-10-01", null, null)]
    [InlineData("1999-12", null, null)]
    [InlineData("october", null, null)]
    [InlineData("", null, null)]
    [InlineData(null, null, null)]
    public void Months_parse_as_year_and_month(string? text, int? year, int? month) =>
        Assert.Equal(year is null ? null : new DateOnly(year.Value, month!.Value, 1), AreaHistoryMath.ParseMonth(text));

    private static AreaHistoryMath.StatRow Row(string category, DateOnly month, int basis, int? providers = null, int? issued = null,
        int? deactivated = null, int? movedIn = null, int? movedOut = null, int? specialty = null, int? current = null, int? check = null, int? stale = null) =>
        new(category, month, basis, providers, issued, deactivated, movedIn, movedOut, specialty, null, null, current, check, stale);

    // New NPIs derived from August, counts recorded from October, the change log from October; data as of November.
    private static readonly AreaHistoryCoverage Coverage = new(M(2026, 8), M(2026, 10), M(2026, 10), M(2026, 11), new DateOnly(2026, 11, 8));

    private static readonly AreaHistoryMath.StatRow[] Rows =
    [
        Row("", M(2026, 8), 1, issued: 5),
        Row("Dentists", M(2026, 8), 1, issued: 2),
        Row("", M(2026, 10), 2, providers: 100, issued: 4, deactivated: 1, movedIn: 2, specialty: 3, current: 60, check: 20, stale: 20),
        Row("Dentists", M(2026, 10), 2, providers: 10, issued: 1),
        Row("", M(2026, 11), 2, providers: 103, movedOut: 1),
    ];

    [Fact]
    public void A_county_series_fills_its_gaps_inside_each_series_only()
    {
        var months = AreaHistoryMath.Fill(Rows, Coverage, "county", "");

        Assert.Equal([M(2026, 8), M(2026, 9), M(2026, 10), M(2026, 11)], months.Select(m => m.Month));
        var (aug, sep, oct, nov) = (months[0], months[1], months[2], months[3]);
        Assert.Equal((5, (int?)null, (int?)null, (int?)null, AreaHistoryMath.Derived), (aug.NewProviders!.Value, aug.Providers, aug.MovedIn, aug.Deactivated, aug.Basis));
        Assert.Equal(0, sep.NewProviders);                    // derived months without a row had none
        Assert.Null(sep.SpecialtyChanges);                    // before the change log
        Assert.Equal((100, 2, 0, 1, 3), (oct.Providers!.Value, oct.MovedIn!.Value, oct.MovedOut!.Value, oct.Deactivated!.Value, oct.SpecialtyChanges!.Value));
        Assert.Null(oct.NetChange);                           // September wasn't counted
        Assert.Equal((103, 3, 0, 0, true, AreaHistoryMath.Recorded), (nov.Providers!.Value, nov.NetChange!.Value, nov.NewProviders!.Value, nov.MovedIn!.Value, nov.Partial, nov.Basis));
        Assert.Null(nov.QualityCurrent);                      // no quality snapshot that month
        Assert.False(oct.Partial);
    }

    [Fact]
    public void A_category_series_counts_its_providers_and_new_npis_and_every_provider_for_the_rest()
    {
        var months = AreaHistoryMath.Fill(Rows, Coverage, "county", "Dentists");
        Assert.Equal((2, 0, 1), (months[0].NewProviders!.Value, months[1].NewProviders!.Value, months[2].NewProviders!.Value));
        Assert.Equal(10, months[2].Providers);
        Assert.Equal(0, months[3].Providers);                 // counted that month, no dentist row: none
        Assert.Equal(-10, months[3].NetChange);
        Assert.Equal(2, months[2].MovedIn);                   // moves are for every provider
    }

    [Fact]
    public void The_us_has_no_moves_and_its_deactivations_start_with_the_derived_months()
    {
        var months = AreaHistoryMath.Fill(Rows, Coverage, "us", "");
        Assert.All(months, m => Assert.Null(m.MovedIn));
        Assert.All(months, m => Assert.Null(m.MovedOut));
        Assert.Equal(0, months[0].Deactivated);
        Assert.Null(AreaHistoryMath.Fill(Rows, Coverage, "county", "")[0].Deactivated);
    }

    [Fact]
    public void An_empty_history_has_no_months() =>
        Assert.Empty(AreaHistoryMath.Fill([], new AreaHistoryCoverage(null, null, null, null, null), "us", ""));

    [Fact]
    public void Quality_shares_are_percentages_of_the_mix()
    {
        Assert.Equal(20.0, AreaHistoryMath.QualityShare(20, 60, 20, 20));
        Assert.Null(AreaHistoryMath.QualityShare(0, 0, 0, 0));
        Assert.Null(AreaHistoryMath.QualityShare(null, null, null, null));
    }

    [Fact]
    public void Change_kinds_gather_only_labelled_change_types()
    {
        foreach (var type in AreaChangeKinds.FromChangeLog.Values.SelectMany(t => t))
        {
            Assert.True(ChangeTypes.Labels.ContainsKey(type), type);
            Assert.Contains($"'{type}'", AreaChangeKinds.ChangeTypeSqlList, StringComparison.Ordinal);
        }

        Assert.StartsWith("CASE WHEN x IN ('specialty', 'taxonomy_added', 'taxonomy_removed') THEN 'specialty' ",
            AreaChangeKinds.KindSql("x"), StringComparison.Ordinal);
        Assert.EndsWith("END", AreaChangeKinds.KindSql("x"), StringComparison.Ordinal);
        Assert.True(AreaChangeKinds.IsKnown("moved_in"));
        Assert.False(AreaChangeKinds.IsKnown("added"));
        Assert.Equal("Moved out", AreaChangeKinds.Label("moved_out"));
    }

    [Fact]
    public void Lists_exist_for_every_kind_but_the_us_has_no_moves()
    {
        Assert.True(AreaHistoryService.HasKind("county", AreaChangeKinds.MovedIn));
        Assert.True(AreaHistoryService.HasKind("us", AreaChangeKinds.Deactivated));
        Assert.False(AreaHistoryService.HasKind("us", AreaChangeKinds.MovedOut));
        Assert.False(AreaHistoryService.HasKind("state", "bogus"));
        Assert.Equal(("county", "36103"), AreaHistoryService.Area("NY", "36103"));
        Assert.Equal(("state", "NY"), AreaHistoryService.Area("ny", null));
        Assert.Equal(("us", "US"), AreaHistoryService.Area(null, null));
        Assert.Null(AreaHistoryService.Area("ZZ", null));
        Assert.Null(AreaHistoryService.Area("NY", "3610"));
        Assert.Null(AreaHistoryService.Area(null, "36103"));
    }

    [Fact]
    public void Months_of_a_coverage_run_newest_first()
    {
        Assert.Equal([M(2026, 11), M(2026, 10), M(2026, 9), M(2026, 8)], AreaHistoryService.MonthsOf(Coverage));
        Assert.Empty(AreaHistoryService.MonthsOf(new AreaHistoryCoverage(null, null, null, null, null)));
    }

    [Fact]
    public void Map_requests_default_to_states_and_providers_per_100000()
    {
        var (query, errors) = AreaMapQuery.Parse(null, null, null, null, null, null);
        Assert.Empty(errors);
        Assert.Equal(new AreaMapQuery("state", null, AreaMapStats.Per100k, null, null, null), query);

        (query, errors) = AreaMapQuery.Parse(null, " ny ", "movedIn", "Dentists", null, "2026-10");
        Assert.Empty(errors);
        Assert.Equal(new AreaMapQuery("county", "NY", "movedIn", "Dentists", null, M(2026, 10)), query);
    }

    [Theory]
    [InlineData("nation", null, null, null, null, null, "level")]
    [InlineData("county", "ZZ", null, null, null, null, "state")]
    [InlineData("state", "NY", null, null, null, null, "state")]
    [InlineData(null, null, "bogus", null, null, null, "stat")]
    [InlineData(null, null, null, "Wizards", null, null, "category")]
    [InlineData(null, null, null, "Dentists", "Heart (cardiology)", null, "subcategory")]
    [InlineData(null, null, null, null, null, "2026-13", "month")]
    public void Bad_map_requests_name_the_parameter(string? level, string? state, string? stat, string? category, string? subcategory, string? month,
        string parameter)
    {
        var (query, errors) = AreaMapQuery.Parse(level, state, stat, category, subcategory, month);
        Assert.Null(query);
        Assert.Equal([parameter], errors.Keys);
    }

    [Fact]
    public void Every_map_statistic_has_a_label()
    {
        Assert.Equal(AreaMapStats.All.Count, AreaMapStats.All.Select(s => s.Stat).Distinct().Count());
        Assert.All(AreaMapStats.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Label)));
        Assert.True(AreaMapStats.Get(AreaMapStats.New).Monthly);
        Assert.False(AreaMapStats.Get(AreaMapStats.MovedIn).BySpecialty);
    }

    [Fact]
    public void Archive_months_come_first_with_gaps_and_no_net_change_across_the_join()
    {
        // The archive (step 3.4) from August 2026 to September, a month without a file in between... August, September with a
        // file, October recorded (by every practice location).
        var coverage = new AreaHistoryCoverage(null, M(2026, 10), M(2026, 10), M(2026, 10), new DateOnly(2026, 10, 11))
        {
            ArchiveFrom = M(2026, 6),
            ArchiveTo = M(2026, 9),
        };
        var rows = new[]
        {
            Row("", M(2026, 6), 3, providers: 90, issued: 3, deactivated: 0, movedIn: 0, movedOut: 0, specialty: 0),
            Row("", M(2026, 7), 3, issued: 2),                                                   // no file: new NPIs only
            Row("", M(2026, 8), 3, providers: 92, issued: 1, deactivated: 1, movedIn: 2, movedOut: 1, specialty: 1),
            Row("Dentists", M(2026, 8), 3, providers: 9, issued: 1),
            Row("", M(2026, 9), 3, providers: 95, deactivated: 0, movedIn: 0, movedOut: 0, specialty: 0),
            Row("", M(2026, 10), 2, providers: 101, issued: 4, deactivated: 0, movedIn: 1, movedOut: 0),
        };
        Assert.True(coverage.InArchive(M(2026, 9)));
        Assert.False(coverage.InArchive(M(2026, 10)));
        var months = AreaHistoryMath.Fill(rows, coverage, "county", "");
        Assert.Equal(5, months.Count);
        Assert.All(months.Take(4), m => Assert.Equal(AreaHistoryMath.Archive, m.Basis));
        Assert.Equal(AreaHistoryMath.Recorded, months[4].Basis);

        var (june, july, august, september, october) = (months[0], months[1], months[2], months[3], months[4]);
        Assert.Equal((90, 3, 0), (june.Providers, june.NewProviders, june.Deactivated));
        Assert.Null(june.MovedIn);                                                               // the archive's first month: nothing to compare
        Assert.Equal(((int?)null, 2, 0, (int?)null, (int?)null), (july.Providers, july.NewProviders, july.Deactivated, july.MovedIn, july.SpecialtyChanges));
        Assert.Null(july.NetChange);
        Assert.Equal((92, 1, 2, 1, 1), (august.Providers, august.Deactivated, august.MovedIn, august.MovedOut, august.SpecialtyChanges));
        Assert.Null(august.NetChange);                                                           // July has no count
        Assert.Equal(3, september.NetChange);
        Assert.Null(september.LicenseChanges);                                                   // the archive has no license changes
        Assert.Null(october.NetChange);                                                          // primary location → every location
        Assert.Equal(1, october.MovedIn);

        var dentists = AreaHistoryMath.Fill(rows, coverage, "county", "Dentists");
        Assert.Equal((9, 1), (dentists[2].Providers, dentists[2].NewProviders));
        Assert.Equal(0, dentists[1].NewProviders);                                               // known: 0 in July
        Assert.Equal(0, dentists[3].Providers);                                                  // a month with a file: none listed

        // With the secondary locations counted from August (the first file listing them): no net change for August (the
        // definition steps up there), and the join with the recorded series in October is smooth.
        var withLocations = coverage with { AnyLocationFrom = M(2026, 8) };
        Assert.Equal(M(2026, 8), withLocations.DefinitionStep());
        Assert.False(withLocations.EveryLocation(M(2026, 7)));
        Assert.True(withLocations.EveryLocation(M(2026, 8)));
        months = AreaHistoryMath.Fill(rows, withLocations, "county", "");
        Assert.Equal(((int?)null, 3, 6), (months[2].NetChange, months[3].NetChange, months[4].NetChange));
        Assert.Null((coverage with { AnyLocationFrom = M(2026, 6) }).DefinitionStep());       // every archive month counts every location
        Assert.Equal(6, AreaHistoryMath.Fill(rows, coverage with { AnyLocationFrom = M(2026, 6) }, "county", "")[4].NetChange);
    }
}
