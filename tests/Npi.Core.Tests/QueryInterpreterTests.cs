using Npi.Core.Search;

namespace Npi.Core.Tests;

/// <summary>The search box's reading of a typed query (docs/PRODUCT_PLAN.md step 1.3), on a small vocabulary.</summary>
public class QueryInterpreterTests
{
    private static readonly SearchVocabulary Vocabulary = new(
        [
            new TaxonomyEntry("111N00000X", "Chiropractor", null, "Chiropractor"),
            new TaxonomyEntry("111NS0005X", "Chiropractor", "Sports Physician", "Sports Chiropractor"),
            new TaxonomyEntry("122300000X", "Dentist", null, "Dentist"),
            new TaxonomyEntry("1223X0400X", "Dentist", "Orthodontics and Dentofacial Orthopedics", "Orthodontist"),
            new TaxonomyEntry("207RC0000X", "Internal Medicine", "Cardiovascular Disease", "Cardiovascular Disease Physician"),
            new TaxonomyEntry("207R00000X", "Internal Medicine", null, "Internal Medicine Physician"),
            new TaxonomyEntry("2084P0800X", "Psychiatry & Neurology", "Psychiatry", "Psychiatry Physician"),
            new TaxonomyEntry("207Q00000X", "Family Medicine", null, "Family Medicine Physician"),
            new TaxonomyEntry("3336C0003X", "Pharmacy", "Community/Retail Pharmacy", "Community/Retail Pharmacy"),
            new TaxonomyEntry("282N00000X", "General Acute Care Hospital", null, "General Acute Care Hospital"),
        ],
        [
            new CountyTerm("36103", "NY", "Suffolk County", 1_520_000), new CountyTerm("25025", "MA", "Suffolk County", 770_000),
            new CountyTerm("36047", "NY", "Kings County", 2_600_000), new CountyTerm("29189", "MO", "St. Louis County", 990_000),
        ],
        [
            new CityTerm("BROOKLYN", "NY", 40), new CityTerm("BROOKLYN", "MI", 1), new CityTerm("BABYLON", "NY", 1),
            new CityTerm("SAINT JAMES", "NY", 1), new CityTerm("NEW YORK", "NY", 90), new CityTerm("PORTLAND", "OR", 30), new CityTerm("PORTLAND", "ME", 6),
        ],
        new Dictionary<string, string> { ["NY"] = "New York", ["MA"] = "Massachusetts", ["MO"] = "Missouri", ["OR"] = "Oregon", ["ME"] = "Maine", ["IN"] = "Indiana", ["CA"] = "California" });

    private static QueryInterpretation Read(string text) => QueryInterpreter.Interpret(text, Vocabulary);

    [Theory]
    [InlineData("1234567893")]
    [InlineData("1234 567 893")]
    [InlineData("123-456-7893")]
    public void An_npi_opens_its_page(string text)
    {
        var q = Read(text);
        Assert.Equal((QueryKind.Npi, "1234567893"), (q.Kind, q.Npi));
    }

    [Fact]
    public void A_specialty_and_a_county_make_a_list()
    {
        var q = Read("Chiropractors in Suffolk County, NY");
        Assert.Equal(QueryKind.List, q.Kind);
        Assert.Equal(("Chiropractic & complementary care", "Chiropractors", null, "NY", "36103"),
            (q.Filter.Category, q.Filter.Subcategory, q.Filter.Classification, q.Filter.State, q.Filter.CountyFips));
        Assert.Equal(["Specialty: Chiropractors", "State: New York", "County: Suffolk County, NY"], q.Parts.Select(p => $"{p.Label}: {p.Value}"));
        Assert.Equal(["category", "subcategory"], q.Parts[0].Parameters);
        Assert.Null(q.NameText);
    }

    [Fact]
    public void A_county_without_a_state_is_the_most_populous_one_with_a_note()
    {
        var q = Read("dentists suffolk county");
        Assert.Equal(("Dentists", null, "36103", "NY"), (q.Filter.Category, q.Filter.Subcategory, q.Filter.CountyFips, q.Filter.State));
        Assert.Contains(q.Notes, n => n.Contains("MA", StringComparison.Ordinal));

        Assert.Equal("25025", Read("dentists in suffolk county ma").Filter.CountyFips);
        Assert.Equal("29189", Read("dentists saint louis county").Filter.CountyFips); // St. = Saint
    }

    [Theory]
    [InlineData("cardiologists in brooklyn", "Doctors (MD, DO)", "Heart (cardiology)", "Brooklyn", "NY")]
    [InlineData("heart doctor in brooklyn", "Doctors (MD, DO)", "Heart (cardiology)", "Brooklyn", "NY")]
    [InlineData("cardiology brooklyn ny", "Doctors (MD, DO)", "Heart (cardiology)", "Brooklyn", "NY")]
    [InlineData("psychiatrist brooklyn ny", "Doctors (MD, DO)", "Mental health (psychiatry)", "Brooklyn", "NY")]
    [InlineData("family doctor in babylon", "Doctors (MD, DO)", "Family medicine & general practice", "Babylon", "NY")]
    [InlineData("orthodontists in portland me", "Dentists", "Orthodontists (braces)", "Portland", "ME")]
    [InlineData("pharmacies in new york city", "Pharmacies", null, "New York", "NY")]
    [InlineData("pharmacists in new york city", "Pharmacists", "Pharmacists", "New York", "NY")]
    [InlineData("doctors in brooklyn", "Doctors (MD, DO)", null, "Brooklyn", "NY")]
    [InlineData("family nurse practitioners in babylon", "Nurse practitioners", "Family & primary care", "Babylon", "NY")]
    public void Everyday_words_plurals_and_cities(string text, string category, string? subcategory, string city, string state)
    {
        var q = Read(text);
        Assert.Equal((QueryKind.List, category, subcategory, null as string, city, state),
            (q.Kind, q.Filter.Category, q.Filter.Subcategory, q.Filter.Classification, q.Filter.City, q.Filter.State));
    }

    [Fact]
    public void A_nucc_name_without_a_plain_category_stays_a_nucc_filter()
    {
        var q = Read("sports chiropractor saint james");
        Assert.Equal((QueryKind.List, null as string, "Chiropractor", "Sports Physician", "Saint James", "NY"),
            (q.Kind, q.Filter.Category, q.Filter.Classification, q.Filter.Specialization, q.Filter.City, q.Filter.State));
        Assert.Equal(["classification", "specialization"], q.Parts[0].Parameters);
    }

    [Fact]
    public void A_zip_with_near_or_a_distance_is_a_radius_search()
    {
        var near = Read("dentists near 11701");
        Assert.Equal(("Dentists", "11701", 10), (near.Filter.Category, near.Filter.Zip5, near.Filter.RadiusMiles));
        Assert.Equal(25, Read("chiropractors within 25 miles of 11701").Filter.RadiusMiles);
        Assert.Null(Read("dentists 11701").Filter.RadiusMiles);
        Assert.Equal("11701", Read("dentists 11701-4321").Filter.Zip5);
    }

    [Theory]
    [InlineData("smith", "smith")]
    [InlineData("John Smith", "John Smith")]
    [InlineData("Dr. Jordan Ellis", "Jordan Ellis")]
    [InlineData("Brooklyn", "Brooklyn")]          // a surname-like word alone isn't read as a town
    [InlineData("Stony Brook University Hospital", "Stony Brook University Hospital")]
    [InlineData("doctor john smith", "doctor john smith")]
    [InlineData("doctor jane brooklyn", "doctor jane brooklyn")]   // ... and the surname isn't a town   // "doctor" beside a name and no place isn't the Doctors category
    public void Only_a_name_searches_people_and_organizations(string text, string name)
    {
        var q = Read(text);
        Assert.Equal((QueryKind.Names, name), (q.Kind, q.NameText));
    }

    [Fact]
    public void A_name_beside_a_specialty_or_place_narrows_the_list()
    {
        var person = Read("dentist john smith in brooklyn");
        Assert.Equal(("Dentists", "Brooklyn", "john", "smith", "similar"), (person.Filter.Category, person.Filter.City, person.Filter.FirstName, person.Filter.LastName, person.Filter.NameMatch));

        var org = Read("bayside dental group ny");
        Assert.Equal(("NY", "bayside dental group"), (org.Filter.State, org.Filter.OrgName));
    }

    [Theory]
    [InlineData("doctors in Indiana")]
    [InlineData("hospitals in suffolk county ny")]
    public void State_codes_only_count_as_the_last_word_and_state_names_anywhere(string text)
    {
        var q = Read(text);
        Assert.Equal(QueryKind.List, q.Kind);
        Assert.NotNull(q.Filter.State);
    }

    [Fact]
    public void In_is_a_word_not_indiana_unless_it_is_last()
    {
        Assert.Equal("NY", Read("chiropractor in brooklyn ny").Filter.State);
        Assert.Equal("IN", Read("chiropractor in").Filter.State);
    }

    [Fact]
    public void An_organization_word_gives_no_place_context()
    {
        // "Stony Brook" is also a town, but "hospital" alone doesn't make the rest a place.
        var vocabulary = new SearchVocabulary(
            [new TaxonomyEntry("282N00000X", "General Acute Care Hospital", null, "General Acute Care Hospital")], [],
            [new CityTerm("STONY BROOK", "NY", 2)], new Dictionary<string, string> { ["NY"] = "New York" });
        Assert.Equal(QueryKind.Names, QueryInterpreter.Interpret("Stony Brook University Hospital", vocabulary).Kind);
        var list = QueryInterpreter.Interpret("hospitals in stony brook", vocabulary);
        Assert.Equal((QueryKind.List, "Hospitals", "Stony Brook"), (list.Kind, list.Filter.Category, list.Filter.City));
    }

    [Fact]
    public void Near_me_points_to_the_map_and_empty_text_is_empty()
    {
        Assert.Contains(Read("dentists near me").Notes, n => n.Contains("map", StringComparison.Ordinal));
        Assert.True(Read("dentists near me").NearMe);
        Assert.Equal(QueryKind.Empty, Read("   ").Kind);
        Assert.Equal(QueryKind.Empty, Read("dr").Kind);
    }
}
