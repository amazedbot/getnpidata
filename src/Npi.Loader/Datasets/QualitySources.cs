using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// Clinician MIPS performance from Care Compare (CLAUDE.md §7 Stage 5.5 item 14): the newest "PY &lt;year&gt; Clinician
/// Public Reporting: Overall MIPS Performance" dataset. Each program year is a new dataset id, so it is found by title
/// in the Provider Data Catalog's list of datasets. One row per score (individual, group, APM entity, subgroup).
/// </summary>
public sealed partial class MipsSource : DatasetSource
{
    public override string Name => "cc_mips";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    private static readonly CsvColumn[] Columns =
    [
        new("NPI", "npi", CsvValue.Npi), new("Org_PAC_ID", "org_pac_id"), new("source", "source"), new("Facility Name", "facility_name"),
        new("Quality_category_score", "quality_score", CsvValue.Number), new("PI_category_score", "pi_score", CsvValue.Number),
        new("IA_category_score", "ia_score", CsvValue.Number), new("Cost_category_score", "cost_score", CsvValue.Number),
        new("final_MIPS_score", "final_score", CsvValue.Number),
    ];

    [GeneratedRegex(@"^PY (?<year>\d{4}) Clinician Public Reporting: Overall MIPS Performance$")]
    private static partial Regex Title();

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        await using var stream = await context.Http.GetStreamAsync(new Uri(context.Options.ProviderDataMetastoreUrl), ct);
        return ParseList(stream);
    }

    /// <summary>The newest program year's entry in the Provider Data Catalog's dataset list.</summary>
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
                if (year > bestYear && data.TryGetProperty("downloadURL", out var download) && download.GetString() is { } u)
                {
                    var url = new Uri(u);
                    best = new DatasetRelease($"PY {year} {modified} {Path.GetFileName(url.AbsolutePath)}", url, $"{year}-12-31");
                    bestYear = year;
                }
            }
        }

        return best ?? throw new InvalidDataException("The Provider Data Catalog lists no \"PY <year> Clinician Public Reporting: Overall MIPS Performance\".");
    }

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "cc_mips.csv", async (connection, path) =>
        {
            var year = (release.DataYear ?? throw new InvalidDataException("The MIPS release has no program year.")).ToString(CultureInfo.InvariantCulture);
            var counts = await TableSwap.ReplaceAsync(connection, ["cc_mips"], context.Options.MinRowRatio,
                () => CsvTableLoader.LoadAsync(connection, path, "cc_mips_staging", Columns, ct,
                    constants: new Dictionary<string, string> { ["program_year"] = year }), ct);
            return counts["cc_mips"];
        }, ct);
}

/// <summary>Care Compare "Patient survey (HCAHPS) - Hospital": each hospital's HCAHPS summary star rating (item 14).</summary>
public sealed class HcahpsSource : DatasetSource
{
    public const string DatasetId = "dgck-syfz";

    public override string Name => "cc_hcahps";

    private static readonly CsvColumn[] Columns =
    [
        new("Facility ID", "ccn"), new("HCAHPS Measure ID", "measure_id"), new("Patient Survey Star Rating", "star_rating", CsvValue.OptionalWholeNumber),
        new("Number of Completed Surveys", "surveys", CsvValue.OptionalWholeNumber),
        new("Survey Response Rate Percent", "response_rate", CsvValue.OptionalNumber),
    ];

    public override Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) => context.ProviderDataAsync(DatasetId, ct);

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "cc_hcahps.csv", async (connection, path) =>
        {
            const string raw = "cms_hcahps_raw_staging";
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` LIKE `cms_hcahps_raw`", ct);
                await CsvTableLoader.LoadAsync(connection, path, raw, Columns, ct);
                var counts = await TableSwap.ReplaceAsync(connection, ["cms_hospital_survey"], context.Options.MinRowRatio, () =>
                    Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `cms_hospital_survey_staging` (`ccn`, `star_rating`, `surveys`, `response_rate`)
                        SELECT `ccn`, MAX(`star_rating`), MAX(`surveys`), MAX(`response_rate`)
                        FROM `{raw}` WHERE `ccn` IS NOT NULL AND `measure_id` = 'H_STAR_RATING' GROUP BY `ccn`
                        """, ct), ct);
                return counts["cms_hospital_survey"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }, ct);
}

/// <summary>Care Compare "Home Health Care Agencies": ownership and the quality of patient care star rating (item 14).</summary>
public sealed class HomeHealthSource : DatasetSource
{
    public const string DatasetId = "6jpm-sxkc";

    public override string Name => "cc_home_health";

    private static readonly CsvColumn[] Columns =
    [
        new("CMS Certification Number (CCN)", "ccn"), new("Provider Name", "name"), new("Address", "address"), new("City/Town", "city"),
        new("State", "state"), new("ZIP Code", "zip"), new("Telephone Number", "phone"), new("Type of Ownership", "ownership"),
        new("Quality of patient care star rating", "quality_rating", CsvValue.OptionalNumber),
    ];

    public override Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) => context.ProviderDataAsync(DatasetId, ct);

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        FacilityAffiliationSource.ReplaceOneAsync(context, release, "cc_home_health.csv", "cms_home_health", Columns, ct);
}

/// <summary>
/// Care Compare hospices (item 14): "Hospice - General Information" plus the family caregiver survey summary star
/// rating from "Hospice care - Provider CAHPS Hospice Survey Data". Two files, one table, so one source.
/// </summary>
public sealed class HospiceSource : DatasetSource
{
    public const string GeneralId = "yc9t-dgbk";
    public const string SurveyId = "gxki-hrr8";

    public override string Name => "cc_hospices";

    private static readonly CsvColumn[] GeneralColumns =
    [
        new("CMS Certification Number (CCN)", "ccn"), new("Facility Name", "name"), new("Address Line 1", "address"), new("City/Town", "city"),
        new("State", "state"), new("ZIP Code", "zip"), new("Telephone Number", "phone"), new("Ownership Type", "ownership"),
    ];

    private static readonly CsvColumn[] SurveyColumns =
    [
        new("CMS Certification Number (CCN)", "ccn"), new("Measure Code", "measure_code"), new("Star Rating", "star_rating", CsvValue.OptionalWholeNumber),
    ];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var general = await context.ProviderDataAsync(GeneralId, ct);
        var survey = await context.ProviderDataAsync(SurveyId, ct);
        return new DatasetRelease($"{general.Version} | {survey.Version}", general.Url);
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var general = await context.ProviderDataAsync(GeneralId, ct);
        var survey = await context.ProviderDataAsync(SurveyId, ct);
        var generalFile = await context.DownloadAsync(general, "cc_hospice_general.csv", ct);
        var surveyFile = await context.DownloadAsync(survey, "cc_hospice_cahps.csv", ct);
        const string raw = "cms_hospice_cahps_raw_staging";
        try
        {
            await using var connection = await context.Database.OpenAsync(ct);
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` LIKE `cms_hospice_cahps_raw`", ct);
                await CsvTableLoader.LoadAsync(connection, surveyFile, raw, SurveyColumns, ct);
                var counts = await TableSwap.ReplaceAsync(connection, ["cms_hospice"], context.Options.MinRowRatio, async () =>
                {
                    await CsvTableLoader.LoadAsync(connection, generalFile, "cms_hospice_staging", GeneralColumns, ct);
                    await Database.ExecuteAsync(connection,
                        $"""
                        UPDATE `cms_hospice_staging` h
                        JOIN (SELECT `ccn`, MAX(`star_rating`) AS `star` FROM `{raw}` WHERE `measure_code` = 'SUMMARY_STAR_RATING' GROUP BY `ccn`) s
                          ON s.`ccn` = h.`ccn`
                        SET h.`family_rating` = s.`star`
                        """, ct);
                }, ct);
                return counts["cms_hospice"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }
        finally
        {
            File.Delete(generalFile);
            File.Delete(surveyFile);
        }
    }
}
