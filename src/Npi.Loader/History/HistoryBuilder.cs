using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dapper;
using MySqlConnector;
using Npi.Core.Search;
using Npi.Loader.Db;
using Npi.Loader.Load;
using Npi.Loader.Nppes;
using Serilog;

namespace Npi.Loader.History;

public static class HistoryPeriodStatus
{
    public const string Loading = "Loading";
    public const string Completed = "Completed";
    public const string Failed = "Failed";

    /// <summary>No file was published or kept for the month (or a later month was processed first).</summary>
    public const string Missing = "Missing";
}

/// <summary>
/// NPPES history before October 2026 (docs/PRODUCT_PLAN.md step 3.4; migration 103): every archived monthly file, oldest
/// first, compared with the one before into npi_history (a version per NPI per change of the fields that matter), and each
/// month's area figures (basis 3 of area_month_stat, <see cref="HistoryAreaStats"/>). One file at a time: downloaded (the
/// next one meanwhile), read straight from the zip (<see cref="NppesHistoryReader"/>), compared in memory
/// (<see cref="HistoryState"/>), the changes written and the month marked Completed in one transaction, then the zip deleted.
/// A stopped run reads the open versions back and goes on with the first month not done. The monthly file <c>run</c> loads
/// is added the same way (<see cref="AppendMonthlyAsync"/>), so the series continues.
/// </summary>
public sealed class HistoryBuilder(LoaderOptions options, Database database, HttpClient http, ILogger log)
{
    private const string Batch = "npi_history_batch";

    private sealed record PeriodRow(DateTime Period, string Status, DateTime? FileDate, int? RowsRead, sbyte StatsDone);

    /// <summary>
    /// The <c>history</c> command: finds the archived files (CMS from June 2015, NBER before), records the months with none as
    /// Missing, processes every month not done up to <paramref name="until"/> (default: the newest file), then writes the area
    /// figures of every month not written yet.
    /// </summary>
    /// <returns>False when a month failed (the run stops there; the next one starts again with it).</returns>
    public async Task<bool> RunAsync(DateOnly? until, CancellationToken ct)
    {
        var statuses = await StatusesAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var last = until is { } u && u < thisMonth ? u : thisMonth;
        foreach (var month in HistoryPlan.Superseded(statuses))
        {
            await RecordMissingAsync(month, "skipped: a later month was compared first", ct);
        }

        var skip = HistoryPlan.Skip(statuses);
        var catalog = new HistoryCatalog(http, log, new HistoryCatalogOptions { CmsBase = new Uri(options.HistoryCmsBaseUrl), NberBase = new Uri(options.HistoryNberBaseUrl) });
        var files = await catalog.FindAsync(last, skip, ct);
        foreach (var month in HistoryPlan.Missing(files.Keys.ToHashSet(), skip, HistoryPlan.LastCompleted(statuses)))
        {
            await RecordMissingAsync(month, "no file published or kept", ct);
        }

        var queue = files.Values.OrderBy(f => f.Period).ToList();
        log.Information("History: {Count} month(s) to process{Range}, {Missing} missing", queue.Count,
            queue.Count > 0 ? $" ({Key(queue[0].Period)} to {Key(queue[^1].Period)})" : "",
            (await StatusesAsync(ct)).Values.Count(s => s == HistoryPeriodStatus.Missing));

        var downloader = new Downloader(http, log, options.HistoryDownloadAttempts, TimeSpan.FromSeconds(10), options.DownloadParts, options.DownloadPartMinBytes,
            maxRetryDelay: TimeSpan.FromMinutes(2));
        Directory.CreateDirectory(options.ResolvedHistoryFolder);
        return await ProcessFilesAsync(queue, file => downloader.DownloadAsync(file.Url, file.FileName, options.ResolvedHistoryFolder, ct), ct);
    }

    /// <summary>
    /// The <c>history discover</c> command: lists where each month's archived file is and which months have none, without
    /// downloading anything or reading the database.
    /// </summary>
    public async Task<int> DiscoverAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var catalog = new HistoryCatalog(http, log, new HistoryCatalogOptions { CmsBase = new Uri(options.HistoryCmsBaseUrl), NberBase = new Uri(options.HistoryNberBaseUrl) });
        var files = await catalog.FindAsync(new DateOnly(today.Year, today.Month, 1), new HashSet<DateOnly>(), ct);
        foreach (var file in files.Values.OrderBy(f => f.Period))
        {
            log.Information("History {Month}: {Source} {Url}", Key(file.Period), file.Source, file.Url);
        }

        var missing = HistoryPlan.Missing(files.Keys.ToHashSet(), new HashSet<DateOnly>(), null);
        log.Information("History: {Files} monthly files ({Cms} CMS, {Nber} NBER); {Missing} months without one: {List}", files.Count,
            files.Values.Count(f => f.Source == "cms"), files.Values.Count(f => f.Source == "nber"), missing.Count, string.Join(", ", missing.Select(Key)));
        return 0;
    }

    /// <summary>
    /// Writes the area figures still owed, then compares each file of <paramref name="queue"/> (oldest first; months already
    /// Completed are skipped) with the history, stopping at the first that fails. <paramref name="fetch"/> gives a file's local
    /// zip (a download, the next one fetched meanwhile when <see cref="LoaderOptions.HistoryPrefetch"/>); each zip is deleted
    /// after use unless <see cref="LoaderOptions.HistoryKeepZips"/>.
    /// </summary>
    internal async Task<bool> ProcessFilesAsync(IReadOnlyList<HistoryFile> queue, Func<HistoryFile, Task<string>> fetch, CancellationToken ct)
    {
        await WriteStatsAsync(ct);
        var completed = HistoryPlan.LastCompleted(await StatusesAsync(ct));
        queue = queue.Where(f => completed is not { } c || f.Period > c).OrderBy(f => f.Period).ToList();
        if (queue.Count == 0)
        {
            return true;
        }

        var state = await LoadStateAsync(ct);
        var zips = await LoadCrosswalkZipsAsync(ct);
        Task<string>? next = null;
        for (var i = 0; i < queue.Count; i++)
        {
            var file = queue[i];
            string zip;
            try
            {
                zip = await (next ?? fetch(file));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.Error(ex, "History {Month}: getting {File} failed; the next run starts again here", Key(file.Period), file.FileName);
                await RecordFailedAsync(file, ex.Message, ct);
                return false;
            }

            next = options.HistoryPrefetch && i + 1 < queue.Count ? fetch(queue[i + 1]) : null;
            try
            {
                await ProcessAsync(state, zips, file, zip, ct);
            }
            catch (DamagedArchiveException ex)
            {
                // Nothing was written (the file is read in full before any version is), but the state in memory took the rows
                // read so far, so it is read back from the database.
                log.Warning("History {Month}: {File} ({Url}) is damaged: {Message}; recorded as a month without a file", Key(file.Period),
                    file.FileName, file.Url, ex.Message);
                await RecordMissingAsync(file.Period, $"damaged copy ({file.Url}): {ex.Message}", ct);
                state = await LoadStateAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.Error(ex, "History {Month}: {File} failed; the next run starts again here", Key(file.Period), file.FileName);
                await RecordFailedAsync(file, ex.Message, ct);
                await ForgetAsync(next);
                return false;
            }
            catch (OperationCanceledException)
            {
                await ForgetAsync(next);
                throw;
            }
            finally
            {
                DeleteZip(zip);
            }
        }

        return true;
    }

    /// <summary>
    /// After <c>run</c> loaded a monthly file: adds it to the history from the same zip, when the history has started and the
    /// month is the next one (the months between recorded as Missing). Otherwise logs what <c>history</c> would do.
    /// </summary>
    /// <returns>True when the month was added.</returns>
    public async Task<bool> AppendMonthlyAsync(string zipPath, NppesFile file, CancellationToken ct)
    {
        var month = new DateOnly(file.FileDate.Year, file.FileDate.Month, 1);
        var statuses = await StatusesAsync(ct);
        switch (HistoryPlan.CanAppend(statuses, month, out var first))
        {
            case HistoryPlan.Append.NotStarted:
            case HistoryPlan.Append.AlreadyDone:
                return false;
            case HistoryPlan.Append.Blocked:
                log.Warning("History: {File} not added: {Months} come first (Npi.Loader history adds them, then this month)", file.FileName, string.Join(", ", first.Select(Key)));
                return false;
        }

        var watch = Stopwatch.StartNew();
        var state = await LoadStateAsync(ct);
        log.Information("History: open versions read back in {Seconds:N0}s", watch.Elapsed.TotalSeconds);
        var url = new Uri(new Uri(options.NppesPageUrl), file.FileName);
        await WriteStatsAsync(ct);
        await ProcessAsync(state, await LoadCrosswalkZipsAsync(ct), new HistoryFile(month, file.FileName, url, "cms"), zipPath, ct);
        log.Information("History: {File} added in {Seconds:N0}s", file.FileName, watch.Elapsed.TotalSeconds);
        return true;
    }

    /// <summary>
    /// The <c>history measure</c> command: reads local zips in order and compares them in memory only (no database), logging
    /// the read time, versions per file and memory, to size a full run. When the database answers, the practice ZIPs not in its
    /// HUD crosswalk are counted too.
    /// </summary>
    public async Task MeasureAsync(IReadOnlyList<string> zips, CancellationToken ct)
    {
        var state = new HistoryState(new HistoryValues());
        IReadOnlySet<int> crosswalk;
        try
        {
            crosswalk = await LoadCrosswalkZipsAsync(ct);
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException)
        {
            log.Information("History measure: no database ({Message}); ZIPs not checked against the crosswalk", ex.Message);
            crosswalk = new HashSet<int>();
        }

        foreach (var zip in zips)
        {
            var watch = Stopwatch.StartNew();
            var dataDate = NppesHistoryReader.PeekDataDate(zip) ?? HistoryCatalog.PeriodOf(Path.GetFileName(zip)) ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var diff = state.Apply(dataDate, handler => NppesHistoryReader.Read(zip, state.Dictionary, handler, ct));
            var bytes = new FileInfo(zip).Length;
            GC.Collect();
            log.Information(
                "History measure {File} ({Date}): {Summary}; read and compared in {Seconds:N1}s ({Rate:N0} MB/s of zip); {Versions:N0} versions: {New:N0} new NPIs, " +
                "{Moved:N0} moved, {Specialty:N0} primary taxonomy changed, {Deactivated:N0} deactivated ({Gone:N0} no longer listed), {Reactivated:N0} reactivated, " +
                "{Other:N0} other changes (dates, entity, other codes); {Active:N0} active ({WithLocations:N0} with secondary locations, {LocationsChanged:N0} of them changed), {Npis:N0} NPIs in memory, {Sets:N0} taxonomy sets, {LocationSets:N0} location sets, {Memory:N0} MB managed",
                Path.GetFileName(zip), dataDate, NppesHistoryReader.Describe(diff.File), watch.Elapsed.TotalSeconds, bytes / 1e6 / watch.Elapsed.TotalSeconds,
                diff.Versions, diff.NewNpis, diff.Moved, diff.SpecialtyChanged, diff.Deactivated, diff.Gone, diff.Reactivated,
                diff.Versions - diff.NewNpis - diff.Deactivated - diff.Reactivated - diff.Moved, diff.Active, diff.WithLocations, diff.LocationsChanged, state.Count,
                state.Dictionary.SetCount,
                state.Dictionary.LocationSetCount,
                GC.GetTotalMemory(false) / 1_000_000);
            LogUnmapped(state, Path.GetFileName(zip));
            if (diff.File.HasLocations)
            {
                var perNpi = state.All.Sum(e => (long)state.Dictionary.LocationPairCountOf(e.Value.LocationSet));
                log.Information("History measure {File}: {Sets:N0} location sets hold {Pairs:N0} (state, ZIP) rows; stored per NPI the open versions alone would need {PerNpi:N0}",
                    Path.GetFileName(zip), state.Dictionary.LocationSetCount, state.Dictionary.LocationPairCount, perNpi);
            }
            if (crosswalk.Count > 0)
            {
                var (unmatched, providers) = UnmatchedZips(state, crosswalk);
                log.Information("History measure {File}: ZIPs not in today's crosswalk: {Zips:N0} ({Providers:N0} of {Active:N0} active providers)",
                    Path.GetFileName(zip), unmatched, providers, diff.Active);
            }
        }
    }

    /// <summary>The area figures of every Completed month not written yet, oldest first.</summary>
    public async Task<bool> WriteStatsAsync(CancellationToken ct)
    {
        var periods = await ReadPeriodsAsync(ct);
        var first = periods.Values.Where(p => p.Status == HistoryPeriodStatus.Completed).Select(p => DateOnly.FromDateTime(p.Period)).DefaultIfEmpty().Min();
        var pending = periods.Values.Where(p => p.Status == HistoryPeriodStatus.Completed && p.StatsDone == 0).OrderBy(p => p.Period).ToList();
        if (pending.Count == 0)
        {
            return true;
        }

        await using var connection = await database.OpenAsync(ct);
        foreach (var p in pending)
        {
            var watch = Stopwatch.StartNew();
            var month = DateOnly.FromDateTime(p.Period);
            var rows = await HistoryAreaStats.WriteAsync(connection, month, DateOnly.FromDateTime(p.FileDate ?? p.Period), first, ct);
            log.Information("History {Month}: area figures written ({Rows:N0} rows) in {Seconds:N1}s", Key(month), rows, watch.Elapsed.TotalSeconds);
        }

        return true;
    }

    private async Task ProcessAsync(HistoryState state, IReadOnlySet<int> crosswalkZips, HistoryFile file, string zip, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        await StartPeriodAsync(file, new FileInfo(zip).Length, ct);
        var previous = await ScalarDateAsync(
            "SELECT MAX(`file_date`) FROM `history_period` WHERE `status` = 'Completed'", ct);
        var dataDate = NppesHistoryReader.PeekDataDate(zip) ?? file.Period;
        if (previous is { } before && dataDate <= DateOnly.FromDateTime(before))
        {
            throw new InvalidDataException($"{file.FileName} is dated {dataDate:yyyy-MM-dd}, not after the last file compared ({before:yyyy-MM-dd}).");
        }

        var diff = state.Apply(dataDate, handler => NppesHistoryReader.Read(zip, state.Dictionary, handler, ct), options.MinRowRatio);
        if (new DateOnly(dataDate.Year, dataDate.Month, 1) != file.Period)
        {
            log.Warning("History {Month}: {File}'s data is dated {Date:yyyy-MM-dd}, in another month; counted as {Month}", Key(file.Period), file.FileName, dataDate, Key(file.Period));
        }

        var read = watch.Elapsed.TotalSeconds;
        var (zipUnmatched, zipUnmatchedProviders) = UnmatchedZips(state, crosswalkZips);
        await WriteVersionsAsync(state, file, diff, zipUnmatched, zipUnmatchedProviders, (int)watch.Elapsed.TotalSeconds, ct);
        var written = watch.Elapsed.TotalSeconds - read;
        log.Information(
            "History {Month} ({Date:yyyy-MM-dd}, {Source}): {Summary}; read and compared in {Read:N0}s, {Versions:N0} versions written in {Written:N0}s " +
            "({New:N0} new NPIs, {Moved:N0} moved, {Specialty:N0} primary taxonomy changed, {Deactivated:N0} deactivated of which {Gone:N0} no longer listed, " +
            "{Reactivated:N0} reactivated); {Active:N0} active; ZIPs not in today's crosswalk: {Zips:N0} ({ZipProviders:N0} providers)",
            Key(file.Period), dataDate, file.Source, NppesHistoryReader.Describe(diff.File), read, diff.Versions, written, diff.NewNpis, diff.Moved,
            diff.SpecialtyChanged, diff.Deactivated, diff.Gone, diff.Reactivated, diff.Active, zipUnmatched, zipUnmatchedProviders);
        LogUnmapped(state, Key(file.Period));

        await using var connection = await database.OpenAsync(ct);
        var first = await connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT MIN(`period`) FROM `history_period` WHERE `status` = 'Completed'", cancellationToken: ct));
        var statsWatch = Stopwatch.StartNew();
        var rows = await HistoryAreaStats.WriteAsync(connection, file.Period, dataDate, DateOnly.FromDateTime(first), ct);
        log.Information("History {Month}: area figures written ({Rows:N0} rows) in {Seconds:N1}s", Key(file.Period), rows, statsWatch.Elapsed.TotalSeconds);
    }

    /// <summary>The new versions to a scratch table, then in one transaction: old versions closed, new ones added, the taxonomy sets, the month Completed.</summary>
    private async Task WriteVersionsAsync(HistoryState state, HistoryFile file, PeriodDiff diff, int zipUnmatched, int zipUnmatchedProviders, int seconds,
        CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{Batch}`", ct);
        await Database.ExecuteAsync(connection,
            $"""
            CREATE TABLE `{Batch}` (
              `npi` INT UNSIGNED NOT NULL, `from_period` DATE NOT NULL, `status` TINYINT NOT NULL, `entity_type` TINYINT NULL, `state` VARCHAR(40) NULL,
              `zip5` CHAR(5) NULL, `country` CHAR(2) NULL, `primary_taxonomy` VARCHAR(10) NULL, `taxonomy_set` INT UNSIGNED NULL,
              `location_set` INT UNSIGNED NULL, `enumeration_date` DATE NULL, `last_update_date` DATE NULL, `deactivation_date` DATE NULL, `reactivation_date` DATE NULL,
              PRIMARY KEY (`npi`)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
            """, ct);

        var path = Path.Combine(options.ResolvedHistoryFolder, "npi_history_batch.tsv");
        Directory.CreateDirectory(options.ResolvedHistoryFolder);
        try
        {
            await using (var writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20))
            {
                var line = new StringBuilder(160);
                foreach (var key in diff.Changed.Distinct())
                {
                    state.TryGet(key, out var v);
                    VersionLine(line, key, v, state.Dictionary);
                    await writer.WriteAsync(line, ct);
                }
            }

            var loader = new MySqlBulkLoader(connection)
            {
                TableName = $"`{Batch}`",
                Local = true,
                FileName = path,
                CharacterSet = "utf8mb4",
                FieldTerminator = "\t",
                LineTerminator = "\n",
            };
            foreach (var column in VersionColumns)
            {
                loader.Columns.Add(column);
            }

            await StagingLoader.WithWarningsAsFailures(connection, $"{file.FileName} versions", () => loader.LoadAsync(ct));
        }
        finally
        {
            File.Delete(path);
        }

        var sets = state.Dictionary.TakeNewSets();
        var locationRows = state.Dictionary.TakeNewLocationSets()
            .SelectMany(id => state.Dictionary.LocationPairs(id).Select(pair => new { Id = id, pair.State, pair.Zip }))
            .ToList();
        var param = new { p = diff.DataDate.ToDateTime(TimeOnly.MinValue), period = file.Period.ToDateTime(TimeOnly.MinValue) };
        await using var tx = await connection.BeginTransactionAsync(ct);
        foreach (var chunk in sets.Chunk(1000))
        {
            await connection.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO `npi_history_taxonomy_set` (`id`, `codes`) VALUES (@Id, @Codes)",
                chunk.Select(s => new { s.Id, s.Codes }), tx, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO `npi_history_set_category` (`set_id`, `category`) VALUES (@Id, @Category)",
                chunk.SelectMany(s => HistoryAreaStats.CategoriesOf(s.Codes).Select(c => new { s.Id, Category = c })), tx, cancellationToken: ct));
        }

        foreach (var chunk in locationRows.Chunk(1000))
        {
            await connection.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO `npi_history_location_set` (`set_id`, `state`, `zip5`) VALUES (@Id, @State, @Zip)",
                chunk, tx, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE `npi_history` h JOIN `{Batch}` b ON b.`npi` = h.`npi` SET h.`to_period` = DATE(@p)
            WHERE h.`to_period` IS NULL AND h.`from_period` < DATE(@p)
            """, param, tx, commandTimeout: 0, cancellationToken: ct));
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            $"INSERT INTO `npi_history` ({string.Join(", ", VersionColumns.Select(c => $"`{c}`"))}) SELECT {string.Join(", ", VersionColumns.Select(c => $"`{c}`"))} FROM `{Batch}`",
            param, tx, commandTimeout: 0, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE `history_period` SET `status` = 'Completed', `file_date` = DATE(@p), `columns_count` = @columns, `rows_read` = @rows,
              `deactivated_rows` = @deactivatedRows, `active` = @active, `versions` = @versions, `new_npis` = @newNpis, `gone` = @gone,
              `zip_unmatched` = @zipUnmatched, `zip_unmatched_providers` = @zipUnmatchedProviders, `stats_done` = 0, `locations` = @locations,
              `secondary_rows` = @secondaryRows, `completed_at` = UTC_TIMESTAMP(),
              `seconds` = @seconds, `error` = NULL
            WHERE `period` = DATE(@period)
            """, new
        {
            param.p, param.period, columns = diff.File.Columns, rows = diff.File.Rows, deactivatedRows = diff.File.DeactivatedRows, active = diff.Active,
            versions = inserted, newNpis = diff.NewNpis, gone = diff.Gone, zipUnmatched, zipUnmatchedProviders, seconds,
            locations = diff.File.HasLocations ? 1 : 0, secondaryRows = diff.File.HasLocations ? diff.File.SecondaryRows : (long?)null,
        }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{Batch}`", CancellationToken.None);
    }

    private static readonly string[] VersionColumns =
    [
        "npi", "from_period", "status", "entity_type", "state", "zip5", "country", "primary_taxonomy", "taxonomy_set", "location_set",
        "enumeration_date", "last_update_date", "deactivation_date", "reactivation_date",
    ];

    // One tab-separated line of a version (\N = NULL; the values are upper-case ASCII without tabs or backslashes).
    internal static void VersionLine(StringBuilder line, int key, in NpiVersion v, HistoryValues d)
    {
        line.Clear();
        static void Text(StringBuilder s, string? value)
        {
            s.Append('\t');
            if (value is null)
            {
                s.Append("\\N");
                return;
            }

            foreach (var c in value)
            {
                s.Append(c is '\t' or '\\' or '\n' or '\r' ? ' ' : c);
            }
        }

        static void Date(StringBuilder s, int day)
        {
            s.Append('\t');
            if (day == 0)
            {
                s.Append("\\N");
            }
            else
            {
                s.Append(DateOnly.FromDayNumber(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
        }

        line.Append(NpiVersion.Npi(key).ToString(CultureInfo.InvariantCulture));
        Date(line, v.FromPeriod);
        line.Append('\t').Append((int)v.Status);
        line.Append('\t').Append(v.Entity == 0 ? "\\N" : v.Entity.ToString(CultureInfo.InvariantCulture));
        Text(line, d.States[v.State]);
        Text(line, v.Zip == 0 ? null : (v.Zip - 1).ToString("D5", CultureInfo.InvariantCulture));
        Text(line, d.Countries[v.Country]);
        Text(line, d.Codes[v.Primary]);
        line.Append('\t').Append(v.TaxonomySet == 0 ? "\\N" : v.TaxonomySet.ToString(CultureInfo.InvariantCulture));
        line.Append('\t').Append(v.LocationSet <= 0 ? "\\N" : v.LocationSet.ToString(CultureInfo.InvariantCulture));
        Date(line, v.Enumeration);
        Date(line, v.LastUpdate);
        Date(line, v.Deactivation);
        Date(line, v.Reactivation);
        line.Append('\n');
    }

    /// <summary>The open versions (and every taxonomy set) read back from MySQL, to go on from the last completed month.</summary>
    internal async Task<HistoryState> LoadStateAsync(CancellationToken ct)
    {
        var dictionary = new HistoryValues();
        await using var connection = await database.OpenAsync(ct);
        foreach (var (id, codes) in await connection.QueryAsync<(uint, string)>(new CommandDefinition(
                     "SELECT `id`, `codes` FROM `npi_history_taxonomy_set`", cancellationToken: ct)))
        {
            dictionary.AddStoredSet((int)id, codes);
        }

        foreach (var set in (await connection.QueryAsync<(uint, string, string)>(new CommandDefinition(
                     "SELECT `set_id`, `state`, `zip5` FROM `npi_history_location_set` ORDER BY `set_id`", cancellationToken: ct)))
                 .GroupBy(r => r.Item1))
        {
            dictionary.AddStoredLocationSet((int)set.Key, set.Select(r => (r.Item2, r.Item3)));
        }

        var listed = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT `rows_read` FROM `history_period` WHERE `status` = 'Completed' ORDER BY `period` DESC LIMIT 1", cancellationToken: ct));
        // Room for the NPIs the last file listed and a year of new ones, so the dictionary doesn't grow (and double) mid-file.
        var state = new HistoryState(dictionary, listed is { } n ? (int)Math.Min(int.MaxValue / 2, n + n / 10 + 1000) : 0);
        await using (var command = new MySqlCommand(
                   """
                   SELECT `npi`, `from_period`, `status`, `entity_type`, `state`, `zip5`, `country`, `primary_taxonomy`, `taxonomy_set`,
                          `enumeration_date`, `last_update_date`, `deactivation_date`, `reactivation_date`, `location_set`
                   FROM `npi_history` WHERE `to_period` IS NULL
                   """, connection) { CommandTimeout = 0 })
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            static int Day(MySqlDataReader r, int i) => r.IsDBNull(i) ? 0 : DateOnly.FromDateTime(r.GetDateTime(i)).DayNumber;
            while (await reader.ReadAsync(ct))
            {
                var zip = reader.IsDBNull(5) ? null : reader.GetString(5);
                state.Load(NpiVersion.Key(reader.GetUInt32(0)), new NpiVersion
                {
                    FromPeriod = Day(reader, 1),
                    Status = (HistoryStatus)reader.GetByte(2),
                    Entity = reader.IsDBNull(3) ? (byte)0 : reader.GetByte(3),
                    State = dictionary.States.Add(reader.IsDBNull(4) ? null : reader.GetString(4)),
                    Zip = zip is { Length: 5 } && int.TryParse(zip, NumberStyles.None, CultureInfo.InvariantCulture, out var z) ? z + 1 : 0,
                    Country = dictionary.Countries.Add(reader.IsDBNull(6) ? null : reader.GetString(6)),
                    Primary = dictionary.Codes.Add(reader.IsDBNull(7) ? null : reader.GetString(7)),
                    TaxonomySet = reader.IsDBNull(8) ? 0 : (int)reader.GetUInt32(8),
                    Enumeration = Day(reader, 9),
                    LastUpdate = Day(reader, 10),
                    Deactivation = Day(reader, 11),
                    Reactivation = Day(reader, 12),
                    LocationSet = reader.IsDBNull(13) ? 0 : (int)reader.GetUInt32(13),
                });
            }
        }

        state.SetLastListed(listed ?? 0);
        return state;
    }

    private async Task<IReadOnlySet<int>> LoadCrosswalkZipsAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return (await connection.QueryAsync<string>(new CommandDefinition("SELECT DISTINCT `zip5` FROM `zip_county`", cancellationToken: ct)))
            .Select(z => int.TryParse(z, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n + 1 : 0)
            .Where(n => n > 0)
            .ToHashSet();
    }

    // Active US practice ZIPs (state of at most 5 characters) that today's HUD crosswalk doesn't have, and the providers there.
    private static (int Zips, int Providers) UnmatchedZips(HistoryState state, IReadOnlySet<int> crosswalk)
    {
        if (crosswalk.Count == 0)
        {
            return (0, 0);
        }

        var zips = new HashSet<int>();
        var providers = 0;
        foreach (var (_, v) in state.All)
        {
            if (v.Status == HistoryStatus.Active && v.Zip != 0 && state.Dictionary.States[v.State] is { Length: <= 5 } && !crosswalk.Contains(v.Zip))
            {
                zips.Add(v.Zip);
                providers++;
            }
        }

        return (zips.Count, providers);
    }

    // Taxonomy codes not in specialty_codes.csv (retired or never listed), counted under Other: the active NPIs holding each.
    private readonly Dictionary<string, int> _unmappedLogged = new(StringComparer.Ordinal);

    private void LogUnmapped(HistoryState state, string what)
    {
        var categories = SpecialtyCategories.Default;
        var unmappedSets = new Dictionary<int, string[]>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, v) in state.All)
        {
            if (v.Status != HistoryStatus.Active || v.TaxonomySet == 0)
            {
                continue;
            }

            if (!unmappedSets.TryGetValue(v.TaxonomySet, out var codes))
            {
                codes = (state.Dictionary.SetCodes(v.TaxonomySet) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(c => !categories.IsMapped(c)).ToArray();
                unmappedSets[v.TaxonomySet] = codes;
            }

            foreach (var code in codes)
            {
                counts[code] = counts.GetValueOrDefault(code) + 1;
            }
        }

        var fresh = counts.Keys.Where(c => !_unmappedLogged.ContainsKey(c)).ToList();
        foreach (var code in counts.Keys)
        {
            _unmappedLogged[code] = Math.Max(_unmappedLogged.GetValueOrDefault(code), counts[code]);
        }

        if (counts.Count > 0)
        {
            log.Information("History {What}: {Codes:N0} taxonomy codes not in the specialty list (counted under Other), held by {Providers:N0} active NPIs{New}: {List}",
                what, counts.Count, counts.Values.Sum(), fresh.Count > 0 ? $" ({fresh.Count} new)" : "",
                string.Join(", ", counts.OrderByDescending(c => c.Value).Take(15).Select(c => $"{c.Key} {c.Value:N0}")));
        }
    }

    private async Task<string> DownloadAsync(Downloader downloader, HistoryFile file, CancellationToken ct) =>
        await downloader.DownloadAsync(file.Url, file.FileName, options.ResolvedHistoryFolder, ct);

    private void DeleteZip(string zip)
    {
        if (options.HistoryKeepZips)
        {
            return;
        }

        try
        {
            File.Delete(zip);
        }
        catch (IOException ex)
        {
            log.Warning(ex, "Could not delete {Zip}", zip);
        }
    }

    // A prefetched download no longer wanted: wait for it to end and delete what it wrote.
    private async Task ForgetAsync(Task<string>? next)
    {
        if (next is null)
        {
            return;
        }

        try
        {
            DeleteZip(await next);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "History: the next file's download ended");
        }
    }

    private async Task<Dictionary<DateOnly, PeriodRow>> ReadPeriodsAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return (await connection.QueryAsync<PeriodRow>(new CommandDefinition(
                "SELECT `period` AS Period, `status` AS Status, `file_date` AS FileDate, `rows_read` AS RowsRead, `stats_done` AS StatsDone FROM `history_period`",
                cancellationToken: ct)))
            .ToDictionary(p => DateOnly.FromDateTime(p.Period));
    }

    private async Task<IReadOnlyDictionary<DateOnly, string>> StatusesAsync(CancellationToken ct) =>
        (await ReadPeriodsAsync(ct)).ToDictionary(p => p.Key, p => p.Value.Status);

    private async Task StartPeriodAsync(HistoryFile file, long bytes, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `history_period` (`period`, `status`, `source`, `file_name`, `source_url`, `bytes`, `started_at`)
            VALUES (DATE(@period), 'Loading', @Source, @FileName, @url, @bytes, UTC_TIMESTAMP()) AS new
            ON DUPLICATE KEY UPDATE `status` = new.`status`, `source` = new.`source`, `file_name` = new.`file_name`, `source_url` = new.`source_url`,
              `bytes` = new.`bytes`, `started_at` = new.`started_at`, `error` = NULL
            """, ct, param: new { period = file.Period.ToDateTime(TimeOnly.MinValue), file.Source, file.FileName, url = file.Url.ToString(), bytes });
    }

    private async Task RecordMissingAsync(DateOnly month, string why, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `history_period` (`period`, `status`, `error`) VALUES (DATE(@month), 'Missing', @why) AS new
            ON DUPLICATE KEY UPDATE `status` = IF(`history_period`.`status` = 'Completed', 'Completed', new.`status`),
              `error` = IF(`history_period`.`status` = 'Completed', `history_period`.`error`, new.`error`)
            """, ct, param: new { month = month.ToDateTime(TimeOnly.MinValue), why });
    }

    private async Task RecordFailedAsync(HistoryFile file, string error, CancellationToken ct)
    {
        try
        {
            await using var connection = await database.OpenAsync(CancellationToken.None);
            await Database.ExecuteAsync(connection,
                """
                INSERT INTO `history_period` (`period`, `status`, `source`, `file_name`, `source_url`, `error`)
                VALUES (DATE(@period), 'Failed', @Source, @FileName, @url, @error) AS new
                ON DUPLICATE KEY UPDATE `status` = IF(`history_period`.`status` = 'Completed', 'Completed', new.`status`), `error` = new.`error`
                """, CancellationToken.None, param: new
                {
                    period = file.Period.ToDateTime(TimeOnly.MinValue), file.Source, file.FileName, url = file.Url.ToString(),
                    error = error.Length > 4000 ? error[..4000] : error,
                });
        }
        catch (Exception ex)
        {
            log.Error(ex, "Could not record the failure of {File} in history_period", file.FileName);
        }
    }

    private async Task<DateTime?> ScalarDateAsync(string sql, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(sql, cancellationToken: ct));
    }

    private static string Key(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
