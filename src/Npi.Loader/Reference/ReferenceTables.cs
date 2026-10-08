using System.Data;
using MySqlConnector;
using Npi.Core.Sql;
using Npi.Loader.Db;

namespace Npi.Loader.Reference;

/// <summary>Bulk-writes reference rows into a staging copy of a table and swaps it in.</summary>
public static class ReferenceTables
{
    /// <summary>
    /// Replaces <paramref name="table"/> with <paramref name="rows"/>: staging copy, bulk copy, row-count
    /// check (at least <paramref name="minRowRatio"/> of the current rows), atomic RENAME, drop old.
    /// </summary>
    public static async Task<int> ReplaceAsync(MySqlConnection connection, string table, DataTable rows, double minRowRatio, CancellationToken ct)
    {
        var staging = table + "_staging";
        var old = table + "_old";
        try
        {
            await CopyToStagingAsync(connection, table, rows, ct);
            var current = await Database.CountAsync(connection, table, ct);
            if (current > 0 && rows.Rows.Count < current * minRowRatio)
            {
                throw new InvalidDataException(
                    $"New {table} has {rows.Rows.Count:N0} rows, fewer than {minRowRatio:P0} of the current {current:N0}; not replacing it.");
            }

            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {SqlIdentifier.Quote(old)}", ct);
            await Database.ExecuteAsync(connection,
                $"RENAME TABLE {SqlIdentifier.Quote(table)} TO {SqlIdentifier.Quote(old)}, {SqlIdentifier.Quote(staging)} TO {SqlIdentifier.Quote(table)}", ct);
            await Database.ExecuteAsync(connection, $"DROP TABLE {SqlIdentifier.Quote(old)}", ct);
            return rows.Rows.Count;
        }
        finally
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {SqlIdentifier.Quote(staging)}", CancellationToken.None);
        }
    }

    /// <summary>Creates <c>{table}_staging</c> LIKE <paramref name="table"/> and bulk-copies the rows into it. Any MySQL warning fails.</summary>
    public static async Task CopyToStagingAsync(MySqlConnection connection, string table, DataTable rows, CancellationToken ct)
    {
        var staging = table + "_staging";
        await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {SqlIdentifier.Quote(staging)}", ct);
        await Database.ExecuteAsync(connection, $"CREATE TABLE {SqlIdentifier.Quote(staging)} LIKE {SqlIdentifier.Quote(table)}", ct);

        var bulk = new MySqlBulkCopy(connection) { DestinationTableName = SqlIdentifier.Quote(staging), BulkCopyTimeout = 0 };
        for (var i = 0; i < rows.Columns.Count; i++)
        {
            // MySqlBulkCopy quotes destination columns itself, so pass the bare (validated) name.
            var column = rows.Columns[i].ColumnName;
            if (!SqlIdentifier.IsValid(column))
            {
                throw new ArgumentException($"Not a valid column name: '{column}'.", nameof(rows));
            }

            bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, column));
        }

        var result = await bulk.WriteToServerAsync(rows, ct);
        if (result.Warnings.Count > 0)
        {
            var sample = string.Join("; ", result.Warnings.Take(5).Select(w => $"{w.Level} {w.ErrorCode}: {w.Message}"));
            throw new InvalidDataException($"Loading {table} produced {result.Warnings.Count} warning(s), e.g. {sample}");
        }

        if (result.RowsInserted != rows.Rows.Count)
        {
            throw new InvalidDataException($"Loading {table}: {result.RowsInserted:N0} of {rows.Rows.Count:N0} rows were inserted.");
        }
    }

    public static DataTable ToDataTable<T>(IEnumerable<T> items, params (string Column, Type Type, Func<T, object?> Value)[] columns)
    {
        var table = new DataTable();
        foreach (var (column, type, _) in columns)
        {
            table.Columns.Add(column, type);
        }

        foreach (var item in items)
        {
            table.Rows.Add(columns.Select(c => c.Value(item) ?? DBNull.Value).ToArray());
        }

        return table;
    }
}
