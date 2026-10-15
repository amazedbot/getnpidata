using System.Collections.Concurrent;
using System.Net;
using Npi.Loader.Datasets;
using Npi.Loader.Db;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Npi.Loader.Tests;

/// <summary>Sources, states and feed pages side by side (docs/PRODUCT_PLAN.md §7, speed item 7), with fake jobs: no network, no MySQL.</summary>
public sealed class ParallelScheduleTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    /// <summary>Records when jobs start and end, and how many ran at once.</summary>
    private sealed class Recorder
    {
        private readonly Lock _lock = new();
        private int _running;
        private int _heavyRunning;

        public List<string> Events { get; } = [];

        public int MaxRunning { get; private set; }

        public int MaxHeavyRunning { get; private set; }

        public ScheduledJob Job(string name, int milliseconds = 20, string[]? after = null, bool heavy = false, bool fail = false, Func<Task>? wait = null) =>
            new(name, after ?? [], heavy, async ct =>
            {
                lock (_lock)
                {
                    Events.Add("start " + name);
                    MaxRunning = Math.Max(MaxRunning, ++_running);
                    MaxHeavyRunning = Math.Max(MaxHeavyRunning, heavy ? ++_heavyRunning : _heavyRunning);
                }

                try
                {
                    if (wait is not null)
                    {
                        await wait();
                    }

                    await Task.Delay(milliseconds, ct);
                    if (fail)
                    {
                        throw new InvalidDataException(name + " failed");
                    }
                }
                finally
                {
                    lock (_lock)
                    {
                        Events.Add("end " + name);
                        _running--;
                        _heavyRunning -= heavy ? 1 : 0;
                    }
                }
            });

        public int Index(string e) => Events.IndexOf(e);

        public bool Has(string e)
        {
            lock (_lock)
            {
                return Events.Contains(e);
            }
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return Events.Count;
                }
            }
        }
    }

    [Fact]
    public async Task A_limit_of_one_runs_the_jobs_one_after_another_in_list_order()
    {
        var r = new Recorder();
        var jobs = new[] { r.Job("a"), r.Job("b", heavy: true), r.Job("c", after: ["a"]), r.Job("d", heavy: true, after: ["b"]), r.Job("e") };

        Assert.Empty(await ParallelSchedule.RunAsync(jobs, limit: 1, _ct));

        Assert.Equal(["start a", "end a", "start b", "end b", "start c", "end c", "start d", "end d", "start e", "end e"], r.Events);
    }

    [Fact]
    public async Task No_more_jobs_than_the_limit_run_at_once()
    {
        var r = new Recorder();
        var jobs = Enumerable.Range(1, 12).Select(i => r.Job("job" + i, milliseconds: 30)).ToList();

        Assert.Empty(await ParallelSchedule.RunAsync(jobs, limit: 3, _ct));

        Assert.Equal(3, r.MaxRunning);
        Assert.Equal(24, r.Events.Count);
    }

    [Fact]
    public async Task A_job_starts_only_after_the_jobs_it_waits_for_and_others_go_on_meanwhile()
    {
        var r = new Recorder();
        var release = new TaskCompletionSource();
        var jobs = new[]
        {
            r.Job("open_payments", wait: () => release.Task),
            r.Job("fda_products", after: ["open_payments"]),
            r.Job("leie", milliseconds: 1),
            r.Job("part_d", after: ["open_payments", "fda_products"]),
            r.Job("nadac", milliseconds: 1),
        };

        var run = ParallelSchedule.RunAsync(jobs, limit: 3, _ct);
        // The independent jobs finish while open_payments is still running; its dependents haven't started.
        while (!(r.Has("end leie") && r.Has("end nadac")))
        {
            await Task.Delay(5, _ct);
        }

        Assert.False(r.Has("start fda_products"));
        release.SetResult();
        Assert.Empty(await run);

        Assert.True(r.Index("start fda_products") > r.Index("end open_payments"));
        Assert.True(r.Index("start part_d") > r.Index("end fda_products"));
    }

    [Fact]
    public async Task At_most_one_heavy_job_runs_at_a_time_while_light_ones_run_beside_it()
    {
        var r = new Recorder();
        var jobs = new[]
        {
            r.Job("services", milliseconds: 60, heavy: true), r.Job("open_payments", milliseconds: 60, heavy: true), r.Job("years", milliseconds: 60, heavy: true),
            r.Job("leie", milliseconds: 10), r.Job("nadac", milliseconds: 10), r.Job("hrsa", milliseconds: 10),
        };

        Assert.Empty(await ParallelSchedule.RunAsync(jobs, limit: 3, _ct));

        Assert.Equal(1, r.MaxHeavyRunning);
        Assert.True(r.Index("start leie") < r.Index("end services")); // a heavy job waiting doesn't hold a slot
        Assert.True(r.Index("start open_payments") > r.Index("end services"));
        Assert.True(r.Index("start years") > r.Index("end open_payments"));
    }

    [Fact]
    public async Task A_failing_job_does_not_stop_the_others_or_the_jobs_waiting_for_it()
    {
        var r = new Recorder();
        var failures = new ConcurrentBag<(string, Exception)>();
        var jobs = new[] { r.Job("leie", fail: true), r.Job("opt_out"), r.Job("after_leie", after: ["leie"]), r.Job("nadac", fail: true) };

        var failed = await ParallelSchedule.RunAsync(jobs, limit: 2, _ct, (job, ex) => failures.Add((job.Name, ex)));

        Assert.Equal(["leie", "nadac"], failed.Order());
        Assert.Equal(["leie", "nadac"], failures.Select(f => f.Item1).Order());
        Assert.All(failures, f => Assert.IsType<InvalidDataException>(f.Item2));
        Assert.Contains("end opt_out", r.Events);
        Assert.Contains("end after_leie", r.Events);
    }

    [Fact]
    public async Task Jobs_waiting_for_one_not_in_the_schedule_run_at_once()
    {
        var r = new Recorder();
        Assert.Empty(await ParallelSchedule.RunAsync([r.Job("fda_products", after: ["open_payments"])], limit: 3, _ct));
        Assert.Equal(["start fda_products", "end fda_products"], r.Events);
    }

    [Fact]
    public async Task A_job_waiting_for_a_later_one_or_two_of_one_name_are_refused()
    {
        var r = new Recorder();
        await Assert.ThrowsAsync<ArgumentException>(() => ParallelSchedule.RunAsync([r.Job("a", after: ["b"]), r.Job("b")], limit: 3, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ParallelSchedule.RunAsync([r.Job("a"), r.Job("a")], limit: 3, _ct));
        Assert.Empty(r.Events);
    }

    [Fact]
    public async Task Cancelling_starts_no_more_jobs_and_waits_for_the_running_ones()
    {
        var r = new Recorder();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var jobs = Enumerable.Range(1, 6).Select(i => r.Job("job" + i, milliseconds: 5_000)).ToList();

        var run = ParallelSchedule.RunAsync(jobs, limit: 2, cts.Token);
        while (r.Count < 2)
        {
            await Task.Delay(5, _ct);
        }

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(["end job1", "end job2", "start job1", "start job2"], r.Events.Order());
    }

    [Fact]
    public void Every_dataset_waits_only_for_known_sources_listed_before_it()
    {
        var sources = DatasetLoader.AllSources;
        var position = sources.Select((s, i) => (s.Name, i)).ToDictionary(x => x.Name, x => x.i);
        Assert.Equal(sources.Count, position.Count); // names are unique
        foreach (var (source, i) in sources.Select((s, i) => (s, i)))
        {
            Assert.All(source.After, name => Assert.True(position.TryGetValue(name, out var p) && p < i, $"{source.Name} waits for {name}"));
        }

        // The ones that read another's tables (CLAUDE.md §7 Stage 5.5 items 19 parts 2–4).
        Assert.Equal(
            [("fda_products", "open_payments"), ("part_d_prescribers", "fda_products, open_payments"), ("product_adverse_events", "fda_products, open_payments"),
             ("product_trials", "fda_products, open_payments")],
            sources.Where(s => s.After.Count > 0).Select(s => (s.Name, string.Join(", ", s.After.Order(StringComparer.Ordinal)))).OrderBy(x => x.Name, StringComparer.Ordinal));
        Assert.Equal(["cms_physician_by_service", "fema_structures", "open_payments", "open_payments_companies", "open_payments_years", "part_d_prescribers"],
            sources.Where(s => s.Heavy).Select(s => s.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Pages_are_fetched_a_few_at_a_time_and_read_in_order()
    {
        var inFlight = 0;
        var maxInFlight = 0;
        var pages = new List<int>();
        await foreach (var page in ParallelSchedule.InOrderAsync(10, async (i, ct) =>
                       {
                           var now = Interlocked.Increment(ref inFlight);
                           InterlockedMax(ref maxInFlight, now);
                           await Task.Delay((10 - i) * 5, ct); // later pages answer sooner
                           Interlocked.Decrement(ref inFlight);
                           return i;
                       }, parallel: 4, TimeSpan.Zero, _ct))
        {
            pages.Add(page);
        }

        Assert.Equal(Enumerable.Range(0, 10), pages);
        Assert.Equal(4, maxInFlight);
    }

    [Fact]
    public async Task Leaving_the_pages_early_cancels_the_requests_in_flight()
    {
        var cancelled = 0;
        await foreach (var page in ParallelSchedule.InOrderAsync(50, async (i, ct) =>
                       {
                           try
                           {
                               await Task.Delay(i == 0 ? 0 : 5_000, ct);
                           }
                           catch (OperationCanceledException)
                           {
                               Interlocked.Increment(ref cancelled);
                               throw;
                           }

                           return i;
                       }, parallel: 3, TimeSpan.Zero, _ct))
        {
            Assert.Equal(0, page);
            break;
        }

        Assert.Equal(2, cancelled); // pages 1 and 2 were in flight; nothing after them started
    }

    [Fact]
    public async Task A_failed_page_fails_the_enumeration_in_its_turn()
    {
        var read = new List<int>();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var page in ParallelSchedule.InOrderAsync(5, (i, _) => i == 2 ? Task.FromException<int>(new HttpRequestException("500")) : Task.FromResult(i),
                               parallel: 4, TimeSpan.Zero, _ct))
            {
                read.Add(page);
            }
        });
        Assert.Equal([0, 1], read);
    }

    [Fact]
    public async Task Vermont_pages_are_read_in_order_until_the_total()
    {
        var requests = new ConcurrentBag<int>();
        var handler = new FeedHandler(start =>
        {
            requests.Add(start);
            // 250 records: pages of 100, 100 and 50; the second answers last.
            var count = start switch { 0 or 100 => 100, 200 => 50, _ => 0 };
            if (start == 100)
            {
                Thread.Sleep(50);
            }

            var rows = string.Join(",", Enumerable.Range(start, count).Select(n => $"[\"{n}\", \"ACTIVE\", \"First\", \"\", \"Last{n}\"]"));
            return $$"""{"draw":1,"recordsTotal":250,"recordsFiltered":250,"data":[{{rows}}]}""";
        });
        var context = new DatasetContext(new LoaderOptions { ApiRequestDelay = TimeSpan.Zero, StateFeedParallelRequests = 4 },
            new Database("Server=localhost;Database=unused"), new HttpClient(handler), Logger.None);

        var numbers = new List<string?>();
        await foreach (var row in new VermontDataset().RowsAsync(context, _ct))
        {
            numbers.Add(row.LicenseNumber);
        }

        Assert.Equal(Enumerable.Range(0, 250).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)), numbers);
        Assert.Equal([0, 100, 200], requests.Order());
    }

    [Fact]
    public void Vermont_feed_pages_parse_the_total_and_rows()
    {
        var (total, rows) = VermontDataset.ParsePage(Fixtures.Text("datasets/state_vt_feed.json"));
        Assert.Equal(2, total);
        Assert.Equal([("042.0010818", "Green", "Physician", "INACTIVE - DECEASED", "Stipulation and Consent Order"), ("Pending", "Levesque", "Physician", "Initial", null)],
            rows.Select(r => (r.LicenseNumber, r.LastName, r.LicenseType, r.Status, r.Action)));
    }

    [Fact]
    public async Task Sources_running_at_once_fetch_the_CMS_catalog_once()
    {
        var calls = 0;
        var handler = new FeedHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(50);
            return Fixtures.Text("datasets/cms_catalog_sample.json");
        });
        var context = new DatasetContext(new LoaderOptions { CmsCatalogUrl = "https://cms.test/data.json" }, new Database("Server=localhost;Database=unused"),
            new HttpClient(handler), Logger.None);

        var catalogs = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => (i % 2 == 0 ? context : context.ForStep("source" + i)).CmsCatalogAsync(_ct)));

        Assert.Equal(1, calls);
        Assert.All(catalogs, c => Assert.Same(catalogs[0], c));
    }

    [Theory]
    [InlineData(LoaderLog.OutputTemplate)]
    [InlineData(LoaderLog.FileOutputTemplate)]
    public void Log_lines_of_a_step_start_with_its_name_and_others_have_no_prefix(string template)
    {
        var formatter = new MessageTemplateTextFormatter(template, System.Globalization.CultureInfo.InvariantCulture);
        string Render(bool step)
        {
            var sink = new CollectingSink();
            using var logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
            (step ? LoaderLog.ForStep(logger, "open_payments") : logger).Information("Downloaded {File}", "open_payments_general.csv");
            using var text = new StringWriter();
            formatter.Format(sink.Events.Single(), text);
            return text.ToString();
        }

        Assert.EndsWith("INF] Downloaded open_payments_general.csv" + Environment.NewLine, Render(step: false));
        Assert.EndsWith("INF] [open_payments] Downloaded open_payments_general.csv" + Environment.NewLine, Render(step: true));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        for (var current = Volatile.Read(ref target); value > current; current = Volatile.Read(ref target))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    private sealed class CollectingSink : Serilog.Core.ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    /// <summary>Answers every GET with the text <paramref name="answer"/> gives for the request's <c>start</c> parameter (0 without one).</summary>
    private sealed class FeedHandler(Func<int, string> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.Run(() =>
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
                var start = int.TryParse(query["start"], out var s) ? s : 0;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(start)) };
            }, cancellationToken);
    }
}
