namespace Npi.Core;

/// <summary>Format rules for user-supplied search values (CLAUDE.md §7, Stage 3.4).</summary>
public static class InputFormats
{
    /// <summary>An NPI is exactly 10 ASCII digits.</summary>
    public static bool IsNpi(string? value) => IsDigits(value, 10);

    /// <summary>A search ZIP is exactly 5 ASCII digits.</summary>
    public static bool IsZip5(string? value) => IsDigits(value, 5);

    private static bool IsDigits(string? value, int length) =>
        value is not null && value.Length == length && value.All(char.IsAsciiDigit);
}
