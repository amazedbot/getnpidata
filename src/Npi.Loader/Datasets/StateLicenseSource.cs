using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Dapper;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// State license records and board actions (CLAUDE.md §7 Stage 5.5 item 15), from state sources that publish them for
/// reuse: open-data portals (Socrata), CSV files a board posts, lists assembled from a board's download pages
/// (<see cref="AssembledStateDataset"/>), and files the owner downloads from a board that needs a sign-in (put in
/// <see cref="LoaderOptions.StateLicenseFilesFolder"/>, one subfolder per state). Every dataset is
/// loaded into one scratch table (<c>state_license_raw</c>); the rows whose license matches an NPPES taxonomy license of a
/// provider are kept in <c>provider_state_license</c>: same state, same number (all its digits, or its last run of
/// digits, without leading zeros) and the same last name (or the last name as a whole word of a full name). Rows are
/// never matched by name alone. One source for all states, so the table is replaced in one swap; a state that fails to
/// load keeps the previous data for every state. A dropped-file state whose files aren't there is skipped.
/// </summary>
public sealed class StateLicenseSource : DatasetSource
{
    /// <summary>One state's dataset: which kind of rows it has and how its columns map to state_license_raw.</summary>
    /// <param name="Kind">"license" (a license record with status) or "action" (a board action).</param>
    public abstract record StateDataset(string State, string Source, string Kind, IReadOnlyList<CsvColumn> Columns)
    {
        /// <summary>The field separator.</summary>
        public virtual char Delimiter => ',';

        /// <summary>The first field of the header line when the file starts with other lines; null when the header is first.</summary>
        public virtual string? HeaderStartsWith => null;

        /// <summary>The file's MySQL character set (MD's lists are Windows-1252, which MySQL calls latin1).</summary>
        public virtual string CharacterSet => "utf8mb4";

        /// <summary>The current version, or null when the dataset isn't available (a dropped file that isn't there).</summary>
        public abstract Task<string?> VersionAsync(DatasetContext context, CancellationToken ct);

        /// <summary>The file to load, and whether to delete it afterwards (downloads and assembled lists yes, dropped files no).</summary>
        public abstract Task<(string Path, bool Delete)> FetchAsync(DatasetContext context, CancellationToken ct);

        /// <summary>
        /// The rows to filter, as a stream: by default the file <see cref="FetchAsync"/> gives (deleted when the stream is
        /// disposed, if it should be). A dropped file is read straight from the file or its zip entry instead (speed item 9).
        /// </summary>
        public virtual async Task<StateFile> OpenAsync(DatasetContext context, CancellationToken ct)
        {
            var (path, delete) = await FetchAsync(context, ct);
            return new StateFile(Path.GetFileName(path), File.OpenRead(path), delete ? () => File.Delete(path) : null);
        }
    }

    /// <summary>A dataset's rows opened for reading; disposing it closes the stream, then runs the cleanup (delete a download, close a zip).</summary>
    public sealed class StateFile(string name, Stream stream, Action? cleanup = null) : IAsyncDisposable
    {
        public string Name => name;

        public Stream Stream => stream;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await stream.DisposeAsync();
            }
            finally
            {
                cleanup?.Invoke();
            }
        }
    }

    /// <summary>A Socrata open-data dataset, exported as CSV with only the mapped columns (the headers are the field names).</summary>
    public sealed record SocrataDataset(string State, string Source, string Domain, string Id, string Kind, IReadOnlyList<CsvColumn> Columns,
        string? Where = null) : StateDataset(State, Source, Kind, Columns)
    {
        public Uri ExportUrl => new($"https://{Domain}/resource/{Id}.csv?$select={string.Join(",", Columns.Select(c => c.Header))}"
                                    + (Where is null ? "" : $"&$where={Uri.EscapeDataString(Where)}") + "&$limit=50000000");

        public Uri MetadataUrl => new($"https://{Domain}/api/views/{Id}.json");

        public override async Task<string?> VersionAsync(DatasetContext context, CancellationToken ct)
        {
            await using var stream = await context.Http.GetStreamAsync(MetadataUrl, ct);
            return ParseRowsUpdated(stream);
        }

        public override async Task<(string Path, bool Delete)> FetchAsync(DatasetContext context, CancellationToken ct) =>
            (await context.DownloadAsync(new DatasetRelease(Source, ExportUrl), $"state_{Source}.csv", ct), true);
    }

    /// <summary>A CSV file a board posts at a fixed URL; its version is the server's Last-Modified, ETag or length.</summary>
    public sealed record CsvUrlDataset(string State, string Source, Uri Url, string Kind, IReadOnlyList<CsvColumn> Columns, string? Header = null,
        string Charset = "utf8mb4") : StateDataset(State, Source, Kind, Columns)
    {
        public override string? HeaderStartsWith => Header;

        public override string CharacterSet => Charset;

        public override async Task<string?> VersionAsync(DatasetContext context, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, Url);
            using var response = await context.Http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var content = response.Content.Headers;
            return content.LastModified?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                ?? response.Headers.ETag?.Tag
                ?? (content.ContentLength is { } length ? $"{length} bytes"
                    : $"week {ISOWeek.GetYear(DateTime.UtcNow)}-{ISOWeek.GetWeekOfYear(DateTime.UtcNow)}");
        }

        public override async Task<(string Path, bool Delete)> FetchAsync(DatasetContext context, CancellationToken ct) =>
            (await context.DownloadAsync(new DatasetRelease(Source, Url), $"state_{Source}.csv", ct), true);
    }

    /// <summary>
    /// A file the owner downloads from a board that requires a sign-in, saved under
    /// <see cref="LoaderOptions.StateLicenseFilesFolder"/>\&lt;State&gt;\<see cref="FileName"/>. A .zip is read straight from
    /// its <see cref="ZipEntry"/>, never extracted. Its version is the file's time and size.
    /// </summary>
    public sealed record DroppedFileDataset(string State, string Source, string FileName, string Kind, IReadOnlyList<CsvColumn> Columns,
        string? ZipEntry = null, char Separator = ',') : StateDataset(State, Source, Kind, Columns)
    {
        public override char Delimiter => Separator;

        private string PathIn(DatasetContext context) => Path.Combine(context.Options.ResolvedStateLicenseFilesFolder, State, FileName);

        public override Task<string?> VersionAsync(DatasetContext context, CancellationToken ct)
        {
            var file = new FileInfo(PathIn(context));
            return Task.FromResult(file.Exists
                ? $"{FileName} {file.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)} {file.Length}"
                : null);
        }

        /// <summary>
        /// The clean rows (<see cref="DroppedFileRowStream"/>), read straight from the file or its zip entry while the filter reads
        /// them: nothing is extracted or copied to the work folder (speed item 9; FL's PROF_ALL.txt is 959 MB unzipped).
        /// </summary>
        public override Task<StateFile> OpenAsync(DatasetContext context, CancellationToken ct)
        {
            var path = PathIn(context);
            if (ZipEntry is null)
            {
                return Task.FromResult(new StateFile(FileName, Rows(context, File.OpenRead(path))));
            }

            var zip = ZipFile.OpenRead(path);
            try
            {
                var entry = zip.GetEntry(ZipEntry) ?? throw new InvalidDataException($"{FileName} has no entry {ZipEntry}.");
                return Task.FromResult(new StateFile(FileName, Rows(context, entry.Open()), zip.Dispose));
            }
            catch
            {
                zip.Dispose();
                throw;
            }
        }

        /// <summary>The clean rows written to a file in the work folder: the same bytes <see cref="OpenAsync"/> streams.</summary>
        public override async Task<(string Path, bool Delete)> FetchAsync(DatasetContext context, CancellationToken ct)
        {
            var target = Path.Combine(context.Options.ResolvedWorkFolder, "datasets", $"state_{Source}_{Kind}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var file = await OpenAsync(context, ct))
            await using (var output = File.Create(target))
            {
                await file.Stream.CopyToAsync(output, ct);
            }

            return (target, true);
        }

        private DroppedFileRowStream Rows(DatasetContext context, Stream source) => new(source, Separator, skipped =>
        {
            if (skipped > 0)
            {
                context.Log.Warning("State licenses {State}: {Skipped:N0} rows of {File} have the wrong number of fields; left out", State, skipped, FileName);
            }
        });
    }

    /// <summary>
    /// A dropped file's rows, cleaned as they are read: these exports don't quote fields, so a stray double quote (FL has "DE
    /// as a suffix) is changed to a single quote, as NPPES does; one trailing separator is dropped (FL ends its rows with
    /// one); and a row whose field count differs from the header's (a separator inside a value, 27 of 3.7M FL rows) is left
    /// out and counted (<paramref name="onEnd"/> gets the count once, at the end). The source is read as UTF-8 and the rows
    /// come out as UTF-8 without a byte order mark, each ending in "\n".
    /// </summary>
    public sealed class DroppedFileRowStream(Stream source, char separator, Action<int> onEnd) : Stream
    {
        private readonly StreamReader _reader = new(source, System.Text.Encoding.UTF8);
        private byte[] _line = new byte[4096];
        private int _offset;
        private int _length;
        private int _fields = -1;
        private int _skipped;
        private bool _ended;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_offset == _length)
            {
                if (!Next(_reader.ReadLine()))
                {
                    return 0;
                }
            }

            return Copy(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_offset == _length)
            {
                if (!Next(await _reader.ReadLineAsync(cancellationToken)))
                {
                    return 0;
                }
            }

            return Copy(buffer.Span);
        }

        private int Copy(Span<byte> buffer)
        {
            var n = Math.Min(buffer.Length, _length - _offset);
            _line.AsSpan(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        // Takes the next source line: false at the end; otherwise the clean row (none for a row left out) is ready to copy.
        private bool Next(string? read)
        {
            (_offset, _length) = (0, 0);
            if (read is null)
            {
                if (!_ended)
                {
                    _ended = true;
                    onEnd(_skipped);
                }

                return false;
            }

            var line = read.EndsWith(separator) ? read[..^1] : read;
            var count = line.Count(c => c == separator) + 1;
            if (_fields < 0)
            {
                _fields = count;
            }
            else if (count != _fields)
            {
                _skipped++;
                return true;
            }

            var clean = line.Replace('"', '\'');
            var needed = System.Text.Encoding.UTF8.GetMaxByteCount(clean.Length) + 1;
            if (_line.Length < needed)
            {
                _line = new byte[Math.Max(needed, _line.Length * 2)];
            }

            _length = System.Text.Encoding.UTF8.GetBytes(clean, _line);
            _line[_length++] = (byte)'\n';
            return true;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reader.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    public static readonly IReadOnlyList<StateDataset> Datasets =
    [
        // NY Department of Health, Board for Professional Medical Conduct: every public action since 1990 (MD, DO, PA, SA).
        new SocrataDataset("NY", "ny_bpmc", "health.data.ny.gov", "ebmi-8ctw", "action",
        [
            new("licensenum", "license_number"), new("last_name", "last_name"), new("first_name", "first_name"), new("licensetype", "license_type"),
            new("effectivedate", "action_date", CsvValue.FlexibleDate), new("webaction", "action"), new("webnotes", "description"),
        ]),
        // Texas Medical Board: every TMB license (physicians, PAs, radiologic technologists, respiratory care, …).
        new SocrataDataset("TX", "tx_tmb", "data.texas.gov", "tm3v-pfq9", "license",
        [
            new("license_number", "license_number"), new("last_name", "last_name"), new("first_name", "first_name"), new("license_type", "license_type"),
            new("registration_status", "status"), new("license_expiration_date", "expiration_date", CsvValue.FlexibleDate),
            new("disciplinary_status", "discipline"), new("current_board_action_date", "action_date", CsvValue.FlexibleDate),
            new("current_board_action_description", "action"), new("license_status", "description"),
        ]),
        // Washington State Department of Health: every health care credential (public domain).
        new SocrataDataset("WA", "wa_doh", "data.wa.gov", "qxh8-f4bd", "license",
        [
            new("credentialnumber", "license_number"), new("lastname", "last_name"), new("firstname", "first_name"), new("credentialtype", "license_type"),
            new("status", "status"), new("expirationdate", "expiration_date", CsvValue.FlexibleDate), new("actiontaken", "discipline"),
        ]),
        // Illinois IDFPR: every professional license, with the most recent discipline (Open Database License).
        new SocrataDataset("IL", "il_idfpr", "illinois-edp.data.socrata.com", "pzzh-kp68", "license",
        [
            new("license_number", "license_number"), new("last_name", "last_name"), new("first_name", "first_name"), new("description", "license_type"),
            new("license_status", "status"), new("expiration_date", "expiration_date", CsvValue.FlexibleDate), new("ever_disciplined", "discipline"),
            new("discipline_start_date", "action_date", CsvValue.FlexibleDate), new("action", "action"), new("discipline_reason", "description"),
        ]),
        // Colorado DORA: every professional and occupational license, one row per discipline case (public domain).
        new SocrataDataset("CO", "co_dora", "data.colorado.gov", "7s5z-vewr", "license",
        [
            new("licensenumber", "license_number"), new("lastname", "last_name"), new("firstname", "first_name"), new("licensetype", "license_type"),
            new("licensestatusdescription", "status"), new("licenseexpirationdate", "expiration_date", CsvValue.FlexibleDate),
            new("programaction", "action"), new("disciplineeffectivedate", "action_date", CsvValue.FlexibleDate), new("casenumber", "description"),
            new("linktoverifylicense", "verify_url"),
        ]),
        // Delaware Division of Professional Regulation: every professional license and every disciplinary action (public domain).
        new SocrataDataset("DE", "de_dpr", "data.delaware.gov", "pjnv-eaih", "license",
        [
            new("license_no", "license_number"), new("last_name", "last_name"), new("first_name", "first_name"), new("license_type", "license_type"),
            new("license_status", "status"), new("expiration_date", "expiration_date", CsvValue.FlexibleDate), new("disciplinary_action", "discipline"),
        ]),
        new SocrataDataset("DE", "de_dpr", "data.delaware.gov", "dz6p-akeq", "action",
        [
            new("license_id_l", "license_number"), new("last_name", "last_name"), new("first_name", "first_name"), new("license_type", "license_type"),
            new("disp_start", "action_date", CsvValue.FlexibleDate), new("item_text", "action"),
        ]),
        // Connecticut eLicensing (DPH and DCP): every individual's license or credential, status only (public domain).
        new SocrataDataset("CT", "ct_elicense", "data.ct.gov", "ngch-56tr", "license",
        [
            new("credentialnumber", "license_number"), new("name", "full_name"), new("credential", "license_type"), new("status", "status"),
            new("expirationdate", "expiration_date", CsvValue.FlexibleDate),
        ], Where: "type = 'INDIVIDUAL'"),
        // Maryland Board of Physicians: active physicians and allied health practitioners, with a discipline flag (monthly).
        new CsvUrlDataset("MD", "md_bop", new Uri("https://www.mbp.state.md.us/forms/doctor_list_revised.csv"), "license",
        [
            new("License #", "license_number"), new("Last Name", "last_name"), new("First Name", "first_name"), new("License Status", "status"),
            new("Expiration Date", "expiration_date", CsvValue.FlexibleDate), new("Discipline", "discipline"),
        ], Header: "License #", Charset: "latin1"),
        new CsvUrlDataset("MD", "md_bop", new Uri("https://www.mbp.state.md.us/forms/allied_health_list.csv"), "license",
        [
            new("Profession", "license_type"), new("License #", "license_number"), new("Last Name", "last_name"), new("First Name", "first_name"),
            new("Status", "status"), new("Expire Date", "expiration_date", CsvValue.FlexibleDate), new("Discipline", "discipline"),
        ], Header: "Profession", Charset: "latin1"),
        // Florida Department of Health (MQA data download, sign-in required, so the owner drops the files): every license and
        // the recent administrative complaints. Public records (Florida Statutes ch. 119).
        new DroppedFileDataset("FL", "fl_doh", "PROF_ALL.zip", "license",
        [
            new("License-Number", "license_number"), new("Last-Name", "last_name"), new("First-Name", "first_name"), new("Profession-Name", "license_type"),
            new("License-Status-Description", "status"), new("Expire-Date", "expiration_date", CsvValue.FlexibleDate),
            new("Board-Action-Indicator", "discipline"),
        ], ZipEntry: "dbdumps/ldms/PROF_ALL.txt", Separator: '|'),
        new DroppedFileDataset("FL", "fl_doh", "dxe004dd.txt", "action",
        [
            new("License Number", "license_number"), new("Respondent Name", "full_name"), new("Profession", "license_type"),
            new("Case Activity Type", "action"), new("Case Activity Date", "action_date", CsvValue.FlexibleDate), new("Case Number", "description"),
        ], Separator: '|'),
        // Rhode Island, Vermont and West Virginia: lists the loader assembles from the boards' own downloads (StateLicenseBoards.cs).
        new RhodeIslandDataset(),
        new VermontDataset(),
        new WestVirginiaDataset(),
        // Alabama, Michigan and California: files the owner downloads (StateLicenseDroppedFiles.cs).
        new AlabamaDataset(),
        new MichiganDataset(),
        new CaliforniaDataset(),
    ];

    public override string Name => "state_licenses";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        // Up to ParallelStates probes at once, kept in Datasets order.
        var found = new string?[Datasets.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, Datasets.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Options.ParallelStates), CancellationToken = ct },
            async (i, probeCt) => found[i] = await Datasets[i].VersionAsync(context, probeCt));
        var versions = Datasets.Select((d, i) => (d.State, Version: found[i])).ToList();

        context.Log.Information("State licenses versions: {Versions}", string.Join(" | ", versions.Select(v => $"{v.State} {v.Version ?? "none"}")));
        return new StateLicenseRelease(CombineVersions(versions), new Uri("https://data.cms.gov/"), versions);
    }

    /// <summary>
    /// What <see cref="FindLatestAsync"/> found: the combined version and each dataset's own, in <see cref="Datasets"/> order, for
    /// <see cref="LoadAsync"/>. They travel with the release, not on this object: one instance serves every refresh in the process
    /// (<see cref="DatasetLoader.AllSources"/>), and a field set by one refresh's probe was read by another's load running at the
    /// same time, which then removed the states it had files for.
    /// </summary>
    public sealed record StateLicenseRelease : DatasetRelease
    {
        public StateLicenseRelease(string version, Uri url, IReadOnlyList<(string State, string? Version)> states)
            : base(version, url) => States = states;

        public IReadOnlyList<(string State, string? Version)> States { get; }
    }

    /// <summary>
    /// One short version for all the datasets (reference_data.version holds 200 characters): the states with data and a
    /// hash of every dataset's version, so any change reloads them all.
    /// </summary>
    public static string CombineVersions(IReadOnlyList<(string State, string? Version)> versions)
    {
        var all = string.Join(" | ", versions.Select(v => $"{v.State} {v.Version ?? "none"}"));
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(all)))[..12];
        return $"{string.Join(" ", versions.Where(v => v.Version is not null).Select(v => v.State).Distinct().Order(StringComparer.Ordinal))} {hash}";
    }

    /// <summary>When the dataset's rows last changed (Socrata <c>rowsUpdatedAt</c>, Unix seconds), as a UTC timestamp.</summary>
    public static string ParseRowsUpdated(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("rowsUpdatedAt", out var updated) && updated.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            : throw new InvalidDataException("The dataset metadata has no rowsUpdatedAt.");
    }

    // The NPPES license number in the two forms of state_license_raw's keys (all digits; the last run of digits).
    private const string TaxonomyKey = "NULLIF(TRIM(LEADING '0' FROM REGEXP_REPLACE(COALESCE(t.`license_no`, ''), '[^0-9]', '')), '')";

    private const string TaxonomyKeyLast =
        "IF(COALESCE(t.`license_no`, '') REGEXP '[0-9]', NULLIF(TRIM(LEADING '0' FROM REGEXP_REPLACE(t.`license_no`, '^.*?([0-9]+)[^0-9]*$', '$1')), ''), NULL)";

    // The same person: the last name (or its first word), or the last name as a whole word of a published full name.
    private const string SameName = """
        ((r.`last_name` IS NOT NULL AND (p.`last_name` = r.`last_name` OR SUBSTRING_INDEX(p.`last_name`, ' ', 1) = SUBSTRING_INDEX(r.`last_name`, ' ', 1)))
          OR (r.`last_name` IS NULL AND r.`full_name` IS NOT NULL AND p.`last_name` IS NOT NULL
              AND CONCAT(' ', REPLACE(r.`full_name`, ',', ' '), ' ') LIKE CONCAT('% ', REPLACE(REPLACE(p.`last_name`, '%', ''), '_', ''), ' %')))
        """;

    /// <summary>
    /// Reloads only the states whose files changed since they were last loaded (<c>state_license_version</c>): up to
    /// <see cref="LoaderOptions.ParallelStates"/> states at once download their files and filter them to the rows whose license
    /// number an NPPES provider lists for that state (<see cref="StateLicenseFilter"/>); then, one state at a time, the rows are
    /// loaded into the scratch table, matched, and that state's rows in <c>provider_state_license</c> replaced in one transaction
    /// (with the ≥ MinRowRatio check against its previous rows). The NPIs whose rows changed are queued in
    /// <c>quality_pending</c> for the record-quality label. A state that fails keeps its previous rows and is tried again next
    /// time; the others are kept. A state whose files are gone is removed.
    /// </summary>
    /// <returns>The rows in provider_state_license afterwards.</returns>
    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var versions = release is StateLicenseRelease found
            ? found.States
            : await Task.WhenAll(Datasets.Select(async d => (d.State, await d.VersionAsync(context, ct))));
        await using var connection = await context.Database.OpenAsync(ct);
        var stored = (await connection.QueryAsync<(string State, string Version)>(new CommandDefinition(
                "SELECT `state`, `version` FROM `state_license_version`", cancellationToken: ct)))
            .ToDictionary(v => v.State, v => v.Version, StringComparer.Ordinal);
        var states = Datasets.Select(d => d.State).Distinct().ToList();
        var failed = new bool[states.Count];

        // The scratch tables (state_license_raw_staging, provider_state_license_new) and the replacement are one state's at a time,
        // on this connection; the downloads and filtering beside them use their own.
        using var applying = new SemaphoreSlim(1, 1);
        await Parallel.ForEachAsync(Enumerable.Range(0, states.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, context.Options.ParallelStates), CancellationToken = ct }, async (s, stateCt) =>
            {
                var state = states[s];
                var stateContext = context.ForStep($"state_licenses {state}");
                var mine = Datasets.Select((d, i) => (Dataset: d, versions[i].Version)).Where(x => x.Dataset.State == state).ToList();
                var available = mine.Where(x => x.Version is not null).ToList();
                var version = CombineVersions(mine.Select(x => (state, x.Version)).ToList());
                if (stored.TryGetValue(state, out var previous) && previous == version && available.Count > 0)
                {
                    stateContext.Log.Information("State licenses {State}: unchanged", state);
                    return;
                }

                IReadOnlyList<PreparedFile> prepared = [];
                try
                {
                    if (available.Count > 0)
                    {
                        prepared = await PrepareStateAsync(stateContext, state, available.Select(x => x.Dataset).ToList(), stateCt);
                    }

                    await applying.WaitAsync(stateCt);
                    long rows;
                    try
                    {
                        rows = available.Count == 0
                            ? await RemoveStateAsync(connection, state, stateCt)
                            : await LoadStateAsync(stateContext, connection, state, prepared, version, stateCt);
                    }
                    finally
                    {
                        applying.Release();
                    }

                    stateContext.Log.Information("State licenses {State}: {Rows:N0} matched rows", state, rows);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stateCt.IsCancellationRequested)
                {
                    stateContext.Log.Error(ex, "State licenses {State} failed; its previous rows are kept", state);
                    failed[s] = true;
                }
                finally
                {
                    foreach (var file in prepared)
                    {
                        File.Delete(file.Path);
                    }
                }
            });

        if (failed.Any(f => f))
        {
            throw new InvalidOperationException(
                $"State licenses failed for {string.Join(", ", states.Where((_, s) => failed[s]))}; the other states were updated.");
        }

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM `provider_state_license`", cancellationToken: ct));
    }

    private const string Columns = "`npi`, `state`, `kind`, `source`, `license_number`, `license_type`, `status`, `expiration_date`, `discipline`, " +
                                   "`action_date`, `action`, `description`, `verify_url`";

    /// <summary>A state dataset's rows that could match, ready to load (<see cref="PrepareStateAsync"/>).</summary>
    private sealed record PreparedFile(StateDataset Dataset, string Path);

    /// <summary>
    /// Downloads (or copies) each of the state's files and keeps the rows whose license number an NPPES provider lists for the
    /// state, one file after another, into a file per dataset in the work folder. Its own connection reads the license numbers.
    /// </summary>
    private static async Task<IReadOnlyList<PreparedFile>> PrepareStateAsync(DatasetContext context, string state, List<StateDataset> datasets,
        CancellationToken ct)
    {
        var keys = new LicenseKeys();
        await using (var connection = await context.Database.OpenAsync(ct))
        {
            await foreach (var key in connection.QueryUnbufferedAsync<(string? All, string? Last)>(
                               $"SELECT DISTINCT {TaxonomyKey}, {TaxonomyKeyLast} FROM `provider_taxonomy` t WHERE t.`license_state` = @state AND t.`license_no` IS NOT NULL",
                               new { state }).WithCancellation(ct))
            {
                keys.Add(key.All, key.Last);
            }
        }

        var prepared = new List<PreparedFile>();
        try
        {
            for (var i = 0; i < datasets.Count; i++)
            {
                var dataset = datasets[i];
                // Numbered: two of a state's datasets can share a source and kind (MD's two lists).
                var filtered = Path.Combine(context.Options.ResolvedWorkFolder, "datasets", $"state_{dataset.Source}_{dataset.Kind}_{i}_filtered.csv");
                Directory.CreateDirectory(Path.GetDirectoryName(filtered)!);
                await using var file = await dataset.OpenAsync(context, ct); // a download is deleted when it's closed, after the filter
                prepared.Add(new PreparedFile(dataset, filtered));
                var (read, kept) = await StateLicenseFilter.FilterAsync(file.Stream, file.Name, filtered, dataset, keys, ct);
                context.Log.Information("State licenses {State} ({Kind}): {Read:N0} rows read, {Kept:N0} with a license number NPPES lists",
                    state, dataset.Kind, read, kept);
            }

            return prepared;
        }
        catch
        {
            foreach (var file in prepared)
            {
                File.Delete(file.Path);
            }

            throw;
        }
    }

    private static async Task<long> LoadStateAsync(DatasetContext context, MySqlConnector.MySqlConnection connection, string state,
        IReadOnlyList<PreparedFile> files, string version, CancellationToken ct)
    {
        const string raw = "state_license_raw_staging";
        const string matched = "provider_state_license_new";
        try
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
            await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` LIKE `state_license_raw`", ct);
            foreach (var (dataset, filtered) in files)
            {
                var loaded = await CsvTableLoader.LoadAsync(connection, filtered, raw, dataset.Columns, ct, dataset.CharacterSet, constants: new Dictionary<string, string>
                {
                    ["state"] = dataset.State, ["kind"] = dataset.Kind, ["source"] = dataset.Source,
                }, delimiter: dataset.Delimiter);
                context.Log.Information("State licenses {State} ({Kind}): {Rows:N0} rows loaded", state, dataset.Kind, loaded);
            }

            // Match into a scratch copy, then replace the state's rows in one transaction (readers see the old rows until it commits).
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{matched}`", ct);
            await Database.ExecuteAsync(connection, $"CREATE TABLE `{matched}` LIKE `provider_state_license`", ct);
            await Database.ExecuteAsync(connection,
                $"""
                INSERT INTO `{matched}` ({Columns})
                SELECT x.`npi`, r.`state`, r.`kind`, r.`source`, r.`license_number`, r.`license_type`, r.`status`, r.`expiration_date`,
                  r.`discipline`, r.`action_date`, r.`action`, r.`description`, r.`verify_url`
                FROM (
                  SELECT m.`npi`, r.`id` FROM (
                    SELECT DISTINCT t.`npi`, {TaxonomyKey} AS `k_all`, {TaxonomyKeyLast} AS `k_last`
                    FROM `provider_taxonomy` t WHERE t.`license_state` = @state AND t.`license_no` IS NOT NULL
                  ) m
                  JOIN `{raw}` r ON r.`state` = @state AND r.`license_key` = m.`k_all`
                  JOIN `provider` p ON p.`npi` = m.`npi`
                  WHERE {SameName}
                  UNION
                  SELECT m.`npi`, r.`id` FROM (
                    SELECT DISTINCT t.`npi`, {TaxonomyKey} AS `k_all`, {TaxonomyKeyLast} AS `k_last`
                    FROM `provider_taxonomy` t WHERE t.`license_state` = @state AND t.`license_no` IS NOT NULL
                  ) m
                  JOIN `{raw}` r ON r.`state` = @state AND r.`license_key_last` = m.`k_last`
                  JOIN `provider` p ON p.`npi` = m.`npi`
                  WHERE {SameName}
                ) x
                JOIN `{raw}` r ON r.`id` = x.`id`
                """, ct, param: new { state });
            var rows = await connection.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT COUNT(*) FROM `{matched}`", cancellationToken: ct));
            var before = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM `provider_state_license` WHERE `state` = @state", new { state }, cancellationToken: ct));
            if (before > 0 && rows < before * context.Options.MinRowRatio)
            {
                throw new InvalidDataException(
                    $"{state} matched {rows:N0} rows, fewer than {context.Options.MinRowRatio:P0} of the {before:N0} it had; the previous rows are kept.");
            }

            if (before > 0)
            {
                // The change log (Phase 3 step 3.2): licenses added, dropped, changing status or renewed, and new board actions.
                await DatasetChanges.RecordStateLicensesAsync(connection, state, matched, DateTime.UtcNow, context.Log, ct);
            }

            await ReplaceStateAsync(connection, state, matched, version, rows, ct);
            return rows;
        }
        finally
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{matched}`", CancellationToken.None);
        }
    }

    // A state with no files any more (a dropped file removed, or CA off Windows): its rows go, as when all states were swapped at once.
    private static async Task<long> RemoveStateAsync(MySqlConnector.MySqlConnection connection, string state, CancellationToken ct)
    {
        await ReplaceStateAsync(connection, state, null, null, 0, ct);
        return 0;
    }

    /// <summary>
    /// Replaces one state's rows (and its version) with those of <paramref name="newTable"/> (none when null) in one transaction.
    /// The NPIs of the old and new rows are queued for relabelling first, in one statement in NPI order (other sources queue
    /// NPIs at the same time, speed item 7), so a failure leaves at most a too long queue.
    /// </summary>
    private static async Task ReplaceStateAsync(MySqlConnector.MySqlConnection connection, string state, string? newTable, string? version, long rows,
        CancellationToken ct)
    {
        var param = new { state, version, rows, now = DateTime.UtcNow };
        var newNpis = newTable is null ? "" : $" UNION SELECT `npi` FROM `{newTable}`";
        await Database.RetryOnLockConflictAsync(() => connection.ExecuteAsync(new CommandDefinition(
            $"INSERT IGNORE INTO `quality_pending` (`npi`) SELECT `npi` FROM (SELECT `npi` FROM `provider_state_license` WHERE `state` = @state{newNpis}) x ORDER BY `npi`",
            param, commandTimeout: 0, cancellationToken: ct)), ct);

        await using var transaction = await connection.BeginTransactionAsync(ct);
        async Task RunAsync(string sql) =>
            await connection.ExecuteAsync(new CommandDefinition(sql, param, transaction, commandTimeout: 0, cancellationToken: ct));

        await RunAsync("DELETE FROM `provider_state_license` WHERE `state` = @state");
        if (newTable is null)
        {
            await RunAsync("DELETE FROM `state_license_version` WHERE `state` = @state");
        }
        else
        {
            await RunAsync($"INSERT INTO `provider_state_license` ({Columns}) SELECT {Columns} FROM `{newTable}`");
            await RunAsync(
                """
                INSERT INTO `state_license_version` (`state`, `version`, `rows_loaded`, `loaded_at`) VALUES (@state, @version, @rows, @now) AS new
                ON DUPLICATE KEY UPDATE `version` = new.`version`, `rows_loaded` = new.`rows_loaded`, `loaded_at` = new.`loaded_at`
                """);
        }

        await transaction.CommitAsync(ct);
    }
}
