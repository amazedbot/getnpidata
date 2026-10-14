using Dapper;
using MySqlConnector;
using Npi.Core.Sql;
using Serilog;

namespace Npi.Loader.Datasets;

/// <summary>
/// The change log's entries from the datasets (docs/PRODUCT_PLAN.md Phase 3 step 3.2): when a reload replaces the OIG
/// exclusions, the Medicare opt-outs or a state's licenses, the new rows are compared with the current ones per NPI just
/// before the swap, and what started, ended or changed is appended to <c>provider_change</c>: exclusions and their end
/// (reinstatement), opt-outs, their renewal and end, licenses added, dropped, changing status or renewed, and new board
/// actions. A first load (nothing to compare with) records nothing. A failure is logged, not thrown: a missed entry is not
/// worth failing the load.
/// </summary>
public static class DatasetChanges
{
    /// <summary>The tables whose reloads are compared; others are not.</summary>
    public static readonly IReadOnlySet<string> RecordedTables = new HashSet<string>(StringComparer.Ordinal) { "oig_exclusion", "medicare_opt_out" };

    private const string Insert = "INSERT INTO `provider_change` (`npi`, `detected_at`, `change_type`, `old_value`, `new_value`)";

    /// <summary>Before <c>table</c> is replaced by <c>table_staging</c> (TableSwap): records what the reload changes.</summary>
    public static async Task RecordTableAsync(MySqlConnection connection, string table, DateTime now, ILogger log, CancellationToken ct)
    {
        var statements = table switch
        {
            "oig_exclusion" => Exclusions("`oig_exclusion_staging`", "`oig_exclusion`"),
            "medicare_opt_out" => OptOuts("`medicare_opt_out_staging`", "`medicare_opt_out`"),
            _ => null,
        };
        if (statements is not null)
        {
            await RunAsync(connection, table, statements, new { now }, log, ct);
        }
    }

    /// <summary>Before a state's licenses are replaced by the rows of <paramref name="newTable"/> (StateLicenseSource).</summary>
    public static Task RecordStateLicensesAsync(MySqlConnection connection, string state, string newTable, DateTime now, ILogger log, CancellationToken ct) =>
        RunAsync(connection, $"{state} licenses", Licenses(SqlIdentifier.Quote(newTable)), new { now, state }, log, ct);

    private static async Task RunAsync(MySqlConnection connection, string what, string[] statements, object param, ILogger log, CancellationToken ct)
    {
        try
        {
            var rows = 0;
            foreach (var sql in statements)
            {
                rows += await connection.ExecuteAsync(new CommandDefinition(sql, param, commandTimeout: 0, cancellationToken: ct));
            }

            log.Information("Changes recorded from {What}: {Rows:N0}", what, rows);
        }
        catch (MySqlException e)
        {
            log.Error(e, "Changes from {What} could not be recorded; the load goes on", what);
        }
    }

    // One row per NPI: the exclusion types and the earliest date (an NPI can be listed more than once).
    private static string ExclusionText(string table) =>
        $"SELECT `npi`, LEFT(CONCAT(GROUP_CONCAT(DISTINCT `exclusion_type` ORDER BY `exclusion_type` SEPARATOR ', '), ' since ', MIN(`exclusion_date`)), 300) AS `text` " +
        $"FROM {table} WHERE `npi` IS NOT NULL GROUP BY `npi`";

    internal static string[] Exclusions(string next, string current) =>
    [
        $"""
        {Insert}
        SELECT n.`npi`, @now, 'excluded', NULL, n.`text`
        FROM ({ExclusionText(next)}) n LEFT JOIN ({ExclusionText(current)}) c ON c.`npi` = n.`npi` WHERE c.`npi` IS NULL
        """,
        // Gone from the list: OIG removes reinstated providers.
        $"""
        {Insert}
        SELECT c.`npi`, @now, 'exclusion_ended', c.`text`, NULL
        FROM ({ExclusionText(current)}) c LEFT JOIN ({ExclusionText(next)}) n ON n.`npi` = c.`npi` WHERE n.`npi` IS NULL
        """,
    ];

    private static string OptOutEnd(string table) => $"SELECT `npi`, MAX(`end_date`) AS `end_date` FROM {table} GROUP BY `npi`";

    internal static string[] OptOuts(string next, string current) =>
    [
        $"""
        {Insert}
        SELECT n.`npi`, @now, 'opted_out', NULL, CONCAT('until ', n.`end_date`)
        FROM ({OptOutEnd(next)}) n LEFT JOIN ({OptOutEnd(current)}) c ON c.`npi` = n.`npi` WHERE c.`npi` IS NULL
        """,
        $"""
        {Insert}
        SELECT c.`npi`, @now, 'opt_out_ended', CONCAT('until ', c.`end_date`), NULL
        FROM ({OptOutEnd(current)}) c LEFT JOIN ({OptOutEnd(next)}) n ON n.`npi` = c.`npi` WHERE n.`npi` IS NULL
        """,
        // An affidavit lasts two years and is renewed with a later end date.
        $"""
        {Insert}
        SELECT n.`npi`, @now, 'opt_out_renewed', CONCAT('until ', c.`end_date`), CONCAT('until ', n.`end_date`)
        FROM ({OptOutEnd(next)}) n JOIN ({OptOutEnd(current)}) c ON c.`npi` = n.`npi` WHERE n.`end_date` > c.`end_date`
        """,
    ];

    // One row per license of an NPI in the state (a license can have several rows, one per discipline case in CO).
    private static string LicenseRows(string table) =>
        $"SELECT `npi`, `source`, IFNULL(`license_number`, '') AS `number`, IFNULL(`license_type`, '') AS `type`, " +
        $"MIN(`status`) AS `status`, MAX(`expiration_date`) AS `expires` " +
        $"FROM {table} WHERE `state` = @state AND `kind` = 'license' GROUP BY `npi`, `source`, `number`, `type`";

    private static string LicenseText(string alias) =>
        $"LEFT(CONCAT_WS(' ', @state, NULLIF({alias}.`number`, ''), IF({alias}.`type` = '', NULL, CONCAT('(', {alias}.`type`, ')'))), 200)";

    // Board actions, on license rows (TX) and action rows (NY, DE …): one per NPI, date and wording.
    private static string ActionRows(string table) =>
        $"SELECT DISTINCT `npi`, `action_date`, LEFT(`action`, 250) AS `action` FROM {table} " +
        $"WHERE `state` = @state AND `action_date` IS NOT NULL AND NULLIF(TRIM(`action`), '') IS NOT NULL";

    internal static string[] Licenses(string next)
    {
        const string current = "`provider_state_license`";
        const string sameLicense = "c.`npi` = n.`npi` AND c.`source` = n.`source` AND c.`number` = n.`number` AND c.`type` = n.`type`";
        return
        [
            $"""
            {Insert}
            SELECT n.`npi`, @now, 'license_added', NULL, LEFT(CONCAT_WS(': ', {LicenseText("n")}, n.`status`), 300)
            FROM ({LicenseRows(next)}) n LEFT JOIN ({LicenseRows(current)}) c ON {sameLicense} WHERE c.`npi` IS NULL
            """,
            $"""
            {Insert}
            SELECT c.`npi`, @now, 'license_removed', LEFT(CONCAT_WS(': ', {LicenseText("c")}, c.`status`), 300), NULL
            FROM ({LicenseRows(current)}) c LEFT JOIN ({LicenseRows(next)}) n ON {sameLicense} WHERE n.`npi` IS NULL
            """,
            $"""
            {Insert}
            SELECT n.`npi`, @now, 'license_status', LEFT(CONCAT_WS(': ', {LicenseText("c")}, c.`status`), 300), LEFT(CONCAT_WS(': ', {LicenseText("n")}, n.`status`), 300)
            FROM ({LicenseRows(next)}) n JOIN ({LicenseRows(current)}) c ON {sameLicense} WHERE NOT (n.`status` <=> c.`status`)
            """,
            $"""
            {Insert}
            SELECT n.`npi`, @now, 'license_renewed', LEFT(CONCAT({LicenseText("c")}, ': expires ', c.`expires`), 300), LEFT(CONCAT({LicenseText("n")}, ': expires ', n.`expires`), 300)
            FROM ({LicenseRows(next)}) n JOIN ({LicenseRows(current)}) c ON {sameLicense}
            WHERE n.`expires` > c.`expires` AND n.`status` <=> c.`status`
            """,
            $"""
            {Insert}
            SELECT n.`npi`, @now, 'board_action', NULL, LEFT(CONCAT(@state, ' ', n.`action_date`, ': ', n.`action`), 300)
            FROM ({ActionRows(next)}) n
            LEFT JOIN ({ActionRows(current)}) c ON c.`npi` = n.`npi` AND c.`action_date` = n.`action_date` AND c.`action` = n.`action`
            WHERE c.`npi` IS NULL
            """,
        ];
    }
}
