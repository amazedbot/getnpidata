namespace Npi.Core;

/// <summary>Format rules for user-supplied search values (CLAUDE.md §7, Stage 3.4).</summary>
public static class InputFormats
{
    /// <summary>An NPI is exactly 10 ASCII digits.</summary>
    public static bool IsNpi(string? value) => IsDigits(value, 10);

    /// <summary>
    /// True when the NPI's last digit is its check digit: the Luhn algorithm over the first nine digits
    /// with the "80840" health-industry prefix (CMS NPI standard). Catches most typos.
    /// </summary>
    public static bool HasValidCheckDigit(string? npi)
    {
        if (!IsNpi(npi))
        {
            return false;
        }

        var sum = 24; // the doubled-digit Luhn sum of the 80840 prefix
        for (var i = 0; i < 9; i++)
        {
            var d = npi![8 - i] - '0';
            if (i % 2 == 0)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return (10 - (sum % 10)) % 10 == npi![9] - '0';
    }

    /// <summary>A search ZIP is exactly 5 ASCII digits.</summary>
    public static bool IsZip5(string? value) => IsDigits(value, 5);

    private static bool IsDigits(string? value, int length) =>
        value is not null && value.Length == length && value.All(char.IsAsciiDigit);
}
