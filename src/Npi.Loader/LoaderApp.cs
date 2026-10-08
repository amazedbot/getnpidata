using System.Globalization;
using Dapper;
using Npi.Loader.Db;
using Npi.Loader.Load;
using Npi.Loader.Nppes;
using Npi.Loader.Projection;
using Npi.Loader.Reference;
using Serilog;

namespace Npi.Loader;

/// <summary>The loader's commands. Each returns the process exit code: 0 = everything completed, 1 = a step failed.</summary>
public sealed class LoaderApp(LoaderOptions options, Database database, HttpClient http, ILogger log)
{
    private readonly DownLog _downLog = new(database);

    public async Task<int> MigrateAsync(CancellationToken ct)
    {
        await new MigrationRunner(database, log).ApplyAsync(ct);
        return 0;
    }

    public async Task<int> DiscoverAsync(CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        var plan = await PlanAsync(ct);
        foreach (var skipped in plan.Skipped)
        {
            log.Information("Skip  {File}: {Reason}", skipped.FileName, skipped.Reason);
        }

        foreach (var planned in plan.Files)
        {
            log.Information("Would load {Kind,-12} {File}", planned.File.Kind, planned.File.FileName);
        }

        log.Information("{Count} file(s) to load", plan.Files.Count);
        return 0;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var plan = await PlanAsync(ct);
        foreach (var skipped in plan.Skipped.Where(s => !s.Reason.StartsWith("already", StringComparison.Ordinal)))
        {
            log.Information("Skip {File}: {Reason}", skipped.FileName, skipped.Reason);
        }

        var downloader = new Downloader(http, log, options.DownloadAttempts);
        var failures = 0;
        foreach (var planned in plan.Files)
        {
            var ok = await ProcessAsync(planned.File, async () =>
                await downloader.DownloadAsync(planned.Url, planned.File.FileName, options.ResolvedWorkFolder, ct), ct);
            failures += ok ? 0 : 1;
        }

        if (plan.Files.Count == 0)
        {
            log.Information("No new NPPES files");
        }

        // Stage 2: reference data, reloaded only when its published version changed.
        var referenceOk = await new ReferenceLoader(options, database, http, log).RefreshAllAsync(force: false, ct);

        // Stage 3: rebuild the search projection when anything it is built from changed.
        var projectionOk = true;
        var projection = new ProjectionBuilder(database, log, options.MinRowRatio);
        if (await projection.IsStaleAsync(ct))
        {
            try
            {
                await projection.BuildAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.Error(ex, "Building the search projection failed");
                projectionOk = false;
            }
        }
        else
        {
            log.Information("Search projection is current");
        }

        log.Information("Run finished: {Ok} NPPES file(s) completed, {Failed} failed; reference data {Reference}; projection {Projection}",
            plan.Files.Count - failures, failures, referenceOk ? "current" : "FAILED", projectionOk ? "current" : "FAILED");
        return failures == 0 && referenceOk && projectionOk ? 0 : 1;
    }

    /// <summary>The <c>project</c> command: rebuild the search projection now.</summary>
    public async Task<int> ProjectAsync(CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await new ProjectionBuilder(database, log, options.MinRowRatio).BuildAsync(ct);
        return 0;
    }

    /// <summary>The <c>reference</c> command: reload NUCC, HUD and Census data even if unchanged.</summary>
    public async Task<int> ReferenceAsync(CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        return await new ReferenceLoader(options, database, http, log).RefreshAllAsync(force: true, ct) ? 0 : 1;
    }

    public async Task<int> LoadFileAsync(string zipPath, CancellationToken ct)
    {
        var file = NppesFileClassifier.Classify(Path.GetFileName(zipPath));
        if (file is null)
        {
            log.Error("{File} is not a recognised V2 NPPES file name", Path.GetFileName(zipPath));
            return 1;
        }

        if (!File.Exists(zipPath))
        {
            log.Error("{Path} does not exist", zipPath);
            return 1;
        }

        await EnsureReadyAsync(ct);
        return await ProcessAsync(file, () => Task.FromResult(Path.GetFullPath(zipPath)), ct, deleteZip: false) ? 0 : 1;
    }

    private async Task<bool> ProcessAsync(NppesFile file, Func<Task<string>> getZip, CancellationToken ct, bool deleteZip = true)
    {
        var started = DateTime.UtcNow;
        try
        {
            await _downLog.StartAsync(file, ct);
            var zipPath = await getZip();
            await _downLog.SetLoadingAsync(file, ct);
            var rows = file.Kind == NppesFileKind.Deactivation
                ? await new DeactivationLoader(database, log, options.MinRowRatio).LoadAsync(zipPath, file, ct)
                : await new NpiDataLoader(database, log, options.MinRowRatio).LoadAsync(zipPath, file, ct);
            await _downLog.CompleteAsync(file, rows, ct);
            log.Information("Completed {File}: {Rows:N0} rows in {Duration}", file.FileName, rows, Elapsed(started));
            if (deleteZip)
            {
                CleanWorkFolder(zipPath);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.Error(ex, "Failed {File} after {Duration}", file.FileName, Elapsed(started));
            await TryMarkFailedAsync(file, ex.Message);
            return false;
        }
        catch (OperationCanceledException)
        {
            await TryMarkFailedAsync(file, "Cancelled");
            throw;
        }
    }

    private async Task TryMarkFailedAsync(NppesFile file, string error)
    {
        try
        {
            await _downLog.FailAsync(file, error, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Could not record the failure of {File} in downlog", file.FileName);
        }
    }

    // After a successful load: delete the loaded zip, or keep it and delete the older NPPES zips.
    private void CleanWorkFolder(string loadedZip)
    {
        var folder = Path.GetDirectoryName(loadedZip)!;
        foreach (var zip in Directory.EnumerateFiles(folder, "*.zip"))
        {
            var isLoaded = string.Equals(Path.GetFullPath(zip), Path.GetFullPath(loadedZip), StringComparison.OrdinalIgnoreCase);
            if ((isLoaded && options.KeepLastZip) || NppesFileClassifier.Classify(Path.GetFileName(zip)) is null)
            {
                continue;
            }

            try
            {
                File.Delete(zip);
                log.Debug("Deleted {Zip}", zip);
            }
            catch (IOException ex)
            {
                log.Warning(ex, "Could not delete {Zip}", zip);
            }
        }
    }

    private async Task<LoadPlan> PlanAsync(CancellationToken ct)
    {
        var pageUrl = new Uri(options.NppesPageUrl);
        var html = await http.GetStringAsync(pageUrl, ct);
        var links = NppesPage.ParseZipLinks(html, pageUrl);
        if (links.Count == 0)
        {
            throw new InvalidDataException($"No .zip links found on {pageUrl}; the page layout may have changed.");
        }

        return LoadPlanner.Plan(links, await _downLog.GetCompletedAsync(ct));
    }

    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await using var connection = await database.OpenAsync(ct);
        var localInfile = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT @@GLOBAL.local_infile", cancellationToken: ct));
        if (localInfile != 1)
        {
            throw new InvalidOperationException(
                "MySQL has local_infile=OFF, so bulk loading is impossible. Add 'local_infile=ON' under [mysqld] in my.ini " +
                @"(C:\ProgramData\MySQL\MySQL Server 8.4\my.ini) and restart the MySQL84 service.");
        }
    }

    private async Task EnsureMigratedAsync(CancellationToken ct)
    {
        var pending = await new MigrationRunner(database, log).GetPendingAsync(ct);
        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                $"{pending.Count} migration(s) not applied ({string.Join(", ", pending.Select(m => $"{m.Version:D3}_{m.Name}"))}). Run: Npi.Loader migrate");
        }
    }

    private static string Elapsed(DateTime startedUtc) => (DateTime.UtcNow - startedUtc).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
}
