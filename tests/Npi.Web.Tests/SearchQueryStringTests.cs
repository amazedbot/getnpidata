using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Npi.Core.Search;
using Npi.Web.Search;

namespace Npi.Web.Tests;

public class SearchQueryStringTests
{
    private static IQueryCollection Query(string qs) => new QueryCollection(QueryHelpers.ParseQuery(qs));

    [Fact]
    public void All_api_parameter_names_are_read()
    {
        var (f, errors) = SearchQueryString.Parse(Query(
            "?classification=Chiropractor&specialization=Sports%20Physician&taxonomy=111NS0005X&state=NY&county=36103&city=Babylon&zip=11702" +
            "&radius=10&lastName=o%27br&firstName=jose&orgName=acme&npi=1234567893&entityType=1&gender=F&credential=MD&sort=-city&page=3&pageSize=100"));

        Assert.Empty(errors);
        Assert.Equal(new SearchFilter
        {
            Classification = "Chiropractor", Specialization = "Sports Physician", TaxonomyCode = "111NS0005X", State = "NY", CountyFips = "36103",
            City = "Babylon", Zip5 = "11702", RadiusMiles = 10, LastName = "o'br", FirstName = "jose", OrgName = "acme", Npi = "1234567893",
            EntityType = 1, Gender = "F", Credential = "MD", Sort = "-city", Page = 3, PageSize = 100,
        }, f);
    }

    [Fact]
    public void Non_numeric_numbers_are_reported_not_ignored()
    {
        var (_, errors) = SearchQueryString.Parse(Query("?state=NY&radius=ten&page=x"));

        Assert.Equal(["Page", "RadiusMiles"], errors.Keys.Order());
    }

    [Fact]
    public void A_filter_round_trips_through_its_query_string()
    {
        var filter = new SearchFilter { Classification = "Chiropractor", State = "NY", CountyFips = "36103", LastName = "O'Brien & Sons", Page = 4 };

        var qs = SearchQueryString.ToQueryString(filter);
        var (back, _) = SearchQueryString.Parse(Query(qs));

        Assert.Equal(filter, back);
        Assert.DoesNotContain("pageSize", qs, StringComparison.Ordinal); // defaults are left out
    }

    [Fact]
    public void Csv_and_sort_links_keep_the_filters_and_reset_paging()
    {
        var filter = new SearchFilter { Classification = "Chiropractor", State = "NY", Page = 7, Sort = "name" };

        Assert.Equal("?classification=Chiropractor&state=NY&sort=name", SearchQueryString.ToQueryString(filter, includePaging: false));
        Assert.Equal("?classification=Chiropractor&state=NY&sort=-name", SearchQueryString.ToQueryString(filter, page: 1, sort: "-name"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("?page=2&sort=name", false)] // paging/sorting alone is not a search
    [InlineData("?state=", false)]
    [InlineData("?state=NY", true)]
    [InlineData("?entityType=2", true)]
    public void Only_real_filters_start_a_search(string qs, bool expected) =>
        Assert.Equal(expected, SearchQueryString.HasAnyFilter(Query(qs)));

    [Fact]
    public void Parameter_table_matches_the_parser()
    {
        // Every documented parameter sets its SearchFilter property, so the OpenAPI document can't drift from Parse.
        foreach (var p in SearchQueryString.Parameters)
        {
            var raw = p.Type switch { SearchQueryString.ParameterType.WholeNumber => "7", SearchQueryString.ParameterType.TrueFalse => "true", _ => "x" };
            var (filter, errors) = SearchQueryString.Parse(Query($"?{p.Name}={raw}"));
            Assert.Empty(errors);
            var value = typeof(SearchFilter).GetProperty(p.Field)!.GetValue(filter);
            var unset = typeof(SearchFilter).GetProperty(p.Field)!.GetValue(new SearchFilter());
            Assert.True(!Equals(value, unset), $"{p.Name} does not set {p.Field}");
        }

        Assert.Equal(SearchQueryString.Parameters.Count, SearchQueryString.Parameters.Select(p => p.Name).Distinct().Count());
    }

    [Fact]
    public void Validation_errors_use_parameter_names()
    {
        var errors = SearchQueryString.ByParameterName(new Dictionary<string, string[]>
        {
            [nameof(SearchFilter.RadiusMiles)] = ["bad radius"],
            [nameof(SearchFilter.Zip5)] = ["bad zip"],
            ["filter"] = ["need a filter"],
        });

        Assert.Equal(["bad radius"], errors["radius"]);
        Assert.Equal(["bad zip"], errors["zip"]);
        Assert.Equal(["need a filter"], errors["filter"]);
    }
}
