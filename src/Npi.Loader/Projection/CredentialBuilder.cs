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

    /// <summary>True when the projection was rebuilt since the credential tables were.</summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT NOT EXISTS (SELECT 1 FROM `reference_data` WHERE `source` = @source)
                OR (SELECT `loaded_at` FROM `reference_data` WHERE `source` = @source)
                   < COALESCE((SELECT `projected_at` FROM `data_version` WHERE `id` = 1), '1970-01-01')
            """, new { source = Source }, cancellationToken: ct)) != 0;
    }

    public async Task BuildAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        await using var connection = await database.OpenAsync(ct);
        var raws = (await connection.QueryAsync<RawCount>(new CommandDefinition(
            "SELECT `credential` AS Raw, COUNT(*) AS Providers FROM `provider` WHERE `credential` IS NOT NULL GROUP BY `credential`",
            cancellationToken: ct))).ToList();
        var map = Map(raws.Select(r => (r.Raw, r.Providers)), minProvidersForKnown);

        var counts = await TableSwap.ReplaceAsync(connection, ["credential_map", "provider_credential", "credential_list", "credential_search", "provider_map_credential"], minRowRatio, async () =>
        {
            foreach (var chunk in map.ByRaw.SelectMany(e => e.Value.Select((c, i) => (Raw: e.Key, Ord: i + 1, Credential: c))).Chunk(1000))
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

                await Database.ExecuteAsync(connection, "INSERT INTO `credential_map_staging` (`raw`, `ord`, `credential`) VALUES " + string.Join(", ", rows), ct, param: p);
            }

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
            """, ct, param: new { source = Source, version = $"{map.Known.Count(k => k.Value.Count == 1)} known credentials", rows = counts["provider_credential"], now });
        log.Information("Credentials: {Raw:N0} raw values → {Known:N0} known credentials; {Rows:N0} provider credentials, {List:N0} distinct, in {Seconds:N0}s",
            raws.Count, map.Known.Count, counts["provider_credential"], counts["credential_list"], watch.Elapsed.TotalSeconds);
    }
}
