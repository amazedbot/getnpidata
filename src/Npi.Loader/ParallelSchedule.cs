namespace Npi.Loader;

/// <summary>One job of a <see cref="ParallelSchedule"/>.</summary>
/// <param name="Name">The job's name, unique in the schedule.</param>
/// <param name="After">
/// The jobs (by name) that must have finished, successfully or not, before this one starts. Names not in the schedule are
/// ignored (a dataset refreshed on its own); those in it must come earlier in the list.
/// </param>
/// <param name="Heavy">At most one heavy job runs at a time (the loads of several GB, which would compete for disk and memory).</param>
/// <param name="Run">The work.</param>
public sealed record ScheduledJob(string Name, IReadOnlyList<string> After, bool Heavy, Func<CancellationToken, Task> Run);

/// <summary>
/// Runs jobs side by side (docs/PRODUCT_PLAN.md §7, speed item 7): at most <c>limit</c> at a time, each started once the jobs
/// it waits for have finished and, if it is heavy, no other heavy job is running. A free slot goes to the first job in list
/// order that may start, so a limit of 1 runs them one after another in list order. A job that fails doesn't stop the others.
/// </summary>
public static class ParallelSchedule
{
    /// <param name="onFailure">Called when a job throws (anything but the cancellation of <paramref name="ct"/>).</param>
    /// <returns>The names of the jobs that failed, in the order they finished.</returns>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is cancelled: no job starts any more, and the running ones are awaited first.</exception>
    public static async Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<ScheduledJob> jobs, int limit, CancellationToken ct,
        Action<ScheduledJob, Exception>? onFailure = null)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < jobs.Count; i++)
        {
            if (!index.TryAdd(jobs[i].Name, i))
            {
                throw new ArgumentException($"Two jobs are named {jobs[i].Name}.", nameof(jobs));
            }
        }

        var waitsFor = new int[jobs.Count][];
        for (var i = 0; i < jobs.Count; i++)
        {
            waitsFor[i] = jobs[i].After.Where(index.ContainsKey).Select(n => index[n]).ToArray();
            if (waitsFor[i].FirstOrDefault(d => d >= i, -1) is var later and >= 0)
            {
                throw new ArgumentException($"{jobs[i].Name} waits for {jobs[later].Name}, which comes after it.", nameof(jobs));
            }
        }

        limit = Math.Max(1, limit);
        var started = new bool[jobs.Count];
        var finished = new bool[jobs.Count];
        var running = new Dictionary<Task, int>();
        var failed = new List<string>();
        var heavyRunning = false;
        while (true)
        {
            for (var i = 0; i < jobs.Count && running.Count < limit && !ct.IsCancellationRequested; i++)
            {
                if (started[i] || waitsFor[i].Any(d => !finished[d]) || (jobs[i].Heavy && heavyRunning))
                {
                    continue;
                }

                started[i] = true;
                heavyRunning |= jobs[i].Heavy;
                var job = jobs[i];
                // On the thread pool: some jobs do long synchronous work (DuckDB, an Access database) before their first await.
                running.Add(Task.Run(() => job.Run(ct), CancellationToken.None), i);
            }

            if (running.Count == 0)
            {
                break;
            }

            var done = await Task.WhenAny(running.Keys);
            var j = running[done];
            running.Remove(done);
            finished[j] = true;
            if (jobs[j].Heavy)
            {
                heavyRunning = false;
            }

            try
            {
                await done;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Stops the loop from starting more; the running jobs are awaited, then the cancellation is thrown.
            }
            catch (Exception ex)
            {
                failed.Add(jobs[j].Name);
                onFailure?.Invoke(jobs[j], ex);
            }
        }

        ct.ThrowIfCancellationRequested();
        return failed;
    }

    /// <summary>
    /// Fetches items 0 … <paramref name="count"/> − 1 (the pages of a feed) with up to <paramref name="parallel"/> requests in
    /// flight, starting one at most every <paramref name="interval"/>, and yields the answers in item order. Leaving the loop
    /// early (an empty page) cancels the requests still in flight. A failed request fails the enumeration when its turn comes.
    /// </summary>
    public static async IAsyncEnumerable<T> InOrderAsync<T>(int count, Func<int, CancellationToken, Task<T>> fetch, int parallel, TimeSpan interval,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var inFlight = new Queue<Task<T>>();
        var next = 0;
        var lastStart = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (next < count || inFlight.Count > 0)
            {
                while (next < count && inFlight.Count < Math.Max(1, parallel))
                {
                    if (next > 0 && interval - lastStart.Elapsed is { Ticks: > 0 } wait)
                    {
                        await Task.Delay(wait, ct);
                    }

                    var item = next++;
                    lastStart.Restart();
                    inFlight.Enqueue(Task.Run(() => fetch(item, stop.Token), CancellationToken.None));
                }

                yield return await inFlight.Dequeue();
            }
        }
        finally
        {
            await stop.CancelAsync();
            foreach (var request in inFlight)
            {
                try
                {
                    await request;
                }
                catch (Exception)
                {
                    // Not needed any more (cancelled, or failing after the caller stopped reading).
                }
            }
        }
    }
}
