using Npi.Loader.Db;
using Npi.Loader.Load;

namespace Npi.Loader.Tests;

public class MigrationTests
{
    [Fact]
    public void Migrations_are_embedded_and_numbered_without_gaps()
    {
        var migrations = MigrationRunner.LoadEmbedded();

        Assert.Equal(Enumerable.Range(1, migrations.Count), migrations.Select(m => m.Version));
        Assert.Equal("baseline", migrations[0].Name);
        Assert.All(migrations, m => Assert.False(string.IsNullOrWhiteSpace(m.Sql)));
    }

    [Fact]
    public void The_phonetic_columns_use_the_same_expression_as_the_search()
    {
        // Migration 056 stores the key the search computes for the typed name; if they drift, similar names find nothing.
        var sql = MigrationRunner.LoadEmbedded().Single(m => m.Version == 56).Sql;

        Assert.Contains($"`last_phonetic` VARCHAR(10) GENERATED ALWAYS AS ({Npi.Core.Search.NameSearch.PhoneticSql("`last_name`")}) VIRTUAL", sql, StringComparison.Ordinal);
        Assert.Contains($"`first_phonetic` VARCHAR(10) GENERATED ALWAYS AS ({Npi.Core.Search.NameSearch.PhoneticSql("`first_name`")}) VIRTUAL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Checksums_ignore_line_endings()
    {
        var lf = new Migration(1, "x", "SELECT 1;\nSELECT 2;\n");
        var crlf = new Migration(1, "x", "SELECT 1;\r\nSELECT 2;\r\n");

        Assert.Equal(lf.Checksum, crlf.Checksum);
        Assert.NotEqual(lf.Checksum, new Migration(1, "x", "SELECT 3;\n").Checksum);
    }

    [Theory]
    [InlineData("date", "STR_TO_DATE(NULLIF(@c0, ''), '%m/%d/%Y')")]
    [InlineData("text", "NULLIF(@c0, '')")]
    [InlineData("varchar", "NULLIF(@c0, '')")]
    public void Empty_strings_become_null_and_dates_are_parsed_in_sql(string type, string expected) =>
        Assert.Equal(expected, StagingLoader.ConvertExpression("@c0", type));
}
