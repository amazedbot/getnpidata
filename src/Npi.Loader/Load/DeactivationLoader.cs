using System.Globalization;
using System.IO.Compression;
using System.Text;
using ExcelDataReader;
using MySqlConnector;
using Npi.Core;
using Npi.Loader.Db;
using Npi.Loader.Nppes;
using Serilog;

namespace Npi.Loader.Load;

public sealed record DeactivatedNpi(string Npi, DateOnly DeactivationDate);

/// <summary>Reads the NPPES Deactivated NPI Report spreadsheet (CLAUDE.md §7 Stage 1.7).</summary>
public static class DeactivationReport
{
    static DeactivationReport() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Reads (NPI, deactivation date) rows. The data starts at the first row whose first cell is a
    /// 10-digit NPI; the title and header rows above it are not counted (legacy defect #6). After that
    /// every non-blank row must be valid, otherwise the report is rejected.
    /// </summary>
    public static IReadOnlyList<DeactivatedNpi> Read(Stream xlsx)
    {
        using var reader = ExcelReaderFactory.CreateReader(xlsx);
        var rows = new List<DeactivatedNpi>();
        var seen = new HashSet<string>();
        var started = false;
        var rowNumber = 0;
        while (reader.Read())
        {
            rowNumber++;
            var npi = CellText(reader, 0);
            if (!started)
            {
                if (!InputFormats.IsNpi(npi))
                {
                    continue;
                }

                started = true;
            }

            if (string.IsNullOrEmpty(npi) && string.IsNullOrEmpty(CellText(reader, 1)))
            {
                continue;
            }

            if (!InputFormats.IsNpi(npi))
            {
                throw new InvalidDataException($"Row {rowNumber}: '{npi}' is not a 10-digit NPI.");
            }

            var date = ParseDate(reader.FieldCount > 1 ? reader.GetValue(1) : null)
                ?? throw new InvalidDataException($"Row {rowNumber}: NPI {npi} has no valid deactivation date.");
            if (seen.Add(npi!))
            {
                rows.Add(new DeactivatedNpi(npi!, date));
            }
        }

        return started ? rows : throw new InvalidDataException("The deactivation report contains no NPI rows.");
    }

    private static string? CellText(IExcelDataReader reader, int column)
    {
        if (column >= reader.FieldCount)
        {
            return null;
        }

        return reader.GetValue(column) switch
        {
            null => null,
            double d => d.ToString("0", CultureInfo.InvariantCulture),
            var v => Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim(),
        };
    }

    private static DateOnly? ParseDate(object? value) => value switch
    {
        DateTime dt => DateOnly.FromDateTime(dt),
        double oa => DateOnly.FromDateTime(DateTime.FromOADate(oa)),
        string s when DateOnly.TryParseExact(s.Trim(), ["MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
        _ => null,
    };
}

/// <summary>
/// Replaces <c>nppes_deactivated_npi_report</c> with the latest report (it lists every currently
/// deactivated NPI, so a reactivated NPI drops out), then refreshes the npidata flags.
/// </summary>
public sealed class DeactivationLoader(Database database, ILogger log, double minRowRatio)
{
    private const string Table = "nppes_deactivated_npi_report";

    public async Task<long> LoadAsync(string zipPath, NppesFile file, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Where(e => e.FullName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Count != 1)
        {
            throw new InvalidDataException($"{file.FileName} should contain exactly one .xlsx file, found {entries.Count}.");
        }

        IReadOnlyList<DeactivatedNpi> rows;
        using (var buffer = new MemoryStream())
        {
            // ExcelDataReader needs a seekable stream; the report is a few MB.
            await using (var entryStream = entries[0].Open())
            {
                await entryStream.CopyToAsync(buffer, ct);
            }

            buffer.Position = 0;
            rows = DeactivationReport.Read(buffer);
        }

        log.Information("Read {Rows:N0} deactivated NPIs from {Entry}", rows.Count, entries[0].Name);

        await using var connection = await database.OpenAsync(ct);
        var current = await Database.CountAsync(connection, Table, ct);
        if (current > 0 && rows.Count < current * minRowRatio)
        {
            throw new InvalidDataException(
                $"The report has {rows.Count:N0} NPIs, fewer than {minRowRatio:P0} of the current {current:N0}; not replacing it.");
        }

        await Database.ExecuteAsync(connection, "DROP TABLE IF EXISTS `nppes_deactivated_npi_report_staging`", ct);
        await Database.ExecuteAsync(connection, "CREATE TABLE `nppes_deactivated_npi_report_staging` LIKE `nppes_deactivated_npi_report`", ct);

        // Values are validated digits and ISO dates, so a tab-separated stream needs no quoting.
        var tsv = new StringBuilder(rows.Count * 22);
        foreach (var row in rows)
        {
            tsv.Append(row.Npi).Append('\t').Append(row.DeactivationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        }

        await using (var source = new MemoryStream(Encoding.UTF8.GetBytes(tsv.ToString())))
        {
            var loader = new MySqlBulkLoader(connection)
            {
                TableName = "`nppes_deactivated_npi_report_staging`",
                Local = true,
                SourceStream = source,
                FieldTerminator = "\t",
                LineTerminator = "\n",
            };
            loader.Columns.AddRange(["`NPI`", "`NPPES_Deactivation_Date`"]);
            await StagingLoader.WithWarningsAsFailures(connection, entries[0].Name, () => loader.LoadAsync(ct));
        }

        await Database.ExecuteAsync(connection, "DROP TABLE IF EXISTS `nppes_deactivated_npi_report_old`", ct);
        await Database.ExecuteAsync(connection,
            "RENAME TABLE `nppes_deactivated_npi_report` TO `nppes_deactivated_npi_report_old`, `nppes_deactivated_npi_report_staging` TO `nppes_deactivated_npi_report`", ct);
        await Database.ExecuteAsync(connection, "DROP TABLE `nppes_deactivated_npi_report_old`", ct);

        var flagged = await Deactivations.ApplyAsync(connection, scopeTable: null, ct);
        log.Information("Deactivation flags refreshed: {Rows:N0} rows changed", flagged);
        await Database.ExecuteAsync(connection,
            "INSERT INTO `extractlog` (`ZipFileName`, `ExtractFileName`, `rows_loaded`) VALUES (@zip, @entry, @rows)", ct,
            param: new { zip = file.FileName, entry = entries[0].Name, rows = rows.Count });
        return rows.Count;
    }
}
