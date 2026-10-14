using MySqlConnector;
using Npi.Core.Sql;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// Replaces whole tables the safe way (CLAUDE.md §6.3): fill <c>{table}_staging</c> copies, check each has at
/// least <c>minRowRatio</c> of the current rows, then swap them all in with one atomic RENAME. For a table the record quality
/// reads per NPI (<see cref="Projection.QualityBuilder.QueuedTables"/>), the NPIs whose rows differ are queued for relabelling
/// first, so a reload relabels only them.
/// </summary>
public static class TableSwap
{
    /// <param name="fill">Fills the staging tables (named <c>{table}_staging</c>, already created LIKE the targets).</param>
    /// <param name="deferIndexes">
    /// Fill the staging tables without their secondary indexes and build those afterwards (<see cref="DeferredIndexes"/>), for
    /// large tables whose fill doesn't read them through one.
    /// </param>
    /// <param name="log">Where the index builds are logged (with <paramref name="deferIndexes"/>).</param>
    /// <returns>The row count of each new table.</returns>
    public static async Task<IReadOnlyDictionary<string, long>> ReplaceAsync(
        MySqlConnection connection, IReadOnlyList<string> tables, double minRowRatio, Func<Task> fill, CancellationToken ct,
        bool deferIndexes = false, Serilog.ILogger? log = null)
    {
        try
        {
            foreach (var table in tables)
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {Staging(table)}", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE {Staging(table)} LIKE {SqlIdentifier.Quote(table)}", ct);
            }

            var deferred = deferIndexes ? await DeferredIndexes.DropAsync(connection, tables.Select(t => t + "_staging"), ct) : [];
            await fill();
            await DeferredIndexes.RestoreAsync(connection, deferred, log ?? Serilog.Core.Logger.None, ct);

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

            // Before the swap, so a failure leaves the current data (and an at most too long queue).
            foreach (var table in tables.Where(Projection.QualityBuilder.QueuedTables.Contains))
            {
                if (await Database.CountAsync(connection, Projection.QualityBuilder.Source, ct) > 0)
                {
                    await NpiRowDiff.QueueAsync(connection, table + "_staging", table, "npi", Projection.QualityBuilder.Pending, ct);
                }
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
