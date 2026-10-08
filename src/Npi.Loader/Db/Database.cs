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
}
