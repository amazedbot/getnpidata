using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using MySqlConnector;
using Npi.Loader.Csv;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>One of the two yearly CMS datasets kept for every older data year (<see cref="MedicareHistorySource"/>).</summary>
public sealed record MedicareHistoryPart(char Code, string Title, int Bit, string Column, string Label, IReadOnlyList<MedicareHistoryLayout> Layouts)
{
    /// <summary>Medicare Physician &amp; Other Practitioners - by Provider: Part B billing, with the number of patients (Tot_Benes).</summary>
    public static MedicareHistoryPart PartB { get; } = new('B', "Medicare Physician & Other Practitioners - by Provider", 1, "part_b_beneficiaries", "Part B",
    [
        new("2021 on (every year since CMS's 2021 re-release)", "Rndrng_NPI", "Tot_Benes"),
        new("Physician and Other Supplier PUF (before 2021)", "npi", "total_unique_benes"),
    ]);

    /// <summary>Medicare Part D Prescribers - by Provider: Part D prescribing, with the number of claims (Tot_Clms).</summary>
    public static MedicareHistoryPart PartD { get; } = new('D', "Medicare Part D Prescribers - by Provider", 2, "part_d_claims", "Part D",
    [
        new("2021 on (every year since CMS's 2021 re-release)", "Prscrbr_NPI", "Tot_Clms"),
        new("Part D Prescriber Summary PUF (before 2021)", "npi", "total_claim_count"),
    ]);

    public static IReadOnlyList<MedicareHistoryPart> All { get; } = [PartB, PartD];

    /// <summary>The columns to load from a file with this header: the first layout whose two columns it has.</summary>
    /// <exception cref="InvalidDataException">No known layout: CMS renamed its columns again, so a layout must be added.</exception>
    public IReadOnlyList<CsvColumn> ColumnsFor(IReadOnlyList<string> header, string fileName)
    {
        var names = header.Select(h => h.Trim().TrimStart('﻿').Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var layout = Layouts.FirstOrDefault(l => names.Contains(l.NpiHeader) && names.Contains(l.CountHeader))
                     ?? throw new InvalidDataException(
                         $"{fileName} ({Label}) has an unknown layout: none of {string.Join(" / ", Layouts.Select(l => $"{l.NpiHeader} + {l.CountHeader}"))} " +
                         $"in its header ({string.Join(", ", header.Take(12))}{(header.Count > 12 ? ", …" : "")}).");
        return [new(layout.NpiHeader, "npi", CsvValue.Npi), new(layout.CountHeader, "value", CsvValue.OptionalNumber)];
    }
}

/// <summary>A column layout CMS used for a dataset: the NPI column and the count kept.</summary>
public sealed record MedicareHistoryLayout(string Name, string NpiHeader, string CountHeader);

/// <summary>One older data year of one part, as the catalog lists it.</summary>
public sealed record MedicareHistoryFile(MedicareHistoryPart Part, int Year, DatasetRelease Release);

/// <summary>
/// Older Medicare years (docs/PRODUCT_PLAN.md Phase 3 "Older Medicare years"; migration 100): for every data year from
/// <see cref="FirstYear"/> to the year before the newest, which NPIs billed Part B ("Medicare Physician &amp; Other Practitioners - by
/// Provider", ~330–500 MB a year) or prescribed under Part D ("Medicare Part D Prescribers - by Provider", ~560–760 MB a year), with
/// the patients and the claims → <c>medicare_activity_year</c>. The newest year stays with <see cref="MedicareUtilizationSource"/>
/// and <see cref="PartDPrescriberSource"/>; when CMS publishes a newer one, the year before joins the history at the next check.
/// Old years don't change, so each (part, year) is loaded once and again only when the catalog lists another file for it
/// (<c>medicare_history_version</c>); only the NPIs whose rows changed are rewritten and queued for the record quality.
/// </summary>
public sealed class MedicareHistorySource : DatasetSource
{
    public const int FirstYear = 2013;

    public const string Table = "medicare_activity_year";

    public const string VersionTable = "medicare_history_version";

    public override string Name => "medicare_history";

    /// <summary>22 files of 0.3–0.8 GB on a first load (speed item 7: at most one heavy load at a time).</summary>
    public override bool Heavy => true;

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var files = Files(await context.CmsCatalogAsync(ct));
        return new DatasetRelease(Version(files), new Uri(context.Options.CmsCatalogUrl));
    }

    /// <summary>The older years of both parts the catalog lists: from <see cref="FirstYear"/>, before each part's newest year.</summary>
    public static IReadOnlyList<MedicareHistoryFile> Files(CmsCatalog catalog) =>
        MedicareHistoryPart.All.SelectMany(part =>
        {
            var years = catalog.FindYears(part.Title);
            var newest = years.Count == 0 ? 0 : years[^1].DataYear!.Value;
            return years.Where(r => r.DataYear >= FirstYear && r.DataYear < newest).Select(r => new MedicareHistoryFile(part, r.DataYear!.Value, r));
        }).ToList();

    /// <summary>The source's version: the years covered and a hash of every file, so that any new or changed year shows.</summary>
    public static string Version(IReadOnlyList<MedicareHistoryFile> files)
    {
        if (files.Count == 0)
        {
            return "no older years";
        }

        var lines = string.Join("\n", files.OrderBy(f => f.Part.Code).ThenBy(f => f.Year).Select(f => $"{f.Part.Code} {f.Year} {f.Release.Version}"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(lines)))[..12];
        return string.Create(CultureInfo.InvariantCulture, $"{files.Min(f => f.Year)}-{files.Max(f => f.Year)} {hash}");
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var files = Files(await context.CmsCatalogAsync(ct));
        Dictionary<(char, int), string> stored;
        await using (var connection = await context.Database.OpenAsync(ct))
        {
            stored = (await connection.QueryAsync<(string Part, short Year, string Version)>(new CommandDefinition(
                    $"SELECT `part`, `data_year`, `version` FROM `{VersionTable}`", cancellationToken: ct)))
                .ToDictionary(r => (r.Part[0], (int)r.Year), r => r.Version);
        }

        var due = files.Where(f => stored.GetValueOrDefault((f.Part.Code, f.Year)) != f.Release.Version).ToList();
        context.Log.Information("Medicare history: {Due} of {Files} older-year files new or changed{List}", due.Count, files.Count,
            due.Count == 0 ? "" : ": " + string.Join(", ", due.Select(f => $"{f.Part.Label} {f.Year}")));
        // A year that fails keeps its rows and version and doesn't stop the others; the next run tries it again.
        var failed = new List<string>();
        foreach (var file in due)
        {
            try
            {
                var path = await context.DownloadAsync(file.Release, $"medicare_history_{file.Part.Code}_{file.Year}.csv", ct);
                await using (var connection = await context.Database.OpenAsync(ct))
                {
                    var (rows, changed) = await LoadYearAsync(connection, file, path, context.Options.MinRowRatio, ct);
                    context.Log.Information("Medicare history {Part} {Year}: {Rows:N0} NPIs, {Changed:N0} changed", file.Part.Label, file.Year, rows, changed);
                }

                FileDownloader.Delete(path); // kept after a failed load: the next attempt downloads it again only if CMS changed it
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                context.Log.Error(ex, "Medicare history {Part} {Year} failed", file.Part.Label, file.Year);
                failed.Add($"{file.Part.Label} {file.Year}: {ex.Message}");
            }
        }

        if (failed.Count > 0)
        {
            throw new InvalidDataException($"Medicare history: {failed.Count} of {due.Count} files failed ({string.Join("; ", failed)}).");
        }

        await using var count = await context.Database.OpenAsync(ct);
        return await Database.CountAsync(count, Table, ct);
    }

    /// <summary>
    /// Replaces one part of one year with the file at <paramref name="path"/>: the NPIs whose value differs (or that come or go) are
    /// queued for the record quality and rewritten, in one transaction with the year's new version; the other NPIs aren't touched.
    /// </summary>
    /// <returns>The NPIs in the file and the NPIs that changed.</returns>
    public static async Task<(long Rows, long Changed)> LoadYearAsync(MySqlConnection connection, MedicareHistoryFile file, string path, double minRowRatio,
        CancellationToken ct)
    {
        const string raw = "medicare_history_raw_scratch";
        const string incoming = "medicare_history_new_scratch";
        const string changed = "medicare_history_changed_scratch";
        Task RunAsync(string sql, object? param = null, MySqlTransaction? tx = null) =>
            connection.ExecuteAsync(new CommandDefinition(sql, param, tx, commandTimeout: 0, cancellationToken: ct));

        IReadOnlyList<CsvColumn> columns;
        await using (var stream = File.OpenRead(path))
        {
            columns = file.Part.ColumnsFor(CsvHeader.Read(stream).Columns, Path.GetFileName(path));
        }

        var part = file.Part;
        var year = new { year = file.Year };
        try
        {
            foreach (var table in new[] { raw, incoming, changed })
            {
                await RunAsync($"DROP TABLE IF EXISTS `{table}`");
            }

            await RunAsync($"CREATE TABLE `{raw}` (`npi` CHAR(10) NULL, `value` DOUBLE NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci");
            await RunAsync($"CREATE TABLE `{incoming}` (`npi` CHAR(10) NOT NULL PRIMARY KEY, `value` INT NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci");
            await RunAsync($"CREATE TABLE `{changed}` (`npi` CHAR(10) NOT NULL PRIMARY KEY) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci");

            // Only the NPI and one count are kept, both ASCII, so the file's other text never has to decode (latin1 takes any byte).
            await CsvTableLoader.LoadAsync(connection, path, raw, columns, ct, characterSet: "latin1");
            await RunAsync($"INSERT INTO `{incoming}` (`npi`, `value`) SELECT `npi`, ROUND(MAX(`value`)) FROM `{raw}` WHERE `npi` IS NOT NULL GROUP BY `npi`");
            var rows = await Database.CountAsync(connection, incoming, ct);
            var current = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                $"SELECT COUNT(*) FROM `{Table}` WHERE `data_year` = @year AND `parts` & {part.Bit} <> 0", year, commandTimeout: 0, cancellationToken: ct));
            if (current > 0 && rows < current * minRowRatio)
            {
                throw new InvalidDataException(
                    $"{part.Label} {file.Year} has {rows:N0} NPIs, fewer than {minRowRatio:P0} of the current {current:N0}; not replacing it.");
            }

            // New, gone or with another count in this part (the other part's rows of the year stay as they are).
            await RunAsync(
                $"""
                INSERT INTO `{changed}` (`npi`)
                SELECT n.`npi` FROM `{incoming}` n LEFT JOIN `{Table}` a ON a.`data_year` = @year AND a.`npi` = n.`npi`
                WHERE a.`npi` IS NULL OR a.`parts` & {part.Bit} = 0 OR NOT (a.`{part.Column}` <=> n.`value`)
                """, year);
            await RunAsync(
                $"""
                INSERT IGNORE INTO `{changed}` (`npi`)
                SELECT a.`npi` FROM `{Table}` a LEFT JOIN `{incoming}` n ON n.`npi` = a.`npi`
                WHERE a.`data_year` = @year AND a.`parts` & {part.Bit} <> 0 AND n.`npi` IS NULL
                """, year);
            var changes = await Database.CountAsync(connection, changed, ct);

            // Before the rewrite, so a failure leaves the current rows (and an at most too long queue); not before the first labels.
            if (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    $"SELECT EXISTS (SELECT 1 FROM `{Projection.QualityBuilder.Source}`)", cancellationToken: ct)) != 0)
            {
                await RunAsync($"INSERT IGNORE INTO `{Projection.QualityBuilder.Pending}` (`npi`) SELECT `npi` FROM `{changed}`");
            }

            await using var transaction = await connection.BeginTransactionAsync(ct);
            await RunAsync(
                $"""
                UPDATE `{Table}` a JOIN `{changed}` c ON c.`npi` = a.`npi`
                SET a.`parts` = a.`parts` & ~{part.Bit}, a.`{part.Column}` = NULL WHERE a.`data_year` = @year
                """, year, transaction);
            await RunAsync(
                $"""
                INSERT INTO `{Table}` (`data_year`, `npi`, `parts`, `{part.Column}`)
                SELECT * FROM (SELECT @year AS `data_year`, n.`npi`, {part.Bit} AS `parts`, n.`value` FROM `{incoming}` n JOIN `{changed}` c ON c.`npi` = n.`npi`) s
                ON DUPLICATE KEY UPDATE `parts` = `{Table}`.`parts` | {part.Bit}, `{part.Column}` = s.`value`
                """, year, transaction);
            await RunAsync(
                $"DELETE a FROM `{Table}` a JOIN `{changed}` c ON c.`npi` = a.`npi` WHERE a.`data_year` = @year AND a.`parts` = 0", year, transaction);
            await RunAsync(
                $"""
                INSERT INTO `{VersionTable}` (`part`, `data_year`, `version`, `rows_loaded`, `loaded_at`)
                VALUES (@part, @year, @version, @rows, UTC_TIMESTAMP()) AS new
                ON DUPLICATE KEY UPDATE `version` = new.`version`, `rows_loaded` = new.`rows_loaded`, `loaded_at` = new.`loaded_at`
                """, new { part = part.Code.ToString(), year = file.Year, version = file.Release.Version, rows }, transaction);
            await transaction.CommitAsync(ct);
            return (rows, changes);
        }
        finally
        {
            foreach (var table in new[] { raw, incoming, changed })
            {
                await connection.ExecuteAsync(new CommandDefinition($"DROP TABLE IF EXISTS `{table}`", cancellationToken: CancellationToken.None));
            }
        }
    }
}
