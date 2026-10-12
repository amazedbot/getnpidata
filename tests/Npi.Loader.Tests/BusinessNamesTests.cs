using Npi.Loader.Projection;

namespace Npi.Loader.Tests;

public class BusinessNamesTests
{
    [Theory]
    [InlineData("SUNRISE FAMILY DENTAL PC", "Sunrise Dental")]
    [InlineData("SMITH JONES DENTAL CLINIC", "SMITH, JONES & CO, LLC")]
    [InlineData("GOOD SAMARITAN HOSPITAL MEDICAL CENTER", "Good Samaritan University Hospital")]
    [InlineData("ACME PHYSICAL THERAPY PLLC", "Acme Physical Therapy & Sports Rehab")]
    public void The_same_business_under_different_spellings(string a, string b) => Assert.True(BusinessNames.Same(a, b));

    [Theory]
    [InlineData("SMITH CHIROPRACTIC PC", "Jones Chiropractic")]
    [InlineData("FAMILY MEDICAL CENTER PC", "Family Health Center")]  // only words every clinic shares
    [InlineData("NORTHWELL HEALTH PHYSICIAN PARTNERS", "Main Street Pizza")]
    [InlineData("SUNRISE DENTAL", null)]
    public void Different_businesses(string a, string? b) => Assert.False(BusinessNames.Same(a, b));

    [Fact]
    public void Generic_words_are_dropped() =>
        Assert.Equal(["SUNRISE"], BusinessNames.Words("Sunrise Family Dental Care, P.C.").Order());
}
