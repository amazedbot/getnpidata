using Npi.Core.Search;

namespace Npi.Core.Tests;

public class SearchQueryTests
{
    [Fact]
    public void A_search_needs_at_least_one_filter()
    {
        var ex = Assert.Throws<SearchValidationException>(() => SearchValidation.Normalize(new SearchFilter()));

        Assert.Contains("filter", ex.Errors.Keys);
    }

    [Theory]
    [InlineData(nameof(SearchFilter.Npi), "123")]
    [InlineData(nameof(SearchFilter.Zip5), "1170")]
    [InlineData(nameof(SearchFilter.State), "New York")]
    [InlineData(nameof(SearchFilter.CountyFips), "3610")]
    [InlineData(nameof(SearchFilter.TaxonomyCode), "111N")]
    [InlineData(nameof(SearchFilter.Gender), "Q")]
    [InlineData(nameof(SearchFilter.Sort), "password")]
    public void Malformed_values_are_rejected(string field, string value)
    {
        var filter = field switch
        {
            nameof(SearchFilter.Npi) => new SearchFilter { Npi = value },
            nameof(SearchFilter.Zip5) => new SearchFilter { Zip5 = value },
            nameof(SearchFilter.State) => new SearchFilter { State = value },
            nameof(SearchFilter.CountyFips) => new SearchFilter { CountyFips = value },
            nameof(SearchFilter.TaxonomyCode) => new SearchFilter { TaxonomyCode = value },
            nameof(SearchFilter.Gender) => new SearchFilter { Gender = value },
            _ => new SearchFilter { State = "NY", Sort = value },
        };

        var ex = Assert.Throws<SearchValidationException>(() => SearchValidation.Normalize(filter));

        Assert.Contains(field, ex.Errors.Keys);
    }

    [Theory]
    [InlineData(0, "11701")]
    [InlineData(101, "11701")]
    [InlineData(10, null)]
    public void Radius_must_be_1_to_100_miles_with_a_zip(int radius, string? zip)
    {
        var ex = Assert.Throws<SearchValidationException>(() =>
            SearchValidation.Normalize(new SearchFilter { State = "NY", Zip5 = zip, RadiusMiles = radius }));

        Assert.Contains(nameof(SearchFilter.RadiusMiles), ex.Errors.Keys);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 201)]
    [InlineData(0, 50)]
    [InlineData(201, 50)] // past the 10,000-row paging window
    public void Paging_is_bounded(int page, int pageSize)
    {
        Assert.Throws<SearchValidationException>(() =>
            SearchValidation.Normalize(new SearchFilter { State = "NY", Page = page, PageSize = pageSize }));
    }

    [Fact]
    public void Specialization_needs_a_classification() =>
        Assert.Throws<SearchValidationException>(() => SearchValidation.Normalize(new SearchFilter { State = "NY", Specialization = "Pediatrics" }));

    [Fact]
    public void Values_are_trimmed_and_codes_upper_cased()
    {
        var f = SearchValidation.Normalize(new SearchFilter { State = " ny ", TaxonomyCode = "111n00000x", LastName = "  smith ", City = " " });

        Assert.Equal(("NY", "111N00000X", "smith", (string?)null), (f.State, f.TaxonomyCode, f.LastName, f.City));
    }

    [Fact]
    public void User_input_only_travels_as_parameters()
    {
        const string evil = "x' OR 1=1; DROP TABLE provider; --";
        var filter = SearchValidation.Normalize(new SearchFilter
        {
            LastName = evil[..30], FirstName = "Ann", OrgName = "Acme", City = evil[..20], Credential = "M.D.",
            State = "NY", Gender = "F", EntityType = 1,
        });

        var query = new SearchQuery(filter, ["111N00000X"], null);

        foreach (var sql in new[] { query.CountSql, query.PageSql, query.LocationsSql, query.AllSql })
        {
            Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Acme", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("111N00000X", sql, StringComparison.Ordinal);
        }

        Assert.Contains("p.last_name LIKE @lastName", query.CountSql, StringComparison.Ordinal);
        Assert.Contains("s.city = @city", query.CountSql, StringComparison.Ordinal);
        Assert.Equal("MD%", query.Parameters.Get<string>("credential")); // punctuation-insensitive credential prefix
    }

    [Theory]
    [InlineData("O'Br", "O'Br%")]
    [InlineData("100%", @"100\%%")]
    [InlineData("A_B", @"A\_B%")]
    [InlineData(@"C:\x", @"C:\\x%")]
    public void Name_prefixes_escape_like_wildcards(string input, string expected) => Assert.Equal(expected, SearchQuery.Prefix(input));

    [Fact]
    public void Specialty_matches_any_slot_and_location_filters_any_location()
    {
        var filter = SearchValidation.Normalize(new SearchFilter { Classification = "Chiropractor", CountyFips = "36103" });

        var query = new SearchQuery(filter, ["111N00000X", "111NI0013X"], null);

        // Specialty + location is answered by provider_search in one index range read.
        Assert.Equal("FROM (SELECT DISTINCT s.npi FROM provider_search s WHERE s.taxonomy_code IN @taxonomyCodes AND " +
            "s.zip5 IN (SELECT z.zip5 FROM zip_county z WHERE z.county_fips = @countyFips)) c JOIN provider p ON p.npi = c.npi", query.From);
        Assert.Equal(["111N00000X", "111NI0013X"], query.Parameters.Get<string[]>("taxonomyCodes"));
    }

    [Fact]
    public void A_classification_with_no_codes_matches_nothing_but_stays_valid_sql()
    {
        var query = new SearchQuery(SearchValidation.Normalize(new SearchFilter { Classification = "Nothing" }), [], null);

        Assert.Equal(["-"], query.Parameters.Get<string[]>("taxonomyCodes"));
    }

    [Fact]
    public void Radius_uses_a_bounding_box_then_the_great_circle_distance()
    {
        var filter = SearchValidation.Normalize(new SearchFilter { Zip5 = "11701", RadiusMiles = 10 });

        var query = new SearchQuery(filter, null, new GeoPoint(40.682177, -73.414596));

        Assert.Contains("c.lat BETWEEN @latMin AND @latMax", query.From, StringComparison.Ordinal);
        Assert.Contains("ASIN(SQRT(", query.From, StringComparison.Ordinal);
        Assert.DoesNotContain("zip5 = @zip5", query.From, StringComparison.Ordinal);
        Assert.Equal(40.682177 - 10 / 69.0, query.Parameters.Get<double>("latMin"), 6);
        Assert.True(query.Parameters.Get<double>("lonMax") - -73.414596 > 10 / 69.0); // longitude degrees are shorter
    }

    [Theory]
    [InlineData(null, "p.sort_name ASC, p.npi")]
    [InlineData("name", "p.sort_name ASC, p.npi")]
    [InlineData("-lastUpdate", "p.last_update_date DESC, p.npi DESC")]
    [InlineData("NPI", "p.npi ASC")]
    public void Sorting_comes_from_a_whitelist(string? sort, string orderBy)
    {
        var query = new SearchQuery(SearchValidation.Normalize(new SearchFilter { State = "NY", Sort = sort }), null, null);

        Assert.Equal(orderBy, query.OrderBy);
    }

    [Fact]
    public void Location_sorts_use_the_matching_location()
    {
        var query = new SearchQuery(SearchValidation.Normalize(new SearchFilter { State = "NY", Sort = "-city" }), null, null);

        Assert.StartsWith("(SELECT ml.city FROM provider_location ml WHERE ml.npi = p.npi AND ml.state = @state ORDER BY ml.is_primary DESC, ml.id LIMIT 1) DESC", query.OrderBy, StringComparison.Ordinal);
    }

    // The most selective filter drives the query (Stage 3.5): it becomes the derived table c, and the
    // remaining filters check the candidates.
    [Theory]
    [InlineData("npi", "FROM (SELECT @npi AS npi) c JOIN provider p ON p.npi = c.npi WHERE p.last_name LIKE @lastName")]
    [InlineData("taxonomy", "FROM (SELECT DISTINCT t.npi FROM provider_taxonomy t WHERE t.taxonomy_code IN @taxonomyCodes) c JOIN provider p ON p.npi = c.npi WHERE p.last_name LIKE @lastName")]
    [InlineData("name", "FROM (SELECT d.npi FROM provider d WHERE d.last_name LIKE @lastName) c JOIN provider p ON p.npi = c.npi WHERE EXISTS (SELECT 1 FROM provider_location l WHERE l.npi = p.npi AND l.state = @state)")]
    [InlineData("location", "FROM (SELECT DISTINCT l.npi FROM provider_location l WHERE l.state = @state) c JOIN provider p ON p.npi = c.npi WHERE p.gender = @gender")]
    [InlineData("credential", "FROM (SELECT d.npi FROM provider d WHERE d.credential_key LIKE @credential) c JOIN provider p ON p.npi = c.npi WHERE p.entity_type = @entityType")]
    [InlineData("attributes", "FROM provider p WHERE p.entity_type = @entityType AND p.gender = @gender")]
    public void The_most_selective_filter_drives_the_query(string driver, string from)
    {
        var (filter, codes) = driver switch
        {
            "npi" => (new SearchFilter { Npi = "1234567893", LastName = "Smith" }, (string[]?)null),
            "taxonomy" => (new SearchFilter { Classification = "Chiropractor", LastName = "Smith" }, ["111N00000X"]),
            "name" => (new SearchFilter { LastName = "Smith", State = "NY" }, null),
            "location" => (new SearchFilter { State = "NY", Gender = "F" }, null),
            "credential" => (new SearchFilter { Credential = "MD", EntityType = 1 }, null),
            _ => (new SearchFilter { EntityType = 1, Gender = "F" }, null),
        };

        var query = new SearchQuery(SearchValidation.Normalize(filter), codes, null);

        Assert.Equal(from, query.From);
    }

    // §11 item 6: a whole-state search can match over a million providers. Those searches count first
    // and, when huge, page by walking the sort index instead of sorting every match.
    [Theory]
    [InlineData("state", true, "SELECT p.npi FROM provider p FORCE INDEX FOR ORDER BY (ix_provider_sort) WHERE p.gender = @gender AND EXISTS (SELECT /*+ NO_SEMIJOIN() */ 1 FROM provider_location l WHERE l.npi = p.npi AND l.state = @state) ORDER BY p.sort_name ASC, p.npi LIMIT @take OFFSET @skip")]
    [InlineData("attributes", true, "SELECT p.npi FROM provider p FORCE INDEX FOR ORDER BY (ix_provider_sort) WHERE p.entity_type = @entityType ORDER BY p.sort_name ASC, p.npi LIMIT @take OFFSET @skip")]
    [InlineData("specialty", false, null)]
    [InlineData("name", false, null)]
    public void Possibly_huge_searches_get_a_sort_index_plan(string kind, bool mayBeBroad, string? indexOrderSql)
    {
        var (filter, codes) = kind switch
        {
            "state" => (new SearchFilter { State = "CA", Gender = "F" }, (string[]?)null),
            "attributes" => (new SearchFilter { EntityType = 2 }, null),
            "specialty" => (new SearchFilter { Classification = "Chiropractor", State = "CA" }, ["111N00000X"]),
            _ => (new SearchFilter { LastName = "smith", State = "CA" }, null),
        };

        var query = new SearchQuery(SearchValidation.Normalize(filter), codes, null);

        Assert.Equal(mayBeBroad, query.MayBeBroad);
        if (mayBeBroad)
        {
            Assert.Equal(indexOrderSql, query.IndexOrderPageSql);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public void Day_windows_must_be_1_to_3650(int days)
    {
        Assert.Throws<SearchValidationException>(() => SearchValidation.Normalize(new SearchFilter { NewWithinDays = days }));
        Assert.Throws<SearchValidationException>(() => SearchValidation.Normalize(new SearchFilter { UpdatedWithinDays = days }));
    }

    [Fact]
    public void A_short_new_or_updated_window_drives_the_search_and_a_long_one_only_checks()
    {
        var recent = new SearchQuery(SearchValidation.Normalize(new SearchFilter { NewWithinDays = 30, State = "NY" }), null, null);
        Assert.Contains("FROM (SELECT DISTINCT d.npi FROM provider d WHERE d.enumeration_date >= @enumeratedSince) c", recent.PageSql, StringComparison.Ordinal);
        Assert.Contains("EXISTS (SELECT 1 FROM provider_location l", recent.PageSql, StringComparison.Ordinal);
        Assert.Equal(DateTime.UtcNow.Date.AddDays(-30), recent.Parameters.Get<DateTime>("enumeratedSince"));
        Assert.True(recent.HasSelectiveDriver);

        var updated = new SearchQuery(SearchValidation.Normalize(new SearchFilter { UpdatedWithinDays = 7 }), null, null);
        Assert.Contains("d.last_update_date >= @updatedSince", updated.PageSql, StringComparison.Ordinal);

        // Dates drive and count first, so a long window can walk a sort index, including the date indexes.
        var year = new SearchQuery(SearchValidation.Normalize(new SearchFilter { NewWithinDays = 365, Sort = "-enumeration" }), null, null);
        Assert.True(year.MayBeBroad);
        Assert.Contains("FORCE INDEX FOR ORDER BY (ix_provider_enumeration)", year.IndexOrderPageSql, StringComparison.Ordinal);
        Assert.EndsWith("ORDER BY p.enumeration_date DESC, p.npi DESC LIMIT @take OFFSET @skip", year.IndexOrderPageSql, StringComparison.Ordinal);
        // The map drives only from shorter windows.
        Assert.True(new SearchQuery(SearchValidation.Normalize(new SearchFilter { NewWithinDays = 90 }, requireFilter: false), null, null, areaSearch: true).HasSelectiveDriver);
        Assert.False(recent.MayBeBroad); // with a state, no count first
        Assert.False(new SearchQuery(SearchValidation.Normalize(new SearchFilter { NewWithinDays = 365 }, requireFilter: false), null, null, areaSearch: true).HasSelectiveDriver);

        // A county's locations are few: they drive, and the date is checked per candidate.
        Assert.Contains("FROM (SELECT DISTINCT l.npi FROM provider_location l",
            new SearchQuery(SearchValidation.Normalize(new SearchFilter { NewWithinDays = 30, CountyFips = "36103" }), null, null).PageSql, StringComparison.Ordinal);

        // A year of updates is millions of providers: the state drives, the date is checked per candidate.
        var older = new SearchQuery(SearchValidation.Normalize(new SearchFilter { UpdatedWithinDays = 365, State = "NY" }), null, null);
        Assert.Contains("FROM (SELECT DISTINCT l.npi FROM provider_location l", older.PageSql, StringComparison.Ordinal);
        Assert.Contains("p.last_update_date >= @updatedSince", older.PageSql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("North Shore, the L.I. hospital", "+north* +shore* +hospital*")]
    [InlineData("St. Mary's", "+mary*")]
    [InlineData("ACME acme Clinic", "+acme* +clinic*")]
    [InlineData("NY", null)]
    [InlineData("the", null)]
    public void Organization_words_are_required_word_prefixes(string name, string? expected) =>
        Assert.Equal(expected, NameSearch.OrganizationWords(name));

    [Fact]
    public void Similar_people_names_match_by_prefix_or_sound_with_prefix_matches_first()
    {
        var query = new SearchQuery(SearchValidation.Normalize(new SearchFilter { LastName = "Smiht", FirstName = "Jon", NameMatch = "SIMILAR", State = "NY" }), null, null);

        Assert.Contains("(d.last_name LIKE @lastName OR d.last_phonetic = LEFT(SOUNDEX(", query.PageSql, StringComparison.Ordinal);
        Assert.Contains("(d.first_name LIKE @firstName OR d.first_phonetic = LEFT(SOUNDEX(", query.PageSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY (p.last_name LIKE @lastName) DESC, (p.first_name LIKE @firstName) DESC, p.sort_name ASC", query.PageSql, StringComparison.Ordinal);
        Assert.Equal("Smiht", query.Parameters.Get<string>("lastNameSound"));

        // An explicit sort is honoured as asked; the default "prefix" match is unchanged.
        Assert.Contains("ORDER BY p.npi ASC",
            new SearchQuery(SearchValidation.Normalize(new SearchFilter { LastName = "Smiht", NameMatch = "similar", Sort = "npi" }), null, null).PageSql, StringComparison.Ordinal);
        var prefix = new SearchQuery(SearchValidation.Normalize(new SearchFilter { LastName = "Smiht", NameMatch = "prefix" }), null, null);
        Assert.Null(prefix.Filter.NameMatch);
        Assert.DoesNotContain("phonetic", prefix.PageSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Similar_organization_names_drive_from_the_fulltext_table_ranked_by_relevance()
    {
        var query = new SearchQuery(SearchValidation.Normalize(new SearchFilter { OrgName = "shore north", NameMatch = "similar" }), null, null);

        Assert.Contains("FROM provider_org_name o WHERE MATCH(o.name) AGAINST (@orgWords IN BOOLEAN MODE)", query.PageSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY c.score DESC, p.sort_name ASC", query.PageSql, StringComparison.Ordinal);
        Assert.Equal("+shore* +north*", query.Parameters.Get<string>("orgWords"));
        Assert.True(query.HasSelectiveDriver);

        // With a specialty + location driving, the words are a per-candidate check and the legal-name prefix ranks first.
        var checkedOnly = new SearchQuery(SearchValidation.Normalize(new SearchFilter { OrgName = "shore north", NameMatch = "similar", State = "NY" }), ["282N00000X"], null);
        Assert.Contains("p.npi IN (SELECT o.npi FROM provider_org_name o WHERE MATCH", checkedOnly.PageSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY (p.org_name LIKE @orgName) DESC, p.sort_name", checkedOnly.PageSql, StringComparison.Ordinal);

        // No indexable word: the prefix match alone.
        Assert.DoesNotContain("MATCH", new SearchQuery(SearchValidation.Normalize(new SearchFilter { OrgName = "NY", NameMatch = "similar" }), null, null).PageSql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Name_match_must_be_prefix_or_similar() =>
        Assert.Throws<SearchValidationException>(() => SearchValidation.Normalize(new SearchFilter { LastName = "x", NameMatch = "fuzzy" }));

    [Fact]
    public void Sorts_without_an_index_have_no_sort_index_plan() =>
        Assert.Null(new SearchQuery(SearchValidation.Normalize(new SearchFilter { State = "CA", Sort = "-city" }), null, null).IndexOrderPageSql);

    [Fact]
    public void The_page_query_starts_from_the_driver_and_returns_the_total()
    {
        var query = new SearchQuery(SearchValidation.Normalize(new SearchFilter { State = "NY" }), null, null);

        Assert.StartsWith("SELECT /*+ JOIN_ORDER(c, p) */ p.npi AS Npi, COUNT(*) OVER () AS Total FROM (", query.PageSql, StringComparison.Ordinal);
        Assert.EndsWith("ORDER BY p.sort_name ASC, p.npi LIMIT @take OFFSET @skip", query.PageSql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "SMITH", "JOHN", "Q", "JR", null, "SMITH, JOHN Q JR")]
    [InlineData(1, "SMITH", null, null, null, null, "SMITH")]
    [InlineData(2, null, null, null, null, "ACME CLINIC, LLC", "ACME CLINIC, LLC")]
    public void Names_display_as_last_first_middle_suffix_or_organization(int type, string? last, string? first, string? middle, string? suffix, string? org, string expected) =>
        Assert.Equal(expected, ProviderNames.Display(type, last, first, middle, suffix, org));

    [Theory]
    [InlineData("11701", "1234", null, "11701-1234")]
    [InlineData("11701", null, null, "11701")]
    [InlineData(null, null, "M5V 2T6", "M5V 2T6")]
    public void Zips_display_as_zip_plus_four(string? zip5, string? zip4, string? postal, string expected) =>
        Assert.Equal(expected, ProviderNames.Zip(zip5, zip4, postal));
}
