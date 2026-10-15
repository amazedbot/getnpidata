using Npi.Loader.Projection;
using Serilog.Core;

namespace Npi.Loader.Tests;

public class BuilderStepsTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Builders_run_side_by_side_up_to_the_limit()
    {
        var running = 0;
        var most = 0;
        var built = 0;
        var twoRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BuilderStep Step(string name) => new(name, null, async c =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            if (now >= 2)
            {
                twoRunning.TrySetResult();
            }

            // One after another, the first would wait here until the timeout.
            await twoRunning.Task.WaitAsync(TimeSpan.FromSeconds(10), c);
            Interlocked.Decrement(ref running);
            Interlocked.Increment(ref built);
        });

        Assert.True(await BuilderSteps.RunAsync([Step("a"), Step("b"), Step("c"), Step("d")], parallel: 2, Logger.None, _ct));
        Assert.Equal(2, most);
        Assert.Equal(4, built);
    }

    [Fact]
    public async Task One_at_a_time_they_run_in_order()
    {
        var order = new List<string>();
        BuilderStep Step(string name) => new(name, null, async _ =>
        {
            await Task.Yield();
            lock (order)
            {
                order.Add(name);
            }
        });

        Assert.True(await BuilderSteps.RunAsync([Step("credentials"), Step("address types"), Step("areas")], parallel: 1, Logger.None, _ct));
        Assert.Equal(["credentials", "address types", "areas"], order);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_failing_builder_does_not_stop_the_others(int parallel)
    {
        var built = new List<string>();
        BuilderStep Step(string name, bool fails = false, bool stale = true) => new(name, _ => Task.FromResult(stale), async _ =>
        {
            await Task.Yield();
            if (fails)
            {
                throw new InvalidOperationException("broken");
            }

            lock (built)
            {
                built.Add(name);
            }
        });

        Assert.False(await BuilderSteps.RunAsync([Step("a", fails: true), Step("b"), Step("c", stale: false)], parallel, Logger.None, _ct));
        Assert.Equal(["b"], built); // c had nothing to do
        Assert.True(await BuilderSteps.RunAsync([Step("b"), Step("c", stale: false)], parallel, Logger.None, _ct));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
