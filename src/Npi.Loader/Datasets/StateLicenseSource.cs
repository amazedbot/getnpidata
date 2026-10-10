using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// State license records and board actions (CLAUDE.md §7 Stage 5.5 item 15), from state sources that publish them for
/// reuse: open-data portals (Socrata), CSV files a board posts, and files the owner downloads from a board that needs a
/// sign-in (put in <see cref="LoaderOptions.StateLicenseFilesFolder"/>, one subfolder per state). Every dataset is
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

        /// <summary>The current version, or null when the dataset isn't available (a dropped file that isn't there).</summary>
        public abstract Task<string?> VersionAsync(DatasetContext context, CancellationToken ct);

        /// <summary>The file to load, and whether to delete it afterwards (downloads and extractions yes, dropped files no).</summary>
        public abstract Task<(string Path, bool Delete)> FetchAsync(DatasetContext context, CancellationToken ct);
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
    public sealed record CsvUrlDataset(string State, string Source, Uri Url, string Kind, IReadOnlyList<CsvColumn> Columns, string? Header = null)
        : StateDataset(State, Source, Kind, Columns)
    {
        public override string? HeaderStartsWith => Header;

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
    /// <see cref="LoaderOptions.StateLicenseFilesFolder"/>\&lt;State&gt;\<see cref="FileName"/>. A .zip is read from its
    /// <see cref="ZipEntry"/> (extracted to the work folder first). Its version is the file's time and size.
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
        /// Copies the file (or its zip entry) to the work folder as clean rows: these exports don't quote fields, so a stray
        /// double quote (FL has "DE as a suffix) is changed to a single quote, as NPPES does, and a row whose field count
        /// differs from the header's (a separator inside a value, 27 of 3.7M FL rows) is left out and counted in the log.
        /// </summary>
        public override async Task<(string Path, bool Delete)> FetchAsync(DatasetContext context, CancellationToken ct)
        {
            var path = PathIn(context);
            var target = Path.Combine(context.Options.ResolvedWorkFolder, "datasets", $"state_{Source}_{Kind}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var zip = ZipEntry is null ? null : ZipFile.OpenRead(path);
            var entry = zip is null ? null : zip.GetEntry(ZipEntry!) ?? throw new InvalidDataException($"{FileName} has no entry {ZipEntry}.");
            await using var source = entry is null ? File.OpenRead(path) : entry.Open();
            using var reader = new StreamReader(source, System.Text.Encoding.UTF8);
            await using (var writer = new StreamWriter(target, false, new System.Text.UTF8Encoding(false)) { NewLine = "\n" })
            {
                var (fields, skipped) = (-1, 0);
                while (await reader.ReadLineAsync(ct) is { } read)
                {
                    var line = read.EndsWith(Separator) ? read[..^1] : read; // FL ends lines with a separator (PROF_ALL: rows only)
                    var count = line.Count(c => c == Separator) + 1;
                    if (fields < 0)
                    {
                        fields = count;
                    }
                    else if (count != fields)
                    {
                        skipped++;
                        continue;
                    }

                    await writer.WriteLineAsync(line.Replace('"', '\''));
                }

                if (skipped > 0)
                {
                    context.Log.Warning("State licenses {State}: {Skipped:N0} rows of {File} have the wrong number of fields; left out", State, skipped, FileName);
                }
            }

            return (target, true);
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
        ], Header: "License #"),
        new CsvUrlDataset("MD", "md_bop", new Uri("https://www.mbp.state.md.us/forms/allied_health_list.csv"), "license",
        [
            new("Profession", "license_type"), new("License #", "license_number"), new("Last Name", "last_name"), new("First Name", "first_name"),
            new("Status", "status"), new("Expire Date", "expiration_date", CsvValue.FlexibleDate), new("Discipline", "discipline"),
        ], Header: "Profession"),
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
    ];

    public override string Name => "state_licenses";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var versions = new List<string>();
        foreach (var dataset in Datasets)
        {
            versions.Add($"{dataset.State} {await dataset.VersionAsync(context, ct) ?? "none"}");
        }

        return new DatasetRelease(string.Join(" | ", versions), new Uri("https://data.cms.gov/"));
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

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        const string raw = "state_license_raw_staging";
        await using var connection = await context.Database.OpenAsync(ct);
        try
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
            await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` LIKE `state_license_raw`", ct);
            foreach (var dataset in Datasets)
            {
                if (await dataset.VersionAsync(context, ct) is null)
                {
                    context.Log.Information("State licenses {State}: {Source} has no files in the state files folder; skipped", dataset.State, dataset.Source);
                    continue;
                }

                var (path, delete) = await dataset.FetchAsync(context, ct);
                try
                {
                    var rows = await CsvTableLoader.LoadAsync(connection, path, raw, dataset.Columns, ct, constants: new Dictionary<string, string>
                    {
                        ["state"] = dataset.State, ["kind"] = dataset.Kind, ["source"] = dataset.Source,
                    }, delimiter: dataset.Delimiter, headerStartsWith: dataset.HeaderStartsWith);
                    context.Log.Information("State licenses {State} ({Kind}): {Rows:N0} rows", dataset.State, dataset.Kind, rows);
                }
                finally
                {
                    if (delete)
                    {
                        File.Delete(path);
                    }
                }
            }

            var states = string.Join(", ", Datasets.Select(d => $"'{d.State}'").Distinct());
            var counts = await TableSwap.ReplaceAsync(connection, ["provider_state_license"], context.Options.MinRowRatio, () =>
                Database.ExecuteAsync(connection,
                    $"""
                    INSERT INTO `provider_state_license_staging` (`npi`, `state`, `kind`, `source`, `license_number`, `license_type`, `status`,
                      `expiration_date`, `discipline`, `action_date`, `action`, `description`, `verify_url`)
                    SELECT x.`npi`, r.`state`, r.`kind`, r.`source`, r.`license_number`, r.`license_type`, r.`status`, r.`expiration_date`,
                      r.`discipline`, r.`action_date`, r.`action`, r.`description`, r.`verify_url`
                    FROM (
                      SELECT m.`npi`, r.`id` FROM (
                        SELECT DISTINCT t.`npi`, t.`license_state` AS `state`, {TaxonomyKey} AS `k_all`, {TaxonomyKeyLast} AS `k_last`
                        FROM `provider_taxonomy` t WHERE t.`license_state` IN ({states}) AND t.`license_no` IS NOT NULL
                      ) m
                      JOIN `{raw}` r ON r.`state` = m.`state` AND r.`license_key` = m.`k_all`
                      JOIN `provider` p ON p.`npi` = m.`npi`
                      WHERE {SameName}
                      UNION
                      SELECT m.`npi`, r.`id` FROM (
                        SELECT DISTINCT t.`npi`, t.`license_state` AS `state`, {TaxonomyKey} AS `k_all`, {TaxonomyKeyLast} AS `k_last`
                        FROM `provider_taxonomy` t WHERE t.`license_state` IN ({states}) AND t.`license_no` IS NOT NULL
                      ) m
                      JOIN `{raw}` r ON r.`state` = m.`state` AND r.`license_key_last` = m.`k_last`
                      JOIN `provider` p ON p.`npi` = m.`npi`
                      WHERE {SameName}
                    ) x
                    JOIN `{raw}` r ON r.`id` = x.`id`
                    """, ct), ct);
            return counts["provider_state_license"];
        }
        finally
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
        }
    }
}
