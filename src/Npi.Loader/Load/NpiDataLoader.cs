using System.IO.Compression;
using Dapper;
using MySqlConnector;
using Npi.Core.Sql;
using Npi.Loader.Db;
using Npi.Loader.Nppes;
using Serilog;

namespace Npi.Loader.Load;

/// <summary>
/// Loads an NPPES data zip (monthly full or weekly incremental) into npidata, other_names and
/// practice_locations (CLAUDE.md §7 Stage 1.4–1.6). Every CSV goes into a <c>*_staging</c> copy first.
/// </summary>
public sealed class NpiDataLoader(Database database, ILogger log, double minRowRatio)
{
    private static readonly (NppesEntryKind Kind, string Table)[] Targets =
    [
        (NppesEntryKind.NpiData, "npidata"),
        (NppesEntryKind.OtherName, "other_names"),
        (NppesEntryKind.PracticeLocation, "practice_locations"),
    ];

    /// <returns>Total rows loaded into staging across the three tables.</returns>
    public async Task<long> LoadAsync(string zipPath, NppesFile file, CancellationToken ct)
    {
        if (file.Kind is not (NppesFileKind.Monthly or NppesFileKind.Weekly))
        {
            throw new ArgumentException($"{file.FileName} is not a data file.", nameof(file));
        }

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = FindEntries(zip, file);
        await using var connection = await database.OpenAsync(ct);

        // CREATE TABLE npidata_staging LIKE npidata fails InnoDB's worst-case row-size check in strict
        // mode, although every real row fits (see db/migrations/002_npidata_v2.sql).
        await Database.ExecuteAsync(connection, "SET SESSION innodb_strict_mode = OFF", ct);

        try
        {
            long total = 0;
            foreach (var (kind, table) in Targets)
            {
                var entry = entries[kind];
                var staging = table + "_staging";
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {SqlIdentifier.Quote(staging)}", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE {SqlIdentifier.Quote(staging)} LIKE {SqlIdentifier.Quote(table)}", ct);

                var started = DateTime.UtcNow;
                var rows = await StagingLoader.LoadCsvAsync(connection, entry, staging, file.FileName, ct);
                total += rows;
                await Database.ExecuteAsync(connection,
                    "INSERT INTO `extractlog` (`ZipFileName`, `ExtractFileName`, `rows_loaded`) VALUES (@zip, @entry, @rows)", ct,
                    param: new { zip = file.FileName, entry = entry.Name, rows });
                log.Information("Loaded {Entry} into {Table}: {Rows:N0} rows in {Seconds:N0}s", entry.Name, staging, rows, (DateTime.UtcNow - started).TotalSeconds);
            }

            if (file.Kind == NppesFileKind.Monthly)
            {
                await ReplaceAllAsync(connection, ct);
                var flagged = await Deactivations.ApplyAsync(connection, scopeTable: null, ct);
                log.Information("Deactivation flags refreshed: {Rows:N0} rows changed", flagged);
            }
            else
            {
                await ApplyWeeklyAsync(connection, ct);
            }

            return total;
        }
        finally
        {
            // Also after a failure: a monthly's staging tables hold ~10 GB.
            foreach (var (_, table) in Targets)
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
                case NppesEntryKind.NpiData or NppesEntryKind.OtherName or NppesEntryKind.PracticeLocation:
                    if (!found.TryAdd(kind, entry))
                    {
                        throw new InvalidDataException($"{file.FileName} contains more than one {kind} file.");
                    }

                    break;
                case NppesEntryKind.Endpoint:
                    log.Information("Skipping {Entry}: endpoints are out of scope", entry.FullName);
                    break;
                case NppesEntryKind.Unknown:
                    log.Warning("Skipping unexpected entry {Entry} in {File}", entry.FullName, file.FileName);
                    break;
            }
        }

        var missing = Targets.Where(t => !found.ContainsKey(t.Kind)).Select(t => t.Kind).ToList();
        return missing.Count == 0
            ? found
            : throw new InvalidDataException($"{file.FileName} has no {string.Join(", ", missing)} file.");
    }

    /// <summary>
    /// Monthly: check the staging row counts, then swap all three tables in one atomic RENAME so the
    /// site never sees an empty or half-loaded table (legacy defect #11).
    /// </summary>
    private async Task ReplaceAllAsync(MySqlConnection connection, CancellationToken ct)
    {
        foreach (var (_, table) in Targets)
        {
            var current = await Database.CountAsync(connection, table, ct);
            var incoming = await Database.CountAsync(connection, table + "_staging", ct);
            if (current > 0 && incoming < current * minRowRatio)
            {
                throw new InvalidDataException(
                    $"Monthly {table} has {incoming:N0} rows, fewer than {minRowRatio:P0} of the current {current:N0}; not replacing it.");
            }
        }

        foreach (var (_, table) in Targets)
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {SqlIdentifier.Quote(table + "_old")}", ct);
        }

        var renames = Targets.SelectMany(t => new[]
        {
            $"{SqlIdentifier.Quote(t.Table)} TO {SqlIdentifier.Quote(t.Table + "_old")}",
            $"{SqlIdentifier.Quote(t.Table + "_staging")} TO {SqlIdentifier.Quote(t.Table)}",
        });
        await Database.ExecuteAsync(connection, $"RENAME TABLE {string.Join(", ", renames)}", ct);
        log.Information("Swapped in the new npidata, other_names and practice_locations");

        foreach (var (_, table) in Targets)
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE {SqlIdentifier.Quote(table + "_old")}", ct);
        }
    }

    /// <summary>
    /// Weekly: upsert npidata rows unless the stored row is newer (legacy defect #9), then replace the
    /// other names and practice locations of exactly those NPIs (legacy defect #5). One transaction.
    /// </summary>
    private async Task ApplyWeeklyAsync(MySqlConnection connection, CancellationToken ct)
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
        foreach (var table in new[] { "other_names", "practice_locations" })
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

        var upserted = await connection.ExecuteAsync(new CommandDefinition(
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
        log.Information("Weekly applied: {Rows:N0} npidata rows inserted/updated, {Flagged:N0} deactivation flags changed",
            upserted, flagged);
    }
}
