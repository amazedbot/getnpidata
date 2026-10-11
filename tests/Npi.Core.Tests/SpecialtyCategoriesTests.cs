using Npi.Core.Search;

namespace Npi.Core.Tests;

/// <summary>The plain specialty categories (owner, 2026-10-11): the embedded lists and how codes resolve.</summary>
public class SpecialtyCategoriesTests
{
    private static readonly SpecialtyCategories Categories = SpecialtyCategories.Default;

    [Fact]
    public void The_embedded_lists_load_with_every_category_filled()
    {
        Assert.Equal("Doctors (MD, DO)", Categories.Categories[0].Name);
        Assert.Equal("Other", Categories.Categories[^1].Name);
        Assert.All(Categories.Categories, c => Assert.NotEmpty(c.Subcategories));
        Assert.All(Categories.Categories.SelectMany(c => c.Subcategories), s => Assert.NotEmpty(s.Codes));

        // Names are unique within a category, and every "Other …" subcategory comes last.
        Assert.All(Categories.Categories, c =>
        {
            Assert.Equal(c.Subcategories.Count, c.Subcategories.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.DoesNotContain(c.Subcategories.SkipWhile(s => !s.Name.StartsWith("Other", StringComparison.Ordinal)), s => !s.Name.StartsWith("Other", StringComparison.Ordinal));
        });

        // An everyday word means one thing.
        var synonyms = Categories.Categories.SelectMany(c => c.Synonyms.Concat(c.Subcategories.SelectMany(s => s.Synonyms))).ToList();
        Assert.Equal(synonyms.Count, synonyms.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("207RC0000X", "Doctors (MD, DO)", "Heart (cardiology)", "Cardiologist")]
    [InlineData("2080P0202X", "Doctors (MD, DO)", "Heart (cardiology)", "Cardiologist")]          // pediatric cardiology: the organ first
    [InlineData("2084P0800X", "Doctors (MD, DO)", "Mental health (psychiatry)", "Psychiatrist")]
    [InlineData("111N00000X", "Chiropractic & complementary care", "Chiropractors", "Chiropractor")]
    [InlineData("1223X0400X", "Dentists", "Orthodontists (braces)", "Orthodontist")]
    [InlineData("363LF0000X", "Nurse practitioners", "Family & primary care", "Family nurse practitioner")]
    [InlineData("363L00000X", "Nurse practitioners", "Other or not specified", "Nurse practitioner")]
    [InlineData("3336C0003X", "Pharmacies", "Retail pharmacies", "Retail pharmacy")]
    [InlineData("183500000X", "Pharmacists", "Pharmacists", "Pharmacist")]
    [InlineData("261QU0200X", "Clinics & centers", "Urgent care & emergency centers", "Urgent care center")]
    public void A_code_is_shown_in_its_first_subcategory(string code, string category, string subcategory, string singular)
    {
        Assert.Equal(new SpecialtyPlacement(category, subcategory, singular), Categories.Place(code));
        Assert.Equal(new SpecialtyPlacement(category, subcategory, singular), Categories.Place(code.ToLowerInvariant()));
    }

    [Fact]
    public void A_code_can_sit_in_several_subcategories()
    {
        var doctors = Categories.Find("doctors (md, do)")!;
        var mentalHealth = Categories.Find("Mental & behavioral health")!;
        Assert.True(Categories.Contains(doctors, doctors.Find("Mental health (psychiatry)"), "2084P0800X"));
        Assert.True(Categories.Contains(mentalHealth, mentalHealth.Find("psychiatrists"), "2084P0800X"));
        Assert.True(Categories.Contains(mentalHealth, null, "2084P0800X"));
        Assert.False(Categories.Contains(doctors, doctors.Find("Heart (cardiology)"), "2084P0800X"));

        // Pediatric cardiologists are cardiologists and pediatricians.
        Assert.True(Categories.Contains(doctors, doctors.Find("Children & teens (pediatrics)"), "2080P0202X"));
    }

    [Fact]
    public void A_code_missing_from_the_lists_falls_under_other()
    {
        Assert.False(Categories.IsMapped("999X00000X"));
        Assert.Equal(new SpecialtyPlacement("Other", "Other", "Health care provider"), Categories.Place("999X00000X"));
        var other = Categories.Find("Other")!;
        Assert.True(Categories.Contains(other, null, "999X00000X"));
        Assert.True(Categories.Contains(other, Categories.Fallback, "999X00000X"));
        Assert.False(Categories.Contains(other, other.Find("Veterinarians"), "999X00000X"));
        Assert.False(Categories.Contains(Categories.Find("Dentists")!, null, "999X00000X"));
        Assert.Null(Categories.Place(null));
    }

    [Fact]
    public void A_code_list_naming_an_unknown_subcategory_is_refused()
    {
        var categories = "category,subcategory,title,singular,synonyms\nOther,,Other providers,Health care provider,\nOther,Other,Other providers,Health care provider,\n";
        var ok = SpecialtyCategories.Parse(new StringReader(categories), new StringReader("code,category,subcategory\n111N00000X,Other,Other\n"));
        Assert.True(ok.IsMapped("111N00000X"));
        Assert.Throws<InvalidDataException>(() =>
            SpecialtyCategories.Parse(new StringReader(categories), new StringReader("code,category,subcategory\n111N00000X,Other,Typo\n")));
    }
}
