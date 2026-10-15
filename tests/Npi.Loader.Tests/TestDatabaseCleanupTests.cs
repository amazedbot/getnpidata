using Npi.Loader.Tests.Integration;

namespace Npi.Loader.Tests;

/// <summary>Which test databases the start-of-run sweep may drop (no MySQL needed).</summary>
public class TestDatabaseCleanupTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 22, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void New_names_carry_their_creation_time_and_are_not_stale_yet()
    {
        var name = TestDatabase.NewName(Now);

        Assert.StartsWith("npi_test_it_20261009220000_", name, StringComparison.Ordinal);
        Assert.True(name.Length <= 64);
        Assert.False(TestDatabase.IsStale(name, Now - TestDatabase.StaleAfter, "abcdef012345"));
    }

    [Theory]
    [InlineData("npi_test_it_20261009194500_ab12cd34", true)]   // created before the cutoff (20:00)
    [InlineData("npi_test_it_20261009201500_ab12cd34", false)]  // a run in progress
    [InlineData("npi_test_it_ffbbbf68bca5", true)]              // the old naming, without a timestamp
    [InlineData("npi_test", false)]                              // never anything outside the prefix
    [InlineData("workplace", false)]
    [InlineData("npi_test_it_pool_abcdef012345_03", false)]       // the pool of the current schema, whatever its age
    [InlineData("npi_test_it_pool_abcdef012345_tpl", false)]
    [InlineData("npi_test_it_pool_0123456789ab_03", true)]        // a pool of another schema (an older migration set)
    [InlineData("npi_test_it_pool_0123456789ab_tpl", true)]
    public void Only_old_test_databases_are_stale(string name, bool stale) =>
        Assert.Equal(stale, TestDatabase.IsStale(name, Now - TestDatabase.StaleAfter, "abcdef012345"));

    [Theory]
    [InlineData("npi_test_it_pool_0123456789ab_03", "0123456789ab")]
    [InlineData("npi_test_it_pool_0123456789ab_tpl", "0123456789ab")]
    [InlineData("npi_test_it_20261009194500_ab12cd34", null)]
    [InlineData("npi_test_it_run_0123456789ab_00", null)]
    [InlineData("workplace", null)]
    public void Pool_databases_name_their_schema(string name, string? schema) => Assert.Equal(schema, TestDatabase.PoolSchema(name));

    [Fact]
    public void Run_locks_are_named_per_schema_and_slot()
    {
        Assert.Equal("npi_test_it_run_0123456789ab_07", TestDatabase.RunLockName("0123456789ab", 7));
        Assert.True(TestDatabase.RunLockName("0123456789ab", TestDatabase.RunSlots - 1).Length <= 64); // MySQL's limit for a lock name
    }

    [Fact]
    public void The_schema_hash_covers_every_migration() =>
        Assert.Matches("^[0-9a-f]{12}$", TestDatabase.SchemaHash);
}
