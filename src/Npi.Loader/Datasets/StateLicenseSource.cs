using System.Globalization;
using System.Text.Json;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// State license records and board actions (CLAUDE.md §7 Stage 5.5 item 15), from state open-data portals (Socrata)
/// that publish them for reuse. Each dataset is exported as CSV with only the columns used, into one scratch table
/// (<c>state_license_raw</c>); the rows whose license matches an NPPES taxonomy license of a provider (same state,
/// same number ignoring punctuation and leading zeros, same last name) are kept in <c>provider_state_license</c>.
/// Rows are never matched by name alone. One source for all states, so the table is replaced in one swap; a state that
/// fails to load keeps the previous data for every state.
/// </summary>
public sealed class StateLicenseSource : DatasetSource
{
    /// <summary>One state's dataset: where it is, which kind of rows it has, and how its columns map to state_license_raw.</summary>
    /// <param name="Kind">"license" (a license record with status) or "action" (a board action).</param>
    public sealed record StateDataset(string State, string Source, string Domain, string Id, string Kind, IReadOnlyList<CsvColumn> Columns)
    {
        /// <summary>The CSV export of just the mapped columns (SODA 2.1; the column headers are the field names).</summary>
        public Uri ExportUrl => new($"https://{Domain}/resource/{Id}.csv?$select={string.Join(",", Columns.Select(c => c.Header))}&$limit=50000000");

        /// <summary>The dataset's metadata (its rowsUpdatedAt is the version).</summary>
        public Uri MetadataUrl => new($"https://{Domain}/api/views/{Id}.json");

        /// <summary>The dataset's page, linked from the provider page.</summary>
        public Uri PageUrl => new($"https://{Domain}/d/{Id}");
    }

    public static readonly IReadOnlyList<StateDataset> Datasets =
    [
        // NY Department of Health, Board for Professional Medical Conduct: every public action since 1990 (MD, DO, PA, SA).
        new("NY", "ny_bpmc", "health.data.ny.gov", "ebmi-8ctw", "action",
        [
            new("licensenum", "license_number"), new("last_name", "last_name"), new("first_name", "first_name"), new("licensetype", "license_type"),
            new("effectivedate", "action_date", CsvValue.FlexibleDate), new("webaction", "action"), new("webnotes", "description"),
        ]),
        // Texas Medical Board: every TMB license (physicians, PAs, radiologic technologists, respiratory care, …).
        new("TX", "tx_tmb", "data.texas.gov", "tm3v-pfq9", "license",
        [
            new("license_number", "license_number"), new("last_name", "last_name"), new("first_name", "first_name"), new("license_type", "license_type"),
            new("registration_status", "status"), new("license_expiration_date", "expiration_date", CsvValue.FlexibleDate),
            new("disciplinary_status", "discipline"), new("current_board_action_date", "action_date", CsvValue.FlexibleDate),
            new("current_board_action_description", "action"), new("license_status", "description"),
        ]),
        // Washington State Department of Health: every health care credential (public domain).
        new("WA", "wa_doh", "data.wa.gov", "qxh8-f4bd", "license",
        [
            new("credentialnumber", "license_number"), new("lastname", "last_name"), new("firstname", "first_name"), new("credentialtype", "license_type"),
            new("status", "status"), new("expirationdate", "expiration_date", CsvValue.FlexibleDate), new("actiontaken", "discipline"),
        ]),
        // Illinois IDFPR: every professional license, with the most recent discipline (Open Database License).
        new("IL", "il_idfpr", "illinois-edp.data.socrata.com", "pzzh-kp68", "license",
        [
            new("license_number", "license_number"), new("last_name", "last_name"), new("first_name", "first_name"), new("description", "license_type"),
            new("license_status", "status"), new("expiration_date", "expiration_date", CsvValue.FlexibleDate), new("ever_disciplined", "discipline"),
            new("discipline_start_date", "action_date", CsvValue.FlexibleDate), new("action", "action"), new("discipline_reason", "description"),
        ]),
        // Colorado DORA: every professional and occupational license, one row per discipline case (public domain).
        new("CO", "co_dora", "data.colorado.gov", "7s5z-vewr", "license",
        [
            new("licensenumber", "license_number"), new("lastname", "last_name"), new("firstname", "first_name"), new("licensetype", "license_type"),
            new("licensestatusdescription", "status"), new("licenseexpirationdate", "expiration_date", CsvValue.FlexibleDate),
            new("programaction", "action"), new("disciplineeffectivedate", "action_date", CsvValue.FlexibleDate), new("casenumber", "description"),
            new("linktoverifylicense", "verify_url"),
        ]),
    ];

    public override string Name => "state_licenses";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var versions = new List<string>();
        foreach (var dataset in Datasets)
        {
            await using var stream = await context.Http.GetStreamAsync(dataset.MetadataUrl, ct);
            versions.Add($"{dataset.State} {ParseRowsUpdated(stream)}");
        }

        return new DatasetRelease(string.Join(" | ", versions), Datasets[0].ExportUrl);
    }

    /// <summary>When the dataset's rows last changed (Socrata <c>rowsUpdatedAt</c>, Unix seconds), as a UTC timestamp.</summary>
    public static string ParseRowsUpdated(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("rowsUpdatedAt", out var updated) && updated.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            : throw new InvalidDataException("The dataset metadata has no rowsUpdatedAt.");
    }

    // The NPPES license number in the same form as state_license_raw.license_key.
    private const string TaxonomyKey = "NULLIF(TRIM(LEADING '0' FROM REGEXP_REPLACE(COALESCE(t.`license_no`, ''), '[^0-9]', '')), '')";

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
                var path = await context.DownloadAsync(new DatasetRelease(dataset.Source, dataset.ExportUrl), $"state_{dataset.Source}.csv", ct);
                try
                {
                    var rows = await CsvTableLoader.LoadAsync(connection, path, raw, dataset.Columns, ct, constants: new Dictionary<string, string>
                    {
                        ["state"] = dataset.State, ["kind"] = dataset.Kind, ["source"] = dataset.Source,
                    });
                    context.Log.Information("State licenses {State}: {Rows:N0} rows", dataset.State, rows);
                }
                finally
                {
                    File.Delete(path);
                }
            }

            var states = string.Join(", ", Datasets.Select(d => $"'{d.State}'").Distinct());
            var counts = await TableSwap.ReplaceAsync(connection, ["provider_state_license"], context.Options.MinRowRatio, () =>
                Database.ExecuteAsync(connection,
                    $"""
                    INSERT INTO `provider_state_license_staging` (`npi`, `state`, `kind`, `source`, `license_number`, `license_type`, `status`,
                      `expiration_date`, `discipline`, `action_date`, `action`, `description`, `verify_url`)
                    SELECT DISTINCT m.`npi`, r.`state`, r.`kind`, r.`source`, r.`license_number`, r.`license_type`, r.`status`, r.`expiration_date`,
                      r.`discipline`, r.`action_date`, r.`action`, r.`description`, r.`verify_url`
                    FROM (
                      SELECT DISTINCT t.`npi`, t.`license_state` AS `state`, {TaxonomyKey} AS `license_key`
                      FROM `provider_taxonomy` t
                      WHERE t.`license_state` IN ({states}) AND t.`license_no` IS NOT NULL
                    ) m
                    JOIN `{raw}` r ON r.`state` = m.`state` AND r.`license_key` = m.`license_key`
                    JOIN `provider` p ON p.`npi` = m.`npi`
                    WHERE r.`last_name` IS NOT NULL
                      AND (p.`last_name` = r.`last_name` OR SUBSTRING_INDEX(p.`last_name`, ' ', 1) = SUBSTRING_INDEX(r.`last_name`, ' ', 1))
                    """, ct), ct);
            return counts["provider_state_license"];
        }
        finally
        {
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
        }
    }
}
