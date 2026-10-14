using System.IO.Compression;
using Dapper;
using MySqlConnector;
using Npi.Core.Sql;
using Npi.Loader.Db;
using Npi.Loader.Nppes;
using Npi.Loader.Projection;
using Serilog;

namespace Npi.Loader.Load;

/// <summary>
/// Loads an NPPES data zip (monthly full or weekly incremental) into npidata, other_names,
/// practice_locations and endpoints (CLAUDE.md §7 Stage 1.4–1.6, Stage 5.5 item 1). Every CSV goes
/// into a <c>*_staging</c> copy first.
/// </summary>
public sealed class NpiDataLoader(Database database, ILogger log, double minRowRatio)
{
    // Endpoints are optional: a zip without an endpoint file leaves the stored endpoints alone.
    private static readonly (NppesEntryKind Kind, string Table, bool Required)[] AllTargets =
    [
        (NppesEntryKind.NpiData, "npidata", true),
        (NppesEntryKind.OtherName, "other_names", true),
        (NppesEntryKind.PracticeLocation, "practice_locations", true),
        (NppesEntryKind.Endpoint, "endpoints", false),
    ];

    /// <summary>The child tables replaced per NPI by a weekly file (and in full by a monthly).</summary>
    private static readonly string[] ChildTables = ["other_names", "practice_locations", "endpoints"];

    /// <returns>Total rows loaded into staging across the tables.</returns>
    public async Task<long> LoadAsync(string zipPath, NppesFile file, CancellationToken ct)
    {
        if (file.Kind is not (NppesFileKind.Monthly or NppesFileKind.Weekly))
        {
            throw new ArgumentException($"{file.FileName} is not a data file.", nameof(file));
        }

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = FindEntries(zip, file);
        var targets = AllTargets.Where(t => entries.ContainsKey(t.Kind)).Select(t => (t.Kind, t.Table)).ToList();
        await using var connection = await database.OpenAsync(ct);

        // CREATE TABLE npidata_staging LIKE npidata fails InnoDB's worst-case row-size check in strict
        // mode, although every real row fits (see db/migrations/nppes/002_npidata_v2.sql).
        await Database.ExecuteAsync(connection, "SET SESSION innodb_strict_mode = OFF", ct);

        try
        {
            long total = 0;
            foreach (var (kind, table) in targets)
            {
                var entry = entries[kind];
                var staging = table + "_staging";
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {SqlIdentifier.Quote(staging)}", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE {SqlIdentifier.Quote(staging)} LIKE {SqlIdentifier.Quote(table)}", ct);

                var started = DateTime.UtcNow;
                var rows = await StagingLoader.LoadCsvAsync(connection, entry, staging, file.FileName, ct, ProjectionBuilder.ReadsColumn);
                total += rows;
                await Database.ExecuteAsync(connection,
                    "INSERT INTO `extractlog` (`ZipFileName`, `ExtractFileName`, `rows_loaded`) VALUES (@zip, @entry, @rows)", ct,
                    param: new { zip = file.FileName, entry = entry.Name, rows });
                log.Information("Loaded {Entry} into {Table}: {Rows:N0} rows in {Seconds:N0}s", entry.Name, staging, rows, (DateTime.UtcNow - started).TotalSeconds);
            }

            if (file.Kind == NppesFileKind.Monthly)
            {
                var compared = await QueueMonthlyChangesAsync(connection, targets.Select(t => t.Table).ToList(), ct);
                await ReplaceAllAsync(connection, targets.Select(t => t.Table).ToList(), ct);
                var flagged = await Deactivations.ApplyPendingAsync(connection, all: !compared, ct);
                log.Information("Deactivation flags refreshed: {Rows:N0} rows changed", flagged);
            }
            else
            {
                await ApplyWeeklyAsync(connection, ChildTables.Where(t => targets.Any(x => x.Table == t)).ToList(), ct);
            }

            return total;
        }
        finally
        {
            // Also after a failure: a monthly's staging tables hold ~10 GB.
            foreach (var (_, table, _) in AllTargets)
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {SqlIdentifier.Quote(table + "_staging")}", CancellationToken.None);
            }
        }
    }

    private Dictionary<NppesEntryKind, ZipArchiveEntry> FindEntries(ZipArchive zip, NppesFile file)
    {
        var found = new Dictionary<NppesEntryKind, ZipArchiveEntry>();
        foreach (var entry in zip.Entries)
        {
            var kind = NppesEntryClassifier.Classify(entry.FullName);
            switch (kind)
            {
                case NppesEntryKind.NpiData or NppesEntryKind.OtherName or NppesEntryKind.PracticeLocation or NppesEntryKind.Endpoint:
                    if (!found.TryAdd(kind, entry))
                    {
                        throw new InvalidDataException($"{file.FileName} contains more than one {kind} file.");
                    }

                    break;
                case NppesEntryKind.Unknown:
                    log.Warning("Skipping unexpected entry {Entry} in {File}", entry.FullName, file.FileName);
                    break;
            }
        }

        var missing = AllTargets.Where(t => t.Required && !found.ContainsKey(t.Kind)).Select(t => t.Kind).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidDataException($"{file.FileName} has no {string.Join(", ", missing)} file.");
        }

        if (!found.ContainsKey(NppesEntryKind.Endpoint))
        {
            log.Warning("{File} has no endpoint file; the stored endpoints are kept", file.FileName);
        }

        return found;
    }

    // Columns that say where or when a row was loaded, not what NPPES says: left out when comparing rows.
    private static readonly HashSet<string> Bookkeeping = new(["ID", "Loaded_From", "Is_Deactivated", StagingLoader.SourceHash], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Monthly, before the swap: queues in <c>projection_pending</c> the NPIs whose data differ between the new file and the
    /// stored tables (a new or changed npidata row, an NPI no longer in the file, or different other names, practice
    /// locations or endpoints), so the projection rebuilds only them. After the weeklies, a monthly changes few NPIs.
    /// Only the columns the projection reads count (<see cref="ProjectionBuilder.ReadsColumn"/>), so changes in columns the
    /// site never shows queue nothing (comparing every column, the October 2026 file differed in 4.6M NPIs, in columns such
    /// as the EIN and certification date). npidata rows compare by <see cref="StagingLoader.SourceHash"/>, written while the
    /// files load; child rows by an MD5 per NPI, computed here (those tables are small). The same pass over npidata queues in
    /// <c>deactivation_pending</c> the NPIs whose deactivation flag may change (speed item 10): those whose row differs, or
    /// whose deactivation or reactivation date does (the reactivation date isn't in the hash: the projection doesn't read it).
    /// </summary>
    /// <returns>False on a first load (nothing stored to compare with): every flag is then computed.</returns>
    private async Task<bool> QueueMonthlyChangesAsync(MySqlConnection connection, IReadOnlyList<string> tables, CancellationToken ct)
    {
        if (await Database.CountAsync(connection, "npidata", ct) == 0)
        {
            return false; // a first load: the projection is built in full
        }

        var started = DateTime.UtcNow;
        Task<int> RunAsync(string sql) => connection.ExecuteAsync(new CommandDefinition(sql, commandTimeout: 0, cancellationToken: ct));
        await RunAsync("SET SESSION group_concat_max_len = 67108864");
        var before = await Database.CountAsync(connection, "projection_pending", ct);

        // The flags and the deactivation dates filled from the report (Deactivations.ApplyAsync) are carried over first, as
        // the flag pass after the swap would set them again: otherwise every deactivated NPI (~357,000) would look changed.
        await RunAsync(
            """
            UPDATE `npidata_staging` s
            JOIN `npidata` n ON n.`NPI` = s.`NPI`
            LEFT JOIN `nppes_deactivated_npi_report` r ON r.`NPI` = n.`NPI`
            SET s.`Is_Deactivated` = n.`Is_Deactivated`,
                s.`NPI_Deactivation_Date` = IF(COALESCE(s.`NPI_Deactivation_Date`, '') = ''
                                                 AND n.`NPI_Deactivation_Date` = DATE_FORMAT(r.`NPPES_Deactivation_Date`, '%m/%d/%Y'),
                                               n.`NPI_Deactivation_Date`, s.`NPI_Deactivation_Date`)
            WHERE n.`Is_Deactivated` = 1 OR r.`NPI` IS NOT NULL
            """);
        foreach (var table in tables)
        {
            var tableStarted = DateTime.UtcNow;
            var queued = await Database.CountAsync(connection, "projection_pending", ct);
            await QueueTableChangesAsync(table);
            log.Information("Monthly diff {Table}: {Rows:N0} NPIs in {Seconds:N0}s", table,
                await Database.CountAsync(connection, "projection_pending", ct) - queued, (DateTime.UtcNow - tableStarted).TotalSeconds);
        }

        log.Information("Monthly: {Rows:N0} NPIs differ from the stored data ({Seconds:N0}s)",
            await Database.CountAsync(connection, "projection_pending", ct) - before, (DateTime.UtcNow - started).TotalSeconds);
        return true;

        async Task QueueTableChangesAsync(string table)
        {
            var columns = (await Database.GetColumnsAsync(connection, table, ct)).Select(c => c.Name)
                .Where(c => !Bookkeeping.Contains(c) && (c.Equals("NPI", StringComparison.OrdinalIgnoreCase) || ProjectionBuilder.ReadsColumn(c))).ToList();
            var t = SqlIdentifier.Quote(table);
            var st = SqlIdentifier.Quote(table + "_staging");
            if (table == "npidata")
            {
                // One read of both tables: every NPI whose flag may change, then those of them whose row differs.
                var differs = $"NOT (s.`{StagingLoader.SourceHash}` <=> n.`{StagingLoader.SourceHash}`) OR s.`{StagingLoader.SourceHash}` IS NULL";
                await RunAsync(
                    $"""
                    INSERT IGNORE INTO `deactivation_pending` (`NPI`)
                    SELECT s.`NPI` FROM {st} s LEFT JOIN {t} n ON n.`NPI` = s.`NPI`
                    WHERE {differs} OR NOT (s.`NPI_Deactivation_Date` <=> n.`NPI_Deactivation_Date`)
                       OR NOT (s.`NPI_Reactivation_Date` <=> n.`NPI_Reactivation_Date`)
                    """);
                await RunAsync(
                    $"""
                    INSERT IGNORE INTO `projection_pending` (`npi`)
                    SELECT s.`NPI` FROM `deactivation_pending` p JOIN {st} s ON s.`NPI` = p.`NPI` LEFT JOIN {t} n ON n.`NPI` = s.`NPI`
                    WHERE {differs}
                    """);
                await RunAsync(
                    $"""
                    INSERT IGNORE INTO `projection_pending` (`npi`)
                    SELECT n.`NPI` FROM {t} n LEFT JOIN {st} s ON s.`NPI` = n.`NPI` WHERE s.`NPI` IS NULL
                    """);
                return;
            }

            // Child rows: one hash per NPI over its rows' hashes in sorted order (row order and ids don't matter).
            await NpiRowDiff.QueueAsync(connection, table + "_staging", table, "NPI", "projection_pending", ct, columns);
        }
    }

    /// <summary>
    /// Monthly: check the staging row counts, then swap all tables in one atomic RENAME so the
    /// site never sees an empty or half-loaded table (legacy defect #11).
    /// </summary>
    private async Task ReplaceAllAsync(MySqlConnection connection, IReadOnlyList<string> tables, CancellationToken ct)
    {
        foreach (var table in tables)
        {
            var current = await Database.CountAsync(connection, table, ct);
            var incoming = await Database.CountAsync(connection, table + "_staging", ct);
            if (current > 0 && incoming < current * minRowRatio)
            {
                throw new InvalidDataException(
                    $"Monthly {table} has {incoming:N0} rows, fewer than {minRowRatio:P0} of the current {current:N0}; not replacing it.");
            }
        }

        foreach (var table in tables)
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {SqlIdentifier.Quote(table + "_old")}", ct);
        }

        var renames = tables.SelectMany(t => new[]
        {
            $"{SqlIdentifier.Quote(t)} TO {SqlIdentifier.Quote(t + "_old")}",
            $"{SqlIdentifier.Quote(t + "_staging")} TO {SqlIdentifier.Quote(t)}",
        });
        await Database.ExecuteAsync(connection, $"RENAME TABLE {string.Join(", ", renames)}", ct);
        log.Information("Swapped in the new {Tables}", string.Join(", ", tables));

        foreach (var table in tables)
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE {SqlIdentifier.Quote(table + "_old")}", ct);
        }
    }

    /// <summary>
    /// Weekly: upsert npidata rows unless the stored row is newer (legacy defect #9), then replace the
    /// other names and practice locations of exactly those NPIs (legacy defect #5). One transaction.
    /// </summary>
    private async Task ApplyWeeklyAsync(MySqlConnection connection, IReadOnlyList<string> childTableNames, CancellationToken ct)
    {
        var npiColumns = (await Database.GetColumnsAsync(connection, "npidata", ct))
            .Select(c => c.Name)
            .Where(c => !c.Equals("Is_Deactivated", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var columnList = string.Join(", ", npiColumns.Select(SqlIdentifier.Quote));
        var updates = string.Join(", ", npiColumns
            .Where(c => !c.Equals("NPI", StringComparison.OrdinalIgnoreCase))
            .Select(c => $"{SqlIdentifier.Quote(c)} = s.{SqlIdentifier.Quote(c)}"));
        var childTables = new List<(string Table, string Columns)>();
        foreach (var table in childTableNames)
        {
            var childColumns = (await Database.GetColumnsAsync(connection, table, ct))
                .Select(c => c.Name)
                .Where(c => !c.Equals("ID", StringComparison.OrdinalIgnoreCase))
                .Select(SqlIdentifier.Quote);
            childTables.Add((table, string.Join(", ", childColumns)));
        }

        // Every command below must carry tx: MySqlConnector rejects commands outside the active transaction.
        await using var tx = await connection.BeginTransactionAsync(ct);

        var stale = await connection.ExecuteAsync(new CommandDefinition(
            $"""
            DELETE s FROM `npidata_staging` s JOIN `npidata` n ON n.`NPI` = s.`NPI`
            WHERE {Deactivations.Ymd("n.`Last_Update_Date`")} > {Deactivations.Ymd("s.`Last_Update_Date`")}
            """, transaction: tx, cancellationToken: ct));
        if (stale > 0)
        {
            log.Information("Skipping {Rows:N0} weekly rows older than the stored ones", stale);
        }

        var applied = await Database.CountAsync(connection, "npidata_staging", ct, tx);
        // The projection rebuilds exactly these NPIs (with their child rows) next time.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO `projection_pending` (`npi`) SELECT `NPI` FROM `npidata_staging`", transaction: tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO `npidata` ({columnList})
            SELECT {columnList} FROM `npidata_staging` s
            ON DUPLICATE KEY UPDATE {updates}
            """, transaction: tx, cancellationToken: ct));

        foreach (var (table, list) in childTables)
        {
            var t = SqlIdentifier.Quote(table);
            var st = SqlIdentifier.Quote(table + "_staging");
            await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE c FROM {t} c JOIN `npidata_staging` s ON s.`NPI` = c.`NPI`", transaction: tx, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                $"""
                INSERT INTO {t} ({list})
                SELECT {list} FROM {st} c
                WHERE EXISTS (SELECT 1 FROM `npidata_staging` s WHERE s.`NPI` = c.`NPI`)
                """, transaction: tx, cancellationToken: ct));
        }

        var flagged = await Deactivations.ApplyAsync(connection, scopeTable: "npidata_staging", ct, tx);
        await tx.CommitAsync(ct);
        log.Information("Weekly applied: {Rows:N0} NPIs inserted or updated, {Flagged:N0} rows had their deactivation flag or date changed",
            applied, flagged);
    }
}
