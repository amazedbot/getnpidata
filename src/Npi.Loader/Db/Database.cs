using Dapper;
using MySqlConnector;
using Npi.Core.Sql;

namespace Npi.Loader.Db;

/// <summary>A table column from information_schema.</summary>
public sealed record TableColumn(string Name, string DataType);

/// <summary>Opens loader connections with the settings bulk loading needs.</summary>
public sealed class Database(string connectionString)
{
    private readonly string _connectionString = Normalize(connectionString);

    public string DatabaseName => new MySqlConnectionStringBuilder(_connectionString).Database;

    /// <summary>The connection string, for the Npi.Core services the loader uses (the full export).</summary>
    public string ConnectionString => _connectionString;

    public async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static string Normalize(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'LocalMySql' is not set. Set it with: dotnet user-secrets --project src/Npi.Loader set \"ConnectionStrings:LocalMySql\" \"server=localhost;database=…;user=…;password=…\"");
        }

        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            AllowLoadLocalInfile = true,  // MySqlBulkLoader with Local = true
            AllowUserVariables = true,    // LOAD DATA ... (@c0, @c1) SET col = NULLIF(@c0, '')
            DefaultCommandTimeout = 0,    // monthly loads and table rebuilds run for many minutes
            UseAffectedRows = true,       // UPDATE returns rows actually changed, not rows matched
            CharacterSet = "utf8mb4",
        };
        if (string.IsNullOrEmpty(builder.Database))
        {
            throw new InvalidOperationException("Connection string 'LocalMySql' must name a database.");
        }

        return builder.ConnectionString;
    }

    public static async Task<IReadOnlyList<TableColumn>> GetColumnsAsync(MySqlConnection connection, string table, CancellationToken ct)
    {
        var columns = await connection.QueryAsync<TableColumn>(new CommandDefinition(
            """
            SELECT column_name AS Name, data_type AS DataType
            FROM information_schema.columns
            WHERE table_schema = DATABASE() AND table_name = @table
            ORDER BY ordinal_position
            """,
            new { table }, cancellationToken: ct));
        var list = columns.ToList();
        return list.Count > 0 ? list : throw new InvalidOperationException($"Table {table} does not exist. Run: Npi.Loader migrate");
    }

    public static Task<long> CountAsync(MySqlConnection connection, string table, CancellationToken ct, MySqlTransaction? tx = null) =>
        connection.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT COUNT(*) FROM {SqlIdentifier.Quote(table)}", transaction: tx, cancellationToken: ct));

    public static Task ExecuteAsync(MySqlConnection connection, string sql, CancellationToken ct, MySqlTransaction? tx = null, object? param = null) =>
        connection.ExecuteAsync(new CommandDefinition(sql, param, tx, cancellationToken: ct));

    /// <summary>
    /// Runs <paramref name="action"/> again when MySQL rolled it back as a deadlock victim or a lock wait timed out, up to
    /// <paramref name="attempts"/> times: datasets load side by side (speed item 7) and queue NPIs into the same tables. The
    /// action must be safe to repeat (an INSERT IGNORE, or a whole transaction).
    /// </summary>
    public static async Task<T> RetryOnLockConflictAsync<T>(Func<Task<T>> action, CancellationToken ct, int attempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (MySqlException e) when (attempt < attempts && IsLockConflict(e))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
    }

    /// <summary>A deadlock (1213) or a lock wait timeout (1205).</summary>
    public static bool IsLockConflict(MySqlException e) => e.ErrorCode is MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout;
}
