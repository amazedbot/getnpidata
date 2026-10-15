using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Npi.Loader.Datasets;
using Npi.Loader.Db;
using Serilog.Core;

namespace Npi.Loader.Tests;

/// <summary>
/// The ClinicalTrials.gov cache asking several products at once (docs/PRODUCT_PLAN.md §7, speed item 11), against a fake
/// API: no network, no MySQL. The saves are collected instead of written.
/// </summary>
public sealed class ClinicalTrialsCacheTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static (string Slug, string Query)[] Products(int count) =>
        Enumerable.Range(0, count).Select(i => ($"drug-{i}", $"drug{i}")).ToArray();

    private static DatasetContext Context(FakeTrials api, int parallel, TimeSpan? interval = null, TimeSpan? delay = null, int failuresInARow = 5) =>
        new(new LoaderOptions
        {
            ClinicalTrialsApiUrl = "https://ct.test/api/v2/studies",
            ClinicalTrialsParallelRequests = parallel,
            ClinicalTrialsRequestInterval = interval ?? TimeSpan.Zero,
            ApiRequestDelay = delay ?? TimeSpan.Zero,
            ApiRetryDelay = TimeSpan.FromMilliseconds(1),
            ApiFailuresInARow = failuresInARow,
        }, new Database("Server=localhost;Database=unused"), new HttpClient(api), Logger.None);

    private async Task<ConcurrentQueue<(string Slug, string Query, ProductTrialSource.Counts Counts)>> RunAsync(DatasetContext context,
        (string Slug, string Query)[] products, Action<long>? done = null)
    {
        var saved = new ConcurrentQueue<(string, string, ProductTrialSource.Counts)>();
        var count = await ProductTrialSource.AskProductsAsync(context, products, (slug, query, counts, _) =>
        {
            saved.Enqueue((slug, query, counts));
            return Task.CompletedTask;
        }, _ct);
        done?.Invoke(count);
        Assert.Equal(saved.Count, count);
        return saved;
    }

    [Fact]
    public async Task Products_are_asked_several_at_a_time_and_all_saved()
    {
        var api = new FakeTrials { Latency = TimeSpan.FromMilliseconds(100) };

        var saved = await RunAsync(Context(api, parallel: 4), Products(16));

        Assert.Equal(4, api.MaxInFlight);
        Assert.Equal(32, api.Requests.Count); // two requests each: all studies, recruiting now
        Assert.Equal(Products(16).Select(p => p.Slug).Order(), saved.Select(s => s.Slug).Order());
    }

    [Fact]
    public async Task Answers_are_saved_as_they_finish_with_their_own_counts()
    {
        // The first products answer slowest, so they finish last.
        var api = new FakeTrials { LatencyOf = query => TimeSpan.FromMilliseconds(query is "drug0" or "drug1" ? 300 : 5) };

        var saved = (await RunAsync(Context(api, parallel: 4), Products(8))).ToList();

        Assert.Equal(8, saved.Count);
        Assert.NotEqual("drug-0", saved[0].Slug);
        Assert.Contains(saved.TakeLast(2), s => s.Slug == "drug-0");
        Assert.All(saved, s => Assert.Equal(FakeTrials.CountsOf(s.Query), s.Counts));
    }

    [Fact]
    public async Task A_server_error_is_retried_then_the_product_is_skipped_until_the_next_run()
    {
        var api = new FakeTrials
        {
            Fail = (query, attempt) => query switch
            {
                "drug1" when attempt <= 2 => HttpStatusCode.InternalServerError, // twice, then answers
                "drug2" => HttpStatusCode.BadGateway, // always
                "drug3" when attempt == 1 => (HttpStatusCode)0, // a dropped connection, then answers
                _ => null,
            },
        };

        var saved = await RunAsync(Context(api, parallel: 4), Products(6));

        Assert.Equal(["drug-0", "drug-1", "drug-3", "drug-4", "drug-5"], saved.Select(s => s.Slug).Order());
        Assert.Equal(3, api.Requests.Count(r => r == "drug2")); // tried three times, then skipped
        Assert.Equal(FakeTrials.CountsOf("drug1"), saved.Single(s => s.Slug == "drug-1").Counts);
    }

    [Fact]
    public async Task A_refused_name_is_stored_as_unknown()
    {
        var api = new FakeTrials { Fail = (query, _) => query == "drug1" ? HttpStatusCode.BadRequest : null };

        var saved = await RunAsync(Context(api, parallel: 4), Products(3));

        Assert.Equal(new ProductTrialSource.Counts(null, null), saved.Single(s => s.Slug == "drug-1").Counts);
        Assert.Equal(3, saved.Count);
    }

    [Fact]
    public async Task A_rate_limit_stops_the_run_early_without_failing_it()
    {
        var api = new FakeTrials { Latency = TimeSpan.FromMilliseconds(20), Fail = (query, _) => query == "drug5" ? HttpStatusCode.TooManyRequests : null };

        long done = -1;
        var saved = await RunAsync(Context(api, parallel: 4), Products(40), d => done = d);

        Assert.InRange(done, 1, 12); // what finished before the 429 (and in flight beside it); the rest wait for the next run
        Assert.DoesNotContain(saved, s => s.Slug == "drug-5");
        Assert.InRange(api.Requests.Distinct().Count(), 6, 14); // no new product started after it
    }

    [Fact]
    public async Task A_rate_limit_one_at_a_time_keeps_the_products_before_it()
    {
        var api = new FakeTrials { Fail = (query, _) => query == "drug3" ? HttpStatusCode.TooManyRequests : null };

        var saved = await RunAsync(Context(api, parallel: 1), Products(10));

        Assert.Equal(["drug-0", "drug-1", "drug-2"], saved.Select(s => s.Slug));
        Assert.DoesNotContain(api.Requests, r => r is "drug4" or "drug9");
    }

    [Fact]
    public async Task Products_failing_in_a_row_stop_the_run_early()
    {
        var api = new FakeTrials { Fail = (query, _) => query == "drug0" ? null : HttpStatusCode.ServiceUnavailable };

        var saved = await RunAsync(Context(api, parallel: 1, failuresInARow: 3), Products(20));

        Assert.Equal(["drug-0"], saved.Select(s => s.Slug));
        // drug0's two requests, then three products failing three times each, then none.
        Assert.Equal(["drug0", "drug1", "drug2", "drug3"], api.Requests.Distinct());
        Assert.Equal(2 + 3 * 3, api.Requests.Count);
    }

    [Fact]
    public async Task One_at_a_time_waits_the_request_delay_after_each_answer()
    {
        var api = new FakeTrials { Latency = TimeSpan.FromMilliseconds(10) };

        var saved = await RunAsync(Context(api, parallel: 1, delay: TimeSpan.FromMilliseconds(40)), Products(4));

        Assert.Equal(["drug-0", "drug-1", "drug-2", "drug-3"], saved.Select(s => s.Slug)); // in order
        Assert.Equal(1, api.MaxInFlight);
        var calls = api.Calls.OrderBy(c => c.Start).ToList();
        for (var i = 1; i < calls.Count; i++)
        {
            Assert.True(calls[i].Start - calls[i - 1].End >= TimeSpan.FromMilliseconds(30),
                $"request {i} started {(calls[i].Start - calls[i - 1].End).TotalMilliseconds} ms after the previous one ended");
        }
    }

    [Fact]
    public async Task Several_at_a_time_start_one_request_per_interval()
    {
        var api = new FakeTrials { Latency = TimeSpan.FromMilliseconds(100) };

        await RunAsync(Context(api, parallel: 4, interval: TimeSpan.FromMilliseconds(40)), Products(5));

        // Ten requests, one started every 40 ms: the last at least 8 intervals after the second (the first reaches the
        // handler late, while HttpClient sets itself up), less a margin for a busy test machine.
        var starts = api.Calls.Select(c => c.Start).Order().ToList();
        Assert.Equal(10, starts.Count);
        Assert.True(starts[^1] - starts[1] >= TimeSpan.FromMilliseconds(8 * 40 - 40), $"{(starts[^1] - starts[1]).TotalMilliseconds} ms");
        Assert.True(api.MaxInFlight > 1); // answers take longer than the interval, so they overlap
    }

    /// <summary>
    /// ClinicalTrials.gov's study search: a <c>totalCount</c> per intervention name (and a smaller one with the recruiting
    /// filter), after <see cref="Latency"/>; <see cref="Fail"/> picks a status by name and attempt (0 = a dropped connection).
    /// </summary>
    private sealed class FakeTrials : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, int> _attempts = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _inFlight;

        public TimeSpan Latency { get; init; }

        public Func<string, TimeSpan>? LatencyOf { get; init; }

        public Func<string, int, HttpStatusCode?> Fail { get; init; } = (_, _) => null;

        public ConcurrentQueue<string> Requests { get; } = new();

        public ConcurrentQueue<(TimeSpan Start, TimeSpan End)> Calls { get; } = new();

        public int MaxInFlight { get; private set; }

        public static ProductTrialSource.Counts CountsOf(string query) => new(int.Parse(query[4..], System.Globalization.CultureInfo.InvariantCulture) * 10 + 7,
            int.Parse(query[4..], System.Globalization.CultureInfo.InvariantCulture) + 1);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var start = _clock.Elapsed;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var name = query["query.intr"]!;
            var recruiting = query["filter.overallStatus"] == "RECRUITING";
            Requests.Enqueue(name);
            int attempt;
            lock (_lock)
            {
                MaxInFlight = Math.Max(MaxInFlight, ++_inFlight);
                attempt = _attempts[name + recruiting] = _attempts.GetValueOrDefault(name + recruiting) + 1;
            }

            try
            {
                await Task.Delay(LatencyOf?.Invoke(name) ?? Latency, cancellationToken);
                var status = Fail(name, attempt);
                if (status == 0)
                {
                    throw new HttpRequestException("connection reset");
                }

                if (status is { } failed)
                {
                    return new HttpResponseMessage(failed);
                }

                var counts = CountsOf(name);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"totalCount":{{(recruiting ? counts.Recruiting : counts.Studies)}},"studies":[]}"""),
                };
            }
            finally
            {
                lock (_lock)
                {
                    _inFlight--;
                }

                Calls.Enqueue((start, _clock.Elapsed));
            }
        }
    }
}
