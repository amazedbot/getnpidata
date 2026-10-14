using System.Text;
using System.Text.RegularExpressions;

namespace Npi.Loader.Tests.Integration;

/// <summary>
/// db/schema.sql is every table as the migrations leave it, for reading (db/migrations/README.md). It is checked against a
/// freshly migrated test database; with NPI_UPDATE_SCHEMA=1 the test writes the file instead. Skipped unless NPI_TEST_MYSQL is set.
/// </summary>
public sealed partial class SchemaFileTests
{
    [Fact]
    public async Task Schema_file_matches_the_migrations()
    {
        await using var db = await TestDatabase.CreateAsync();
        var tables = await db.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE' ORDER BY table_name");
        var text = new StringBuilder("""
            -- db/schema.sql: every table as migrations 001 to the newest leave it, generated from a freshly migrated database.
            -- For reading only: the database is built and upgraded by `Npi.Loader migrate` from db/migrations. Regenerate it
            -- after adding a migration: set NPI_UPDATE_SCHEMA=1 and run the MySQL tests.

            """);
        foreach (var table in tables)
        {
            var create = (await db.QueryAsync<(string Table, string Create)>($"SHOW CREATE TABLE `{table}`")).Single().Create;
            text.Append('\n').Append(AutoIncrement().Replace(create, "").ReplaceLineEndings("\n")).Append(";\n");
        }

        var path = Path.Combine(RepositoryRoot(), "db", "schema.sql");
        var expected = text.ToString();
        if (Environment.GetEnvironmentVariable("NPI_UPDATE_SCHEMA") == "1")
        {
            await File.WriteAllTextAsync(path, expected, TestContext.Current.CancellationToken);
            return;
        }

        var actual = File.Exists(path) ? (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n") : "";
        Assert.True(actual == expected, "db/schema.sql is out of date with the migrations: run the MySQL tests with NPI_UPDATE_SCHEMA=1 and commit it.");
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "getnpidata.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("getnpidata.sln not found above " + AppContext.BaseDirectory);
    }

    // The counter after the last insert: data, not schema.
    [GeneratedRegex(@" AUTO_INCREMENT=\d+")]
    private static partial Regex AutoIncrement();
}
