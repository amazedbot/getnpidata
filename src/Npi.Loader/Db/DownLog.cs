using Dapper;
using Npi.Loader.Nppes;

namespace Npi.Loader.Db;

public static class DownLogStatus
{
    public const string Downloading = "Downloading";
    public const string Loading = "Loading";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

/// <summary>
/// The <c>downlog</c> bookkeeping table (CLAUDE.md §7 Stage 1.2). A row is written when work on a
/// file starts, and only <see cref="DownLogStatus.Completed"/> means done (legacy defect #3).
/// </summary>
public sealed class DownLog(Database database)
{
    private const int MaxErrorLength = 4000;

    public async Task<IReadOnlySet<string>> GetCompletedAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        var names = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT `filename` FROM `downlog` WHERE `status` = @status", new { status = DownLogStatus.Completed }, cancellationToken: ct));
        return names.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task StartAsync(NppesFile file, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await Database.ExecuteAsync(connection,
            """
            INSERT INTO `downlog` (`filename`, `kind`, `file_date`, `status`, `started_at`)
            VALUES (@FileName, @Kind, @FileDate, @Status, UTC_TIMESTAMP()) AS new
            ON DUPLICATE KEY UPDATE `kind` = new.`kind`, `file_date` = new.`file_date`, `status` = new.`status`,
              `started_at` = new.`started_at`, `completed_at` = NULL, `rows_loaded` = NULL, `error` = NULL
            """, ct,
            param: new { file.FileName, Kind = file.Kind.ToString(), FileDate = file.FileDate.ToDateTime(TimeOnly.MinValue), Status = DownLogStatus.Downloading });
    }

    public Task SetLoadingAsync(NppesFile file, CancellationToken ct) => UpdateAsync(
        "UPDATE `downlog` SET `status` = @Status WHERE `filename` = @FileName",
        new { file.FileName, Status = DownLogStatus.Loading }, ct);

    public Task CompleteAsync(NppesFile file, long rows, CancellationToken ct) => UpdateAsync(
        "UPDATE `downlog` SET `status` = @Status, `completed_at` = UTC_TIMESTAMP(), `rows_loaded` = @rows WHERE `filename` = @FileName",
        new { file.FileName, Status = DownLogStatus.Completed, rows }, ct);

    public Task FailAsync(NppesFile file, string error, CancellationToken ct) => UpdateAsync(
        "UPDATE `downlog` SET `status` = @Status, `completed_at` = UTC_TIMESTAMP(), `error` = @error WHERE `filename` = @FileName",
        new { file.FileName, Status = DownLogStatus.Failed, error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error }, ct);

    private async Task UpdateAsync(string sql, object param, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await Database.ExecuteAsync(connection, sql, ct, param: param);
    }
}
