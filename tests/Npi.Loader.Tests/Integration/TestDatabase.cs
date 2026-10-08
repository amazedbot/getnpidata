using Dapper;
using MySqlConnector;
using Npi.Loader.Db;
using Serilog.Core;

namespace Npi.Loader.Tests.Integration;

/// <summary>
/// A throwaway database (npi_test_it_…) with all migrations applied, on the server named by the
/// NPI_TEST_MYSQL connection string. Tests using it are skipped when that variable is not set (CLAUDE.md §9).
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    public const string EnvironmentVariable = "NPI_TEST_MYSQL";

    private readonly string _serverConnectionString;

    private TestDatabase(string serverConnectionString, string name)
    {
        _serverConnectionString = serverConnectionString;
        Name = name;
        ConnectionString = new MySqlConnectionStringBuilder(serverConnectionString) { Database = name }.ConnectionString;
        Database = new Database(ConnectionString);
    }

    public string Name { get; }

    /// <summary>Plain connection string to the scratch database (for Npi.Core services).</summary>
    public string ConnectionString { get; }

    public Database Database { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString), $"{EnvironmentVariable} is not set; skipping MySQL integration test.");

        var server = new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ConnectionString;
        var db = new TestDatabase(server, "npi_test_it_" + Guid.NewGuid().ToString("N")[..12]);
        await using (var connection = new MySqlConnection(server))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync($"CREATE DATABASE `{db.Name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
        }

        await new MigrationRunner(db.Database, Logger.None).ApplyAsync(CancellationToken.None);
        return db;
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param = null)
    {
        await using var connection = await Database.OpenAsync(CancellationToken.None);
        return (await connection.QueryAsync<T>(sql, param)).ToList();
    }

    public async Task ExecuteAsync(string sql, object? param = null)
    {
        await using var connection = await Database.OpenAsync(CancellationToken.None);
        await connection.ExecuteAsync(sql, param);
    }

    public async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var connection = await Database.OpenAsync(CancellationToken.None);
        return (await connection.ExecuteScalarAsync<T>(sql, param))!;
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new MySqlConnection(_serverConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"DROP DATABASE IF EXISTS `{Name}`");
    }
}
