using System.Text.RegularExpressions;
using Npi.Core.Sql;

namespace Npi.Loader.Csv;

/// <summary>
/// Maps NPPES CSV header names to database column names (CLAUDE.md §4): every run of
/// non-alphanumeric characters becomes <c>_</c>, leading/trailing <c>_</c> are trimmed, and then a
/// few aliases keep the existing column names where V2 headers differ from them.
/// </summary>
public static partial class HeaderMapper
{
    // "... Country Code (If outside U.S.)" → "..._Country_Code". Also needed because the full name
    // can exceed MySQL's 64-character identifier limit.
    private const string IfOutsideUsSuffix = "_If_outside_U_S";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // V2 renamed "Provider Gender Code" to "Provider Sex Code".
        ["Provider_Sex_Code"] = "Provider_Gender_Code",

        // pl_pfile: "Provider Secondary Practice Location Address- Address Line 1".
        ["Provider_Secondary_Practice_Location_Address_Address_Line_1"] = "Provider_Secondary_Practice_Location_Address_Line_1",
        ["Provider_Secondary_Practice_Location_Address_Address_Line_2"] = "Provider_Secondary_Practice_Location_Address_Line_2",
    };

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonAlphanumeric();

    public static string ToColumnName(string header)
    {
        var name = NonAlphanumeric().Replace(header, "_").Trim('_');
        if (name.EndsWith(IfOutsideUsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^IfOutsideUsSuffix.Length];
        }

        return Aliases.TryGetValue(name, out var alias) ? alias : name;
    }

    /// <summary>
    /// Maps every header to a column of the target table. Throws when a header maps to no column, to
    /// a column twice, or to an invalid identifier: the load must not guess (a new CMS column needs
    /// a migration).
    /// </summary>
    /// <param name="tableColumns">The target table's columns, from information_schema.</param>
    public static IReadOnlyList<string> MapToTable(IReadOnlyList<string> headers, IReadOnlyCollection<string> tableColumns, string tableName)
    {
        var known = new HashSet<string>(tableColumns, StringComparer.OrdinalIgnoreCase);
        var canonical = tableColumns.ToDictionary(c => c, c => c, StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(headers.Count);
        var missing = new List<string>();

        foreach (var header in headers)
        {
            var column = ToColumnName(header);
            if (!SqlIdentifier.IsValid(column) || !known.Contains(column))
            {
                missing.Add($"'{header}' → {column}");
                continue;
            }

            if (!used.Add(column))
            {
                throw new InvalidDataException($"Two headers map to the same column {tableName}.{column}.");
            }

            result.Add(canonical[column]);
        }

        if (missing.Count > 0)
        {
            throw new InvalidDataException(
                $"{missing.Count} CSV header(s) have no column in {tableName}; add a migration: {string.Join("; ", missing)}");
        }

        return result;
    }
}
