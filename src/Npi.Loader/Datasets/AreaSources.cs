using System.Globalization;
using System.Text.RegularExpressions;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// HRSA Health Professional Shortage Areas for primary care, dental and mental health (CLAUDE.md §7 Stage 5.5
/// item 7a), summarized per county in county_shortage. HRSA rebuilds the files daily, so they are checked weekly.
/// </summary>
public sealed class ShortageAreaSource : DatasetSource
{
    /// <summary>Discipline code → file suffix of BCD_HPSA_FCT_DET_{suffix}.csv.</summary>
    public static readonly string[] Disciplines = ["PC", "DH", "MH"];

    public override string Name => "hrsa_hpsa";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    private static readonly CsvColumn[] Columns =
    [
        new("HPSA ID", "hpsa_id"), new("Designation Type", "designation_type"), new("HPSA Status", "status"),
        new("HPSA Score", "score", CsvValue.OptionalWholeNumber), new("HPSA Component Type Description", "component_type"),
        new("Common State County FIPS Code", "county_fips"),
    ];

    private static Uri FileUrl(DatasetContext context, string discipline) =>
        new(context.Options.HrsaHpsaUrlTemplate.Replace("{discipline}", discipline, StringComparison.Ordinal));

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var parts = new List<string>();
        foreach (var discipline in Disciplines)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, FileUrl(context, discipline));
            using var response = await context.Http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            parts.Add(response.Content.Headers.LastModified?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                      ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return new DatasetRelease("HPSA " + string.Join(" ", parts.Distinct()), FileUrl(context, Disciplines[0]));
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        const string raw = "hrsa_shortage_raw_staging";
        var files = new List<string>();
        try
        {
            foreach (var discipline in Disciplines)
            {
                files.Add(await context.DownloadAsync(new DatasetRelease(release.Version, FileUrl(context, discipline)), $"hrsa_hpsa_{discipline}.csv", ct));
            }

            await using var connection = await context.Database.OpenAsync(ct);
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` LIKE `hrsa_shortage_raw`", ct);
                for (var i = 0; i < Disciplines.Length; i++)
                {
                    await CsvTableLoader.LoadAsync(connection, files[i], raw, Columns, ct, constants: new Dictionary<string, string> { ["discipline"] = Disciplines[i] });
                }

                var counts = await TableSwap.ReplaceAsync(connection, ["county_shortage"], context.Options.MinRowRatio, () =>
                    Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `county_shortage_staging` (`county_fips`, `discipline`, `whole_county`, `hpsa_count`, `max_score`)
                        SELECT `county_fips`, `discipline`, COALESCE(MAX(`component_type` = 'Single County'), 0), COUNT(DISTINCT `hpsa_id`), MAX(`score`)
                        FROM `{raw}`
                        WHERE `status` IN ('Designated', 'Proposed For Withdrawal') AND CHAR_LENGTH(`county_fips`) = 5 AND `county_fips` REGEXP '^[0-9]+$'
                        GROUP BY `county_fips`, `discipline`
                        """, ct), ct);
                return counts["county_shortage"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }
        finally
        {
            foreach (var file in files)
            {
                File.Delete(file);
            }
        }
    }
}

/// <summary>
/// Census Bureau county population estimates, newest vintage (Stage 5.5 item 7b): the newest
/// <c>2020-YYYY/</c> folder under the population estimates datasets, file counties/totals/co-estYYYY-alldata.csv.
/// The file is Windows-1252 (county names like "Doña Ana").
/// </summary>
public sealed partial class CountyPopulationSource : DatasetSource
{
    public override string Name => "census_county_population";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    [GeneratedRegex(@"href=""(?<from>\d{4})-(?<to>\d{4})/""")]
    private static partial Regex VintageFolder();

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var listing = new Uri(context.Options.CensusPopulationBaseUrl);
        var html = await context.Http.GetStringAsync(listing, ct);
        var latest = VintageFolder().Matches(html)
            .Select(m => (Folder: $"{m.Groups["from"].Value}-{m.Groups["to"].Value}", Year: int.Parse(m.Groups["to"].Value, CultureInfo.InvariantCulture)))
            .OrderByDescending(v => v.Year)
            .FirstOrDefault();
        if (latest.Folder is null)
        {
            throw new InvalidDataException($"No population estimate vintages listed at {listing}.");
        }

        var url = new Uri(listing, $"{latest.Folder}/counties/totals/co-est{latest.Year}-alldata.csv");
        return new DatasetRelease($"vintage {latest.Year}", url, $"{latest.Year}-07-01");
    }

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "census_county_population.csv", async (connection, path) =>
        {
            const string raw = "county_population_raw_staging";
            var year = release.DataYear ?? throw new InvalidDataException("The population vintage has no year.");
            CsvColumn[] columns =
            [
                new("SUMLEV", "sumlev"), new("STATE", "state_fips"), new("COUNTY", "county_code"),
                new($"POPESTIMATE{year.ToString(CultureInfo.InvariantCulture)}", "population", CsvValue.OptionalWholeNumber),
            ];
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` LIKE `county_population_raw`", ct);
                await CsvTableLoader.LoadAsync(connection, path, raw, columns, ct, "latin1");
                var counts = await TableSwap.ReplaceAsync(connection, ["county_population"], context.Options.MinRowRatio, () =>
                    Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `county_population_staging` (`county_fips`, `state_fips`, `county_code`, `population`, `year`)
                        SELECT CONCAT(`state_fips`, `county_code`), `state_fips`, `county_code`, `population`, {year.ToString(CultureInfo.InvariantCulture)}
                        FROM `{raw}` WHERE `sumlev` = '050' AND `population` IS NOT NULL
                        """, ct), ct);
                return counts["county_population"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }, ct);
}
