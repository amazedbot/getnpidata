using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// Medicare fee-for-service enrollment (owner's OK 2026-10-11; docs/PRODUCT_PLAN.md step 1.5): which NPI is which Medicare
/// enrollment and PAC ID (the Public Provider Enrollment extract), and which clinicians bill through which group (the
/// revalidation group reassignment list). Together they link an organization's NPI to its clinicians. Both files come from
/// the data.cms.gov catalog; the version is both releases. Replaced in full, together.
/// </summary>
public sealed class MedicareEnrollmentSource : DatasetSource
{
    public const string EnrollmentTitle = "Medicare Fee-For-Service Public Provider Enrollment";
    public const string ReassignmentTitle = "Revalidation Clinic Group Practice Reassignment";

    public override string Name => "medicare_enrollment";

    private static readonly CsvColumn[] EnrollmentColumns =
    [
        new("NPI", "npi", CsvValue.Npi), new("ENRLMT_ID", "enrollment_id"), new("PECOS_ASCT_CNTL_ID", "pac_id"),
        new("PROVIDER_TYPE_DESC", "provider_type"), new("STATE_CD", "state"), new("ORG_NAME", "org_name"),
    ];

    private static readonly CsvColumn[] ReassignmentColumns =
    [
        new("Group PAC ID", "group_pac_id"), new("Group Enrollment ID", "group_enrollment_id"), new("Group Legal Business Name", "group_name"),
        new("Group State Code", "group_state"), new("Record Type", "record_type"), new("Individual NPI", "npi", CsvValue.Npi),
        new("Individual Specialty Description", "specialty"),
    ];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var (enrollment, reassignment) = await FindBothAsync(context, ct);
        return new DatasetRelease($"{enrollment.Version} | {reassignment.Version}", enrollment.Url);
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var (enrollment, reassignment) = await FindBothAsync(context, ct);
        var enrollmentPath = await context.DownloadAsync(enrollment, "cms_ppef_enrollment.csv", ct);
        string? reassignmentPath = null;
        const string raw = "medicare_enrollment_raw_staging";
        try
        {
            reassignmentPath = await context.DownloadAsync(reassignment, "cms_group_reassignment.csv", ct);
            await using var connection = await context.Database.OpenAsync(ct);
            try
            {
                // The extract can list an enrollment twice and has rows without a valid NPI: through a scratch table, keeping
                // complete, distinct rows.
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", ct);
                await Database.ExecuteAsync(connection,
                    $"""
                    CREATE TABLE `{raw}` (`npi` CHAR(10) NULL, `enrollment_id` VARCHAR(20) NULL, `pac_id` VARCHAR(10) NULL,
                      `provider_type` VARCHAR(120) NULL, `state` VARCHAR(2) NULL, `org_name` VARCHAR(200) NULL)
                    DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
                    """, ct);
                await CsvTableLoader.LoadAsync(connection, enrollmentPath, raw, EnrollmentColumns, ct, "latin1"); // CMS writes both files in Latin-1

                var counts = await TableSwap.ReplaceAsync(connection, ["medicare_enrollment", "medicare_reassignment"], context.Options.MinRowRatio,
                    async () =>
                    {
                        await Database.ExecuteAsync(connection,
                            $"""
                            INSERT INTO `medicare_enrollment_staging` (`npi`, `enrollment_id`, `pac_id`, `provider_type`, `state`, `org_name`)
                            SELECT `npi`, `enrollment_id`, MAX(`pac_id`), MAX(`provider_type`), MAX(`state`), MAX(`org_name`)
                            FROM `{raw}` WHERE `npi` IS NOT NULL AND `enrollment_id` IS NOT NULL
                            GROUP BY `npi`, `enrollment_id`
                            """, ct);
                        await CsvTableLoader.LoadAsync(connection, reassignmentPath, "medicare_reassignment_staging", ReassignmentColumns, ct, "latin1");
                    }, ct);
                return counts["medicare_enrollment"] + counts["medicare_reassignment"];
            }
            finally
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{raw}`", CancellationToken.None);
            }
        }
        finally
        {
            File.Delete(enrollmentPath);
            if (reassignmentPath is not null)
            {
                File.Delete(reassignmentPath);
            }
        }
    }

    private static async Task<(DatasetRelease Enrollment, DatasetRelease Reassignment)> FindBothAsync(DatasetContext context, CancellationToken ct)
    {
        var catalog = await context.CmsCatalogAsync(ct);
        return (catalog.FindLatest(EnrollmentTitle) ?? throw new InvalidDataException($"data.cms.gov lists no \"{EnrollmentTitle}\" CSV."),
            catalog.FindLatest(ReassignmentTitle) ?? throw new InvalidDataException($"data.cms.gov lists no \"{ReassignmentTitle}\" CSV."));
    }
}
