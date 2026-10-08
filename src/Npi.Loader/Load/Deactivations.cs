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

    /// <param name="scopeTable">Only update NPIs present in this table (a weekly staging table), or all when null.</param>
    /// <returns>Rows whose flag or deactivation date changed.</returns>
    public static Task<int> ApplyAsync(MySqlConnection connection, string? scopeTable, CancellationToken ct, MySqlTransaction? tx = null)
    {
        var scope = scopeTable is null ? "" : $"JOIN {SqlIdentifier.Quote(scopeTable)} k ON k.`NPI` = n.`NPI`";
        var deactivated = $"COALESCE(DATE_FORMAT(r.`NPPES_Deactivation_Date`, '%Y%m%d'), NULLIF({Ymd("n.`NPI_Deactivation_Date`")}, ''))";
        var reactivated = $"NULLIF({Ymd("n.`NPI_Reactivation_Date`")}, '')";
        var sql = $"""
            UPDATE `npidata` n
            {scope}
            LEFT JOIN `nppes_deactivated_npi_report` r ON r.`NPI` = n.`NPI`
            SET n.`Is_Deactivated` = COALESCE({deactivated} IS NOT NULL AND ({reactivated} IS NULL OR {reactivated} < {deactivated}), 0),
                n.`NPI_Deactivation_Date` = IF(COALESCE(n.`NPI_Deactivation_Date`, '') = '' AND r.`NPI` IS NOT NULL,
                                               DATE_FORMAT(r.`NPPES_Deactivation_Date`, '%m/%d/%Y'), n.`NPI_Deactivation_Date`)
            """;
        return connection.ExecuteAsync(new CommandDefinition(sql, transaction: tx, cancellationToken: ct));
    }
}
