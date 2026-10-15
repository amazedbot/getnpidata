using System.Diagnostics;
using System.Globalization;
using Dapper;
using MySqlConnector;
using Npi.Core.Search;
using Npi.Loader.Db;
using Npi.Loader.Reference;
using Serilog;

namespace Npi.Loader.Projection;

/// <summary>
/// Areas over time (docs/PRODUCT_PLAN.md step 3.3; migration 102): monthly figures per area (US, state, county) in
/// area_month_stat, and the providers behind the change counts in area_month_event. Runs after the area counts and the record
/// quality. Past months are frozen; a build recomputes only the open months (the data's as-of month and the month before), so
/// an event dated in a frozen month counts in the first open one.
/// <list type="bullet">
/// <item>Moves and the places of deactivated providers: <c>area_history_location</c> keeps where each active provider practised
/// at the last build (state, ZIP); the providers the projection queued (or, after a full projection, every provider whose
/// locations differ) are compared with it. An area a provider no longer practises in is a move out (or, when the NPI was
/// deactivated, a deactivation there), a new one a move in; NPIs new to the history (new or reactivated) are not moves.</item>
/// <item>Specialty, license and exclusion changes: the provider_change rows recorded since the last build, in the areas the
/// provider practises in now.</item>
/// <item>New NPIs: NPIs issued in each open month whose holders practise there, per specialty category. Deactivations
/// nationwide: by CMS's deactivation date.</item>
/// <item>Snapshots of the as-of month: the provider counts per category (area_provider_count) and the record-quality mix.</item>
/// </list>
/// The first build derives what today's data can say about the past <see cref="AreaHistoryMath.DerivedMonths"/> months (NPIs
/// issued per month by their holders' current location, US deactivations by date) and every month of the change log;
/// moves and the snapshots start with it.
/// </summary>
public sealed class AreaHistoryBuilder(Database database, ILogger log)
{
    public const string Source = "area_history";

    // SQL: when something the open months read last changed, besides the projection's queue, a full projection and the change
    // log (checked on their own): the area counts (the as-of month's provider counts and the code → category table), the
    // record-quality labels (its quality mix; a relabel that changed nothing doesn't count, speed item 18), and the NUCC codes,
    // HUD's ZIP → county crosswalk and Census's counties (every state and county gets a count). The NPPES archive (step 3.4)
    // isn't one: it writes its own months (basis 3) and removes the derived rows under them itself, which a build leaves as they are.
    private const string InputsChangedAt =
        $"""
        GREATEST(COALESCE((SELECT GREATEST(`loaded_at`, `checked_at`) FROM `reference_data` WHERE `source` = '{AreaCountBuilder.Source}'), '1970-01-01'),
                 {QualityBuilder.LabelsChangedAt},
                 COALESCE((SELECT MAX(`loaded_at`) FROM `reference_data`
                           WHERE `source` IN ('{ReferenceLoader.Nucc}', '{ReferenceLoader.Hud}', '{ReferenceLoader.Gazetteer}')), '1970-01-01'))
        """;

    private const string Snapshot = "area_history_location";
    private const string OldAreas = "area_history_old";
    private const string NewAreas = "area_history_new";
    private const string Changes = "area_history_changes";
    private const string Stats = "area_history_stats";
    private static string Batch => BuilderPending.BatchTable(BuilderPending.AreaHistory);
    private static readonly string[] Scratch = [OldAreas, NewAreas, Changes, Stats];

    // A practice location that counts in an area: a state of at most 5 characters (as the area counts), by ZIP for the county.
    private static string IsLocated(string alias) => $"{alias}.`state` IS NOT NULL AND CHAR_LENGTH({alias}.`state`) <= 5";

    private static string Month(string date) => $"CAST(DATE_FORMAT({date}, '%Y-%m-01') AS DATE)";

    // The month an event counts in: its own, else the first open month when its own is frozen, or the as-of month without a date.
    private static string EventMonth(string date) => $"COALESCE(GREATEST({Month(date)}, DATE(@openFrom)), DATE(@asOfMonth))";

    private sealed record Meta(DateTime? DerivedFrom, DateTime? TrackedFrom, DateTime? ChangesFrom, DateTime? AsOfMonth, long LastChangeId, DateTime? ArchiveTo);

    /// <summary>
    /// True when the projection queued providers or was rebuilt in full, an input changed (<see cref="InputsChangedAt"/>), the
    /// change log has a row of a kind the history counts, or the month moved on. Otherwise a build would write the same rows
    /// again (1.19M in 347 s on full data), and <c>run</c> skips it.
    /// </summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"""
            SELECT COALESCE(NOT EXISTS (SELECT 1 FROM `reference_data` WHERE `source` = @source)
                OR NOT EXISTS (SELECT 1 FROM `area_history_meta` WHERE `id` = 1)
                OR EXISTS (SELECT 1 FROM `builder_pending` WHERE `builder` = @builder)
                OR (SELECT `loaded_at` FROM `reference_data` WHERE `source` = @source) < {BuilderPending.FullProjectedAt}
                OR (SELECT `checked_at` FROM `reference_data` WHERE `source` = @source) < {InputsChangedAt}
                OR EXISTS (SELECT 1 FROM `provider_change`
                           WHERE `id` > (SELECT `last_change_id` FROM `area_history_meta` WHERE `id` = 1) AND `change_type` IN ({AreaChangeKinds.ChangeTypeSqlList}))
                OR NOT ((SELECT `as_of_month` FROM `area_history_meta` WHERE `id` = 1)
                        <=> (SELECT {Month("`as_of_date`")} FROM `data_version` WHERE `id` = 1)), 1)
            """, new { source = Source, builder = BuilderPending.AreaHistory }, cancellationToken: ct)) != 0;
    }

    /// <param name="full">Compare every provider's locations with the history's (the <c>area-history full</c> command); otherwise
    /// only the providers the projection queued, unless it was rebuilt in full since (which clears the queue).</param>
    /// <returns>The rows of area_month_stat.</returns>
    public async Task<long> BuildAsync(CancellationToken ct, bool full = false)
    {
        var watch = Stopwatch.StartNew();
        await using var connection = await database.OpenAsync(ct);
        if (await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT `as_of_date` FROM `data_version` WHERE `id` = 1", cancellationToken: ct)) is not { } asOf)
        {
            log.Information("Area history: no data loaded yet");
            return 0;
        }

        var asOfMonth = AreaHistoryMath.MonthOf(asOf);
        var meta = await connection.QuerySingleOrDefaultAsync<Meta>(new CommandDefinition(
            """
            SELECT `derived_from` AS DerivedFrom, `tracked_from` AS TrackedFrom, `changes_from` AS ChangesFrom, `as_of_month` AS AsOfMonth,
                   `last_change_id` AS LastChangeId, `archive_to` AS ArchiveTo
            FROM `area_history_meta` WHERE `id` = 1
            """, cancellationToken: ct));
        // as_of_month is written only when a build's months are in place: a first build that stopped half-way starts again.
        var first = meta?.TrackedFrom is null || meta.AsOfMonth is null;
        var fullDiff = full || first || await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"""
            SELECT NOT EXISTS (SELECT 1 FROM `reference_data` WHERE `source` = @source)
                OR (SELECT `loaded_at` FROM `reference_data` WHERE `source` = @source) < {BuilderPending.FullProjectedAt}
            """, new { source = Source }, cancellationToken: ct)) != 0;

        // The first build derives the past and starts the recorded series; later builds recompute only the open months.
        var trackedFrom = first ? asOfMonth : AreaHistoryMath.MonthOf(meta!.TrackedFrom!.Value);
        var derivedFrom = first ? AreaHistoryMath.DerivedFrom(asOfMonth) : meta!.DerivedFrom is { } derived ? AreaHistoryMath.MonthOf(derived) : trackedFrom;
        var openFrom = AreaHistoryMath.OpenFrom(asOfMonth);
        var from = first ? derivedFrom : openFrom;
        var param = new DynamicParameters(new
        {
            asOfMonth = asOfMonth.ToDateTime(TimeOnly.MinValue),
            openFrom = (first ? new DateOnly(1000, 1, 1) : openFrom).ToDateTime(TimeOnly.MinValue),
            from = from.ToDateTime(TimeOnly.MinValue),
            trackedFrom = trackedFrom.ToDateTime(TimeOnly.MinValue),
            derivedFrom = derivedFrom.ToDateTime(TimeOnly.MinValue),
            // The months the NPPES archive covers (step 3.4, basis 3) before the recorded series: its rows are kept there.
            archiveTo = (meta?.ArchiveTo is { } archived ? AreaHistoryMath.MonthOf(archived) : new DateOnly(1000, 1, 1)).ToDateTime(TimeOnly.MinValue),
        });
        Task<int> RunAsync(string sql, MySqlTransaction? tx = null) =>
            connection.ExecuteAsync(new CommandDefinition(sql, param, tx, commandTimeout: 0, cancellationToken: ct));

        try
        {
            var events = first ? await StartAsync(connection, RunAsync, ct) : await CompareLocationsAsync(connection, RunAsync, fullDiff, ct);
            var changes = await CountChangesAsync(connection, RunAsync, meta, param, first, ct);
            var rows = await WriteStatsAsync(connection, RunAsync, asOfMonth, from, trackedFrom, first, ct);

            // MySQL's clock, or the inputs' time when later: the area counts record DateTime.UtcNow, which a DATETIME column rounds
            // up to the next second, so a build in the same second would otherwise look older than them (IsStaleAsync).
            var at = await connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(
                $"SELECT GREATEST(UTC_TIMESTAMP(), {InputsChangedAt})", cancellationToken: ct));
            await Database.ExecuteAsync(connection,
                """
                INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
                VALUES (@source, @version, '', @rows, @at, @at) AS new
                ON DUPLICATE KEY UPDATE `version` = new.`version`, `rows_loaded` = new.`rows_loaded`, `checked_at` = new.`checked_at`,
                  `loaded_at` = IF(@fullDiff, new.`loaded_at`, `reference_data`.`loaded_at`)
                """, ct, param: new { source = Source, version = "as of " + asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), rows, fullDiff, at });
            log.Information("Area history: {Events:N0} moves and deactivations, {Changes:N0} recorded changes, {Rows:N0} monthly rows ({Months}) in {Seconds:N0}s",
                events, changes, rows, first ? $"derived from {AreaHistoryMath.MonthKey(derivedFrom)}" : $"from {AreaHistoryMath.MonthKey(openFrom)}",
                watch.Elapsed.TotalSeconds);
            return rows;
        }
        finally
        {
            await BuilderPending.DropBatchAsync(connection, BuilderPending.AreaHistory);
            foreach (var table in Scratch)
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{table}`", CancellationToken.None);
            }
        }
    }

    // The first build: where everyone practises now, and the series' first months. Nothing to compare with yet: no moves.
    private static async Task<long> StartAsync(MySqlConnection connection, Func<string, MySqlTransaction?, Task<int>> run, CancellationToken ct)
    {
        await run($"DELETE FROM `{Snapshot}`", null);
        await run($"DELETE FROM `area_month_event`", null);
        await run(
            $"""
            INSERT IGNORE INTO `{Snapshot}` (`npi`, `state`, `zip5`)
            SELECT l.`npi`, l.`state`, IFNULL(l.`zip5`, '') FROM `provider_location` l WHERE {IsLocated("l")}
            """, null);
        await run(
            $"""
            INSERT INTO `area_history_meta` (`id`, `derived_from`, `tracked_from`, `changes_from`, `as_of_month`, `last_change_id`)
            VALUES (1, DATE(@derivedFrom), DATE(@trackedFrom),
                    COALESCE((SELECT {Month("MIN(`detected_at`)")} FROM `provider_change` WHERE `change_type` IN ({AreaChangeKinds.ChangeTypeSqlList})), DATE(@trackedFrom)),
                    NULL, 0) AS new
            ON DUPLICATE KEY UPDATE `derived_from` = new.`derived_from`, `tracked_from` = new.`tracked_from`, `changes_from` = new.`changes_from`,
              `as_of_month` = NULL, `last_change_id` = 0
            """, null);
        await BuilderPending.ClearAsync(connection, BuilderPending.AreaHistory, ct);
        return 0;
    }

    /// <summary>
    /// Compares the providers' locations with the history's: the queued providers, or with <paramref name="fullDiff"/> every
    /// provider whose (state, ZIP) set differs. Records the moves and deactivations per state and county, and moves the
    /// history's locations on, in one transaction with the queue.
    /// </summary>
    private async Task<long> CompareLocationsAsync(MySqlConnection connection, Func<string, MySqlTransaction?, Task<int>> run, bool fullDiff,
        CancellationToken ct)
    {
        long compared;
        if (fullDiff)
        {
            await run($"DROP TABLE IF EXISTS `{Batch}`", null);
            await run($"CREATE TABLE `{Batch}` (`npi` CHAR(10) NOT NULL, PRIMARY KEY (`npi`)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci", null);
            // Each side read once, every row checked by its key on the other: a few minutes on full data.
            await run(
                $"""
                INSERT IGNORE INTO `{Batch}` (`npi`)
                SELECT s.`npi` FROM `{Snapshot}` s
                WHERE NOT EXISTS (SELECT 1 FROM `provider_location` l WHERE l.`npi` = s.`npi` AND l.`state` = s.`state` AND IFNULL(l.`zip5`, '') = s.`zip5`)
                """, null);
            await run(
                $"""
                INSERT IGNORE INTO `{Batch}` (`npi`)
                SELECT l.`npi` FROM `provider_location` l
                WHERE {IsLocated("l")} AND NOT EXISTS (SELECT 1 FROM `{Snapshot}` s WHERE s.`npi` = l.`npi` AND s.`state` = l.`state` AND s.`zip5` = IFNULL(l.`zip5`, ''))
                """, null);
            compared = await Database.CountAsync(connection, Batch, ct);
        }
        else
        {
            (_, compared) = await BuilderPending.TakeAsync(connection, BuilderPending.AreaHistory, ct);
        }

        foreach (var table in new[] { OldAreas, NewAreas })
        {
            await run($"DROP TABLE IF EXISTS `{table}`", null);
            await run(
                $"""
                CREATE TABLE `{table}` (`npi` CHAR(10) NOT NULL, `area_type` VARCHAR(6) NOT NULL, `area_id` VARCHAR(5) NOT NULL,
                  PRIMARY KEY (`npi`, `area_type`, `area_id`)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
                """, null);
        }

        // The batch's states and counties before (the history's locations) and now (the projection's).
        await run($"INSERT IGNORE INTO `{OldAreas}` SELECT s.`npi`, 'state', s.`state` FROM `{Snapshot}` s JOIN `{Batch}` b ON b.`npi` = s.`npi`", null);
        await run(
            $"""
            INSERT IGNORE INTO `{OldAreas}` SELECT s.`npi`, 'county', z.`county_fips`
            FROM `{Snapshot}` s JOIN `{Batch}` b ON b.`npi` = s.`npi` JOIN `zip_county` z ON z.`zip5` = s.`zip5`
            """, null);
        await run(
            $"""
            INSERT IGNORE INTO `{NewAreas}` SELECT l.`npi`, 'state', l.`state`
            FROM `provider_location` l JOIN `{Batch}` b ON b.`npi` = l.`npi` WHERE {IsLocated("l")}
            """, null);
        await run(
            $"""
            INSERT IGNORE INTO `{NewAreas}` SELECT l.`npi`, 'county', z.`county_fips`
            FROM `provider_location` l JOIN `{Batch}` b ON b.`npi` = l.`npi` JOIN `zip_county` z ON z.`zip5` = l.`zip5` WHERE {IsLocated("l")}
            """, null);

        await using var tx = await connection.BeginTransactionAsync(ct);
        static string NotIn(string table, string alias) =>
            $"NOT EXISTS (SELECT 1 FROM `{table}` x WHERE x.`npi` = {alias}.`npi` AND x.`area_type` = {alias}.`area_type` AND x.`area_id` = {alias}.`area_id`)";
        const string insert = "INSERT IGNORE INTO `area_month_event` (`area_type`, `area_id`, `month`, `kind`, `npi`)";
        // Moved out: an area the active provider no longer practises in. Moved in: a new area of a provider the history knew
        // (a new or reactivated NPI is counted as new, not as a move). Deactivated: the areas of a provider now deactivated.
        var events = await run(
            $"""
            {insert}
            SELECT o.`area_type`, o.`area_id`, {EventMonth("p.`last_update_date`")}, '{AreaChangeKinds.MovedOut}', o.`npi`
            FROM `{OldAreas}` o JOIN `provider` p ON p.`npi` = o.`npi` WHERE {NotIn(NewAreas, "o")}
            """, tx);
        events += await run(
            $"""
            {insert}
            SELECT n.`area_type`, n.`area_id`, {EventMonth("p.`last_update_date`")}, '{AreaChangeKinds.MovedIn}', n.`npi`
            FROM `{NewAreas}` n JOIN `provider` p ON p.`npi` = n.`npi`
            WHERE EXISTS (SELECT 1 FROM `{OldAreas}` h WHERE h.`npi` = n.`npi`) AND {NotIn(OldAreas, "n")}
            """, tx);
        events += await run(
            $"""
            {insert}
            SELECT o.`area_type`, o.`area_id`, {EventMonth("d.`deactivation_date`")}, '{AreaChangeKinds.Deactivated}', o.`npi`
            FROM `{OldAreas}` o JOIN `provider_deactivated` d ON d.`npi` = o.`npi`
            WHERE NOT EXISTS (SELECT 1 FROM `provider` p WHERE p.`npi` = o.`npi`)
            """, tx);
        await run($"DELETE s FROM `{Snapshot}` s JOIN `{Batch}` b ON b.`npi` = s.`npi`", tx);
        await run(
            $"""
            INSERT IGNORE INTO `{Snapshot}` (`npi`, `state`, `zip5`)
            SELECT l.`npi`, l.`state`, IFNULL(l.`zip5`, '') FROM `provider_location` l JOIN `{Batch}` b ON b.`npi` = l.`npi` WHERE {IsLocated("l")}
            """, tx);
        if (!fullDiff)
        {
            await BuilderPending.DoneAsync(connection, BuilderPending.AreaHistory, tx, ct);
        }

        await tx.CommitAsync(ct);
        if (fullDiff)
        {
            await BuilderPending.ClearAsync(connection, BuilderPending.AreaHistory, ct);
        }
        else
        {
            await BuilderPending.CleanAsync(connection, null, ct);
        }

        log.Information("Area history: {Compared:N0} providers' locations compared{Full}", compared, fullDiff ? " (every provider)" : "");
        return events;
    }

    /// <summary>
    /// The specialty, license and exclusion changes recorded since the last build (or all, the first time), in every area the
    /// provider practises in now, with the change log position, in one transaction.
    /// </summary>
    private static async Task<long> CountChangesAsync(MySqlConnection connection, Func<string, MySqlTransaction?, Task<int>> run, Meta? meta,
        DynamicParameters param, bool first, CancellationToken ct)
    {
        var last = first ? 0 : meta!.LastChangeId;
        var max = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COALESCE(MAX(`id`), 0) FROM `provider_change`", cancellationToken: ct));
        param.Add("lastId", last);
        param.Add("maxId", max);
        await run($"DROP TABLE IF EXISTS `{Changes}`", null);
        await run(
            $"""
            CREATE TABLE `{Changes}` (`npi` CHAR(10) NOT NULL, `kind` VARCHAR(12) NOT NULL, `month` DATE NOT NULL,
              PRIMARY KEY (`npi`, `kind`, `month`)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
            """, null);
        var changes = await run(
            $"""
            INSERT IGNORE INTO `{Changes}` (`npi`, `kind`, `month`)
            SELECT c.`npi`, {AreaChangeKinds.KindSql("c.`change_type`")}, {EventMonth("c.`detected_at`")}
            FROM `provider_change` c
            WHERE c.`id` > @lastId AND c.`id` <= @maxId AND c.`change_type` IN ({AreaChangeKinds.ChangeTypeSqlList})
            """, null);

        await using var tx = await connection.BeginTransactionAsync(ct);
        const string insert = "INSERT IGNORE INTO `area_month_event` (`area_type`, `area_id`, `month`, `kind`, `npi`)";
        await run(
            $"""
            {insert} SELECT 'us', 'US', x.`month`, x.`kind`, x.`npi` FROM `{Changes}` x
            WHERE EXISTS (SELECT 1 FROM `provider_location` l WHERE l.`npi` = x.`npi` AND {IsLocated("l")})
            """, tx);
        await run(
            $"""
            {insert} SELECT DISTINCT 'state', l.`state`, x.`month`, x.`kind`, x.`npi`
            FROM `{Changes}` x JOIN `provider_location` l ON l.`npi` = x.`npi` WHERE {IsLocated("l")}
            """, tx);
        await run(
            $"""
            {insert} SELECT DISTINCT 'county', z.`county_fips`, x.`month`, x.`kind`, x.`npi`
            FROM `{Changes}` x JOIN `provider_location` l ON l.`npi` = x.`npi` JOIN `zip_county` z ON z.`zip5` = l.`zip5` WHERE {IsLocated("l")}
            """, tx);
        await run("UPDATE `area_history_meta` SET `last_change_id` = @maxId WHERE `id` = 1", tx);
        await tx.CommitAsync(ct);
        return changes;
    }

    /// <summary>
    /// Recomputes every month from <paramref name="from"/> into a scratch table and replaces those months in one transaction.
    /// The snapshots of earlier builds in an open month (providers, the quality mix) are kept; the as-of month's are taken now.
    /// </summary>
    private async Task<long> WriteStatsAsync(MySqlConnection connection, Func<string, MySqlTransaction?, Task<int>> run, DateOnly asOfMonth,
        DateOnly from, DateOnly trackedFrom, bool first, CancellationToken ct)
    {
        await run($"DROP TABLE IF EXISTS `{Stats}`", null);
        await run(
            $"""
            CREATE TABLE `{Stats}` (
              `area_type` VARCHAR(6) NOT NULL, `area_id` VARCHAR(5) NOT NULL, `category` VARCHAR(80) NOT NULL, `subcategory` VARCHAR(80) NOT NULL DEFAULT '',
              `month` DATE NOT NULL, `providers` INT NULL, `new_providers` INT NULL, `deactivated` INT NULL, `moved_in` INT NULL, `moved_out` INT NULL,
              `specialty_changes` INT NULL, `license_changes` INT NULL, `exclusion_changes` INT NULL,
              `quality_current` INT NULL, `quality_check` INT NULL, `quality_stale` INT NULL,
              PRIMARY KEY (`area_type`, `area_id`, `category`, `subcategory`, `month`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
            """, null);
        const string keys = "`area_type`, `area_id`, `category`, `subcategory`, `month`";
        string Upsert(string columns, string select, string update) =>
            $"INSERT INTO `{Stats}` ({keys}, {columns}) SELECT * FROM ({select}) x ON DUPLICATE KEY UPDATE {update}";

        // Snapshots taken by earlier builds in the open months (none before the first build).
        await run(first ? "SELECT 1" :
            $"""
            INSERT INTO `{Stats}` ({keys}, `providers`, `quality_current`, `quality_check`, `quality_stale`)
            SELECT {keys}, `providers`, `quality_current`, `quality_check`, `quality_stale` FROM `area_month_stat`
            WHERE `month` >= DATE(@from) AND `basis` <> 3 AND (`providers` IS NOT NULL OR `quality_current` IS NOT NULL)
            """, null);

        // NPIs issued per month, by where their holders practise now: in total, then per category.
        static string Issued(string joins) => $"`provider` p JOIN `provider_location` l ON l.`npi` = p.`npi` {joins} WHERE p.`enumeration_date` >= DATE(@from) AND {IsLocated("l")}";
        const string byCategory = "JOIN `provider_taxonomy` t ON t.`npi` = p.`npi` JOIN (SELECT DISTINCT `code`, `category` FROM `specialty_code_category`) mc ON mc.`code` = t.`taxonomy_code`";
        foreach (var (category, join) in new[] { ("''", ""), ("mc.`category`", byCategory) })
        {
            foreach (var (type, id, areaJoin) in new[] { ("us", "'US'", ""), ("state", "l.`state`", ""), ("county", "z.`county_fips`", "JOIN `zip_county` z ON z.`zip5` = l.`zip5`") })
            {
                var groups = string.Join(", ", new[] { type == "us" ? null : id, category == "''" ? null : category, "m" }.OfType<string>());
                await run(Upsert("`new_providers`",
                    $"""
                    SELECT '{type}' AS t, {id} AS i, {category} AS c, '' AS s, {Month("p.`enumeration_date`")} AS m, COUNT(DISTINCT p.`npi`) AS n
                    FROM {Issued($"{areaJoin} {join}")}
                    GROUP BY {groups}
                    """, "`new_providers` = x.n"), null);
            }
        }

        // Moves, deactivations and changes from the recorded events; moves aren't counted for the US.
        await run(Upsert("`moved_in`, `moved_out`, `deactivated`, `specialty_changes`, `license_changes`, `exclusion_changes`",
            $"""
            SELECT `area_type` AS t, `area_id` AS i, '' AS c, '' AS s, `month` AS m,
              IF(`area_type` = 'us', NULL, SUM(`kind` = '{AreaChangeKinds.MovedIn}')) AS mi, IF(`area_type` = 'us', NULL, SUM(`kind` = '{AreaChangeKinds.MovedOut}')) AS mo,
              IF(`area_type` = 'us', NULL, SUM(`kind` = '{AreaChangeKinds.Deactivated}')) AS d,
              SUM(`kind` = '{AreaChangeKinds.Specialty}') AS sc, SUM(`kind` = '{AreaChangeKinds.License}') AS lc, SUM(`kind` = '{AreaChangeKinds.Exclusion}') AS ec
            FROM `area_month_event` WHERE `month` >= DATE(@from)
            GROUP BY `area_type`, `area_id`, `month`
            """,
            "`moved_in` = x.mi, `moved_out` = x.mo, `deactivated` = x.d, `specialty_changes` = x.sc, `license_changes` = x.lc, `exclusion_changes` = x.ec"), null);

        // Deactivations nationwide by CMS's date: known for every deactivated NPI, not only those the loader saw go.
        await run(Upsert("`deactivated`",
            $"""
            SELECT 'us' AS t, 'US' AS i, '' AS c, '' AS s, {Month("d.`deactivation_date`")} AS m, COUNT(*) AS n
            FROM `provider_deactivated` d WHERE d.`deactivation_date` >= DATE(@from) GROUP BY m
            """, "`deactivated` = x.n"), null);

        // The as-of month's provider counts, taken again: a category no longer counted there drops out, and every state and
        // county gets a count, 0 when nobody practises there (so a month with a count is told from one without).
        await run($"UPDATE `{Stats}` SET `providers` = NULL WHERE `month` = DATE(@asOfMonth)", null);
        await run(Upsert("`providers`",
            """
            SELECT 'us' AS t, 'US' AS i, '' AS c, '' AS s, DATE(@asOfMonth) AS m, 0 AS n
            UNION ALL SELECT DISTINCT 'state', `state`, '', '', DATE(@asOfMonth), 0 FROM `county`
            UNION ALL SELECT 'county', `county_fips`, '', '', DATE(@asOfMonth), 0 FROM `county`
            """, "`providers` = x.n"), null);
        await run(Upsert("`providers`",
            "SELECT `area_type` AS t, `area_id` AS i, `category` AS c, '' AS s, DATE(@asOfMonth) AS m, `providers` AS n FROM `area_provider_count` WHERE `subcategory` = ''",
            "`providers` = x.n"), null);

        // The as-of month's record-quality mix: taken again when the labels changed since, or not taken yet this month.
        var qualityDue = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"""
            SELECT NOT EXISTS (SELECT 1 FROM `{Stats}` WHERE `month` = DATE(@asOfMonth) AND `quality_current` IS NOT NULL)
                OR {QualityBuilder.LabelsChangedAt} > COALESCE((SELECT `checked_at` FROM `reference_data` WHERE `source` = @source), '1970-01-01')
            """, new { asOfMonth = asOfMonth.ToDateTime(TimeOnly.MinValue), source = Source }, cancellationToken: ct)) != 0;
        if (qualityDue)
        {
            await run($"UPDATE `{Stats}` SET `quality_current` = NULL, `quality_check` = NULL, `quality_stale` = NULL WHERE `month` = DATE(@asOfMonth)", null);
            const string mix = "COUNT(DISTINCT IF(q.`label` = 1, q.`npi`, NULL)) AS qc, COUNT(DISTINCT IF(q.`label` = 2, q.`npi`, NULL)) AS qk, COUNT(DISTINCT IF(q.`label` = 3, q.`npi`, NULL)) AS qs";
            const string located = "q.`state` <> '' AND CHAR_LENGTH(q.`state`) <= 5";
            foreach (var select in new[]
                     {
                         $"SELECT 'us' AS t, 'US' AS i, '' AS c, '' AS s, DATE(@asOfMonth) AS m, {mix} FROM `quality_search` q WHERE {located}",
                         $"SELECT 'state' AS t, q.`state` AS i, '' AS c, '' AS s, DATE(@asOfMonth) AS m, {mix} FROM `quality_search` q WHERE {located} GROUP BY q.`state`",
                         $"SELECT 'county' AS t, z.`county_fips` AS i, '' AS c, '' AS s, DATE(@asOfMonth) AS m, {mix} FROM `quality_search` q JOIN `zip_county` z ON z.`zip5` = q.`zip5` WHERE {located} GROUP BY z.`county_fips`",
                     })
            {
                await run(Upsert("`quality_current`, `quality_check`, `quality_stale`", select,
                    "`quality_current` = x.qc, `quality_check` = x.qk, `quality_stale` = x.qs"), null);
            }
        }

        await using var tx = await connection.BeginTransactionAsync(ct);
        // The archive's rows (basis 3) stay in its months before the recorded series; from the series' first month the recorded figures win.
        await run(first ? "DELETE FROM `area_month_stat` WHERE `basis` <> 3" : "DELETE FROM `area_month_stat` WHERE `month` >= DATE(@from) AND `basis` <> 3", tx);
        await run("DELETE FROM `area_month_stat` WHERE `basis` = 3 AND `month` >= DATE(@trackedFrom)", tx);
        await run(
            $"""
            INSERT INTO `area_month_stat` ({keys}, `basis`, `providers`, `new_providers`, `deactivated`, `moved_in`, `moved_out`,
              `specialty_changes`, `license_changes`, `exclusion_changes`, `quality_current`, `quality_check`, `quality_stale`)
            SELECT {keys}, IF(`month` < DATE(@trackedFrom), 1, 2), `providers`, `new_providers`, `deactivated`, `moved_in`, `moved_out`,
              `specialty_changes`, `license_changes`, `exclusion_changes`, `quality_current`, `quality_check`, `quality_stale`
            FROM `{Stats}`
            WHERE COALESCE(`providers`, `new_providers`, `deactivated`, `moved_in`, `moved_out`, `specialty_changes`, `license_changes`,
                           `exclusion_changes`, `quality_current`, `quality_check`, `quality_stale`) IS NOT NULL
              AND NOT (`month` < DATE(@trackedFrom) AND `month` <= DATE(@archiveTo))
            """, tx);
        await run("UPDATE `area_history_meta` SET `as_of_month` = DATE(@asOfMonth) WHERE `id` = 1", tx);
        await tx.CommitAsync(ct);
        log.Debug("Area history: months from {From} replaced (record quality {Quality}; series recorded from {Tracked})",
            AreaHistoryMath.MonthKey(from), qualityDue ? "taken again" : "kept", AreaHistoryMath.MonthKey(trackedFrom));
        return await Database.CountAsync(connection, "area_month_stat", ct);
    }
}
