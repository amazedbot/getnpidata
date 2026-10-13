using System.IO.Compression;
using MySqlConnector;
using Npi.Core.Sql;
using Npi.Loader.Csv;
using Npi.Loader.Db;

namespace Npi.Loader.Load;

/// <summary>
/// Streams one CSV zip entry into a table with LOAD DATA LOCAL (CLAUDE.md §7 Stage 1.4). The CSV is
/// never rewritten: MySQL parses the quoting (legacy defects #1 and #2), the column list comes from
/// the header row, and empty strings become NULL (and dates become DATE) in the LOAD DATA SET clause.
/// </summary>
public static class StagingLoader
{
    // MySqlBulkLoader cannot emit ESCAPED BY '' (it omits the clause, and MySQL then defaults to
    // backslash, which would turn "C:\new" into a newline). NPPES text never contains U+0001, so using
    // it as the escape character disables escaping in practice. Covered by the backslash fixture test.
    internal const char NoEscape = '\u0001';

    /// <summary>The column holding <see cref="LoadCsvAsync"/>'s row hash (npidata, migration 090).</summary>
    public const string SourceHash = "Source_Hash";

    /// <param name="hashed">When set and the table has <see cref="SourceHash"/>: the columns whose values, as in the file, are
    /// hashed into it while the rows load (empty = NULL; columns in name order, so the file's column order doesn't matter).</param>
    /// <returns>The number of rows loaded.</returns>
    public static async Task<long> LoadCsvAsync(
        MySqlConnection connection, ZipArchiveEntry entry, string table, string? loadedFrom, CancellationToken ct,
        Func<string, bool>? hashed = null)
    {
        CsvHeader header;
        await using (var headerStream = entry.Open())
        {
            header = CsvHeader.Read(headerStream);
        }

        var tableColumns = await Database.GetColumnsAsync(connection, table, ct);
        var types = tableColumns.ToDictionary(c => c.Name, c => c.DataType, StringComparer.OrdinalIgnoreCase);
        var columns = HeaderMapper.MapToTable(header.Columns, tableColumns.Select(c => c.Name).ToList(), table);

        await using var data = entry.Open();
        var loader = new MySqlBulkLoader(connection)
        {
            TableName = SqlIdentifier.Quote(table),
            Local = true,
            SourceStream = data,
            CharacterSet = "utf8mb4",
            FieldTerminator = ",",
            FieldQuotationCharacter = '"',
            EscapeCharacter = NoEscape,
            LineTerminator = header.LineTerminator,
            NumberOfLinesToSkip = 1,
        };

        for (var i = 0; i < columns.Count; i++)
        {
            var variable = $"@c{i}";
            loader.Columns.Add(variable);
            loader.Expressions.Add($"{SqlIdentifier.Quote(columns[i])} = {ConvertExpression(variable, types[columns[i]])}");
        }

        if (hashed is not null && types.ContainsKey(SourceHash))
        {
            var values = columns.Select((c, i) => (Column: c, Variable: $"@c{i}"))
                .Where(c => hashed(c.Column))
                .OrderBy(c => c.Column, StringComparer.OrdinalIgnoreCase)
                .Select(c => $"IFNULL(NULLIF({c.Variable}, ''), CHAR(0))");
            loader.Expressions.Add($"{SqlIdentifier.Quote(SourceHash)} = UNHEX(MD5(CONCAT_WS(CHAR(31), {string.Join(", ", values)})))");
        }

        if (loadedFrom is not null && types.ContainsKey("Loaded_From"))
        {
            loader.Expressions.Add($"`Loaded_From` = '{MySqlHelper.EscapeString(loadedFrom)}'");
        }

        return await WithWarningsAsFailures(connection, $"{entry.Name} → {table}", () => loader.LoadAsync(ct));
    }

    internal static string ConvertExpression(string variable, string dataType) => dataType.ToLowerInvariant() switch
    {
        "date" => $"STR_TO_DATE(NULLIF({variable}, ''), '%m/%d/%Y')",
        _ => $"NULLIF({variable}, '')",
    };

    /// <summary>
    /// LOAD DATA LOCAL turns errors into warnings: rows with too few or too many fields, truncated
    /// values and duplicate keys are silently changed or dropped. Any warning fails the load instead.
    /// </summary>
    public static async Task<long> WithWarningsAsFailures(MySqlConnection connection, string what, Func<Task<int>> load)
    {
        var warnings = new List<MySqlError>();
        void OnInfo(object? sender, MySqlInfoMessageEventArgs e) => warnings.AddRange(e.Errors);
        connection.InfoMessage += OnInfo;
        try
        {
            var rows = await load();
            if (warnings.Count > 0)
            {
                var sample = string.Join("; ", warnings.Take(5).Select(w => $"{w.Level} {w.ErrorCode}: {w.Message}"));
                throw new InvalidDataException($"Loading {what} produced {warnings.Count} warning(s), e.g. {sample}");
            }

            return rows;
        }
        finally
        {
            connection.InfoMessage -= OnInfo;
        }
    }
}
