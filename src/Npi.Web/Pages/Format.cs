namespace Npi.Web.Pages;

/// <summary>Small display helpers for the pages.</summary>
public static class Format
{
    /// <summary>Whole dollars: $1,234.</summary>
    public static string Money(double value) => value.ToString("$#,##0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>An Open Payments amount: "—" for none, else the dollars and, when given, how many payments.</summary>
    public static string Amount(double value, int? records = null) => value <= 0 ? "—"
        : records is > 0 ? $"{Money(value)} ({records.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)})" : Money(value);

    /// <summary>A count with thousands separators.</summary>
    public static string Number(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"5165550100" → "(516) 555-0100"; anything else unchanged.</summary>
    public static string? Phone(string? digits) =>
        digits is { Length: 10 } && digits.All(char.IsAsciiDigit) ? $"({digits[..3]}) {digits[3..6]}-{digits[6..]}" : digits;
}
