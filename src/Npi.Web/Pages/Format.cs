namespace Npi.Web.Pages;

/// <summary>Small display helpers for the pages.</summary>
public static class Format
{
    /// <summary>"5165550100" → "(516) 555-0100"; anything else unchanged.</summary>
    public static string? Phone(string? digits) =>
        digits is { Length: 10 } && digits.All(char.IsAsciiDigit) ? $"({digits[..3]}) {digits[3..6]}-{digits[6..]}" : digits;
}
