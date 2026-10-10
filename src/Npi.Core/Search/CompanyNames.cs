using System.Globalization;
using System.Text;

namespace Npi.Core.Search;

/// <summary>
/// The name key that matches a company across sources (Stage 5.5 item 17): Open Payments, openFDA recalls, SEC EDGAR and
/// OIG integrity agreements write the same company differently ("ELI LILLY AND COMPANY", "LILLY ELI &amp; CO",
/// "Eli Lilly and Company, Inc."). The key is the name's words, upper-case and without accents, without legal suffixes
/// and joining words (INC, LLC, CO, THE, AND …) and single letters, distinct and sorted. Two names match only when their
/// keys are equal, so "LILLY USA, LLC" (LILLY USA) is not "ELI LILLY AND COMPANY" (ELI LILLY).
/// </summary>
public static class CompanyNames
{
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "INC", "INCORPORATED", "CORP", "CORPORATION", "CO", "COMPANY", "COMPANIES", "LLC", "LP", "LLP", "LTD", "LIMITED", "PLC", "SA", "AG",
        "NV", "BV", "GMBH", "SPA", "SRL", "SAS", "AB", "AS", "OY", "KK", "PTY", "PC", "PA", "PLLC", "DBA", "THE", "AND", "OF",
    };

    /// <summary>The key, or null when nothing distinctive is left (fewer than 3 characters).</summary>
    public static string? Key(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var folded = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            // Apostrophes join ("REDDY'S" = REDDYS); every other non-letter, non-digit separates words.
            if (c is '\'' or '’')
            {
                continue;
            }

            folded.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ');
        }

        var words = folded.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !Ignored.Contains(w)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var key = string.Join(' ', words);
        return key.Length < 3 ? null : key.Length > 255 ? key[..255] : key;
    }
}
