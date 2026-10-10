using System.Text.Json;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// The newest file of one titled Open Payments dataset (CLAUDE.md §7 Stage 5.5 item 13): CMS's own summaries over every
/// published program year, which carry the recipient NPI, so the ~9 GB detail file of each year needn't be read. The
/// file name carries the publication date (…_P06302026_…), so a new publication is a new version.
/// </summary>
public abstract class OpenPaymentsSummarySource : DatasetSource
{
    /// <summary>The catalog title of the dataset, matched exactly.</summary>
    protected abstract string Title { get; }

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        await using var stream = await context.Http.GetStreamAsync(new Uri(context.Options.OpenPaymentsCatalogUrl), ct);
        return ParseCatalog(stream, Title);
    }

    /// <summary>The CSV of the catalog entry titled <paramref name="title"/>.</summary>
    public static DatasetRelease ParseCatalog(Stream json, string title)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var dataset in doc.RootElement.EnumerateArray())
        {
            if (!string.Equals((dataset.TryGetProperty("title", out var t) ? t.GetString() : null)?.Trim(), title, StringComparison.Ordinal)
                || !dataset.TryGetProperty("distribution", out var distributions) || distributions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var distribution in distributions.EnumerateArray())
            {
                var data = distribution.TryGetProperty("data", out var d) ? d : distribution;
                if (data.TryGetProperty("downloadURL", out var download) && download.GetString() is { } u && u.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    var url = new Uri(u);
                    return new DatasetRelease(Path.GetFileName(url.AbsolutePath), url);
                }
            }
        }

        throw new InvalidDataException($"The Open Payments catalog lists no CSV titled \"{title}\".");
    }

    /// <summary>Loads the file into a staging copy of <paramref name="rawTemplate"/>, runs <paramref name="summarize"/> on it, and drops it.</summary>
    protected static Task<long> LoadRawAsync(DatasetContext context, DatasetRelease release, string fileName, string rawTemplate,
        IReadOnlyList<CsvColumn> columns, Func<MySqlConnector.MySqlConnection, string, Task<long>> summarize, CancellationToken ct) =>
        WithDownloadAsync(context, release, fileName, async (connection, path) =>
        {
            var raw = rawTemplate + "_staging";
            try
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE `{raw}` LIKE `{rawTemplate}`", ct);
                var rows = await CsvTableLoader.LoadAsync(connection, path, raw, columns, ct);
                context.Log.Information("Loaded {Rows:N0} rows of {File}", rows, release.Version);
                return await summarize(connection, raw);
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }, ct);
}

/// <summary>
/// Open Payments per NPI and program year (2019 on): general payments, research payments, research funding as a
/// principal investigator, and ownership or investment interests. A recipient with several CMS profile IDs is summed;
/// CMS's "All" rows (every year together) are left out and computed when shown.
/// </summary>
public sealed class OpenPaymentsYearSource : OpenPaymentsSummarySource
{
    public override string Name => "open_payments_years";

    protected override string Title => "Payments grouped by physician (distinct) for all years";

    private static readonly CsvColumn[] Columns =
    [
        new("Covered_Recipient_NPI", "npi", CsvValue.Npi),
        new("Program_Year", "program_year"),
        new("General_Total_Payment", "general_amount", CsvValue.Number),
        new("General_Total_Transactions", "general_records", CsvValue.OptionalWholeNumber),
        new("Research_Total_Payment", "research_amount", CsvValue.Number),
        new("Research_Total_Transactions", "research_records", CsvValue.OptionalWholeNumber),
        new("Total_Associated_Research_Payments", "associated_research_amount", CsvValue.Number),
        new("Total_Associated_Research_Transactions", "associated_research_records", CsvValue.OptionalWholeNumber),
        new("Invested_Total_Amount", "invested_amount", CsvValue.Number),
        new("Interest_Total_Amount", "interest_value", CsvValue.Number),
        new("Invested_Total_Transactions", "ownership_records", CsvValue.OptionalWholeNumber),
    ];

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        LoadRawAsync(context, release, "open_payments_years.csv", "open_payments_year_raw", Columns, async (connection, raw) =>
        {
            var counts = await TableSwap.ReplaceAsync(connection, ["open_payments_year"], context.Options.MinRowRatio, () =>
                Database.ExecuteAsync(connection,
                    $"""
                    INSERT INTO `open_payments_year_staging` (`npi`, `program_year`, `general_amount`, `general_records`, `research_amount`,
                      `research_records`, `associated_research_amount`, `associated_research_records`, `invested_amount`, `interest_value`,
                      `ownership_records`)
                    SELECT `npi`, CAST(`program_year` AS UNSIGNED),
                      ROUND(SUM(COALESCE(`general_amount`, 0)), 2), SUM(COALESCE(`general_records`, 0)),
                      ROUND(SUM(COALESCE(`research_amount`, 0)), 2), SUM(COALESCE(`research_records`, 0)),
                      ROUND(SUM(COALESCE(`associated_research_amount`, 0)), 2), SUM(COALESCE(`associated_research_records`, 0)),
                      ROUND(SUM(COALESCE(`invested_amount`, 0)), 2), ROUND(SUM(COALESCE(`interest_value`, 0)), 2), SUM(COALESCE(`ownership_records`, 0))
                    FROM `{raw}`
                    WHERE `npi` IS NOT NULL AND `program_year` REGEXP '^[0-9]+$'
                    GROUP BY `npi`, CAST(`program_year` AS UNSIGNED)
                    """, ct), ct);
            return counts["open_payments_year"];
        }, ct);
}

/// <summary>
/// The <see cref="TopCompanies"/> companies that paid each NPI the most over all published program years, with the
/// amounts split by payment type (general, research, research as principal investigator, ownership/investment). Per
/// company (item 17): how many NPIs it paid, the <see cref="TopSpecialties"/> specialties (primary classification of
/// active providers) and the <see cref="TopRecipients"/> active providers it paid the most.
/// </summary>
public sealed class OpenPaymentsCompanySource : OpenPaymentsSummarySource
{
    public const int TopCompanies = 5;

    public const int TopSpecialties = 10;

    public const int TopRecipients = 100;

    public override string Name => "open_payments_companies";

    protected override string Title => "Payments grouped by covered recipient and reporting entities for all years";

    private static readonly CsvColumn[] Columns =
    [
        new("Covered_Recipient_NPI", "npi", CsvValue.Npi),
        new("Payment_Type", "payment_type"),
        new("AMGPO_Name", "company"),
        new("AMGPO_ID", "company_id"),
        new("Number_of_Transaction", "records", CsvValue.OptionalWholeNumber),
        new("Total_Amount", "amount", CsvValue.Number),
    ];

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        LoadRawAsync(context, release, "open_payments_companies.csv", "open_payments_company_raw", Columns, async (connection, raw) =>
        {
            // Per NPI and company over all years, by payment type; the four tables below are cut from it.
            const string pairs = "op_company_npi_scratch";
            await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{pairs}`", ct);
            try
            {
                await Database.ExecuteAsync(connection,
                    $"""
                    CREATE TABLE `{pairs}` (KEY (`company_id`), KEY (`npi`)) ENGINE=InnoDB
                    SELECT `npi`, COALESCE(`company_id`, '') AS `company_id`, MAX(`company`) AS `company`,
                      ROUND(SUM(COALESCE(`amount`, 0)), 2) AS `total`,
                      ROUND(SUM(IF(`payment_type` = 'General', COALESCE(`amount`, 0), 0)), 2) AS `general`,
                      ROUND(SUM(IF(`payment_type` = 'Research', COALESCE(`amount`, 0), 0)), 2) AS `research`,
                      ROUND(SUM(IF(`payment_type` = 'Associated Research', COALESCE(`amount`, 0), 0)), 2) AS `associated`,
                      ROUND(SUM(IF(`payment_type` LIKE 'Ownership%', COALESCE(`amount`, 0), 0)), 2) AS `ownership`,
                      SUM(COALESCE(`records`, 0)) AS `records`
                    FROM `{raw}` WHERE `npi` IS NOT NULL AND `company` IS NOT NULL
                    GROUP BY `npi`, COALESCE(`company_id`, '')
                    """, ct);
                var counts = await TableSwap.ReplaceAsync(connection,
                    ["open_payments_company", "op_company_reach", "op_company_specialty", "op_company_recipient"], context.Options.MinRowRatio, async () =>
                    {
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `open_payments_company_staging` (`npi`, `company_rank`, `company`, `company_id`, `total_amount`, `general_amount`,
                              `research_amount`, `associated_research_amount`, `ownership_amount`, `records`)
                            SELECT `npi`, `rnk`, `company`, NULLIF(`company_id`, ''), `total`, `general`, `research`, `associated`, `ownership`, `records`
                            FROM (SELECT x.*, ROW_NUMBER() OVER (PARTITION BY `npi` ORDER BY `total` DESC, `company`) AS `rnk` FROM `{pairs}` x) ranked
                            WHERE `rnk` <= {TopCompanies}
                            """, ct);
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `op_company_reach_staging` (`company_id`, `providers`, `amount`)
                            SELECT `company_id`, COUNT(*), ROUND(SUM(`total`), 2) FROM `{pairs}` WHERE `company_id` <> '' GROUP BY `company_id`
                            """, ct);
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `op_company_specialty_staging` (`company_id`, `specialty_rank`, `specialty`, `providers`, `amount`)
                            SELECT `company_id`, `rnk`, `specialty`, `providers`, `amount`
                            FROM (
                              SELECT x.*, ROW_NUMBER() OVER (PARTITION BY `company_id` ORDER BY `amount` DESC, `specialty`) AS `rnk`
                              FROM (
                                SELECT c.`company_id`, t.`Classification` AS `specialty`, COUNT(*) AS `providers`, ROUND(SUM(c.`total`), 2) AS `amount`
                                FROM `{pairs}` c
                                JOIN `provider` p ON p.`npi` = c.`npi`
                                JOIN `taxonomy_codes` t ON t.`Taxonomy_Code` = p.`primary_taxonomy_code`
                                WHERE c.`company_id` <> '' AND t.`Classification` IS NOT NULL
                                GROUP BY c.`company_id`, t.`Classification`
                              ) x
                            ) ranked
                            WHERE `rnk` <= {TopSpecialties}
                            """, ct);
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `op_company_recipient_staging` (`company_id`, `recipient_rank`, `npi`, `total_amount`, `general_amount`, `research_amount`,
                              `associated_research_amount`, `ownership_amount`, `records`)
                            SELECT `company_id`, `rnk`, `npi`, `total`, `general`, `research`, `associated`, `ownership`, `records`
                            FROM (
                              SELECT c.*, ROW_NUMBER() OVER (PARTITION BY c.`company_id` ORDER BY c.`total` DESC, c.`npi`) AS `rnk`
                              FROM `{pairs}` c JOIN `provider` p ON p.`npi` = c.`npi` WHERE c.`company_id` <> ''
                            ) ranked
                            WHERE `rnk` <= {TopRecipients}
                            """, ct);
                    }, ct);
                return counts["open_payments_company"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{pairs}`", CancellationToken.None);
            }
        }, ct);
}

/// <summary>
/// The companies themselves (item 17): CMS's reporting entity profiles (name, other names, state, country) and their
/// payments per program year (general, research, ownership invested and value), over every published year.
/// </summary>
public sealed class OpenPaymentsEntitySource : DatasetSource
{
    public const string ProfileTitle = "Reporting entity profile information";

    public const string YearsTitle = "Payments grouped by reporting entities";

    public override string Name => "open_payments_entities";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    private static readonly CsvColumn[] ProfileColumns =
    [
        new("AMGPO_Making_Payment_ID", "company_id"), new("AMGPO_Making_Payment_Name", "name"),
        new("AMGPO_Making_Payment_State", "state"), new("AMGPO_Making_Payment_Country", "country"),
        new("AMGPO_Making_Payment_Alternate_Name1", "alt_name1"), new("AMGPO_Making_Payment_Alternate_Name2", "alt_name2"),
        new("AMGPO_Making_Payment_Alternate_Name3", "alt_name3"), new("AMGPO_Making_Payment_Alternate_Name4", "alt_name4"),
        new("AMGPO_Making_Payment_Alternate_Name5", "alt_name5"),
    ];

    private static readonly CsvColumn[] YearColumns =
    [
        new("AMGPO_Making_Payment_ID", "company_id"), new("AMGPO_Making_Payment_Name", "name"),
        new("AMGPO_Making_Payment_State", "state"), new("AMGPO_Making_Payment_Country", "country"),
        new("Total_Amount_General", "general_amount", CsvValue.Number), new("Total_Amount_Research", "research_amount", CsvValue.Number),
        new("Total_Amount_Investment", "invested_amount", CsvValue.Number), new("Total_Amount_Interest", "interest_value", CsvValue.Number),
        new("Trans_General", "general_records", CsvValue.OptionalWholeNumber), new("Trans_Research", "research_records", CsvValue.OptionalWholeNumber),
        new("Trans_Invested", "ownership_records", CsvValue.OptionalWholeNumber), new("Program_Year", "program_year"),
    ];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var (profile, years) = await FindFilesAsync(context, ct);
        return new DatasetRelease($"{profile.Version} | {years.Version}", years.Url);
    }

    private static async Task<(DatasetRelease Profile, DatasetRelease Years)> FindFilesAsync(DatasetContext context, CancellationToken ct)
    {
        var catalog = new Uri(context.Options.OpenPaymentsCatalogUrl);
        DatasetRelease profile, years;
        await using (var stream = await context.Http.GetStreamAsync(catalog, ct))
        {
            profile = OpenPaymentsSummarySource.ParseCatalog(stream, ProfileTitle);
        }

        await using (var stream = await context.Http.GetStreamAsync(catalog, ct))
        {
            years = OpenPaymentsSummarySource.ParseCatalog(stream, YearsTitle);
        }

        return (profile, years);
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var (profileRelease, yearsRelease) = await FindFilesAsync(context, ct);
        var profileFile = await context.DownloadAsync(profileRelease, "op_company_profile.csv", ct);
        var yearsFile = await context.DownloadAsync(yearsRelease, "op_company_years.csv", ct);
        await using var connection = await context.Database.OpenAsync(ct);
        try
        {
            foreach (var (template, file, columns) in new[] { ("op_company_profile_raw", profileFile, ProfileColumns), ("op_company_year_raw", yearsFile, YearColumns) })
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{template}_staging`", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE `{template}_staging` LIKE `{template}`", ct);
                var rows = await CsvTableLoader.LoadAsync(connection, file, template + "_staging", columns, ct);
                context.Log.Information("Loaded {Rows:N0} rows of {File}", rows, Path.GetFileName(file));
            }

            var counts = await TableSwap.ReplaceAsync(connection, ["op_company", "op_company_year"], context.Options.MinRowRatio, async () =>
            {
                await Database.ExecuteAsync(connection,
                    """
                    INSERT INTO `op_company_year_staging` (`company_id`, `program_year`, `general_amount`, `research_amount`, `invested_amount`,
                      `interest_value`, `general_records`, `research_records`, `ownership_records`)
                    SELECT `company_id`, CAST(`program_year` AS UNSIGNED), ROUND(SUM(COALESCE(`general_amount`, 0)), 2),
                      ROUND(SUM(COALESCE(`research_amount`, 0)), 2), ROUND(SUM(COALESCE(`invested_amount`, 0)), 2),
                      ROUND(SUM(COALESCE(`interest_value`, 0)), 2), SUM(COALESCE(`general_records`, 0)), SUM(COALESCE(`research_records`, 0)),
                      SUM(COALESCE(`ownership_records`, 0))
                    FROM `op_company_year_raw_staging`
                    WHERE `company_id` IS NOT NULL AND `program_year` REGEXP '^[0-9]+$'
                    GROUP BY `company_id`, CAST(`program_year` AS UNSIGNED)
                    """, ct);
                // Every company in either file; the profile's name and headquarters first, else the newest year's.
                await Database.ExecuteAsync(connection,
                    """
                    INSERT INTO `op_company_staging` (`company_id`, `name`, `other_names`, `state`, `country`, `general_amount`, `research_amount`,
                      `invested_amount`, `interest_value`, `first_year`, `last_year`)
                    SELECT ids.`company_id`,
                      COALESCE(pr.`name`, ly.`name`, ids.`company_id`),
                      NULLIF(CONCAT_WS(' | ', pr.`alt_name1`, pr.`alt_name2`, pr.`alt_name3`, pr.`alt_name4`, pr.`alt_name5`), ''),
                      COALESCE(pr.`state`, ly.`state`), COALESCE(pr.`country`, ly.`country`),
                      COALESCE(t.`general`, 0), COALESCE(t.`research`, 0), COALESCE(t.`invested`, 0), COALESCE(t.`interest`, 0),
                      t.`first_year`, t.`last_year`
                    FROM (
                      SELECT `company_id` FROM `op_company_profile_raw_staging` WHERE `company_id` IS NOT NULL
                      UNION SELECT `company_id` FROM `op_company_year_staging`
                    ) ids
                    LEFT JOIN (
                      SELECT `company_id`, MAX(`name`) AS `name`, MAX(`state`) AS `state`, MAX(`country`) AS `country`, MAX(`alt_name1`) AS `alt_name1`,
                        MAX(`alt_name2`) AS `alt_name2`, MAX(`alt_name3`) AS `alt_name3`, MAX(`alt_name4`) AS `alt_name4`, MAX(`alt_name5`) AS `alt_name5`
                      FROM `op_company_profile_raw_staging` GROUP BY `company_id`
                    ) pr ON pr.`company_id` = ids.`company_id`
                    LEFT JOIN (
                      SELECT `company_id`, `name`, `state`, `country`
                      FROM (
                        SELECT `company_id`, `name`, `state`, `country`,
                          ROW_NUMBER() OVER (PARTITION BY `company_id` ORDER BY CAST(`program_year` AS UNSIGNED) DESC) AS `rn`
                        FROM `op_company_year_raw_staging` WHERE `company_id` IS NOT NULL AND `program_year` REGEXP '^[0-9]+$'
                      ) r WHERE `rn` = 1
                    ) ly ON ly.`company_id` = ids.`company_id`
                    LEFT JOIN (
                      SELECT `company_id`, SUM(`general_amount`) AS `general`, SUM(`research_amount`) AS `research`, SUM(`invested_amount`) AS `invested`,
                        SUM(`interest_value`) AS `interest`, MIN(`program_year`) AS `first_year`, MAX(`program_year`) AS `last_year`
                      FROM `op_company_year_staging` GROUP BY `company_id`
                    ) t ON t.`company_id` = ids.`company_id`
                    """, ct);
            }, ct);
            return counts["op_company"];
        }
        finally
        {
            await Database.ExecuteAsync(connection, "DROP TABLE IF EXISTS `op_company_profile_raw_staging`", CancellationToken.None);
            await Database.ExecuteAsync(connection, "DROP TABLE IF EXISTS `op_company_year_raw_staging`", CancellationToken.None);
            File.Delete(profileFile);
            File.Delete(yearsFile);
        }
    }
}
