namespace Npi.Core.Tests;

public class InputFormatsTests
{
    [Theory]
    [InlineData("1234567893", true)]
    [InlineData("0000000000", true)]
    [InlineData("123456789", false)]
    [InlineData("12345678901", false)]
    [InlineData("12345 6789", false)]
    [InlineData("123456789a", false)]
    [InlineData("١٢٣٤٥٦٧٨٩٠", false)] // non-ASCII digits
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsNpi(string? value, bool expected) => Assert.Equal(expected, InputFormats.IsNpi(value));

    [Theory]
    [InlineData("11701", true)]
    [InlineData("00501", true)]
    [InlineData("1170", false)]
    [InlineData("11701-1234", false)]
    [InlineData("117O1", false)]
    [InlineData(" 11701", false)]
    [InlineData(null, false)]
    public void IsZip5(string? value, bool expected) => Assert.Equal(expected, InputFormats.IsZip5(value));

    [Theory]
    [InlineData("1234567893", true)]  // the CMS example NPI
    [InlineData("1003000126", true)]
    [InlineData("1234567890", false)] // one digit off
    [InlineData("1003000127", false)]
    [InlineData("123456789", false)]
    [InlineData("12345678a3", false)]
    [InlineData(null, false)]
    public void Npi_check_digit_follows_the_luhn_rule_with_the_80840_prefix(string? npi, bool valid) =>
        Assert.Equal(valid, InputFormats.HasValidCheckDigit(npi));
}
