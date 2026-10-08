using System.Text.RegularExpressions;

namespace Npi.Core.Sql;

/// <summary>
/// Quoting for table and column names. Identifiers can't be SQL parameters, so every identifier
/// that reaches SQL text must come from a whitelist or a validated mapping and pass through here
/// (CLAUDE.md §9).
/// </summary>
public static partial class SqlIdentifier
{
    /// <summary>MySQL's maximum identifier length.</summary>
    public const int MaxLength = 64;

    /// <summary>True when <paramref name="name"/> is a plain identifier: ASCII letters, digits and underscores, 1–64 characters.</summary>
    public static bool IsValid(string? name) => name is not null && name.Length <= MaxLength && PlainIdentifier().IsMatch(name);

    /// <summary>Returns the identifier in backticks, or throws if it is not a plain identifier.</summary>
    public static string Quote(string name) =>
        IsValid(name) ? $"`{name}`" : throw new ArgumentException($"Not a valid SQL identifier: '{name}'.", nameof(name));

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex PlainIdentifier();
}
