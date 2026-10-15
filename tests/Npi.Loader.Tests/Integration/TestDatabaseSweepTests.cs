using Dapper;
using MySqlConnector;

namespace Npi.Loader.Tests.Integration;

/// <summary>
/// The start-of-run sweep against MySQL: another schema's pool and template are kept while a run of that schema is going
/// (a run lock, or a test holding one of its pool databases) and dropped once none is. Skipped unless NPI_TEST_MYSQL is set.
/// Only npi_test_it_ databases are created or dropped.
/// </summary>
public sealed class TestDatabaseSweepTests
{
    [Fact]
    public async Task Another_schema_s_databases_are_kept_while_a_run_of_it_is_going()
    {
        var connectionString = Environment.GetEnvironmentVariable(TestDatabase.EnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString), $"{TestDatabase.EnvironmentVariable} is not set; skipping MySQL integration test.");
        var server = new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ConnectionString;

        // A schema no branch has (a fresh one per run, so runs side by side don't share it).
        var schema = "ff" + Guid.NewGuid().ToString("N")[..10];
        string[] names = [$"npi_test_it_pool_{schema}_00", $"npi_test_it_pool_{schema}_tpl"];
        await using var sweeper = new MySqlConnection(server);
        await sweeper.OpenAsync(TestContext.Current.CancellationToken);

        async Task<string[]> LeftAsync() => (await sweeper.QueryAsync<string>(
            "SELECT schema_name FROM information_schema.schemata WHERE schema_name IN @names ORDER BY schema_name", new { names })).ToArray();

        await using var other = new MySqlConnection(server);
        await other.OpenAsync(TestContext.Current.CancellationToken);
        try
        {
            // A run of that schema holds a run lock (taken before it reads its template): nothing of it is dropped.
            await using (var run = await TestDatabase.HoldRunLockAsync(server, schema))
            {
                // Created once the run holds its lock, as a run's are: a sweep (this run's own too) may start at any time.
                foreach (var name in names)
                {
                    await sweeper.ExecuteAsync($"CREATE DATABASE `{name}`");
                }

                Assert.Equal(1L, await run.ExecuteScalarAsync<long?>("SELECT IS_USED_LOCK(@name) = CONNECTION_ID()", new { name = TestDatabase.RunLockName(schema, 0) }));
                await TestDatabase.SweepStaleAsync(sweeper, DateTime.UtcNow, TestDatabase.SchemaHash);
                Assert.Equal(names.Order(StringComparer.Ordinal), await LeftAsync());

                // A run of the older code holds no run lock, only the pool database its test is using: kept too.
                Assert.Equal(1L, await other.ExecuteScalarAsync<long?>("SELECT GET_LOCK(@name, 0)", new { name = names[0] }));
            }

            await TestDatabase.SweepStaleAsync(sweeper, DateTime.UtcNow, TestDatabase.SchemaHash);
            Assert.Equal(names.Order(StringComparer.Ordinal), await LeftAsync());

            // No run of it any more: the pool and its template go.
            await other.ExecuteAsync("SELECT RELEASE_LOCK(@name)", new { name = names[0] });
            await TestDatabase.SweepStaleAsync(sweeper, DateTime.UtcNow, TestDatabase.SchemaHash);
            Assert.Empty(await LeftAsync());
        }
        finally
        {
            foreach (var name in names)
            {
                await sweeper.ExecuteAsync($"DROP DATABASE IF EXISTS `{name}`");
            }
        }
    }
}
