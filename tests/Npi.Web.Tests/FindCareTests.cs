using Npi.Core.Search;
using Npi.Web.Pages;

namespace Npi.Web.Tests;

/// <summary>Find care (docs/PRODUCT_PLAN.md step 1.8): the kinds of care, and the wording on the cards.</summary>
public class FindCareTests
{
    [Fact]
    public void Every_kind_of_care_names_real_specialties()
    {
        foreach (var need in CareFinder.Needs)
        {
            Assert.NotEmpty(need.Specialties);
            foreach (var (category, subcategory) in need.Specialties)
            {
                var c = SpecialtyCategories.Default.Categories.SingleOrDefault(x => x.Name == category);
                Assert.True(c is not null, $"{need.Key}: unknown category {category}");
                Assert.True(subcategory is null || c.Find(subcategory) is not null, $"{need.Key}: unknown subcategory {subcategory}");
            }
        }

        Assert.Equal(CareFinder.Needs.Count, CareFinder.Needs.Select(n => n.Key).Distinct().Count());
    }

    [Fact]
    public void Something_else_picks_a_category_or_a_subcategory()
    {
        Assert.Equal("family", CareFinder.Resolve("FAMILY", "ignored")!.Key);
        var podiatry = CareFinder.Resolve("other", "Foot care (podiatry)")!;
        Assert.Equal(("Podiatrist", (int?)null), (podiatry.Label, podiatry.EntityType));
        Assert.Equal([("Foot care (podiatry)", (string?)null)], podiatry.Specialties);
        var audiologists = CareFinder.Resolve(null, "Hearing & speech|Audiologists")!;
        Assert.Equal(("Audiologist", "Audiologists"), (audiologists.Label, audiologists.Plural));
        Assert.Null(CareFinder.Resolve("other", "Nope"));
        Assert.Null(CareFinder.Resolve("other", "Hearing & speech|Nope"));
        Assert.Null(CareFinder.Resolve(null, null));
    }

    [Fact]
    public void Cards_read_naturally()
    {
        Assert.Equal(["0.3 miles", "1 mile", "2.4 miles", "12 miles"], new[] { 0.3, 1.2, 2.44, 11.6 }.Select(FindCareModel.Miles));
        var person = new ProviderSummary("1234567893", 1, "SMITH, DANA L", "NP", null, null, null, null, null, null, null, null, "F", null, null);
        Assert.Equal("Dana L Smith", FindCareModel.NaturalName(person));
        Assert.Equal("CITY URGENT CARE PC", FindCareModel.NaturalName(person with { EntityType = 2, Name = "CITY URGENT CARE PC" }));

        var page = new FindCareModel(null!, null!, null!) { Need = "back", Zip = "11702", Medicare = true, Gender = "F" };
        Assert.Equal("/find-care?need=back&zip=11702&medicare=true&gender=F&p=2", page.Link(2));
        Assert.Equal("/find-care?need=other&specialty=Hearing%20%26%20speech%7CAudiologists&zip=11702", new FindCareModel(null!, null!, null!)
            { Need = "other", Specialty = "Hearing & speech|Audiologists", Zip = "11702" }.Link());
    }
}
