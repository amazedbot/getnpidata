namespace Npi.Web.Pages;

/// <summary>Small display helpers for the pages.</summary>
public static class Format
{
    /// <summary>Whole dollars: $1,234.</summary>
    public static string Money(double value) => value.ToString("$#,##0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>An Open Payments amount: "—" for none, else the dollars and, when given, how many payments.</summary>
    public static string Amount(double value, int? records = null) => value <= 0 ? "—"
        : records is > 0 ? $"{Money(value)} ({records.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)})" : Money(value);

    /// <summary>Dollars in short form for headlines and charts: $5.12B, $812M, $1.9M, $950K, $640.</summary>
    public static string ShortMoney(double value)
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        var a = Math.Abs(value);
        return a >= 1e9 ? "$" + (value / 1e9).ToString(a >= 1e10 ? "0.0" : "0.00", c) + "B"
            : a >= 1e6 ? "$" + (value / 1e6).ToString(a >= 1e8 ? "0" : a >= 1e7 ? "0.0" : "0.00", c) + "M"
            : a >= 1e4 ? "$" + (value / 1e3).ToString("0", c) + "K"
            : Money(value);
    }

    /// <summary>A count in short form: 4.42M, 332,888.</summary>
    public static string ShortNumber(long value) =>
        value >= 1_000_000 ? (value / 1e6).ToString(value >= 10_000_000 ? "0.0" : "0.00", System.Globalization.CultureInfo.InvariantCulture) + "M" : Number(value);

    /// <summary>
    /// An Open Payments "nature of payment" in short words for lists ("Speaking and faculty fees"); the official wording stays in the
    /// title attribute and the raw data.
    /// </summary>
    public static string Nature(string nature) => nature switch
    {
        _ when nature.StartsWith("Compensation for services other than consulting", StringComparison.OrdinalIgnoreCase) => "Speaking and faculty fees",
        _ when nature.StartsWith("Compensation for serving as faculty or as a speaker for an accredited", StringComparison.OrdinalIgnoreCase) => "Speaking at accredited education",
        _ when nature.StartsWith("Compensation for serving as faculty or as a speaker for a non-accredited", StringComparison.OrdinalIgnoreCase) => "Speaking at other education",
        _ when nature.StartsWith("Compensation for serving as faculty", StringComparison.OrdinalIgnoreCase) => "Speaking and faculty fees",
        _ when nature.StartsWith("Current or prospective ownership", StringComparison.OrdinalIgnoreCase) => "Ownership or investment",
        "Consulting Fee" => "Consulting",
        "Royalty or License" => "Royalties and licenses",
        _ => nature.Length > 1 ? char.ToUpperInvariant(nature[0]) + nature[1..].ToLowerInvariant() : nature,
    };

    /// <summary>A count with thousands separators.</summary>
    public static string Number(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"5165550100" → "(516) 555-0100"; anything else unchanged.</summary>
    public static string? Phone(string? digits) =>
        digits is { Length: 10 } && digits.All(char.IsAsciiDigit) ? $"({digits[..3]}) {digits[3..6]}-{digits[6..]}" : digits;

    /// <summary>Up to two initials for an avatar: "ELLIS, JORDAN A" → "JE", "Shore Point Chiropractic, PC" → "SP".</summary>
    public static string Initials(string name, int entityType)
    {
        var parts = name.Split(',', 2, StringSplitOptions.TrimEntries);
        var words = (entityType == 1 && parts.Length == 2 ? parts[1] + " " + parts[0] : name)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetter(w[0])).ToList();
        return words.Count switch
        {
            0 => "?",
            1 => char.ToUpperInvariant(words[0][0]).ToString(),
            _ => string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(entityType == 1 ? words[^1][0] : words[1][0])),
        };
    }

    /// <summary>The avatar tint (docs/PRODUCT_PLAN.md, owner): blue for men, plum for women, gray otherwise and for organizations.</summary>
    public static string AvatarClass(string? gender, int entityType) =>
        entityType == 2 ? "avatar org" : gender switch { "M" => "avatar male", "F" => "avatar female", _ => "avatar" };

    /// <summary>"today", "12 days ago", "3 months ago", "2 years ago".</summary>
    public static string Ago(DateOnly? date)
    {
        if (date is not { } d)
        {
            return "";
        }

        var days = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - d.DayNumber;
        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 60 => $"{days} days ago",
            < 730 => $"{days / 30} months ago",
            _ => $"{days / 365} years ago",
        };
    }
}
