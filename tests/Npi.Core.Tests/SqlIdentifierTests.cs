using Npi.Core.Sql;

namespace Npi.Core.Tests;

// Legacy defect #10: file and table names were concatenated into SQL.
public class SqlIdentifierTests
{
    [Theory]
    [InlineData("npidata", "`npidata`")]
    [InlineData("Provider_Organization_Name_Legal_Business_Name", "`Provider_Organization_Name_Legal_Business_Name`")]
    [InlineData("x1", "`x1`")]
    public void Plain_identifiers_are_quoted(string name, string quoted) => Assert.Equal(quoted, SqlIdentifier.Quote(name));

    [Theory]
    [InlineData("")]
    [InlineData("npi`data")]
    [InlineData("npidata; DROP TABLE npidata")]
    [InlineData("name with space")]
    [InlineData("naïve")]
    [InlineData("a-b")]
    public void Anything_else_is_rejected(string name) => Assert.Throws<ArgumentException>(() => SqlIdentifier.Quote(name));

    [Fact]
    public void Identifiers_longer_than_64_characters_are_rejected()
    {
        Assert.True(SqlIdentifier.IsValid(new string('a', 64)));
        Assert.False(SqlIdentifier.IsValid(new string('a', 65)));
        Assert.False(SqlIdentifier.IsValid(null));
    }
}
