using System.Collections.Concurrent;
using Npi.Loader.Datasets;
using Serilog.Core;

namespace Npi.Loader.Tests.Integration;

/// <summary>
/// Datasets refreshed side by side (speed item 7) against MySQL, with fake sources: the order between dependent sources, the
/// limit, one heavy load at a time, failures kept to their source, and two reloads queueing overlapping NPIs at once.
/// Skipped unless NPI_TEST_MYSQL is set.
/// </summary>
public sealed class DatasetScheduleIntegrationTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("npi-schedule-").FullName;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class Timeline
    {
        private readonly Lock _lock = new();
        private int _running;
        private int _heavy;

        public ConcurrentQueue<string> Events { get; } = new();

        public int MaxRunning { get; private set; }

        public int MaxHeavy { get; private set; }

        public void Start(string name, bool heavy)
        {
            lock (_lock)
            {
                Events.Enqueue("start " + name);
                MaxRunning = Math.Max(MaxRunning, ++_running);
                MaxHeavy = Math.Max(MaxHeavy, heavy ? ++_heavy : _heavy);
            }
        }

        public void End(string name, bool heavy)
        {
            lock (_lock)
            {
                Events.Enqueue("end " + name);
                _running--;
                _heavy -= heavy ? 1 : 0;
            }
        }

        public int Index(string e) => Events.ToList().IndexOf(e);
    }

    /// <summary>Replaces <paramref name="Table"/> (one the record quality queues) with rows for <paramref name="Npis"/>, or fails.</summary>
    private sealed class FakeSource(Timeline timeline, string name, string? table = null, string[]? npis = null, string[]? after = null,
        bool heavy = false, bool fail = false) : DatasetSource
    {
        public override string Name => name;

        public override IReadOnlyList<string> After => after ?? [];

        public override bool Heavy => heavy;

        public override Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
            Task.FromResult(new DatasetRelease("v1", new Uri("https://fake.test/" + name)));

        public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
        {
            timeline.Start(name, heavy);
            try
            {
                await Task.Delay(150, ct);
                if (fail)
                {
                    throw new InvalidDataException(name + " is broken");
                }

                if (table is null)
                {
                    return 0;
                }

                await using var connection = await context.Database.OpenAsync(ct);
                var values = string.Join(", ", npis!.Select(n => $"('{n}')"));
                var counts = await TableSwap.ReplaceAsync(connection, [table], 0.0,
                    () => Db.Database.ExecuteAsync(connection, $"INSERT INTO `{table}_staging` (`npi`) VALUES {values}", ct), ct, log: context.Log);
                return counts[table];
            }
            finally
            {
                timeline.End(name, heavy);
            }
        }
    }

    [Fact]
    public async Task Sources_run_side_by_side_in_dependency_order_and_a_failure_stays_with_its_source()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.ExecuteAsync("INSERT INTO provider_quality (npi, label, reasons, activity) VALUES ('1000000004', 1, 0, 0)"); // labels exist, so reloads queue
        var npis = Enumerable.Range(0, 2_000).Select(i => (1_000_000_000L + i).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var timeline = new Timeline();
        DatasetSource[] sources =
        [
            new FakeSource(timeline, "big_one", heavy: true),
            new FakeSource(timeline, "otp", "cms_otp_provider", npis[..1_500]),
            new FakeSource(timeline, "revalidation", "medicare_revalidation", npis[500..]),
            new FakeSource(timeline, "broken", fail: true),
            new FakeSource(timeline, "big_two", heavy: true),
            new FakeSource(timeline, "after_big_one", after: ["big_one"]),
            new FakeSource(timeline, "after_broken", after: ["broken"]),
        ];
        var loader = new DatasetLoader(new LoaderOptions { WorkFolder = _folder, LogFolder = _folder, ParallelSources = 3 }, db.Database, new HttpClient(),
            Logger.None, sources);

        Assert.False(await loader.RefreshAsync(force: false, only: null, _ct));

        Assert.Equal(3, timeline.MaxRunning);
        Assert.Equal(1, timeline.MaxHeavy);
        Assert.True(timeline.Index("start after_big_one") > timeline.Index("end big_one"));
        Assert.True(timeline.Index("start after_broken") > timeline.Index("end broken"));
        Assert.Equal(["after_big_one", "after_broken", "big_one", "big_two", "otp", "revalidation"],
            await db.QueryAsync<string>("SELECT source FROM reference_data WHERE version = 'v1' ORDER BY source"));

        // The two loads queued their overlapping NPIs at the same time: each once.
        Assert.Equal(2_000L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM quality_pending"));
        Assert.Equal(1_500L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM cms_otp_provider"));

        // Reloaded together with the halves swapped: only the NPIs whose rows came or went (the first 500 and the last 500).
        await db.ExecuteAsync("DELETE FROM quality_pending");
        sources =
        [
            new FakeSource(timeline, "otp", "cms_otp_provider", npis[500..]),
            new FakeSource(timeline, "revalidation", "medicare_revalidation", npis[..1_500]),
        ];
        Assert.True(await new DatasetLoader(new LoaderOptions { WorkFolder = _folder, LogFolder = _folder, ParallelSources = 3 }, db.Database, new HttpClient(),
            Logger.None, sources).RefreshAsync(force: true, only: null, _ct));
        Assert.Equal(1_000L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM quality_pending"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT(*) FROM quality_pending WHERE npi >= '1000000500' AND npi < '1000001500'"));
    }
}
