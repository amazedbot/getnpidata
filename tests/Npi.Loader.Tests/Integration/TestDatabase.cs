using System.Globalization;
using Dapper;
using MySqlConnector;
using Npi.Loader.Db;
using Serilog.Core;

namespace Npi.Loader.Tests.Integration;

/// <summary>
/// A throwaway database (npi_test_it_&lt;UTC yyyyMMddHHmmss&gt;_&lt;random&gt;) with all migrations applied, on the server
/// named by the NPI_TEST_MYSQL connection string. Tests using it are skipped when that variable is not set (CLAUDE.md §9).
/// <para>
/// Cleanup: a test drops its database when disposed (also when it fails); <see cref="CreateAsync"/> drops it if the
/// setup itself fails; and the first database a test run creates sweeps away any older than <see cref="StaleAfter"/>,
/// left behind by a run that was killed.
/// </para>
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    public const string EnvironmentVariable = "NPI_TEST_MYSQL";

    private const string Prefix = "npi_test_it_";
    private const string StampFormat = "yyyyMMddHHmmss";

    /// <summary>A whole test run takes minutes; a test database older than this belongs to a run that died.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    private static readonly SemaphoreSlim SweepLock = new(1, 1);
    private static bool _swept;

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
        await SweepStaleAsync(server, DateTime.UtcNow);

        var db = new TestDatabase(server, NewName(DateTime.UtcNow));
        await using (var connection = new MySqlConnection(server))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync($"CREATE DATABASE `{db.Name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
        }

        try
        {
            await new MigrationRunner(db.Database, Logger.None).ApplyAsync(CancellationToken.None);
        }
        catch
        {
            // The caller never gets the database (so never disposes it): drop it here.
            await db.DisposeAsync();
            throw;
        }

        return db;
    }

    internal static string NewName(DateTime utcNow) =>
        Prefix + utcNow.ToString(StampFormat, CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// True for a test database created before <paramref name="cutoff"/>, or without a timestamp in its name (the
    /// older naming, before this cleanup existed). Never true for anything outside the npi_test_it_ prefix.
    /// </summary>
    internal static bool IsStale(string name, DateTime cutoff)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = name[Prefix.Length..];
        return rest.Length <= StampFormat.Length || rest[StampFormat.Length] != '_'
            || !DateTime.TryParseExact(rest[..StampFormat.Length], StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created)
            || created < cutoff;
    }

    // Once per test run: drop test databases that a killed run left behind.
    private static async Task SweepStaleAsync(string server, DateTime utcNow)
    {
        await SweepLock.WaitAsync();
        try
        {
            if (_swept)
            {
                return;
            }

            await using var connection = new MySqlConnection(server);
            await connection.OpenAsync();
            var names = await connection.QueryAsync<string>(
                @"SELECT schema_name FROM information_schema.schemata WHERE schema_name LIKE 'npi\_test\_it\_%'");
            foreach (var name in names.Where(n => IsStale(n, utcNow - StaleAfter)))
            {
                await connection.ExecuteAsync($"DROP DATABASE IF EXISTS `{name}`");
            }

            _swept = true;
        }
        finally
        {
            SweepLock.Release();
        }
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
