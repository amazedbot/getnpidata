using MySqlConnector;
using Npi.Core.Sql;
using Npi.Loader.Csv;
using Npi.Loader.Load;

namespace Npi.Loader.Datasets;

/// <summary>How a CSV value is converted while loading.</summary>
public enum CsvValue
{
    /// <summary>Trimmed text; empty → NULL.</summary>
    Text,

    /// <summary>MM/DD/YYYY → DATE.</summary>
    DateMdy,

    /// <summary>YYYYMMDD → DATE; empty or 00000000 → NULL.</summary>
    DateYmd,

    /// <summary>Y/YES → 1, N/NO → 0, anything else → NULL.</summary>
    YesNo,

    /// <summary>A 10-digit NPI; anything else (blank, 0000000000) → NULL.</summary>
    Npi,

    /// <summary>A number; empty → NULL. A non-numeric value fails the load.</summary>
    Number,

    /// <summary>A whole number when the value is all digits; anything else ("Not Available", blank) → NULL.</summary>
    OptionalWholeNumber,

    /// <summary>A decimal number ("3.5", "-2"); anything else ("-", "Not Available", blank) → NULL.</summary>
    OptionalNumber,

    /// <summary>
    /// A date written as YYYY-MM-DD (optionally with a time, as Socrata exports it) or MM/DD/YYYY (optionally with a
    /// time); anything else → NULL.
    /// </summary>
    FlexibleDate,
}

/// <summary>One CSV column to load: its header (matched case-insensitively) and the table column it goes to.</summary>
public sealed record CsvColumn(string Header, string Column, CsvValue Kind = CsvValue.Text);

/// <summary>
/// Streams a downloaded CSV into a table with LOAD DATA LOCAL (CLAUDE.md §7 Stage 5.5). Only the listed
/// columns are kept; other columns are skipped. A listed header that is missing fails the load, because
/// the source changed its format. Like the NPPES loader, the CSV is never rewritten and any MySQL
/// warning (short row, bad date, truncation) fails the load.
/// </summary>
public static class CsvTableLoader
{
    /// <param name="characterSet">The file's encoding as a MySQL character set: utf8mb4, or latin1 for Windows-1252 files.</param>
    /// <param name="constants">Columns set to a fixed value on every row (e.g. which file the row came from).</param>
    /// <returns>The number of rows loaded.</returns>
    public static async Task<long> LoadAsync(MySqlConnection connection, string path, string table, IReadOnlyList<CsvColumn> columns, CancellationToken ct,
        string characterSet = "utf8mb4", IReadOnlyDictionary<string, string>? constants = null)
    {
        CsvHeader header;
        await using (var headerStream = File.OpenRead(path))
        {
            header = CsvHeader.Read(headerStream);
        }

        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Columns.Count; i++)
        {
            var name = header.Columns[i].Trim().TrimStart('﻿').Trim();
            positions.TryAdd(name, i);
        }

        var missing = columns.Where(c => !positions.ContainsKey(c.Header)).Select(c => c.Header).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} has no column(s) {string.Join(", ", missing.Select(m => $"'{m}'"))}; the source changed its format.");
        }

        var wanted = new Dictionary<int, CsvColumn>();
        foreach (var column in columns)
        {
            if (!SqlIdentifier.IsValid(column.Column) || !wanted.TryAdd(positions[column.Header], column))
            {
                throw new ArgumentException($"Invalid or duplicate column mapping {column.Header} → {column.Column}.", nameof(columns));
            }
        }

        await using var data = File.OpenRead(path);
        var loader = new MySqlBulkLoader(connection)
        {
            TableName = SqlIdentifier.Quote(table),
            Local = true,
            SourceStream = data,
            CharacterSet = characterSet,
            FieldTerminator = ",",
            FieldQuotationCharacter = '"',
            FieldQuotationOptional = true,
            EscapeCharacter = StagingLoader.NoEscape,
            LineTerminator = header.LineTerminator,
            NumberOfLinesToSkip = 1,
        };

        for (var i = 0; i < header.Columns.Count; i++)
        {
            var variable = $"@c{i}";
            loader.Columns.Add(variable);
            if (wanted.TryGetValue(i, out var column))
            {
                loader.Expressions.Add($"{SqlIdentifier.Quote(column.Column)} = {Expression(variable, column.Kind)}");
            }
        }

        foreach (var (column, value) in constants ?? new Dictionary<string, string>())
        {
            if (!SqlIdentifier.IsValid(column))
            {
                throw new ArgumentException($"Invalid column name '{column}'.", nameof(constants));
            }

            loader.Expressions.Add($"{SqlIdentifier.Quote(column)} = '{MySqlHelper.EscapeString(value)}'");
        }

        return await StagingLoader.WithWarningsAsFailures(connection, $"{Path.GetFileName(path)} → {table}", () => loader.LoadAsync(ct));
    }

    internal static string Expression(string variable, CsvValue kind) => kind switch
    {
        CsvValue.Text => $"NULLIF(TRIM({variable}), '')",
        CsvValue.DateMdy => $"STR_TO_DATE(NULLIF(TRIM({variable}), ''), '%m/%d/%Y')",
        CsvValue.DateYmd => $"STR_TO_DATE(NULLIF(NULLIF(TRIM({variable}), ''), '00000000'), '%Y%m%d')",
        CsvValue.YesNo => $"CASE UPPER(TRIM({variable})) WHEN 'Y' THEN 1 WHEN 'YES' THEN 1 WHEN 'N' THEN 0 WHEN 'NO' THEN 0 END",
        CsvValue.Npi => $"IF(TRIM({variable}) REGEXP '^[0-9]{{10}}$' AND TRIM({variable}) <> '0000000000', TRIM({variable}), NULL)",
        CsvValue.Number => $"NULLIF(TRIM({variable}), '')",
        CsvValue.OptionalWholeNumber => $"IF(TRIM({variable}) REGEXP '^[0-9]+$', TRIM({variable}), NULL)",
        CsvValue.FlexibleDate =>
            $"CASE WHEN TRIM({variable}) REGEXP '^[0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}}' THEN STR_TO_DATE(LEFT(TRIM({variable}), 10), '%Y-%m-%d') " +
            $"WHEN TRIM({variable}) REGEXP '^[0-9]{{1,2}}/[0-9]{{1,2}}/[0-9]{{4}}' THEN STR_TO_DATE(SUBSTRING_INDEX(TRIM({variable}), ' ', 1), '%m/%d/%Y') END",
        CsvValue.OptionalNumber => $"IF(TRIM({variable}) REGEXP '^-?[0-9]+([.][0-9]+)?$', TRIM({variable}), NULL)",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
