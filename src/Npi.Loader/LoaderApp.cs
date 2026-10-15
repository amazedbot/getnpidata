using System.Globalization;
using Dapper;
using Npi.Loader.Datasets;
using Npi.Loader.Db;
using Npi.Loader.Geocoding;
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

        var downloader = new Downloader(http, log, options.DownloadAttempts, parts: options.DownloadParts, partMinBytes: options.DownloadPartMinBytes);
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

        // Stage 5.5: external datasets joined to NPIs, each reloaded only when its publisher released a new version.
        var datasetsOk = await new DatasetLoader(options, database, http, log).RefreshAsync(force: false, only: null, ct);

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

        // Stage 5.5 item 10: Overture places the new practice addresses at their building (every state for a new release),
        // before the address types, the map and the quality labels read them.
        var overtureFull = false;
        if (projectionOk)
        {
            try
            {
                overtureFull = (await new OvertureMatcher(database, http, log, options).RefreshAsync(ct))?.Full == true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.Error(ex, "Placing new practice addresses with Overture failed");
                datasetsOk = false;
            }
        }

        // Stage 5.5 item 12 (standardized credentials), item 18 (practice address types: new addresses, providers at an address) and
        // the area pages' counts (docs/PRODUCT_PLAN.md step 1.6), each brought up to date after the projection, side by side
        // (speed item 13). The map is built next: the credentials leave provider_map_credential to it, so it is filled once.
        if (projectionOk)
        {
            projectionOk = await BuilderSteps.RunAsync(AfterProjection(full: false, onlyIfStale: true, leaveMapCells: !options.SkipMapBuild),
                options.ParallelBuilders, log, ct);
        }

        // Stage 5.5 item 10: geocode new practice addresses (a bounded number per run) and refresh the map table.
        var mapOk = await RefreshMapAsync(options.GeocodeBatchesPerRun, ct, fullMap: overtureFull);

        // Stage 5.5 item 18: the FEMA buildings of the addresses mapped or moved since the last match, read in place, and their labels.
        if (projectionOk && mapOk)
        {
            try
            {
                await FemaStructureSource.MatchNewAsync(options, database, http, log, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.Error(ex, "Matching new practice addresses to FEMA buildings failed");
                datasetsOk = false;
            }
        }

        // Record quality (docs/PRODUCT_PLAN.md step 3.1): relabelled after the projection, the map and the datasets it reads.
        var quality = new QualityBuilder(database, log, options.MinRowRatio);
        if (projectionOk && await quality.IsStaleAsync(ct))
        {
            try
            {
                await quality.BuildAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.Error(ex, "Labelling the record quality failed");
                projectionOk = false;
            }
        }

        // The ready-made file of every active provider (owner, 2026-10-14), when the load changed its data. A failure keeps the
        // previous file; the site keeps serving it.
        var export = new FullExportBuilder(database, log, options);
        if (projectionOk && await export.IsStaleAsync(ct))
        {
            try
            {
                await export.BuildAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.Error(ex, "Writing the full export file failed");
                projectionOk = false;
            }
        }

        log.Information("Run finished: {Ok} NPPES file(s) completed, {Failed} failed; reference data {Reference}; datasets {Datasets}; projection {Projection}; map {Map}",
            plan.Files.Count - failures, failures, referenceOk ? "current" : "FAILED", datasetsOk ? "current" : "FAILED", projectionOk ? "current" : "FAILED",
            mapOk ? "current" : "FAILED");
        return failures == 0 && referenceOk && datasetsOk && projectionOk && mapOk ? 0 : 1;
    }

    /// <summary>
    /// The <c>sample</c> command, for a quick try of the site: the newest weekly NPPES file only (~50,000 providers, minutes),
    /// the reference data, the search tables, standardized credentials, address types, area counts, the map (Census-geocoded,
    /// a bounded number of addresses) and the record-quality labels. The Stage 5.5 datasets, Overture and FEMA are skipped.
    /// The weekly is recorded as <c>Sample</c>, not Completed, so a later <c>run</c> loads the monthly and then this weekly again.
    /// </summary>
    public async Task<int> SampleAsync(CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var pageUrl = new Uri(options.NppesPageUrl);
        var weekly = NppesPage.ParseZipLinks(await http.GetStringAsync(pageUrl, ct), pageUrl)
            .Select(link => new PlannedFile(NppesFileClassifier.Classify(link.FileName)!, link.Url))
            .Where(x => x.File is { Kind: NppesFileKind.Weekly })
            .MaxBy(x => x.File.FileDate);
        if (weekly is null)
        {
            log.Error("No weekly file is listed on {Page}", pageUrl);
            return 1;
        }

        var downloader = new Downloader(http, log, options.DownloadAttempts, parts: options.DownloadParts, partMinBytes: options.DownloadPartMinBytes);
        if (!await ProcessAsync(weekly.File, () => downloader.DownloadAsync(weekly.Url, weekly.File.FileName, options.ResolvedWorkFolder, ct), ct))
        {
            return 1;
        }

        await _downLog.SetSampleAsync(weekly.File, ct);
        var referenceOk = await new ReferenceLoader(options, database, http, log).RefreshAllAsync(force: false, ct);
        await new ProjectionBuilder(database, log, options.MinRowRatio).BuildAsync(ct, full: true);
        if (!await BuilderSteps.RunAsync(AfterProjection(full: true, onlyIfStale: false, leaveMapCells: !options.SkipMapBuild, everyAddressType: true), options.ParallelBuilders, log, ct))
        {
            return 1;
        }

        var mapOk = await RefreshMapAsync(options.GeocodeBatchesPerRun, ct);
        await new QualityBuilder(database, log, options.MinRowRatio).BuildAsync(ct, allowIncremental: false);
        await new FullExportBuilder(database, log, options).BuildAsync(ct);
        log.Information("Sample loaded from {File}. A full load: Npi.Loader run (it loads the monthly file, then this weekly again)", weekly.File.FileName);
        return referenceOk && mapOk ? 0 : 1;
    }

    /// <summary>
    /// The <c>geocode</c> command: geocode every practice address not geocoded yet, then rebuild the map table.
    /// With a county FIPS, only that county's addresses (to try the map in one area before the whole backlog).
    /// </summary>
    public async Task<int> GeocodeAsync(string? countyFips, CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        if (countyFips is not null)
        {
            return await RefreshMapAsync(maxBatches: 0, ct, countyFips) ? 0 : 1;
        }

        // A backlog of millions takes hours: build the map table first so the map page works meanwhile, with
        // not-yet-geocoded addresses at their ZIP centroid.
        var map = new MapBuilder(database, log, options.MinRowRatio);
        if (!options.SkipMapBuild && await map.IsStaleAsync(ct))
        {
            await map.BuildAsync(ct);
        }

        return await RefreshMapAsync(maxBatches: 0, ct) ? 0 : 1;
    }

    /// <summary>
    /// The <c>overture</c> command: place practice addresses with Overture data, then rebuild the map tables. The area is a
    /// 5-digit county FIPS, a 2-letter state code, or null for every state.
    /// </summary>
    public async Task<int> OvertureAsync(string? area, CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        var matcher = new OvertureMatcher(database, http, log, options);
        var ok = true;
        if (area is null)
        {
            ok = (await matcher.MatchAllAsync(ct)).Failed.Count == 0;
        }
        else if (area.Length == 2)
        {
            await matcher.MatchStateAsync(area, ct);
        }
        else
        {
            await matcher.MatchCountyAsync(area, ct);
        }

        if (!options.SkipMapBuild)
        {
            // Every state's points may have moved: a full build is quicker than replacing them provider by provider.
            await new MapBuilder(database, log, options.MinRowRatio).BuildAsync(ct, full: area is null);
        }

        return ok ? 0 : 1;
    }

    /// <param name="fullMap">Rebuild every map point (after Overture placed every state's addresses).</param>
    private async Task<bool> RefreshMapAsync(int maxBatches, CancellationToken ct, string? countyFips = null, bool fullMap = false)
    {
        try
        {
            var census = new CensusGeocoder(http, options.CensusGeocoderUrl, options.GeocodeBenchmark);
            var outcome = await new AddressGeocoder(database, census, log, options.GeocodeBatchSize, options.GeocodeParallelism)
                .GeocodePendingAsync(maxBatches, ct, countyFips);
            var map = new MapBuilder(database, log, options.MinRowRatio);
            if (options.SkipMapBuild)
            {
                log.Information("Map table not rebuilt (SkipMapBuild)");
            }
            else if (fullMap || outcome.Geocoded > 0 || await map.IsStaleAsync(ct))
            {
                await map.BuildAsync(ct, full: fullMap);
            }
            else
            {
                log.Information("Map table is current");
            }

            if (outcome.Pending > outcome.Geocoded)
            {
                log.Information("{Left:N0} addresses are still to be geocoded; they show at their ZIP centroid until then (Npi.Loader geocode clears the backlog)",
                    outcome.Pending - outcome.Geocoded);
            }

            return outcome.Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.Error(ex, "Geocoding or building the map table failed");
            return false;
        }
    }

    /// <summary>
    /// The <c>project</c> command: rebuild the whole search projection now, or with <paramref name="full"/> false only the NPIs the
    /// loads queued when they are few (as <c>run</c> does); then the tables built on it.
    /// </summary>
    public async Task<int> ProjectAsync(bool full, CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await new ProjectionBuilder(database, log, options.MinRowRatio).BuildAsync(ct, full);
        // No map build follows here: the credentials keep provider_map_credential current themselves.
        if (!await BuilderSteps.RunAsync(AfterProjection(full, onlyIfStale: false, leaveMapCells: false), options.ParallelBuilders, log, ct))
        {
            return 1;
        }

        await new QualityBuilder(database, log, options.MinRowRatio).BuildAsync(ct, allowIncremental: !full);
        return 0;
    }

    /// <summary>
    /// The builders after the projection that read none of each other's tables (<see cref="BuilderSteps"/>), run side by side.
    /// </summary>
    /// <param name="full">Rebuild each in full; otherwise each takes its incremental path when it can.</param>
    /// <param name="onlyIfStale">Skip a builder with nothing to do (<c>run</c>). The address types are labelled only when stale
    /// anyway (before a FEMA load there is nothing to label), unless <paramref name="everyAddressType"/>.</param>
    /// <param name="leaveMapCells">The map is built after these in the same command: it fills provider_map_credential.</param>
    /// <param name="everyAddressType">Label the addresses even without FEMA buildings (<c>sample</c>: by the business signs alone).</param>
    private IReadOnlyList<BuilderStep> AfterProjection(bool full, bool onlyIfStale, bool leaveMapCells, bool everyAddressType = false)
    {
        var credentials = new CredentialBuilder(database, log, options.MinRowRatio);
        var addressTypes = new AddressTypeBuilder(database, log, options.MinRowRatio);
        var areas = new AreaCountBuilder(database, log, options.MinRowRatio);
        return
        [
            new("Standardized credentials", onlyIfStale ? credentials.IsStaleAsync : null, c => credentials.BuildAsync(c, full, leaveMapCells)),
            new("Practice address types", everyAddressType ? null : addressTypes.IsStaleAsync, async c => await addressTypes.BuildAsync(c, full)),
            new("Area counts", onlyIfStale ? areas.IsStaleAsync : null, async c => await areas.BuildAsync(c, full)),
        ];
    }

    /// <summary>The <c>export</c> command: write the ready-made file of every active provider now.</summary>
    public async Task<int> ExportAsync(CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await new FullExportBuilder(database, log, options).BuildAsync(ct);
        return 0;
    }

    /// <summary>The <c>quality</c> command: relabel the records whose state licenses changed, or every record when nothing is queued or another input changed.</summary>
    public async Task<int> QualityAsync(CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await new QualityBuilder(database, log, options.MinRowRatio).BuildAsync(ct);
        return 0;
    }

    /// <summary>The <c>areas</c> command: recount the providers per area and specialty now.</summary>
    public async Task<int> AreasAsync(CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await new AreaCountBuilder(database, log, options.MinRowRatio).BuildAsync(ct, full: true);
        return 0;
    }

    /// <summary>The <c>credentials</c> command: rebuild the standardized credential tables now.</summary>
    public async Task<int> CredentialsAsync(CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await new CredentialBuilder(database, log, options.MinRowRatio).BuildAsync(ct, full: true);
        return 0;
    }

    /// <summary>The <c>address-types</c> command: relabel the practice addresses from the loaded FEMA buildings now.</summary>
    public async Task<int> AddressTypesAsync(CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await new AddressTypeBuilder(database, log, options.MinRowRatio).BuildAsync(ct, full: true);
        return 0;
    }

    /// <summary>The <c>reference</c> command: reload NUCC, HUD and Census data even if unchanged.</summary>
    public async Task<int> ReferenceAsync(CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        return await new ReferenceLoader(options, database, http, log).RefreshAllAsync(force: true, ct) ? 0 : 1;
    }

    /// <summary>The <c>datasets</c> command: reload every Stage 5.5 dataset (or the named one) even if unchanged.</summary>
    public async Task<int> DatasetsAsync(string? only, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        return await new DatasetLoader(options, database, http, log).RefreshAsync(force: true, only, ct) ? 0 : 1;
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
