using Dapper;
using MySqlConnector;
using Npi.Core.Sql;

namespace Npi.Loader.Db;

/// <summary>
/// Finds the NPIs whose rows differ between two versions of a table (a new staging copy and the live table): one MD5 per NPI
/// over its rows' MD5s in sorted order, so row order and AUTO_INCREMENT ids don't matter; an NPI in only one of them differs too.
/// </summary>
public static class NpiRowDiff
{
    /// <summary>The columns compared: all but generated and AUTO_INCREMENT ones.</summary>
    public static async Task<IReadOnlyList<string>> ColumnsAsync(MySqlConnection connection, string table, CancellationToken ct) =>
        (await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = DATABASE() AND table_name = @table AND extra NOT LIKE '%auto_increment%' AND extra NOT LIKE '%GENERATED%'
            ORDER BY ordinal_position
            """, new { table }, cancellationToken: ct))).ToList();

    /// <summary>Inserts into <paramref name="queue"/> (a table with an <c>npi</c> key) every NPI whose rows differ.</summary>
    /// <param name="columns">The columns compared (<see cref="ColumnsAsync"/> when null).</param>
    /// <returns>The NPIs added to the queue.</returns>
    public static async Task<long> QueueAsync(MySqlConnection connection, string newTable, string oldTable, string npiColumn, string queue,
        CancellationToken ct, IReadOnlyList<string>? columns = null)
    {
        columns ??= await ColumnsAsync(connection, oldTable, ct);
        Task<int> RunAsync(string sql) => connection.ExecuteAsync(new CommandDefinition(sql, commandTimeout: 0, cancellationToken: ct));
        var npi = SqlIdentifier.Quote(npiColumn);
        var hash = $"MD5(CONCAT_WS(CHAR(31), {string.Join(", ", columns.Select(c => $"IFNULL(c.{SqlIdentifier.Quote(c)}, CHAR(0))"))}))";
        string PerNpi(string source) =>
            $"SELECT c.{npi} AS `npi`, MD5(GROUP_CONCAT({hash} ORDER BY {hash} SEPARATOR ',')) AS `h` FROM {SqlIdentifier.Quote(source)} c " +
            $"WHERE c.{npi} IS NOT NULL GROUP BY c.{npi}";

        await RunAsync("SET SESSION group_concat_max_len = 67108864");
        var before = await Database.CountAsync(connection, queue, ct);
        try
        {
            await RunAsync("DROP TEMPORARY TABLE IF EXISTS `npi_diff_new`, `npi_diff_old`");
            await RunAsync($"CREATE TEMPORARY TABLE `npi_diff_new` (PRIMARY KEY (`npi`)) {PerNpi(newTable)}");
            await RunAsync($"CREATE TEMPORARY TABLE `npi_diff_old` (PRIMARY KEY (`npi`)) {PerNpi(oldTable)}");
            // Two statements: MySQL can't open a temporary table twice in one. In NPI order, and repeated after a deadlock: other
            // sources may be queueing NPIs at the same time (speed item 7).
            await Database.RetryOnLockConflictAsync(() => RunAsync(
                $"""
                INSERT IGNORE INTO {SqlIdentifier.Quote(queue)} (`npi`)
                SELECT a.`npi` FROM `npi_diff_new` a LEFT JOIN `npi_diff_old` b ON b.`npi` = a.`npi` WHERE NOT (a.`h` <=> b.`h`) ORDER BY a.`npi`
                """), ct);
            await Database.RetryOnLockConflictAsync(() => RunAsync(
                $"""
                INSERT IGNORE INTO {SqlIdentifier.Quote(queue)} (`npi`)
                SELECT b.`npi` FROM `npi_diff_old` b LEFT JOIN `npi_diff_new` a ON a.`npi` = b.`npi` WHERE a.`npi` IS NULL ORDER BY b.`npi`
                """), ct);
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition("DROP TEMPORARY TABLE IF EXISTS `npi_diff_new`, `npi_diff_old`",
                cancellationToken: CancellationToken.None));
        }

        return await Database.CountAsync(connection, queue, ct) - before;
    }
}
