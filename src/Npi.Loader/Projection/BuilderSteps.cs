using MySqlConnector;
using Serilog;

namespace Npi.Loader.Projection;

/// <summary>A builder for <see cref="BuilderSteps.RunAsync"/>: its name in the log, whether it has work (null: always) and its build.</summary>
public sealed record BuilderStep(string Name, Func<CancellationToken, Task<bool>>? IsStaleAsync, Func<CancellationToken, Task> BuildAsync);

/// <summary>
/// Runs the builders after the projection that read none of each other's tables side by side (docs/PRODUCT_PLAN.md §7 speed
/// item 13), each on its own connection: the standardized credentials (provider), the practice address types (provider_location,
/// address_building, address_point) and the area counts (provider_location, provider_search, provider_taxonomy, zip_county and
/// their own specialty_code_category). What reads their output waits for all of them: the map reads provider_credential, record
/// quality address_type and specialty_code_category, the full export all three. Each builder has its own staging, scratch and
/// batch tables, its own <c>reference_data</c> row and its own rows of <c>builder_pending</c>; the snapshot cleanup after each
/// commit runs one at a time (<see cref="BuilderPending.CleanAsync"/>). A failed builder doesn't stop the others; one that loses a
/// lock conflict to another (a deadlock or a lock wait timeout: its transaction rolled back, its queue intact) starts again once.
/// </summary>
public static class BuilderSteps
{
    /// <summary>How many times a builder runs at most when it keeps losing lock conflicts.</summary>
    public const int Attempts = 2;

    /// <param name="parallel">Builders at once (<see cref="LoaderOptions.ParallelBuilders"/>); 1 = one after another, in order.</param>
    /// <returns>True when every builder succeeded (or had nothing to do).</returns>
    public static async Task<bool> RunAsync(IReadOnlyList<BuilderStep> steps, int parallel, ILogger log, CancellationToken ct)
    {
        if (parallel <= 1 || steps.Count <= 1)
        {
            var ok = true;
            foreach (var step in steps)
            {
                ok &= await RunOneAsync(step, log, ct);
            }

            return ok;
        }

        log.Information("Building side by side, {Parallel} at a time: {Builders}", Math.Min(parallel, steps.Count), string.Join(", ", steps.Select(s => s.Name)));
        using var gate = new SemaphoreSlim(parallel);
        var results = await Task.WhenAll(steps.Select(async step =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await RunOneAsync(step, log, ct);
            }
            finally
            {
                gate.Release();
            }
        }));
        return results.All(ok => ok);
    }

    private static async Task<bool> RunOneAsync(BuilderStep step, ILogger log, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (step.IsStaleAsync is not null && !await step.IsStaleAsync(ct))
                {
                    return true;
                }

                await step.BuildAsync(ct);
                return true;
            }
            catch (MySqlException ex) when (attempt < Attempts && IsLockConflict(ex) && !ct.IsCancellationRequested)
            {
                log.Warning("{Builder}: lost a lock conflict with a builder running alongside ({Error}); starting again", step.Name, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.Error(ex, "{Builder} failed", step.Name);
                return false;
            }
        }
    }

    /// <summary>A deadlock (InnoDB rolled the transaction back) or a lock wait timeout.</summary>
    public static bool IsLockConflict(MySqlException ex) => ex.ErrorCode is MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout;
}
