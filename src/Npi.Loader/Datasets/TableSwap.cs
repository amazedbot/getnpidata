using MySqlConnector;
using Npi.Core.Sql;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// Replaces whole tables the safe way (CLAUDE.md §6.3): fill <c>{table}_staging</c> copies, check each has at
/// least <c>minRowRatio</c> of the current rows, then swap them all in with one atomic RENAME.
/// </summary>
public static class TableSwap
{
    /// <param name="fill">Fills the staging tables (named <c>{table}_staging</c>, already created LIKE the targets).</param>
    /// <returns>The row count of each new table.</returns>
    public static async Task<IReadOnlyDictionary<string, long>> ReplaceAsync(
        MySqlConnection connection, IReadOnlyList<string> tables, double minRowRatio, Func<Task> fill, CancellationToken ct)
    {
        try
        {
            foreach (var table in tables)
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {Staging(table)}", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE {Staging(table)} LIKE {SqlIdentifier.Quote(table)}", ct);
            }

            await fill();

            var counts = new Dictionary<string, long>();
            foreach (var table in tables)
            {
                var incoming = await Database.CountAsync(connection, table + "_staging", ct);
                var current = await Database.CountAsync(connection, table, ct);
                if (current > 0 && incoming < current * minRowRatio)
                {
                    throw new InvalidDataException(
                        $"New {table} has {incoming:N0} rows, fewer than {minRowRatio:P0} of the current {current:N0}; not replacing it.");
                }

                counts[table] = incoming;
            }

            foreach (var table in tables)
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {SqlIdentifier.Quote(table + "_old")}", ct);
            }

            var renames = tables.SelectMany(t => new[]
            {
                $"{SqlIdentifier.Quote(t)} TO {SqlIdentifier.Quote(t + "_old")}",
                $"{Staging(t)} TO {SqlIdentifier.Quote(t)}",
            });
            await Database.ExecuteAsync(connection, $"RENAME TABLE {string.Join(", ", renames)}", ct);
            foreach (var table in tables)
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE {SqlIdentifier.Quote(table + "_old")}", ct);
            }

            return counts;
        }
        finally
        {
            foreach (var table in tables)
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {Staging(table)}", CancellationToken.None);
            }
        }
    }

    private static string Staging(string table) => SqlIdentifier.Quote(table + "_staging");
}
