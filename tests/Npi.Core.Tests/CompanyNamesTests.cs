using Npi.Core.Search;

namespace Npi.Core.Tests;

public class CompanyNamesTests
{
    [Theory]
    [InlineData("ELI LILLY AND COMPANY", "LILLY ELI & CO")]
    [InlineData("PFIZER INC.", "Pfizer Inc")]
    [InlineData("TEVA PHARMACEUTICALS USA, INC.", "Teva Pharmaceuticals USA Inc")]
    [InlineData("Dr. Reddy's Laboratories, Inc.", "DR REDDYS LABORATORIES INC")]
    [InlineData("Nestlé Health Science", "NESTLE HEALTH SCIENCE")]
    [InlineData("Boston Scientific Corporation", "BOSTON SCIENTIFIC CORP")]
    [InlineData("Medtronic, L.L.C.", "MEDTRONIC")]
    public void Spellings_of_one_company_share_a_key(string a, string b) =>
        Assert.Equal(CompanyNames.Key(a), CompanyNames.Key(b));

    [Theory]
    [InlineData("ELI LILLY AND COMPANY", "LILLY USA, LLC")]
    [InlineData("MERCK SHARP & DOHME LLC", "MERCK & CO., INC.")]
    [InlineData("Abbott Laboratories", "AbbVie Inc.")]
    public void Different_companies_have_different_keys(string a, string b) =>
        Assert.NotEqual(CompanyNames.Key(a), CompanyNames.Key(b));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("The Company, Inc.")]
    [InlineData("A B")]
    public void Names_without_a_distinctive_word_have_no_key(string? name) => Assert.Null(CompanyNames.Key(name));

    [Fact]
    public void Key_is_sorted_upper_case_words() => Assert.Equal("ELI LILLY", CompanyNames.Key("Eli Lilly and Company"));
}
