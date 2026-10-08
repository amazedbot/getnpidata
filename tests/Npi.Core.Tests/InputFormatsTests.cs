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
}
