using Npi.Core.Search;

namespace Npi.Core.Tests;

public class CompanyNameTests
{
    [Theory]
    [InlineData("ELI LILLY AND COMPANY", "LILLY")]
    [InlineData("LILLY USA, LLC", "LILLY")]
    [InlineData("PFIZER INC.", "PFIZER")]
    [InlineData("The Medical Products Group, Inc.", null)]
    [InlineData("AMERICAN REGENT, INC.", "REGENT")]
    [InlineData("3M Company", null)]
    public void Distinctive_word_skips_short_and_common_words(string name, string? expected) =>
        Assert.Equal(expected, CompanyService.DistinctiveWord(name));
}
