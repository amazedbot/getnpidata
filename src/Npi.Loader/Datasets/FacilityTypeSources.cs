using System.Text.Json;
using System.Text.RegularExpressions;
using Npi.Core.Search;
using Npi.Loader.Db;
using Npi.Loader.Reference;

namespace Npi.Loader.Datasets;

/// <summary>
/// Care Compare "Dialysis Facility - Listing by Facility" (docs/PRODUCT_PLAN.md step 3.6 part 2): name, address, profit status, chain,
/// stations, services, the five-star rating and the outcome categories (survival, hospital admissions, readmissions, ED visits,
/// infections, transfusions, transplant waitlisting) with their periods; plus the patient survey star rating from "Patient survey (ICH
/// CAHPS) - Facility". Two files, one table (<c>cms_dialysis</c>).
/// </summary>
public sealed class DialysisFacilitySource : DatasetSource
{
    public const string ListingId = "23ew-n7w9";
    public const string SurveyId = "59mq-zhts";

    public override string Name => "cc_dialysis";

    private static readonly CsvColumn[] ListingColumns =
    [
        new("CMS Certification Number (CCN)", "ccn"), new("Facility Name", "name"), new("Address Line 1", "address"), new("City/Town", "city"),
        new("State", "state"), new("ZIP Code", "zip"), new("Telephone Number", "phone"), new("Profit or Non-Profit", "ownership"),
        new("Chain Organization", "chain"), new("# of Dialysis Stations", "stations", CsvValue.OptionalWholeNumber),
        new("Offers in-center hemodialysis", "in_center", CsvValue.YesNo), new("Offers peritoneal dialysis", "peritoneal", CsvValue.YesNo),
        new("Offers home hemodialysis training", "home_hemodialysis", CsvValue.YesNo),
        new("Five Star", "five_star", CsvValue.OptionalNumber), new("Five Star Date", "five_star_period"),
        new("Patient Survival Category Text", "survival"), new("SMR Date", "survival_period"),
        new("Patient hospitalization category text", "hospitalization"), new("SHR Date", "hospitalization_period"),
        new("Patient Hospital Readmission Category", "readmission"), new("SRR Date", "readmission_period"),
        new("SEDR category text", "ed_visits"), new("SEDR Date", "ed_visits_period"),
        new("Patient Infection category text", "infection"), new("SIR Date", "infection_period"),
        new("Patient Transfusion category text", "transfusion"), new("STrR Date", "transfusion_period"),
        new("FYSWR category text", "waitlist"), new("FYSWR Date", "waitlist_period"),
    ];

    private static readonly CsvColumn[] SurveyColumns =
    [
        new("CMS Certification Number (CCN)", "ccn"), new("ICH CAHPS Survey of patients' experiences star rating", "star_rating", CsvValue.OptionalNumber),
    ];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) => Combined(await ReleasesAsync(context, ct));

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var releases = await ReleasesAsync(context, ct);
        return await WithDownloadsAsync(context, [(releases[0], "cc_dialysis.csv"), (releases[1], "cc_dialysis_ich_cahps.csv")], async (connection, paths) =>
        {
            const string raw = "cms_dialysis_survey_raw_staging";
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection,
                    $"CREATE TABLE `{raw}` (`ccn` VARCHAR(10) NULL, `star_rating` DOUBLE NULL) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci", ct);
                await CsvTableLoader.LoadAsync(connection, paths[1], raw, SurveyColumns, ct);
                var counts = await TableSwap.ReplaceAsync(connection, ["cms_dialysis"], context.Options.MinRowRatio, async () =>
                {
                    await CsvTableLoader.LoadAsync(connection, paths[0], "cms_dialysis_staging", ListingColumns, ct);
                    await Database.ExecuteAsync(connection,
                        $"""
                        UPDATE `cms_dialysis_staging` d
                        JOIN (SELECT `ccn`, MAX(`star_rating`) AS `star` FROM `{raw}` WHERE `star_rating` BETWEEN 1 AND 5 GROUP BY `ccn`) s ON s.`ccn` = d.`ccn`
                        SET d.`survey_rating` = ROUND(s.`star`)
                        """, ct);
                }, ct);
                return counts["cms_dialysis"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }, ct);
    }

    private static async Task<IReadOnlyList<DatasetRelease>> ReleasesAsync(DatasetContext context, CancellationToken ct) =>
        [await context.ProviderDataAsync(ListingId, ct), await context.ProviderDataAsync(SurveyId, ct)];

    /// <summary>
    /// The versions of a source's Provider Data Catalog files, joined; the URL of the first. When the list is longer than
    /// <c>reference_data.version</c> holds (500 characters; the ten hospital measure files are), the number of files, the newest
    /// "modified" date and a hash of the whole list instead, so any file's new release still changes it.
    /// </summary>
    internal static DatasetRelease Combined(IReadOnlyList<DatasetRelease> releases)
    {
        var all = string.Join(" | ", releases.Select(r => r.Version));
        if (all.Length > MaxVersionLength)
        {
            var newest = releases.Select(r => r.Version.Split(' ')[0]).Max(StringComparer.Ordinal);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(all)))[..12];
            all = $"{releases.Count} files, newest {newest}, {hash}";
        }

        return new(all, releases[0].Url);
    }

    /// <summary>The length of <c>reference_data.version</c>.</summary>
    internal const int MaxVersionLength = 500;
}

/// <summary>
/// The NPI of each dialysis facility (step 3.6 part 2), from data.cms.gov "Medicare Dialysis Facilities" (the yearly Dialysis Facility
/// Report, ~2.7 GB as one CSV of ~12M measure rows). Care Compare's listing has no NPI and CMS publishes no dialysis enrollment file, so
/// the loader asks the report's data API for one measure's rows (one per facility, ~8,200) with only the CCN and NPI: two requests
/// instead of the whole file. Kept apart from <c>cms_facility_npi</c>; the record-quality label counts its NPIs as holding a certified
/// facility (owner, 2026-10-15), and a reload queues only the NPIs whose rows changed (<see cref="TableSwap"/>).
/// </summary>
public sealed partial class DialysisNpiSource : DatasetSource
{
    public const string Title = "Medicare Dialysis Facilities";

    /// <summary>The data API's largest page.</summary>
    public const int PageSize = 5000;

    /// <summary>A safety stop: the report lists ~8,200 facilities, two pages.</summary>
    private const int MaxPages = 20;

    public override string Name => "cms_dialysis_npis";

    [GeneratedRegex("^[0-9A-Z]{6}$")]
    private static partial Regex Ccn();

    [GeneratedRegex("^[0-9]{10}$")]
    private static partial Regex Npi();

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
        (await context.CmsCatalogAsync(ct)).FindLatestApi(Title) ?? throw new InvalidDataException($"data.cms.gov lists no \"{Title}\" data API.");

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var pairs = await FetchAsync(context, release.Url, ct);
        context.Log.Information("Dialysis facility report: {Pairs:N0} CCN-NPI pairs", pairs.Count);
        await using var connection = await context.Database.OpenAsync(ct);
        var rows = ReferenceTables.ToDataTable(pairs, ("ccn", typeof(string), p => p.Ccn), ("npi", typeof(string), p => p.Npi));
        var counts = await TableSwap.ReplaceAsync(connection, ["cms_dialysis_npi"], context.Options.MinRowRatio,
            () => ReferenceTables.CopyToStagingAsync(connection, "cms_dialysis_npi", rows, ct), ct);
        return counts["cms_dialysis_npi"];
    }

    /// <summary>Every (CCN, NPI) of the report: the rows of its first row's measure, a page at a time.</summary>
    internal static async Task<IReadOnlyList<(string Ccn, string Npi)>> FetchAsync(DatasetContext context, Uri api, CancellationToken ct)
    {
        var first = ParsePage(await GetAsync(context, Page(api, "size=1&column=Measure_ID"), ct));
        var measure = first.Count > 0 && first[0].GetValueOrDefault("Measure_ID") is { Length: > 0 } m
            ? m
            : throw new InvalidDataException("The dialysis facility report's data API returned no row; its format changed?");
        var pairs = new SortedSet<(string Ccn, string Npi)>();
        for (var page = 0; ; page++)
        {
            if (page == MaxPages)
            {
                throw new InvalidDataException($"The dialysis facility report lists more than {MaxPages * PageSize:N0} rows for measure {measure}; one per facility expected.");
            }

            var rows = ParsePage(await GetAsync(context,
                Page(api, $"column=CCN,NPI&filter%5BMeasure_ID%5D={Uri.EscapeDataString(measure)}&size={PageSize}&offset={page * PageSize}"), ct));
            foreach (var row in rows)
            {
                var ccn = row.GetValueOrDefault("CCN")?.Trim().ToUpperInvariant();
                var npi = row.GetValueOrDefault("NPI")?.Trim();
                if (ccn is not null && npi is not null && Ccn().IsMatch(ccn) && Npi().IsMatch(npi) && npi != "0000000000")
                {
                    pairs.Add((ccn, npi));
                }
            }

            if (rows.Count < PageSize)
            {
                break;
            }
        }

        return pairs.Count > 0 ? [.. pairs] : throw new InvalidDataException("The dialysis facility report lists no facility with a CCN and an NPI.");
    }

    /// <summary>A data API page: an array of objects of strings (column → value).</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> ParsePage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The data API answered with something other than a list of rows.");
        }

        return [.. doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e =>
            (IReadOnlyDictionary<string, string?>)e.EnumerateObject().ToDictionary(p => p.Name,
                p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.ToString(),
                StringComparer.OrdinalIgnoreCase))];
    }

    private static Uri Page(Uri api, string query) => new(api.AbsoluteUri + (api.Query.Length > 0 ? "&" : "?") + query);

    private static async Task<string> GetAsync(DatasetContext context, Uri url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await context.Http.GetStringAsync(url, ct);
            }
            catch (HttpRequestException ex) when (attempt < context.Options.DownloadAttempts)
            {
                context.Log.Warning("Data API {Url} failed ({Message}); retrying", url, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
        }
    }
}

/// <summary>
/// Care Compare "Ambulatory Surgical Center Quality Measures - Facility" and "- National" (step 3.6 part 2): one row per surgery center and
/// NPI it reports under (name, city, state, ZIP, measure year) with the hospital visits after colonoscopy, orthopedic, urology and general
/// surgery procedures (rate, cases, CMS's comparison) and two process measures; the national rates as the row of CCN "NATION".
/// The file has no street address, phone or star rating.
/// </summary>
public sealed class SurgeryCenterSource : DatasetSource
{
    public const string FacilityId = "4jcv-atw7";
    public const string NationalId = "wue8-3vwe";

    public override string Name => "cc_surgery_centers";

    private static readonly CsvColumn[] FacilityColumns =
    [
        new("Facility ID", "ccn"), new("NPI", "npi", CsvValue.Npi), new("Facility Name", "name"), new("City/Town", "city"), new("State", "state"),
        new("ZIP Code", "zip"), new("Year", "data_year", CsvValue.OptionalWholeNumber),
        new("ASC-9 Rate*", "asc9_rate", CsvValue.OptionalNumber), new("ASC-13 Rate*", "asc13_rate", CsvValue.OptionalNumber),
        new("ASC-12 Total Cases", "asc12_cases", CsvValue.OptionalWholeNumber), new("ASC-12 Performance Category", "asc12_category"),
        new("ASC-12 RSHV Rate", "asc12_rate", CsvValue.OptionalNumber),
        new("ASC-17 Total Cases", "asc17_cases", CsvValue.OptionalWholeNumber), new("ASC-17 Performance Category", "asc17_category"),
        new("ASC-17 RSHV Rate", "asc17_rate", CsvValue.OptionalNumber),
        new("ASC-18 Total Cases", "asc18_cases", CsvValue.OptionalWholeNumber), new("ASC-18 Performance Category", "asc18_category"),
        new("ASC-18 RSHV Rate", "asc18_rate", CsvValue.OptionalNumber),
        new("ASC-19 Total Cases", "asc19_cases", CsvValue.OptionalWholeNumber), new("ASC-19 Performance Category", "asc19_category"),
        new("ASC-19 RSHV Rate", "asc19_rate", CsvValue.OptionalNumber),
    ];

    private static readonly CsvColumn[] NationalColumns =
    [
        new("Year", "data_year", CsvValue.OptionalWholeNumber), new("Avg ASC-9 Nat Rate*", "asc9_rate", CsvValue.OptionalNumber),
        new("Avg ASC-13 Nat Rate*", "asc13_rate", CsvValue.OptionalNumber), new("ASC-12 Nat Rate", "asc12_rate", CsvValue.OptionalNumber),
        new("ASC-17 Nat Rate", "asc17_rate", CsvValue.OptionalNumber), new("ASC-18 Nat Rate", "asc18_rate", CsvValue.OptionalNumber),
        new("ASC-19 Nat Rate", "asc19_rate", CsvValue.OptionalNumber),
    ];

    private const string Measures =
        "`asc9_rate`, `asc13_rate`, `asc12_cases`, `asc12_category`, `asc12_rate`, `asc17_cases`, `asc17_category`, `asc17_rate`, " +
        "`asc18_cases`, `asc18_category`, `asc18_rate`, `asc19_cases`, `asc19_category`, `asc19_rate`";

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
        DialysisFacilitySource.Combined(await ReleasesAsync(context, ct));

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var releases = await ReleasesAsync(context, ct);
        return await WithDownloadsAsync(context, [(releases[0], "cc_asc_facility.csv"), (releases[1], "cc_asc_national.csv")], async (connection, paths) =>
        {
            const string raw = "cms_asc_raw_staging";
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection,
                    $"""
                    CREATE TABLE `{raw}` (`ccn` VARCHAR(10) NULL, `npi` CHAR(10) NULL, `name` VARCHAR(255) NULL, `city` VARCHAR(100) NULL, `state` VARCHAR(40) NULL,
                      `zip` VARCHAR(10) NULL, `data_year` SMALLINT NULL, `asc9_rate` DOUBLE NULL, `asc13_rate` DOUBLE NULL,
                      `asc12_cases` INT NULL, `asc12_category` VARCHAR(255) NULL, `asc12_rate` DOUBLE NULL,
                      `asc17_cases` INT NULL, `asc17_category` VARCHAR(255) NULL, `asc17_rate` DOUBLE NULL,
                      `asc18_cases` INT NULL, `asc18_category` VARCHAR(255) NULL, `asc18_rate` DOUBLE NULL,
                      `asc19_cases` INT NULL, `asc19_category` VARCHAR(255) NULL, `asc19_rate` DOUBLE NULL
                    ) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
                    """, ct);
                await CsvTableLoader.LoadAsync(connection, paths[0], raw, FacilityColumns, ct);
                await CsvTableLoader.LoadAsync(connection, paths[1], raw, NationalColumns, ct,
                    constants: new Dictionary<string, string> { ["ccn"] = FacilityMeasures.National, ["npi"] = "", ["name"] = "National" });
                var counts = await TableSwap.ReplaceAsync(connection, ["cms_asc"], context.Options.MinRowRatio, () =>
                    // One row per surgery center and NPI (a center without a valid NPI under ''); rows without a CCN are left out.
                    Database.ExecuteAsync(connection,
                        $"""
                        INSERT INTO `cms_asc_staging` (`ccn`, `npi`, `name`, `city`, `state`, `zip`, `data_year`, {Measures})
                        SELECT UPPER(`ccn`), COALESCE(`npi`, ''), LEFT(MAX(`name`), 200), MAX(`city`), LEFT(MAX(`state`), 2), MAX(`zip`), MAX(`data_year`),
                               MAX(`asc9_rate`), MAX(`asc13_rate`), MAX(`asc12_cases`), LEFT(MAX(`asc12_category`), 60), MAX(`asc12_rate`),
                               MAX(`asc17_cases`), LEFT(MAX(`asc17_category`), 60), MAX(`asc17_rate`), MAX(`asc18_cases`), LEFT(MAX(`asc18_category`), 60),
                               MAX(`asc18_rate`), MAX(`asc19_cases`), LEFT(MAX(`asc19_category`), 60), MAX(`asc19_rate`)
                        FROM `{raw}` WHERE `ccn` REGEXP '^[0-9A-Za-z]+$' AND CHAR_LENGTH(`ccn`) IN (6, 10)
                        GROUP BY UPPER(`ccn`), COALESCE(`npi`, '')
                        """, ct), ct);
                return counts["cms_asc"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }, ct);
    }

    private static async Task<IReadOnlyList<DatasetRelease>> ReleasesAsync(DatasetContext context, CancellationToken ct) =>
        [await context.ProviderDataAsync(FacilityId, ct), await context.ProviderDataAsync(NationalId, ct)];
}

/// <summary>
/// Care Compare's inpatient rehabilitation facilities and long-term care hospitals (step 3.6 part 2): each type's "General Information"
/// (name, address, phone, ownership, certification date; beds for long-term care hospitals) into <c>cms_post_acute</c>, and its long
/// "Provider Data" and "National Data" files (one row per facility and measure code) reduced to the measures of
/// <see cref="FacilityMeasures.PostAcute"/> into <c>cms_post_acute_measure</c> (value, CMS's comparison, patients or stays, period; the
/// national values under CCN "NATION").
/// </summary>
public sealed class PostAcuteSource : DatasetSource
{
    public const string RehabGeneralId = "7t8x-u3ir";
    public const string RehabMeasuresId = "v9e4-nwhh";
    public const string RehabNationalId = "nasn-k89k";
    public const string LongTermGeneralId = "azum-44iv";
    public const string LongTermMeasuresId = "fp6g-2gsn";
    public const string LongTermNationalId = "5zdx-ny2x";

    private static readonly string[] Ids = [RehabGeneralId, RehabMeasuresId, RehabNationalId, LongTermGeneralId, LongTermMeasuresId, LongTermNationalId];

    public override string Name => "cc_post_acute";

    private static readonly CsvColumn[] RehabColumns =
    [
        new("CMS Certification Number (CCN)", "ccn"), new("Provider Name", "name"), new("Address Line 1", "address"), new("City/Town", "city"),
        new("State", "state"), new("ZIP Code", "zip"), new("Telephone Number", "phone"), new("Ownership Type", "ownership"),
        new("Certification Date", "certified_since", CsvValue.FlexibleDate),
    ];

    private static readonly CsvColumn[] LongTermColumns = [.. RehabColumns, new("Total Number of Beds", "beds", CsvValue.OptionalWholeNumber)];

    private static readonly CsvColumn[] MeasureColumns =
    [
        new("CMS Certification Number (CCN)", "ccn"), new("Measure Code", "code"), new("Score", "score"),
        new("Start Date", "start_date", CsvValue.FlexibleDate), new("End Date", "end_date", CsvValue.FlexibleDate),
    ];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
        DialysisFacilitySource.Combined(await ReleasesAsync(context, ct));

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var r = await ReleasesAsync(context, ct);
        string[] names = ["cc_irf_general.csv", "cc_irf_measures.csv", "cc_irf_national.csv", "cc_ltch_general.csv", "cc_ltch_measures.csv", "cc_ltch_national.csv"];
        return await WithDownloadsAsync(context, [.. r.Select((x, i) => (Release: x, FileName: names[i]))], async (connection, paths) =>
        {
            const string raw = "cms_post_acute_measure_raw_staging";
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` ({MeasureLoad.RawColumns}) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci", ct);
                foreach (var path in new[] { paths[1], paths[2], paths[4], paths[5] })
                {
                    await CsvTableLoader.LoadAsync(connection, path, raw, MeasureColumns, ct);
                }

                var counts = await TableSwap.ReplaceAsync(connection, ["cms_post_acute", "cms_post_acute_measure"], context.Options.MinRowRatio, async () =>
                {
                    await CsvTableLoader.LoadAsync(connection, paths[0], "cms_post_acute_staging", RehabColumns, ct,
                        constants: new Dictionary<string, string> { ["kind"] = FacilityTypes.Rehab });
                    await CsvTableLoader.LoadAsync(connection, paths[3], "cms_post_acute_staging", LongTermColumns, ct,
                        constants: new Dictionary<string, string> { ["kind"] = FacilityTypes.LongTermCare });
                    await MeasureLoad.PivotAsync(connection, raw, "cms_post_acute_measure_staging", FacilityMeasures.PostAcute, ct);
                }, ct);
                return counts["cms_post_acute"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }, ct);
    }

    private static async Task<IReadOnlyList<DatasetRelease>> ReleasesAsync(DatasetContext context, CancellationToken ct)
    {
        var releases = new List<DatasetRelease>();
        foreach (var id in Ids)
        {
            releases.Add(await context.ProviderDataAsync(id, ct));
        }

        return releases;
    }
}
