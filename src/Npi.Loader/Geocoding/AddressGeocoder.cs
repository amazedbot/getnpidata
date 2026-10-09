using System.Diagnostics;
using Dapper;
using MySqlConnector;
using Npi.Loader.Db;
using Serilog;

namespace Npi.Loader.Geocoding;

/// <summary>What one geocoding pass did.</summary>
public sealed record GeocodeOutcome(long Pending, long Geocoded, long Matched, bool Ok);

/// <summary>
/// Geocodes the practice street addresses that aren't in <c>address_geocode</c> yet (CLAUDE.md §7 Stage 5.5 item 10).
/// The backlog is snapshotted into <c>geocode_pending</c> (one scan of provider_location), then sent to the Census
/// geocoder in batches, a few at a time, and every answer is stored at once, so an interrupted run resumes where it
/// stopped. Addresses whose batch failed stay pending for the next run.
/// </summary>
public sealed class AddressGeocoder(Database database, CensusGeocoder census, ILogger log, int batchSize, int parallelism)
{
    private const int MaxFailedRounds = 3;

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    /// <summary>Delays between attempts at one batch; tests set it to zero.</summary>
    public IReadOnlyList<TimeSpan> Delays { get; init; } = RetryDelays;

    /// <param name="maxBatches">Stop after this many batches (0 = the whole backlog).</param>
    public async Task<GeocodeOutcome> GeocodePendingAsync(int maxBatches, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await Database.ExecuteAsync(connection, "DROP TABLE IF EXISTS `geocode_pending`", ct);
        await Database.ExecuteAsync(connection,
            """
            CREATE TABLE `geocode_pending` (
              `addr_key` CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
              `street` VARCHAR(100) NOT NULL, `city` VARCHAR(60) NULL, `state` VARCHAR(40) NOT NULL, `zip5` CHAR(5) NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            SELECT l.`addr_key`, MIN(l.`address1`) AS `street`, MIN(l.`city`) AS `city`, MIN(l.`state`) AS `state`, MIN(l.`zip5`) AS `zip5`
            FROM `provider_location` l
            LEFT JOIN `address_geocode` g ON g.`addr_key` = l.`addr_key`
            WHERE l.`addr_key` IS NOT NULL AND g.`addr_key` IS NULL
            GROUP BY l.`addr_key`
            """, ct);
        var pending = await Database.CountAsync(connection, "geocode_pending", ct);
        var size = Math.Clamp(batchSize, 1, CensusGeocoder.MaxBatch);
        var planned = maxBatches > 0 ? Math.Min(pending, (long)maxBatches * size) : pending;
        log.Information("Geocoding {Planned:N0} of {Pending:N0} new street addresses with the Census geocoder ({Benchmark})", planned, pending, census.Benchmark);

        var watch = Stopwatch.StartNew();
        long geocoded = 0, matched = 0;
        var batches = 0;
        var failedRounds = 0;
        var after = "";
        while (maxBatches <= 0 || batches < maxBatches)
        {
            var round = new List<IReadOnlyList<GeocodeRequest>>();
            while (round.Count < Math.Max(1, parallelism) && (maxBatches <= 0 || batches + round.Count < maxBatches))
            {
                var chunk = (await connection.QueryAsync<GeocodeRequest>(new CommandDefinition(
                    "SELECT `addr_key` AS `Key`, `street` AS Street, `city` AS City, `state` AS State, `zip5` AS Zip5 FROM `geocode_pending` WHERE `addr_key` > @after ORDER BY `addr_key` LIMIT @size",
                    new { after, size }, cancellationToken: ct))).ToList();
                if (chunk.Count == 0)
                {
                    break;
                }

                after = chunk[^1].Key;
                round.Add(chunk);
            }

            if (round.Count == 0)
            {
                break;
            }

            batches += round.Count;
            var answers = await Task.WhenAll(round.Select(batch => TryGeocodeAsync(batch, ct)));
            failedRounds = answers.All(a => a is null) ? failedRounds + 1 : 0;
            foreach (var results in answers.OfType<IReadOnlyList<GeocodeResult>>())
            {
                await SaveAsync(connection, results, ct);
                geocoded += results.Count;
                matched += results.Count(r => r.Status == "Match");
            }

            log.Information("Geocoded {Done:N0} of {Planned:N0} addresses, {Matched:P0} matched, {Rate:N0}/min",
                geocoded, planned, geocoded == 0 ? 0 : (double)matched / geocoded, geocoded / Math.Max(watch.Elapsed.TotalMinutes, 0.01));
            if (failedRounds >= MaxFailedRounds)
            {
                log.Error("The Census geocoder failed {Rounds} rounds in a row; stopping. The remaining addresses stay pending.", failedRounds);
                break;
            }
        }

        await Database.ExecuteAsync(connection, "DROP TABLE IF EXISTS `geocode_pending`", CancellationToken.None);
        return new GeocodeOutcome(pending, geocoded, matched, failedRounds < MaxFailedRounds);
    }

    private async Task<IReadOnlyList<GeocodeResult>?> TryGeocodeAsync(IReadOnlyList<GeocodeRequest> batch, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await census.GeocodeAsync(batch, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= Delays.Count)
                {
                    log.Warning(ex, "Geocoding a batch of {Count:N0} addresses failed {Attempts} times; they stay pending", batch.Count, attempt + 1);
                    return null;
                }

                log.Warning("Geocoding a batch failed ({Error}); retrying in {Delay}", ex.Message, Delays[attempt]);
                await Task.Delay(Delays[attempt], ct);
            }
        }
    }

    private async Task SaveAsync(MySqlConnection connection, IReadOnlyList<GeocodeResult> results, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var chunk in results.Chunk(1000))
        {
            var p = new DynamicParameters();
            var values = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                var r = chunk[i];
                values.Add($"(@k{i}, @s{i}, @t{i}, @m{i}, @lat{i}, @lon{i}, @benchmark, @now)");
                p.Add($"k{i}", r.Key);
                p.Add($"s{i}", r.Status);
                p.Add($"t{i}", r.MatchType);
                p.Add($"m{i}", r.MatchedAddress);
                p.Add($"lat{i}", r.Lat);
                p.Add($"lon{i}", r.Lon);
            }

            p.Add("benchmark", census.Benchmark);
            p.Add("now", now);
            await Database.ExecuteAsync(connection,
                "INSERT INTO `address_geocode` (`addr_key`, `status`, `match_type`, `matched_address`, `lat`, `lon`, `benchmark`, `geocoded_at`) VALUES " +
                string.Join(", ", values) +
                " AS new ON DUPLICATE KEY UPDATE `status` = new.`status`, `match_type` = new.`match_type`, `matched_address` = new.`matched_address`, " +
                "`lat` = new.`lat`, `lon` = new.`lon`, `benchmark` = new.`benchmark`, `geocoded_at` = new.`geocoded_at`",
                ct, param: p);
        }
    }
}
