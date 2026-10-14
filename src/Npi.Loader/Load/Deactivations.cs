using Dapper;
using MySqlConnector;
using Npi.Core.Sql;

namespace Npi.Loader.Load;

/// <summary>
/// Maintains <c>npidata.Is_Deactivated</c>. An NPI is deactivated when it has a deactivation date
/// (from the deactivation report, else from its own NPI_Deactivation_Date) and no reactivation on or
/// after that date. Deactivated NPIs stay in npidata but are never shown (CLAUDE.md §2).
/// </summary>
public static class Deactivations
{
    /// <summary>
    /// SQL turning an MM/DD/YYYY text column into a YYYYMMDD string for comparisons. Plain string
    /// functions: STR_TO_DATE would raise errors on malformed values in strict mode.
    /// </summary>
    public static string Ymd(string column) =>
        $"CONCAT(SUBSTRING({column}, 7, 4), SUBSTRING({column}, 1, 2), SUBSTRING({column}, 4, 2))";

    /// <summary>
    /// The most NPIs in <c>deactivation_pending</c> recomputed one by one; more (a first monthly after migration 095, say)
    /// recompute every row, which reads npidata once (~1 min).
    /// </summary>
    public const int PendingLimit = 500_000;

    /// <summary>
    /// After a monthly or deactivation report swap: recomputes the NPIs queued in <c>deactivation_pending</c> (speed item
    /// 10; migration 095), or every row when <paramref name="all"/> or the queue is long, then empties the queue.
    /// </summary>
    /// <returns>Rows whose flag or deactivation date changed.</returns>
    public static async Task<int> ApplyPendingAsync(MySqlConnection connection, bool all, CancellationToken ct)
    {
        var pending = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM (SELECT 1 FROM `deactivation_pending` LIMIT {PendingLimit + 1}) p", cancellationToken: ct));
        var changed = all || pending > PendingLimit ? await ApplyAsync(connection, scopeTable: null, ct)
            : pending > 0 ? await ApplyAsync(connection, scopeTable: "deactivation_pending", ct)
            : 0;
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM `deactivation_pending`", commandTimeout: 0, cancellationToken: ct));
        return changed;
    }

    /// <summary>
    /// Recomputes the flag (and fills an empty deactivation date from the report). Only the rows whose flag or date
    /// changes are written, and their NPIs are queued in <c>projection_pending</c> for the projection.
    /// </summary>
    /// <param name="scopeTable">Only update NPIs present in this table (a weekly staging table, deactivation_pending), or all when null.</param>
    /// <returns>Rows whose flag or deactivation date changed.</returns>
    public static async Task<int> ApplyAsync(MySqlConnection connection, string? scopeTable, CancellationToken ct, MySqlTransaction? tx = null)
    {
        var scope = scopeTable is null ? "" : $"JOIN {SqlIdentifier.Quote(scopeTable)} k ON k.`NPI` = n.`NPI`";
        var deactivated = $"COALESCE(DATE_FORMAT(r.`NPPES_Deactivation_Date`, '%Y%m%d'), NULLIF({Ymd("n.`NPI_Deactivation_Date`")}, ''))";
        var reactivated = $"NULLIF({Ymd("n.`NPI_Reactivation_Date`")}, '')";
        var flag = $"COALESCE({deactivated} IS NOT NULL AND ({reactivated} IS NULL OR {reactivated} < {deactivated}), 0)";
        var date = "IF(COALESCE(n.`NPI_Deactivation_Date`, '') = '' AND r.`NPI` IS NOT NULL, DATE_FORMAT(r.`NPPES_Deactivation_Date`, '%m/%d/%Y'), n.`NPI_Deactivation_Date`)";
        Task<int> RunAsync(string sql) => connection.ExecuteAsync(new CommandDefinition(sql, transaction: tx, commandTimeout: 0, cancellationToken: ct));

        await RunAsync("DROP TEMPORARY TABLE IF EXISTS `deactivation_change`");
        try
        {
            // One read of the rows in scope: the new flag and date of those that change.
            await RunAsync(
                $"""
                CREATE TEMPORARY TABLE `deactivation_change` (PRIMARY KEY (`npi`))
                SELECT n.`NPI` AS `npi`, {flag} AS `flag`, {date} AS `date`
                FROM `npidata` n
                {scope}
                LEFT JOIN `nppes_deactivated_npi_report` r ON r.`NPI` = n.`NPI`
                WHERE NOT (n.`Is_Deactivated` <=> {flag}) OR NOT (n.`NPI_Deactivation_Date` <=> {date})
                """);
            var changed = await RunAsync(
                """
                UPDATE `npidata` n JOIN `deactivation_change` c ON c.`npi` = n.`NPI`
                SET n.`Is_Deactivated` = c.`flag`, n.`NPI_Deactivation_Date` = c.`date`
                """);
            await RunAsync("INSERT IGNORE INTO `projection_pending` (`npi`) SELECT `npi` FROM `deactivation_change`");
            return changed;
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition("DROP TEMPORARY TABLE IF EXISTS `deactivation_change`", transaction: tx,
                cancellationToken: CancellationToken.None));
        }
    }
}
