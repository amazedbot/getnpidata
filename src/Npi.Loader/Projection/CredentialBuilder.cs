using System.Diagnostics;
using Dapper;
using MySqlConnector;
using Npi.Core.Search;
using Npi.Loader.Datasets;
using Npi.Loader.Db;
using Serilog;

namespace Npi.Loader.Projection;

/// <summary>The standardized credentials of every distinct raw value, and the known credentials they came from.</summary>
public sealed record CredentialMap(IReadOnlyDictionary<string, IReadOnlyList<string>> Known, IReadOnlyDictionary<string, IReadOnlyList<string>> ByRaw);

/// <summary>
/// Builds the standardized credential tables (CLAUDE.md §7 Stage 5.5 item 12) from <c>provider.credential</c>:
/// <c>credential_map</c> (raw value → standard credentials), <c>provider_credential</c> (NPI → standard credentials,
/// for display and the credential filter), <c>credential_list</c> (each standard credential with its provider count,
/// for the dropdown) and <c>credential_search</c> (credential × practice location, for credential + location searches). Works on the ~170k distinct raw values, not the ~5M providers; staging + RENAME.
/// After an incremental projection only the queued NPIs' rows are replaced (<see cref="BuilderPending"/>), unless the known
/// credentials or the credentials too rare for the list changed, which rebuilds everything.
/// </summary>
/// <param name="minListed">Credentials held by fewer providers are not listed; their holders get an "Other" row instead.</param>
public sealed class CredentialBuilder(Database database, ILogger log, double minRowRatio, int minProvidersForKnown = CredentialBuilder.MinProvidersForKnown,
    int minListed = CredentialCatalog.MinProviders)
{
    public const string Source = "credentials";

    /// <summary>A spelling becomes a known credential when at least this many providers use it as a credential on its own.</summary>
    public const int MinProvidersForKnown = 25;

    private sealed record RawCount(string Raw, long Providers);

    /// <summary>
    /// Learns the known credentials from the data, then standardizes every raw value. A credential's standard spelling is
    /// its conventional one (PhD, PharmD) or else the most used spelling among those with the same key ("PA-C" over "PAC").
    /// </summary>
    public static CredentialMap Map(IEnumerable<(string Raw, long Providers)> values, int minProviders = MinProvidersForKnown)
    {
        var all = values.ToList();
        var weight = new Dictionary<string, long>(StringComparer.Ordinal);
        var spellings = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        foreach (var (raw, providers) in all)
        {
            foreach (var piece in Credentials.Pieces(raw).Where(p => !p.Contains(' ', StringComparison.Ordinal)))
            {
                var key = Credentials.Key(piece);
                if (key.Length == 0)
                {
                    continue;
                }

                weight[key] = weight.GetValueOrDefault(key) + providers;
                if (!spellings.TryGetValue(key, out var forKey))
                {
                    spellings[key] = forKey = new Dictionary<string, long>(StringComparer.Ordinal);
                }

                forKey[piece] = forKey.GetValueOrDefault(piece) + providers;
            }
        }

        string Spelling(string key) => Credentials.ConventionalSpelling(key)
                                       ?? spellings[key].OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).First().Key;

        // A spelling that is two more common credentials run together ("MS-CCC-SLP", "MSPT", "MD-PHD") is not a credential of
        // its own: it stands for the two ("MS", "CCC-SLP").
        (string Left, string Right)? Split(string key, long w)
        {
            for (var i = 2; i <= key.Length - 2; i++)
            {
                var (left, right) = (key[..i], key[i..]);
                if (weight.GetValueOrDefault(left) > w && weight.GetValueOrDefault(right) > w
                    && weight[left] >= minProviders && weight[right] >= minProviders)
                {
                    return (left, right);
                }
            }

            return null;
        }

        var known = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (key, w) in weight.Where(w => w.Value >= minProviders))
        {
            known[key] = Split(key, w) is var (left, right) ? [Spelling(left), Spelling(right)] : [Spelling(key)];
        }
        var byRaw = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (raw, _) in all)
        {
            byRaw.TryAdd(raw, Credentials.Standardize(raw, k => known.GetValueOrDefault(k))
                .Select(c => c.Length <= 60 ? c : c[..60]).ToList());
        }

        return new CredentialMap(known, byRaw);
    }

    /// <summary>Fills <c>{target}</c> from provider_credential × provider_map (the cell formula is MapBounds.Cells').</summary>
    public static string MapCredentialSql(string target, string credentials, string map) => $"""
        INSERT INTO `{target}` (`credential`, `cell`, `npi`)
        SELECT DISTINCT c.`credential`, FLOOR((m.`lat` + 90) * 10) * 3600 + FLOOR((m.`lon` + 180) * 10), m.`npi`
        FROM `{map}` m JOIN `{credentials}` c ON c.`npi` = m.`npi`
        """;

    /// <summary>True when the projection was rebuilt in full since the credential tables were, or NPIs are queued.</summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await NeedsFullBuildAsync(connection, ct)
               || await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                   "SELECT EXISTS (SELECT 1 FROM `builder_pending` WHERE `builder` = @builder)", new { builder = BuilderPending.Credentials }, cancellationToken: ct)) != 0;
    }

    private static async Task<bool> NeedsFullBuildAsync(MySqlConnection connection, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"""
            SELECT NOT EXISTS (SELECT 1 FROM `reference_data` WHERE `source` = @source)
                OR (SELECT `loaded_at` FROM `reference_data` WHERE `source` = @source) < {BuilderPending.FullProjectedAt}
            """, new { source = Source }, cancellationToken: ct)) != 0;

    // The known credentials as a short hash, kept in reference_data.version: another set means every provider is mapped again.
    private static string KnownHash(CredentialMap map) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",
            map.Known.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + string.Join("|", k.Value))))))[..12];

    private static string Version(CredentialMap map) => $"{map.Known.Count(k => k.Value.Count == 1)} known credentials {KnownHash(map)}";

    /// <param name="full">Rebuild everything (the <c>credentials</c> command); otherwise only the queued NPIs when that is enough.</param>
    public async Task BuildAsync(CancellationToken ct, bool full = false)
    {
        var watch = Stopwatch.StartNew();
        await using var connection = await database.OpenAsync(ct);
        var raws = (await connection.QueryAsync<RawCount>(new CommandDefinition(
            "SELECT `credential` AS Raw, COUNT(*) AS Providers FROM `provider` WHERE `credential` IS NOT NULL GROUP BY `credential`",
            cancellationToken: ct))).ToList();
        var map = Map(raws.Select(r => (r.Raw, r.Providers)), minProvidersForKnown);
        if (!full && !await NeedsFullBuildAsync(connection, ct))
        {
            var stored = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT `version` FROM `reference_data` WHERE `source` = @source", new { source = Source }, cancellationToken: ct));
            if (stored == Version(map))
            {
                try
                {
                    if (await BuildQueuedAsync(connection, map, watch, ct))
                    {
                        return;
                    }
                }
                finally
                {
                    await BuilderPending.DropBatchAsync(connection, BuilderPending.Credentials);
                }
            }
            else
            {
                log.Information("Credentials: the known credentials changed ({Old} → {New}); rebuilding every provider", stored, Version(map));
            }
        }

        await BuildAllAsync(connection, raws, map, watch, ct);
    }

    /// <summary>
    /// Replaces the queued NPIs' rows, with the stored known credentials (unchanged): raw values new to credential_map are
    /// mapped, unused ones dropped. False (nothing changed) when the credentials too rare for the list would change.
    /// </summary>
    private async Task<bool> BuildQueuedAsync(MySqlConnection connection, CredentialMap map, Stopwatch watch, CancellationToken ct)
    {
        var (batch, queued) = await BuilderPending.TakeAsync(connection, BuilderPending.Credentials, ct);
        var stored = (await connection.QueryAsync<string>(new CommandDefinition("SELECT DISTINCT `raw` FROM `credential_map`", cancellationToken: ct)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = map.ByRaw.Where(e => !stored.Contains(e.Key)).ToList();
        var unused = stored.Where(r => !map.ByRaw.ContainsKey(r)).ToList();
        var rareBefore = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT `credential` FROM `credential_list` WHERE `providers` < @minListed AND `credential` <> @other",
            new { minListed, other = Credentials.Other }, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);

        await using var tx = await connection.BeginTransactionAsync(ct);
        Task<int> RunAsync(string sql, object? param = null) =>
            connection.ExecuteAsync(new CommandDefinition(sql, param, tx, commandTimeout: 0, cancellationToken: ct));

        await InsertMapAsync(connection, "credential_map", added, ct, tx);
        foreach (var chunk in unused.Chunk(500))
        {
            await RunAsync("DELETE FROM `credential_map` WHERE `raw` IN @chunk", new { chunk });
        }

        // The batch's rows by their keys: its credentials as stored × its locations then and now, × its map points.
        var locations = $"""
            (SELECT ol.`npi`, ol.`state`, ol.`city`, ol.`zip5` FROM {BuilderPending.OldLocations(batch)} ol
             UNION SELECT l.`npi`, l.`state`, l.`city`, l.`zip5` FROM `provider_location` l JOIN `{batch}` b3 ON b3.`npi` = l.`npi`)
            """;
        await RunAsync(
            $"""
            DELETE s FROM `credential_search` s
            JOIN (SELECT DISTINCT c.`credential`, IFNULL(l.`state`, '') AS `state`, IFNULL(l.`city`, '') AS `city`, IFNULL(l.`zip5`, '') AS `zip5`, c.`npi`
                  FROM `provider_credential` c JOIN `{batch}` b ON b.`npi` = c.`npi` JOIN {locations} l ON l.`npi` = c.`npi`) k
              ON s.`credential` = k.`credential` AND s.`state` = k.`state` AND s.`city` = k.`city` AND s.`zip5` = k.`zip5` AND s.`npi` = k.`npi`
            """);
        await RunAsync(
            $"""
            DELETE s FROM `provider_map_credential` s
            JOIN (SELECT DISTINCT c.`credential`, {MapCell("m")} AS `cell`, m.`npi`
                  FROM `provider_credential` c JOIN `{batch}` b ON b.`npi` = c.`npi` JOIN `provider_map` m ON m.`npi` = c.`npi`) k
              ON s.`credential` = k.`credential` AND s.`cell` = k.`cell` AND s.`npi` = k.`npi`
            """);
        await RunAsync($"DELETE c FROM `provider_credential` c JOIN `{batch}` b ON b.`npi` = c.`npi`");
        await RunAsync(
            $"""
            INSERT INTO `provider_credential` (`npi`, `ord`, `credential`)
            SELECT p.`npi`, m.`ord`, m.`credential` FROM `provider` p JOIN `{batch}` b ON b.`npi` = p.`npi` JOIN `credential_map` m ON m.`raw` = p.`credential`
            """);

        var counts = (await connection.QueryAsync<(string Credential, long Providers)>(new CommandDefinition(
            "SELECT `credential`, COUNT(DISTINCT `npi`) FROM `provider_credential` WHERE `ord` <> @otherOrd GROUP BY `credential`",
            new { otherOrd = Credentials.OtherOrd }, tx, commandTimeout: 0, cancellationToken: ct))).ToList();
        var rareAfter = counts.Where(c => c.Providers < minListed).Select(c => c.Credential).ToHashSet(StringComparer.Ordinal);
        if (!rareAfter.SetEquals(rareBefore))
        {
            await tx.RollbackAsync(ct);
            log.Information("Credentials: the credentials too rare for the list changed; rebuilding every provider");
            return false;
        }

        await RunAsync(
            $"""
            INSERT INTO `provider_credential` (`npi`, `ord`, `credential`)
            SELECT DISTINCT c.`npi`, @otherOrd, @other FROM `provider_credential` c JOIN `{batch}` b ON b.`npi` = c.`npi`
            JOIN `credential_list` r ON r.`credential` = c.`credential` AND r.`providers` < @minListed AND r.`credential` <> @other
            """, new { otherOrd = Credentials.OtherOrd, other = Credentials.Other, minListed });
        await RunAsync("DELETE FROM `credential_list`");
        await RunAsync("INSERT INTO `credential_list` (`credential`, `providers`) SELECT `credential`, COUNT(DISTINCT `npi`) FROM `provider_credential` GROUP BY `credential`");
        await RunAsync(
            $"""
            INSERT INTO `credential_search` (`credential`, `state`, `city`, `zip5`, `npi`)
            SELECT DISTINCT c.`credential`, IFNULL(l.`state`, ''), IFNULL(l.`city`, ''), IFNULL(l.`zip5`, ''), c.`npi`
            FROM `provider_credential` c JOIN `{batch}` b ON b.`npi` = c.`npi` JOIN `provider_location` l ON l.`npi` = c.`npi`
            """);
        await RunAsync(
            $"""
            INSERT INTO `provider_map_credential` (`credential`, `cell`, `npi`)
            SELECT DISTINCT c.`credential`, {MapCell("m")}, m.`npi`
            FROM `provider_credential` c JOIN `{batch}` b ON b.`npi` = c.`npi` JOIN `provider_map` m ON m.`npi` = c.`npi`
            """);
        await BuilderPending.DoneAsync(connection, BuilderPending.Credentials, tx, ct);
        await RunAsync("UPDATE `reference_data` SET `checked_at` = UTC_TIMESTAMP() WHERE `source` = @source", new { source = Source });
        await tx.CommitAsync(ct);
        log.Information("Credentials: {Queued:N0} queued providers updated ({Added:N0} new raw values, {Unused:N0} unused) in {Seconds:N0}s",
            queued, added.Count, unused.Count, watch.Elapsed.TotalSeconds);
        return true;
    }

    // The 0.1° map cell of a point (MapBounds.Cells' formula, Npi.Core).
    private static string MapCell(string alias) => $"FLOOR(({alias}.`lat` + 90) * 10) * 3600 + FLOOR(({alias}.`lon` + 180) * 10)";

    private static async Task InsertMapAsync(MySqlConnection connection, string table, IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> entries,
        CancellationToken ct, MySqlTransaction? tx = null)
    {
        foreach (var chunk in entries.SelectMany(e => e.Value.Select((c, i) => (Raw: e.Key, Ord: i + 1, Credential: c))).Chunk(1000))
        {
            var p = new DynamicParameters();
            var rows = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                rows.Add($"(@r{i}, @o{i}, @c{i})");
                p.Add($"r{i}", chunk[i].Raw);
                p.Add($"o{i}", chunk[i].Ord);
                p.Add($"c{i}", chunk[i].Credential);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                $"INSERT IGNORE INTO `{table}` (`raw`, `ord`, `credential`) VALUES " + string.Join(", ", rows), p, tx, cancellationToken: ct));
        }
    }

    private async Task BuildAllAsync(MySqlConnection connection, List<RawCount> raws, CredentialMap map, Stopwatch watch, CancellationToken ct)
    {

        var counts = await TableSwap.ReplaceAsync(connection, ["credential_map", "provider_credential", "credential_list", "credential_search", "provider_map_credential"], minRowRatio, async () =>
        {
            await InsertMapAsync(connection, "credential_map_staging", map.ByRaw, ct);

            await Database.ExecuteAsync(connection,
                """
                INSERT INTO `provider_credential_staging` (`npi`, `ord`, `credential`)
                SELECT p.`npi`, m.`ord`, m.`credential` FROM `provider` p JOIN `credential_map_staging` m ON m.`raw` = p.`credential`
                """, ct);
            // "Other": everyone holding a credential too rare for the list, so the list can offer them as one entry.
            await Database.ExecuteAsync(connection,
                """
                INSERT INTO `provider_credential_staging` (`npi`, `ord`, `credential`)
                SELECT DISTINCT c.`npi`, @otherOrd, @other
                FROM `provider_credential_staging` c
                JOIN (SELECT `credential` FROM `provider_credential_staging` GROUP BY `credential` HAVING COUNT(DISTINCT `npi`) < @minListed) rare
                  ON rare.`credential` = c.`credential`
                """, ct, param: new { otherOrd = Credentials.OtherOrd, other = Credentials.Other, minListed });
            await Database.ExecuteAsync(connection,
                "INSERT INTO `credential_list_staging` (`credential`, `providers`) SELECT `credential`, COUNT(DISTINCT `npi`) FROM `provider_credential_staging` GROUP BY `credential`",
                ct);
            await Database.ExecuteAsync(connection,
                """
                INSERT INTO `credential_search_staging` (`credential`, `state`, `city`, `zip5`, `npi`)
                SELECT DISTINCT c.`credential`, IFNULL(l.`state`, ''), IFNULL(l.`city`, ''), IFNULL(l.`zip5`, ''), c.`npi`
                FROM `provider_credential_staging` c JOIN `provider_location` l ON l.`npi` = c.`npi`
                """, ct);
            await Database.ExecuteAsync(connection, MapCredentialSql("provider_map_credential_staging", "provider_credential_staging", "provider_map"), ct);
        }, ct);

        var now = DateTime.UtcNow;
        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
            VALUES (@source, @version, '', @rows, @now, @now) AS new
            ON DUPLICATE KEY UPDATE `version` = new.`version`, `rows_loaded` = new.`rows_loaded`, `loaded_at` = new.`loaded_at`, `checked_at` = new.`checked_at`
            """, ct, param: new { source = Source, version = Version(map), rows = counts["provider_credential"], now });
        await BuilderPending.ClearAsync(connection, BuilderPending.Credentials, ct);
        log.Information("Credentials: {Raw:N0} raw values → {Known:N0} known credentials; {Rows:N0} provider credentials, {List:N0} distinct, in {Seconds:N0}s",
            raws.Count, map.Known.Count, counts["provider_credential"], counts["credential_list"], watch.Elapsed.TotalSeconds);
    }
}
