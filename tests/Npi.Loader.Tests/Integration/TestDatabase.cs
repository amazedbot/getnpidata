using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using MySqlConnector;
using Npi.Loader.Db;
using Serilog.Core;

namespace Npi.Loader.Tests.Integration;

/// <summary>
/// A test database with all migrations applied and no data, on the server named by the NPI_TEST_MYSQL connection
/// string. Tests using it are skipped when that variable is not set (CLAUDE.md §9).
/// <para>
/// Speed: creating a database (88 migrations, ~145 tables) and dropping it took ~5 s and ~4 s per test, mostly MySQL's
/// table DDL. So the databases are a pool kept between runs: <c>npi_test_it_pool_&lt;schema&gt;_&lt;nn&gt;</c>, where
/// &lt;schema&gt; is a hash of every migration (<see cref="SchemaHash"/>), so a new or changed migration means new databases.
/// A test claims a free one (a MySQL user lock, released when the test ends or its process dies) and empties it first:
/// the rows of every table that has any are deleted, AUTO_INCREMENT counters reset, and the rows the migrations put
/// there (schema_migrations) restored. A database whose tables or indexes no longer match the template (a test that
/// failed half-way through a table swap) is dropped and copied again from the template, <c>…_tpl</c>, which holds the
/// migrated schema and is built once per schema.
/// </para>
/// <para>
/// Cleanup: the first test of a run drops pool databases of another schema (unless another run is using them) and any
/// npi_test_it_&lt;UTC time&gt;_&lt;random&gt; database older than <see cref="StaleAfter"/> (the per-test naming used before).
/// </para>
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    public const string EnvironmentVariable = "NPI_TEST_MYSQL";

    private const string Prefix = "npi_test_it_";
    private const string PoolPrefix = Prefix + "pool_";
    private const string StampFormat = "yyyyMMddHHmmss";

    /// <summary>A whole test run takes minutes; a per-test database older than this belongs to a run that died.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    /// <summary>The first 12 hex digits of a SHA-256 over every migration's version and checksum.</summary>
    internal static string SchemaHash { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join(",", MigrationRunner.LoadEmbedded().Select(m => $"{m.Version}:{m.Checksum}")))))[..12];

    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    private static Template? _template;

    /// <summary>The migrated schema every pool database copies.</summary>
    /// <param name="Tables">Every table.</param>
    /// <param name="Filled">The tables the migrations put rows in, with their insertable columns (generated ones left out).</param>
    /// <param name="AutoIncrement">The tables with an AUTO_INCREMENT column.</param>
    /// <param name="Fingerprint">A hash of every table's columns and indexes.</param>
    private sealed record Template(string Name, IReadOnlyList<string> Tables, IReadOnlyList<(string Table, string Columns)> Filled,
        IReadOnlySet<string> AutoIncrement, string Fingerprint);

    private readonly MySqlConnection _claim;

    private TestDatabase(string serverConnectionString, string name, MySqlConnection claim)
    {
        Name = name;
        ConnectionString = new MySqlConnectionStringBuilder(serverConnectionString) { Database = name }.ConnectionString;
        Database = new Database(ConnectionString);
        _claim = claim;
    }

    public string Name { get; }

    /// <summary>Plain connection string to the test database (for Npi.Core services).</summary>
    public string ConnectionString { get; }

    public Database Database { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString), $"{EnvironmentVariable} is not set; skipping MySQL integration test.");

        var server = new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ConnectionString;
        var template = await TemplateAsync(server);
        for (var i = 0; ; i++)
        {
            var name = PoolName(i.ToString("D2", CultureInfo.InvariantCulture));
            var claim = new MySqlConnection(server);
            await claim.OpenAsync();
            if (await claim.ExecuteScalarAsync<long?>("SELECT GET_LOCK(@name, 0)", new { name }) != 1)
            {
                await claim.DisposeAsync(); // another test has it
                continue;
            }

            try
            {
                await PrepareAsync(server, template, name);
                return new TestDatabase(server, name, claim);
            }
            catch
            {
                await ReleaseAsync(claim, name);
                throw;
            }
        }
    }

    /// <summary>
    /// The migrated template itself, which no test writes to: for reading the schema exactly as the migrations leave it (a
    /// pool database a test swapped tables in lists the same indexes in another order).
    /// </summary>
    public static async Task<Database> TemplateAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString), $"{EnvironmentVariable} is not set; skipping MySQL integration test.");
        var server = new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ConnectionString;
        var template = await TemplateAsync(server);
        return new Database(new MySqlConnectionStringBuilder(server) { Database = template.Name }.ConnectionString);
    }

    private static string PoolName(string suffix) => $"{PoolPrefix}{SchemaHash}_{suffix}";

    internal static string NewName(DateTime utcNow) =>
        Prefix + utcNow.ToString(StampFormat, CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// True for a test database that may be dropped: a pool database of another schema than <paramref name="schemaHash"/>,
    /// or a per-test database created before <paramref name="cutoff"/> or without a timestamp in its name (the older
    /// namings). Never true for anything outside the npi_test_it_ prefix.
    /// </summary>
    internal static bool IsStale(string name, DateTime cutoff, string schemaHash)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (name.StartsWith(PoolPrefix, StringComparison.Ordinal))
        {
            return !name.AsSpan(PoolPrefix.Length).StartsWith(schemaHash + "_", StringComparison.Ordinal);
        }

        var rest = name[Prefix.Length..];
        return rest.Length <= StampFormat.Length || rest[StampFormat.Length] != '_'
            || !DateTime.TryParseExact(rest[..StampFormat.Length], StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created)
            || created < cutoff;
    }

    // Once per test run: the sweep, then the template (built when this schema has none yet; another run building it is
    // waited for through its lock).
    private static async Task<Template> TemplateAsync(string server)
    {
        await SetupLock.WaitAsync();
        try
        {
            if (_template is not null)
            {
                return _template;
            }

            await using var connection = new MySqlConnection(server);
            await connection.OpenAsync();
            await SweepStaleAsync(connection, DateTime.UtcNow);

            var name = PoolName("tpl");
            if (await connection.ExecuteScalarAsync<long?>("SELECT GET_LOCK(@name, 600)", new { name }) != 1)
            {
                throw new TimeoutException($"Another test run kept {name} locked for 10 minutes.");
            }

            try
            {
                var database = new Database(new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString);
                var exists = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @name", new { name }) > 0;
                // A template whose migrations didn't all finish (a killed run) is built again.
                if (!exists || (await new MigrationRunner(database, Logger.None).GetPendingAsync(CancellationToken.None)).Count > 0)
                {
                    await connection.ExecuteAsync($"DROP DATABASE IF EXISTS `{name}`");
                    await connection.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
                    await new MigrationRunner(database, Logger.None).ApplyAsync(CancellationToken.None);
                }

                var tables = (await connection.QueryAsync<string>(
                    "SELECT table_name FROM information_schema.tables WHERE table_schema = @name AND table_type = 'BASE TABLE' ORDER BY table_name",
                    new { name })).ToList();
                var filled = new List<(string, string)>();
                foreach (var table in await NonEmptyAsync(connection, name, tables))
                {
                    var columns = await connection.QueryAsync<string>(
                        """
                        SELECT column_name FROM information_schema.columns
                        WHERE table_schema = @name AND table_name = @table AND extra NOT LIKE '%GENERATED%' ORDER BY ordinal_position
                        """, new { name, table });
                    filled.Add((table, string.Join(", ", columns.Select(c => $"`{c}`"))));
                }

                var autoIncrement = (await connection.QueryAsync<string>(
                    "SELECT table_name FROM information_schema.columns WHERE table_schema = @name AND extra LIKE '%auto_increment%'", new { name })).ToHashSet();
                return _template = new Template(name, tables, filled, autoIncrement, await FingerprintAsync(connection, name));
            }
            finally
            {
                await connection.ExecuteAsync("SELECT RELEASE_LOCK(@name)", new { name });
            }
        }
        finally
        {
            SetupLock.Release();
        }
    }

    // A claimed pool database, ready for a test: emptied when its schema matches the template, else copied anew.
    private static async Task PrepareAsync(string server, Template template, string name)
    {
        await using var connection = new MySqlConnection(server);
        await connection.OpenAsync();
        // npidata's 330 TEXT columns fail InnoDB's worst-case row size check in strict mode (CLAUDE.md §6.1).
        await connection.ExecuteAsync("SET SESSION innodb_strict_mode = OFF");
        var exists = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @name", new { name }) > 0;
        if (exists && await FingerprintAsync(connection, name) == template.Fingerprint)
        {
            var used = await NonEmptyAsync(connection, name, template.Tables);
            if (used.Count > 0)
            {
                await connection.ExecuteAsync(string.Concat(used.Select(t => $"DELETE FROM `{name}`.`{t}`;")));
            }

            foreach (var table in used.Where(template.AutoIncrement.Contains))
            {
                await connection.ExecuteAsync($"ALTER TABLE `{name}`.`{table}` AUTO_INCREMENT = 1");
            }
        }
        else
        {
            await connection.ExecuteAsync($"DROP DATABASE IF EXISTS `{name}`");
            await connection.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
            foreach (var batch in template.Tables.Chunk(25))
            {
                await connection.ExecuteAsync(string.Concat(batch.Select(t => $"CREATE TABLE `{name}`.`{t}` LIKE `{template.Name}`.`{t}`;")));
            }
        }

        foreach (var (table, columns) in template.Filled)
        {
            await connection.ExecuteAsync(
                $"DELETE FROM `{name}`.`{table}`; INSERT INTO `{name}`.`{table}` ({columns}) SELECT {columns} FROM `{template.Name}`.`{table}`");
        }
    }

    // The tables of a database that have at least one row, in one query.
    private static async Task<List<string>> NonEmptyAsync(MySqlConnection connection, string database, IReadOnlyList<string> tables) =>
        tables.Count == 0
            ? []
            : (await connection.QueryAsync<string>(string.Join(" UNION ALL ",
                tables.Select(t => $"(SELECT '{t}' FROM `{database}`.`{t}` LIMIT 1)")))).ToList();

    // A hash of every table's columns and indexes.
    private static Task<string> FingerprintAsync(MySqlConnection connection, string database) =>
        connection.ExecuteScalarAsync<string>(
            """
            SET SESSION group_concat_max_len = 67108864;
            SELECT CONCAT(
              (SELECT IFNULL(MD5(GROUP_CONCAT(CONCAT_WS('|', table_name, ordinal_position, column_name, column_type, is_nullable,
                         IFNULL(column_default, '(none)'), extra, IFNULL(generation_expression, ''))
                       ORDER BY table_name, ordinal_position SEPARATOR '\n')), '')
               FROM information_schema.columns WHERE table_schema = @database),
              (SELECT IFNULL(MD5(GROUP_CONCAT(CONCAT_WS('|', table_name, index_name, seq_in_index, IFNULL(column_name, ''), non_unique, index_type,
                         IFNULL(sub_part, ''))
                       ORDER BY table_name, index_name, seq_in_index SEPARATOR '\n')), '')
               FROM information_schema.statistics WHERE table_schema = @database))
            """, new { database })!;

    // Once per test run: drop the test databases that may go (see IsStale); a pool database another run holds is kept.
    private static async Task SweepStaleAsync(MySqlConnection connection, DateTime utcNow)
    {
        var names = await connection.QueryAsync<string>(
            @"SELECT schema_name FROM information_schema.schemata WHERE schema_name LIKE 'npi\_test\_it\_%'");
        foreach (var name in names.Where(n => IsStale(n, utcNow - StaleAfter, SchemaHash)))
        {
            if (await connection.ExecuteScalarAsync<long?>("SELECT IS_USED_LOCK(@name)", new { name }) is null)
            {
                await connection.ExecuteAsync($"DROP DATABASE IF EXISTS `{name}`");
            }
        }
    }

    private static async Task ReleaseAsync(MySqlConnection claim, string name)
    {
        // A pooled connection keeps its user locks until it's reused, so the lock is released explicitly.
        await claim.ExecuteAsync("SELECT RELEASE_LOCK(@name)", new { name });
        await claim.DisposeAsync();
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

    /// <summary>Hands the database back to the pool; the next test to claim it empties it.</summary>
    public ValueTask DisposeAsync() => new(ReleaseAsync(_claim, Name));
}
