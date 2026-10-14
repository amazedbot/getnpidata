using Dapper;
using Npi.Core.Search;
using Npi.Loader.Db;
using Serilog;

namespace Npi.Loader.Projection;

/// <summary>
/// Rebuilds the ready-made file of every active provider (<see cref="FullExport"/>; owner, 2026-10-14) when its data changed
/// since it was written: the projection, the standardized credentials, the address types or the record quality was rebuilt or
/// updated, or a dataset behind the summary flags (OIG exclusions, opt-outs, order and refer, Care Compare, Medicare billing)
/// reloaded. ~9.5M rows read from <see cref="LoaderOptions.ExportParts"/> NPI ranges at once.
/// </summary>
public sealed class FullExportBuilder(Database database, ILogger log, LoaderOptions options)
{
    public const string Source = "full_export";

    // Builders whose incremental updates move only checked_at, so both times count.
    private static readonly string[] Builders = [CredentialBuilder.Source, AddressTypeBuilder.Source, QualityBuilder.Source];

    // Datasets the summary flags read (ProviderFlags): their reloads count.
    private static readonly string[] FlagSources = ["oig_leie", "cms_opt_out", "cms_order_referring", "cc_clinicians", "cms_physician_by_provider"];

    /// <summary>True when there is no file, or its data changed since it was written.</summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        if (FullExport.Read(options.ResolvedExportFolder) is null)
        {
            return true;
        }

        await using var connection = await database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT NOT EXISTS (SELECT 1 FROM `reference_data` WHERE `source` = @source)
                OR (SELECT `loaded_at` FROM `reference_data` WHERE `source` = @source)
                   < GREATEST(COALESCE((SELECT `projected_at` FROM `data_version` WHERE `id` = 1), '1970-01-01'),
                              COALESCE((SELECT MAX(GREATEST(`loaded_at`, `checked_at`)) FROM `reference_data` WHERE `source` IN @builders), '1970-01-01'),
                              COALESCE((SELECT MAX(`loaded_at`) FROM `reference_data` WHERE `source` IN @flags), '1970-01-01'))
            """, new { source = Source, builders = Builders, flags = FlagSources }, cancellationToken: ct)) != 0;
    }

    public async Task BuildAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        await using var connection = await database.OpenAsync(ct);
        var asOf = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition("SELECT `as_of_date` FROM `data_version` WHERE `id` = 1", cancellationToken: ct));
        var cs = database.ConnectionString;
        var search = new SearchService(cs, new TaxonomyCatalog(cs), new CredentialCatalog(cs));
        var info = await FullExport.WriteAsync(search, options.ResolvedExportFolder, asOf is null ? null : DateOnly.FromDateTime(asOf.Value), options.ExportParts, ct);
        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
            VALUES (@source, @version, @path, @rows, @started, UTC_TIMESTAMP()) AS new
            ON DUPLICATE KEY UPDATE `version` = new.`version`, `source_url` = new.`source_url`, `rows_loaded` = new.`rows_loaded`,
              `loaded_at` = new.`loaded_at`, `checked_at` = new.`checked_at`
            """, ct, param: new
            {
                source = Source, version = "as of " + (info.AsOf?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"),
                path = FullExport.FileName, rows = info.Rows, started,
            });
        log.Information("Full export: {Rows:N0} providers, {Megabytes:N0} MB gzipped, in {Seconds:N0}s ({Folder})",
            info.Rows, info.Bytes / 1_000_000, (DateTime.UtcNow - started).TotalSeconds, options.ResolvedExportFolder);
    }
}
