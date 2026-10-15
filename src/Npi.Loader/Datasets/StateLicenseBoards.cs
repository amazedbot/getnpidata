using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;

namespace Npi.Loader.Datasets;

/// <summary>
/// A license list the loader assembles from a board's own download pages (several files, a JSON feed or a workbook) into
/// one CSV with the columns of <see cref="AssembledColumns"/>. These boards publish no version, so the version is the
/// ISO week (plus whatever the board shows, such as its file IDs): the lists reload weekly with the other states.
/// </summary>
public abstract record AssembledStateDataset(string State, string Source)
    : StateLicenseSource.StateDataset(State, Source, "license", AssembledColumns)
{
    public static readonly IReadOnlyList<CsvColumn> AssembledColumns =
    [
        new("License Number", "license_number"), new("Last Name", "last_name"), new("First Name", "first_name"), new("License Type", "license_type"),
        new("Status", "status"), new("Expiration Date", "expiration_date", CsvValue.FlexibleDate), new("Discipline", "discipline"),
        new("Action Date", "action_date", CsvValue.FlexibleDate), new("Action", "action"), new("Description", "description"),
    ];

    /// <summary>One license, or one action on it (a row per action, as Colorado publishes them; the provider page groups them).</summary>
    public sealed record Row(string? LicenseNumber, string? LastName, string? FirstName, string? LicenseType, string? Status, string? ExpirationDate,
        string? Action = null, string? ActionDate = null, string? Description = null, string? Discipline = null);

    /// <summary>The rows, read from the board.</summary>
    public abstract IAsyncEnumerable<Row> RowsAsync(DatasetContext context, CancellationToken ct);

    /// <summary>What the board shows that changes with a new list (null when nothing), added to the week.</summary>
    protected virtual Task<string?> BoardVersionAsync(DatasetContext context, CancellationToken ct) => Task.FromResult<string?>(null);

    public override async Task<string?> VersionAsync(DatasetContext context, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var week = $"week {ISOWeek.GetYear(now)}-{ISOWeek.GetWeekOfYear(now):00}";
        return await BoardVersionAsync(context, ct) is { } board ? $"{board} {week}" : week;
    }

    public override async Task<(string Path, bool Delete)> FetchAsync(DatasetContext context, CancellationToken ct)
    {
        var target = Path.Combine(context.Options.ResolvedWorkFolder, "datasets", $"state_{Source}.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var writer = new StreamWriter(target, false, new UTF8Encoding(false)) { NewLine = "\n" };
        await writer.WriteLineAsync(string.Join(",", AssembledColumns.Select(c => Quote(c.Header))));
        await foreach (var row in RowsAsync(context, ct))
        {
            await writer.WriteLineAsync(string.Join(",",
                new[]
                {
                    row.LicenseNumber, row.LastName, row.FirstName, row.LicenseType, row.Status, row.ExpirationDate, row.Discipline, row.ActionDate, row.Action,
                    row.Description,
                }.Select(Quote)));
        }

        return (target, true);
    }

    /// <summary>
    /// Runs a request to a board's site again after a server error or a dropped connection (Vermont's feed answered one
    /// of its ~260 requests with a 500): up to <paramref name="attempts"/> times, waiting 2, 4, 8 … seconds.
    /// </summary>
    protected static async Task<T> RetryAsync<T>(Func<Task<T>> request, CancellationToken ct, int attempts = 4)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await request();
            }
            catch (HttpRequestException e) when (attempt < attempts && e.StatusCode is null or >= System.Net.HttpStatusCode.InternalServerError)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }
    }

    // Every field quoted; a double quote becomes a single quote and line breaks spaces, as NPPES does, so no escaping is needed.
    private static string Quote(string? value) =>
        $"\"{(value ?? "").Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ').Trim()}\"";
}

/// <summary>
/// Rhode Island Department of Health licensee lists (health.ri.gov/licensing/licensee-lists): active licensees of each
/// profession, updated daily, one CSV per profession from the list tool's download (only the health professions NPPES
/// lists; facilities and food, water or trade licenses are left out). No fee.
/// </summary>
public sealed record RhodeIslandDataset() : AssembledStateDataset("RI", "ri_doh")
{
    public static readonly Uri DownloadUrl = new("https://datahealth.ri.gov/lists/licensees/download.php");

    public static readonly IReadOnlyList<string> Professions =
    [
        "Acupuncture", "Applied Behavior Analyst", "Athletic Trainer", "Chemical Dependency Profession", "Chiropractic", "Dental",
        "Dietitian/Nutritionist", "Emergency Medical Services", "Genetic Counselors", "Hearing Aid Dealers", "Lactation Professional",
        "Marriage & Fam. Ther./Mental", "Massage", "Midwifery", "Music Therapy", "Naturopathic Medicine", "Nursing", "Occupational Therapy",
        "Optometry", "Pharmacy", "Physical Therapy", "Physician", "Physician Assistant", "Podiatric Medicine", "Psychology",
        "Radiologic Technology", "Respiratory Care", "Social Work", "Speech Language Path./Audio",
    ];

    public override async IAsyncEnumerable<Row> RowsAsync(DatasetContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var profession in Professions)
        {
            var text = await RetryAsync(async () =>
            {
                using var form = new FormUrlEncodedContent([new("output", ""), new("prof", profession), new("licType", "")]);
                using var response = await context.Http.PostAsync(DownloadUrl, form, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }, ct);
            using var reader = new StringReader(text);
            using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { BadDataFound = null, MissingFieldFound = null });
            if (!await csv.ReadAsync() || !csv.ReadHeader())
            {
                throw new InvalidDataException($"Rhode Island's {profession} list is empty.");
            }

            var rows = 0;
            while (await csv.ReadAsync())
            {
                rows++;
                yield return new Row(csv.GetField("License No"), csv.GetField("Last"), csv.GetField("First"), csv.GetField("License Type"),
                    csv.GetField("Status"), csv.GetField("Expiration Date"));
            }

            context.Log.Information("State licenses RI: {Profession} {Rows:N0} rows", profession, rows);
        }
    }
}

/// <summary>
/// Vermont Board of Medical Practice roster (roster.medicallicensing.vermont.gov): every license it has issued (physicians,
/// PAs, podiatrists, anesthesiologist and radiologist assistants), current or not, with the status and any action, read
/// from the roster page's JSON feed, 100 records a request (the feed's page limit). Nurses and other professions are
/// licensed by the Secretary of State's Office of Professional Regulation and aren't in it.
/// </summary>
public sealed record VermontDataset() : AssembledStateDataset("VT", "vt_bmp")
{
    public const string FeedUrl = "https://roster.medicallicensing.vermont.gov/vtmbl/data";

    private const int PageSize = 100;

    /// <summary>
    /// The first page gives the total; the other pages are asked <see cref="LoaderOptions.StateFeedParallelRequests"/> at a time
    /// (one started at most every <see cref="LoaderOptions.ApiRequestDelay"/>) and read in order. An empty page ends the list.
    /// </summary>
    public override async IAsyncEnumerable<Row> RowsAsync(DatasetContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        Task<string> PageAsync(int page, CancellationToken pageCt) =>
            RetryAsync(() => context.Http.GetStringAsync(new Uri($"{FeedUrl}?draw=1&start={page * PageSize}&length={PageSize}"), pageCt), pageCt);

        var (total, first) = ParsePage(await PageAsync(0, ct));
        foreach (var row in first)
        {
            yield return row;
        }

        if (first.Count == 0)
        {
            yield break;
        }

        var pages = (total + PageSize - 1) / PageSize;
        await foreach (var json in ParallelSchedule.InOrderAsync(pages - 1, (i, pageCt) => PageAsync(i + 1, pageCt),
                           context.Options.StateFeedParallelRequests, context.Options.ApiRequestDelay, ct))
        {
            var (_, rows) = ParsePage(json);
            if (rows.Count == 0)
            {
                break;
            }

            foreach (var row in rows)
            {
                yield return row;
            }
        }
    }

    /// <summary>One answer of the feed: the total number of records and the page's rows.</summary>
    public static (int Total, IReadOnlyList<Row> Rows) ParsePage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = new List<Row>();
        foreach (var r in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            string? At(int i) => r.GetArrayLength() > i && r[i].ValueKind == JsonValueKind.String ? r[i].GetString() : null;

            // "042.Physician" → "Physician"; an action column of "None" means none.
            var type = At(30) is { } t && t.IndexOf('.', StringComparison.Ordinal) is var dot and >= 0 ? t[(dot + 1)..] : At(30);
            rows.Add(new Row(At(0), At(4), At(2), type, At(1), At(13), At(29) is "None" or "" ? null : At(29)));
        }

        return (doc.RootElement.GetProperty("recordsTotal").GetInt32(), rows);
    }
}

/// <summary>
/// West Virginia Board of Medicine rosters (wvbom.wv.gov/Rosters.asp): workbooks of the MDs, PAs and podiatrists holding an
/// active license, permit or registration, one sheet per credential type, published monthly. Only active credentials are
/// listed, so every row's status is "Active"; the version is the board's file IDs.
/// </summary>
public sealed partial record WestVirginiaDataset() : AssembledStateDataset("WV", "wv_bom")
{
    public static readonly Uri RostersPage = new("https://wvbom.wv.gov/Rosters.asp");

    static WestVirginiaDataset() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [GeneratedRegex("""download_resource\.asp\?id=(?<id>[0-9]+)"[^>]*title="(?<title>[^"]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex RosterLink();

    private static async Task<IReadOnlyList<(string Id, string Title)>> RostersAsync(DatasetContext context, CancellationToken ct)
    {
        var page = await RetryAsync(() => context.Http.GetStringAsync(RostersPage, ct), ct);
        var rosters = RosterLink().Matches(page).Select(m => (m.Groups["id"].Value, m.Groups["title"].Value.Trim())).Distinct().ToList();
        return rosters.Count > 0 ? rosters : throw new InvalidDataException("The West Virginia Board of Medicine lists no rosters.");
    }

    protected override async Task<string?> BoardVersionAsync(DatasetContext context, CancellationToken ct) =>
        "rosters " + string.Join(" ", (await RostersAsync(context, ct)).Select(r => r.Id));

    public override async IAsyncEnumerable<Row> RowsAsync(DatasetContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var (id, title) in await RostersAsync(context, ct))
        {
            var profession = title.EndsWith(" Roster", StringComparison.OrdinalIgnoreCase) ? title[..^" Roster".Length] : title;
            var path = await context.DownloadAsync(new DatasetRelease(id, new Uri(RostersPage, $"download_resource.asp?id={id}")), $"state_wv_{id}.xlsx", ct);
            try
            {
                foreach (var row in ReadRoster(path, profession))
                {
                    yield return row;
                }
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// The rows of every sheet: a title line, then a header line starting "First Name" (the number and expiration columns
    /// are called License, Permit or Registration Number / Expiration Date), then the credential holders.
    /// </summary>
    public static IEnumerable<Row> ReadRoster(string path, string profession)
    {
        using var stream = File.OpenRead(path);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        do
        {
            Dictionary<string, int>? columns = null;
            while (reader.Read())
            {
                string? Cell(int i) => i >= 0 && i < reader.FieldCount ? Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() : null;

                if (columns is null)
                {
                    if (string.Equals(Cell(0), "First Name", StringComparison.OrdinalIgnoreCase))
                    {
                        columns = Enumerable.Range(0, reader.FieldCount).Where(i => !string.IsNullOrEmpty(Cell(i)))
                            .ToDictionary(i => Cell(i)!, i => i, StringComparer.OrdinalIgnoreCase);
                    }

                    continue;
                }

                int Find(string part)
                {
                    foreach (var (name, index) in columns)
                    {
                        if (name.Contains(part, StringComparison.OrdinalIgnoreCase))
                        {
                            return index;
                        }
                    }

                    return -1;
                }

                var number = Cell(Find("Number"));
                if (string.IsNullOrEmpty(number))
                {
                    continue; // an empty sheet's blank row
                }

                // An expiration date may come as a date cell or as text.
                var expirationIndex = Find("Expiration");
                var expiration = expirationIndex >= 0 && expirationIndex < reader.FieldCount && reader.GetValue(expirationIndex) is DateTime date
                    ? date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)
                    : Cell(expirationIndex);
                var type = Cell(Find("License Type")) is { Length: > 0 } t ? t : reader.Name;
                yield return new Row(number, Cell(Find("Last Name")), Cell(Find("First Name")), $"{profession}: {type}", "Active", expiration);
            }
        }
        while (reader.NextResult());
    }
}
