using System.Diagnostics;
using System.Text.RegularExpressions;
using MySqlConnector;
using Npi.Core.Sql;
using Serilog;

namespace Npi.Loader.Db;

/// <summary>
/// Bulk loads into empty staging tables without their secondary indexes, which are added back in one statement afterwards
/// (docs/PRODUCT_PLAN.md §7 speed item 12): InnoDB builds an index from sorted rows much faster than it maintains it row by
/// row. Kept while the table fills: the primary key, unique indexes (a fill may rely on them to skip duplicates), indexes
/// whose first column is <c>npi</c> (later fill steps join the staging tables by NPI) and full-text indexes (adding one to a
/// filled table rebuilds the whole table: provider_org_name's took 200 s afterwards, the fill with it 83 s; measured 2026-10-14).
/// </summary>
public static partial class DeferredIndexes
{
    /// <summary>The indexes dropped from one table: each an <c>ADD …</c> clause, as SHOW CREATE TABLE writes the index.</summary>
    public sealed record Dropped(string Table, IReadOnlyList<string> Indexes);

    /// <summary>Drops the deferrable indexes of each (empty) table.</summary>
    public static async Task<IReadOnlyList<Dropped>> DropAsync(MySqlConnection connection, IEnumerable<string> tables, CancellationToken ct)
    {
        var dropped = new List<Dropped>();
        foreach (var table in tables)
        {
            string create;
            await using (var command = new MySqlCommand($"SHOW CREATE TABLE {SqlIdentifier.Quote(table)}", connection))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                await reader.ReadAsync(ct);
                create = reader.GetString(1);
            }

            var indexes = IndexLine().Matches(create).Where(m => m.Groups["first"].Value != "npi" && m.Groups["kind"].Value != "FULLTEXT ").ToList();
            if (indexes.Count == 0)
            {
                continue;
            }

            await Database.ExecuteAsync(connection,
                $"ALTER TABLE {SqlIdentifier.Quote(table)} " + string.Join(", ", indexes.Select(m => $"DROP INDEX {SqlIdentifier.Quote(m.Groups["name"].Value)}")), ct);
            dropped.Add(new Dropped(table, indexes.Select(m => m.Groups["definition"].Value.TrimEnd(',')).ToList()));
        }

        return dropped;
    }

    /// <summary>Adds the dropped indexes back: the ordinary ones in one statement per table, each spatial index on its own.</summary>
    public static async Task RestoreAsync(MySqlConnection connection, IReadOnlyList<Dropped> dropped, ILogger log, CancellationToken ct)
    {
        foreach (var (table, indexes) in dropped)
        {
            var watch = Stopwatch.StartNew();
            var single = indexes.Where(i => i.StartsWith("SPATIAL", StringComparison.Ordinal)).ToList();
            var together = indexes.Except(single).ToList();
            if (together.Count > 0)
            {
                await Database.ExecuteAsync(connection, $"ALTER TABLE {SqlIdentifier.Quote(table)} " + string.Join(", ", together.Select(i => "ADD " + i)), ct);
            }

            foreach (var index in single)
            {
                await Database.ExecuteAsync(connection, $"ALTER TABLE {SqlIdentifier.Quote(table)} ADD {index}", ct);
            }

            log.Information("Indexes of {Table} built after loading: {Count} in {Seconds:N0}s", table, indexes.Count, watch.Elapsed.TotalSeconds);
        }
    }

    // A non-unique secondary index as SHOW CREATE TABLE writes it: "  KEY `ix` (`col`(20),`col2`) …", FULLTEXT and SPATIAL
    // keys too. The kind and the first column are captured to keep full-text indexes and indexes that start with npi.
    [GeneratedRegex(@"^\s*(?<definition>(?<kind>FULLTEXT |SPATIAL )?KEY `(?<name>[^`]+)` \(`(?<first>[^`]+)`.*?),?\r?$", RegexOptions.Multiline)]
    private static partial Regex IndexLine();
}
