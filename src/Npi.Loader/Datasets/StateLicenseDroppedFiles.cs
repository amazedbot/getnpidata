using System.Data.OleDb;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;

namespace Npi.Loader.Datasets;

/// <summary>
/// A state list read from files the owner downloads into <see cref="LoaderOptions.StateLicenseFilesFolder"/>\&lt;State&gt;
/// (boards whose downloads don't work outside a browser, or that send their data on request). The files are found by
/// <see cref="Pattern"/>, whatever the browser named them; the version is their names, times and sizes, and the state is
/// skipped while there are none.
/// </summary>
public abstract record DroppedStateDataset(string State, string Source, string Pattern) : AssembledStateDataset(State, Source)
{
    /// <summary>The matching files, oldest first.</summary>
    protected IReadOnlyList<FileInfo> Files(DatasetContext context)
    {
        var folder = new DirectoryInfo(Path.Combine(context.Options.ResolvedStateLicenseFilesFolder, State));
        return folder.Exists ? folder.GetFiles(Pattern).OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToList() : [];
    }

    public override Task<string?> VersionAsync(DatasetContext context, CancellationToken ct)
    {
        var files = Files(context);
        return Task.FromResult(files.Count == 0
            ? null
            : string.Join(" ", files.Select(f => $"{f.Name} {f.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)} {f.Length}")));
    }

    /// <summary>An Excel cell as text: dates (date cells, or Excel serial numbers in a date column) as MM/DD/YYYY.</summary>
    protected static string? Cell(IExcelDataReader reader, int index, bool date = false) =>
        index < 0 || index >= reader.FieldCount
            ? null
            : reader.GetValue(index) switch
            {
                null => null,
                DateTime d => d.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
                double serial when date && serial is > 0 and < 2958466 => DateTime.FromOADate(serial).ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
                double number => number.ToString(CultureInfo.InvariantCulture),
                var other => Convert.ToString(other, CultureInfo.InvariantCulture)?.Trim(),
            };
}

/// <summary>
/// Alabama Board of Medical Examiners roster (dashboard.albme.gov/Verification/roster.aspx, "Get Roster"): every active
/// license, registration, certificate or permit the board issues, as CSV, updated daily. The newest roster-*.csv is read.
/// </summary>
public sealed record AlabamaDataset() : DroppedStateDataset("AL", "al_bme", "roster-*.csv")
{
    public override async IAsyncEnumerable<Row> RowsAsync(DatasetContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var file = Files(context)[^1];
        using var reader = new StreamReader(file.FullName, Encoding.UTF8);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { BadDataFound = null, MissingFieldFound = null });
        if (!await csv.ReadAsync() || !csv.ReadHeader())
        {
            throw new InvalidDataException($"{file.Name} is empty.");
        }

        while (await csv.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            var type = string.Join(" ", new[] { csv.GetField("LICENSE TYPE"), csv.GetField("LICENSE SUBTYPE") }.Where(t => !string.IsNullOrWhiteSpace(t)));
            yield return new Row(csv.GetField("LICENSE NUMBER"), csv.GetField("LAST NAME"), csv.GetField("FIRST NAME"), type, csv.GetField("LICENSE STATUS"),
                csv.GetField("EXPIRATION DATE"), Discipline: csv.GetField("PUBLIC ACTIONS?"));
        }
    }
}

/// <summary>
/// Michigan LARA license lists (Bureau of Professional Licensing, "License Lists and Reports": the A–L, M–O and P–V
/// profession lists, Nursing and Pharmacy), Excel workbooks of active licenses. Every .xlsx in the folder is read
/// (a list downloaded twice counts once: rows are kept once per license number); facility licenses (no last name) are
/// left out. E-mail addresses and home addresses in the lists are not read.
/// </summary>
public sealed record MichiganDataset() : DroppedStateDataset("MI", "mi_lara", "*.xlsx")
{
    static MichiganDataset() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public override async IAsyncEnumerable<Row> RowsAsync(DatasetContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Files(context))
        {
            var (rows, kept) = (0, 0);
            foreach (var row in ReadList(file.FullName))
            {
                rows++;
                if (row.LicenseNumber is { } number && seen.Add(number))
                {
                    kept++;
                    yield return row;
                }
            }

            context.Log.Information("State licenses MI: {File} {Rows:N0} people, {Kept:N0} not in an earlier file", file.Name, rows, kept);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>The people in one list: the header row has "Last Name" and "License No".</summary>
    public static IEnumerable<Row> ReadList(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        do
        {
            Dictionary<string, int>? columns = null;
            while (reader.Read())
            {
                if (columns is null)
                {
                    var names = Enumerable.Range(0, reader.FieldCount).Select(i => Cell(reader, i)).ToList();
                    if (names.Contains("Last Name") && names.Contains("License No"))
                    {
                        columns = names.Select((n, i) => (n, i)).Where(c => !string.IsNullOrEmpty(c.n)).ToDictionary(c => c.n!, c => c.i, StringComparer.OrdinalIgnoreCase);
                    }

                    continue;
                }

                int At(string name) => columns.TryGetValue(name, out var i) ? i : -1;
                var last = Cell(reader, At("Last Name"));
                var number = Cell(reader, At("License No"));
                if (string.IsNullOrEmpty(last) || string.IsNullOrEmpty(number))
                {
                    continue;
                }

                var type = string.Join(": ", new[] { Cell(reader, At("Profession")), Cell(reader, At("Type")) }.Where(t => !string.IsNullOrEmpty(t)));
                yield return new Row(number, last, Cell(reader, At("First Name")), type, Cell(reader, At("Status")), Cell(reader, At("Expiration"), date: true));
            }
        }
        while (reader.NextResult());
    }
}

/// <summary>
/// Medical Board of California "Physician and Surgeon" database (obtained from the board on request): weekly Microsoft
/// Access snapshots (MBCPhysicianAndSurgeonInformation-PUBLIC-yyyyMMdd), usually all in one zip; the newest is read with the
/// Access database driver (Windows only; elsewhere the state is skipped). Every physician and surgeon license (types A, C,
/// G), current or not, with its status (the fee-exempt retired and disabled statuses, where no practice is allowed, win
/// over "Current"), and a row per public board action: disciplinary actions, probationary licenses, public letters of
/// reprimand, citations, and actions by other states or the federal government and court orders. The board's survey
/// answers, convictions, malpractice and hospital records in the database are not read.
/// </summary>
public sealed partial record CaliforniaDataset() : DroppedStateDataset("CA", "ca_mbc", "*.zip")
{
    [GeneratedRegex(@"MBCPhysicianAndSurgeonInformation-PUBLIC-(?<date>[0-9]{8})\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex Snapshot();

    /// <summary>The newest snapshot: the zip it is in, the entry (null when the dropped zip is the snapshot itself) and its date.</summary>
    private (FileInfo Zip, string? Entry, string Date)? Newest(DatasetContext context)
    {
        (FileInfo, string?, string)? best = null;
        foreach (var file in Files(context))
        {
            if (Snapshot().Match(file.Name) is { Success: true } own)
            {
                if (best is null || string.CompareOrdinal(own.Groups["date"].Value, best.Value.Item3) > 0)
                {
                    best = (file, null, own.Groups["date"].Value);
                }

                continue;
            }

            using var zip = ZipFile.OpenRead(file.FullName);
            foreach (var entry in zip.Entries)
            {
                if (Snapshot().Match(entry.FullName) is { Success: true } m
                    && (best is null || string.CompareOrdinal(m.Groups["date"].Value, best.Value.Item3) > 0))
                {
                    best = (file, entry.FullName, m.Groups["date"].Value);
                }
            }
        }

        return best;
    }

    public override Task<string?> VersionAsync(DatasetContext context, CancellationToken ct) =>
        Task.FromResult(!OperatingSystem.IsWindows() ? null : Newest(context) is { } n ? $"snapshot {n.Date}" : null);

    public override async IAsyncEnumerable<Row> RowsAsync(DatasetContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || Newest(context) is not { } newest)
        {
            yield break;
        }

        var work = Path.Combine(context.Options.ResolvedWorkFolder, "datasets", "state_ca");
        Directory.CreateDirectory(work);
        try
        {
            var snapshot = newest.Zip.FullName;
            if (newest.Entry is { } name)
            {
                snapshot = Path.Combine(work, "snapshot.zip");
                using var outer = ZipFile.OpenRead(newest.Zip.FullName);
                await using var from = outer.GetEntry(name)!.Open();
                await using var to = File.Create(snapshot);
                await from.CopyToAsync(to, ct);
            }

            string database;
            using (var inner = ZipFile.OpenRead(snapshot))
            {
                var entry = inner.Entries.FirstOrDefault(e => e.FullName.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException($"The California snapshot {newest.Date} has no .accdb database.");
                database = Path.Combine(work, "physicians.accdb");
                entry.ExtractToFile(database, overwrite: true);
            }

            context.Log.Information("State licenses CA: reading snapshot {Date}", newest.Date);
            foreach (var row in CaliforniaAccess.Read(database))
            {
                yield return row;
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}

/// <summary>Reads the Medical Board of California's Access database (the ACE OLE DB provider, installed with Office or the Access runtime).</summary>
[SupportedOSPlatform("windows")]
internal static class CaliforniaAccess
{
    // Public actions: table, date column, action column, and an optional detail (case number, jurisdiction, cause).
    private static readonly (string Table, string Date, string Action, string? Detail)[] Actions =
    [
        ("AdministrativeDisciplinaryAction", "EffectiveDate", "Description", "CaseNumber"),
        ("ProbationaryLicenseIssued", "EffectiveDate", "Description", "CaseNumber"),
        ("PublicLetterOfReprimand", "EffectiveDateOfAction", "Description", null),
        ("AdministrativeCitationIssued", "DateCitationIssued", "Cause", "FineAmount"),
        ("AdministrativeActionTakenByOtherStateOrFederalGovernment", "DateOfAction", "Description", "Jurisdiction"),
        ("CourtOrder", "DateOfAction", "DescriptionOfAction", null),
    ];

    public static IEnumerable<AssembledStateDataset.Row> Read(string database)
    {
        using var connection = new OleDbConnection($"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={database};Mode=Read;");
        connection.Open();

        var actions = new Dictionary<string, List<(string? Date, string Action, string? Detail)>>(StringComparer.Ordinal);
        foreach (var (table, date, action, detail) in Actions)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT [LicenseID], [{date}], [{action}]{(detail is null ? "" : $", [{detail}]")} FROM [{table}]";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = Text(reader, 0);
                var text = Text(reader, 2);
                if (id is null || text is null)
                {
                    continue;
                }

                var more = detail is null ? null : Text(reader, 3);
                (string Action, string? Detail) shown = table switch
                {
                    "AdministrativeCitationIssued" => ("Citation issued" + (more is null ? "" : $", fine ${decimal.Parse(more, CultureInfo.InvariantCulture):N0}"), text),
                    "AdministrativeActionTakenByOtherStateOrFederalGovernment" => (text, more is null ? null : $"By {more}"),
                    _ => (text, more is null ? null : $"Case {more}"),
                };
                if (!actions.TryGetValue(id, out var list))
                {
                    actions[id] = list = [];
                }

                list.Add((Date(reader, 1), shown.Action, shown.Detail));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT l.[LicenseID], l.[LicenseType], l.[LicenseNumber], l.[LastName], l.[FirstName], l.[ExpirationDate], s.[ShortDescription], f.[FeeExemptModifierCode]
                FROM ([License] AS l LEFT JOIN [REF_PrimaryStatusCode] AS s ON s.[PrimaryStatusCode] = l.[PrimaryStatusCode])
                LEFT JOIN [LicenseFeeExemptModifiers] AS f ON f.[LicenseID] = l.[LicenseID]
                """;
            using var reader = command.ExecuteReader();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                if (Text(reader, 0) is not { } id || !seen.Add(id))
                {
                    continue;
                }

                // A64406 style: the type letter then the number, as the board shows it (NPPES has both "A64406" and "64406").
                var number = $"{Text(reader, 1)}{Text(reader, 2)}";
                var status = Text(reader, 7) switch
                {
                    "RETI" => "Retired",
                    "DIS" => "Disabled",
                    _ => Text(reader, 6),
                };
                var type = $"Physician and surgeon ({Text(reader, 1)})";
                var (last, first, expires) = (Text(reader, 3), Text(reader, 4), Date(reader, 5));
                yield return new AssembledStateDataset.Row(number, last, first, type, status, expires);
                if (actions.TryGetValue(id, out var list))
                {
                    foreach (var (date, action, detail) in list)
                    {
                        yield return new AssembledStateDataset.Row(number, last, first, type, status, expires, action, date, detail);
                    }
                }
            }
        }
    }

    private static string? Text(OleDbDataReader reader, int i) =>
        reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() is { Length: > 0 } s ? s : null;

    private static string? Date(OleDbDataReader reader, int i) =>
        reader.IsDBNull(i) ? null : reader.GetValue(i) is DateTime d ? d.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) : null;
}
