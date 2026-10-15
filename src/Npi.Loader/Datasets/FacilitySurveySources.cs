using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npi.Core.Search;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// Care Compare "OAS CAHPS survey for ambulatory surgical centers - Facility" and "- National" (docs/PRODUCT_PLAN.md step 3.6 part 2, owner
/// 2026-10-15): per surgery center the top-box share of each of its four questions (facilities and staff, communication about the procedure,
/// would recommend, rating of the facility), the completed surveys, the response rate and the period; the national shares as CCN "NATION"
/// (<c>cms_asc_survey</c>).
/// </summary>
public sealed class SurgeryCenterSurveySource : DatasetSource
{
    public const string FacilityId = "48nr-hqxx";
    public const string NationalId = "tf3h-mrrs";

    public override string Name => "cc_oas_cahps";

    // The top box of each question, in CMS's column names.
    private const string Professional = "Patients who reported that staff definitely gave care in a professional way and the facility was clean";
    private const string Communication = "Patients who reported that staff definitely communicated about what to expect during and after the procedure";
    private const string Recommend = "Patients who reported YES they would DEFINITELY recommend the facility to family or friends";
    private const string Rating = "Patients who gave the facility a rating of 9 or 10 on a scale from 0 (lowest) to 10 (highest)";

    private static readonly CsvColumn[] NationalColumns =
    [
        new(Professional, "professional", CsvValue.OptionalWholeNumber), new(Communication, "communication", CsvValue.OptionalWholeNumber),
        new(Recommend, "recommend", CsvValue.OptionalWholeNumber), new(Rating, "rating", CsvValue.OptionalWholeNumber),
        new("Number of Completed Surveys", "surveys", CsvValue.OptionalWholeNumber),
        new("Survey Response Rate Percent", "response_rate", CsvValue.OptionalWholeNumber),
        new("Start Date", "start_date", CsvValue.FlexibleDate), new("End Date", "end_date", CsvValue.FlexibleDate),
    ];

    private static readonly CsvColumn[] FacilityColumns = [new("Facility ID", "ccn"), .. NationalColumns];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
        DialysisFacilitySource.Combined(await ReleasesAsync(context, ct));

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var releases = await ReleasesAsync(context, ct);
        return await WithDownloadsAsync(context, [(releases[0], "cc_oas_cahps_facility.csv"), (releases[1], "cc_oas_cahps_national.csv")], async (connection, paths) =>
        {
            var counts = await TableSwap.ReplaceAsync(connection, ["cms_asc_survey"], context.Options.MinRowRatio, async () =>
            {
                await CsvTableLoader.LoadAsync(connection, paths[0], "cms_asc_survey_staging", FacilityColumns, ct);
                await CsvTableLoader.LoadAsync(connection, paths[1], "cms_asc_survey_staging", NationalColumns, ct,
                    constants: new Dictionary<string, string> { ["ccn"] = FacilityMeasures.National });
            }, ct);
            return counts["cms_asc_survey"];
        }, ct);
    }

    private static async Task<IReadOnlyList<DatasetRelease>> ReleasesAsync(DatasetContext context, CancellationToken ct) =>
        [await context.ProviderDataAsync(FacilityId, ct), await context.ProviderDataAsync(NationalId, ct)];
}

/// <summary>
/// The ESRD Quality Incentive Program's total performance scores (step 3.6 part 2, owner 2026-10-15): the Provider Data Catalog lists one
/// "ESRD QIP - Total Performance Scores - Payment Year &lt;year&gt;" a year; the newest payment year's file gives per dialysis facility its
/// score, the state and national averages and the payment reduction (<c>cms_dialysis_qip</c>). Checked weekly: CMS publishes it once a year.
/// </summary>
public sealed partial class DialysisQipSource : DatasetSource
{
    public override string Name => "cc_esrd_qip";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    private static readonly CsvColumn[] Columns =
    [
        new("CMS Certification Number (CCN)", "ccn"), new("Total Performance Score", "total_score"),
        new("State Average Total Performance Score", "state_average"), new("National Average Total Performance Score", "national_average"),
        new("Payment Reduction Percentage", "payment_reduction"),
    ];

    [GeneratedRegex(@"^ESRD QIP - Total Performance Scores - Payment Year (?<year>\d{4})$")]
    private static partial Regex Title();

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        await using var stream = await context.Http.GetStreamAsync(new Uri(context.Options.ProviderDataMetastoreUrl), ct);
        return ParseList(stream);
    }

    /// <summary>The newest payment year's entry in the Provider Data Catalog's dataset list.</summary>
    public static DatasetRelease ParseList(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        DatasetRelease? best = null;
        var bestYear = 0;
        foreach (var dataset in doc.RootElement.EnumerateArray())
        {
            var match = Title().Match((dataset.TryGetProperty("title", out var t) ? t.GetString() : null)?.Trim() ?? "");
            if (!match.Success || !dataset.TryGetProperty("distribution", out var distributions) || distributions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
            var modified = dataset.TryGetProperty("modified", out var m) ? m.GetString() ?? "" : "";
            foreach (var distribution in distributions.EnumerateArray())
            {
                var data = distribution.TryGetProperty("data", out var d) ? d : distribution;
                if (year > bestYear && data.TryGetProperty("downloadURL", out var download) && download.GetString() is { Length: > 0 } u)
                {
                    var url = new Uri(u);
                    best = new DatasetRelease($"PY {year} {modified} {Path.GetFileName(url.AbsolutePath)}", url, $"{year}-12-31");
                    bestYear = year;
                }
            }
        }

        return best ?? throw new InvalidDataException("The Provider Data Catalog lists no \"ESRD QIP - Total Performance Scores - Payment Year <year>\".");
    }

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "cc_esrd_qip_tps.csv", async (connection, path) =>
        {
            var year = release.DataYear ?? throw new InvalidDataException("The ESRD QIP release has no payment year.");
            const string raw = "cms_dialysis_qip_raw_staging";
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection,
                    $"""
                    CREATE TABLE `{raw}` (`ccn` VARCHAR(10) NULL, `total_score` VARCHAR(40) NULL, `state_average` VARCHAR(40) NULL,
                      `national_average` VARCHAR(40) NULL, `payment_reduction` VARCHAR(40) NULL) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
                    """, ct);
                await CsvTableLoader.LoadAsync(connection, path, raw, Columns, ct);
                var counts = await TableSwap.ReplaceAsync(connection, ["cms_dialysis_qip"], context.Options.MinRowRatio, () =>
                    // Scores are whole numbers ("No Score" → NULL); the reduction is written "1.5%".
                    Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `cms_dialysis_qip_staging` (`ccn`, `payment_year`, `total_score`, `state_average`, `national_average`, `payment_reduction`)
                        SELECT UPPER(`ccn`), @year, MAX({Whole("total_score")}), MAX({Whole("state_average")}), MAX({Whole("national_average")}),
                               MAX(IF(REPLACE(`payment_reduction`, '%', '') REGEXP '^[0-9]+([.][0-9]+)?$', CAST(REPLACE(`payment_reduction`, '%', '') AS DOUBLE), NULL))
                        FROM `{raw}` WHERE `ccn` REGEXP '^[0-9A-Za-z]+$' GROUP BY UPPER(`ccn`)
                        """, ct, param: new { year }), ct);
                return counts["cms_dialysis_qip"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }, ct);

    private static string Whole(string column) => $"IF(`{column}` REGEXP '^[0-9]+$', CAST(`{column}` AS SIGNED), NULL)";
}
