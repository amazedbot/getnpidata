using Npi.Loader.Tests.Integration;

namespace Npi.Loader.Tests;

/// <summary>Which throwaway test databases the start-of-run sweep may drop (no MySQL needed).</summary>
public class TestDatabaseCleanupTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 22, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void New_names_carry_their_creation_time_and_are_not_stale_yet()
    {
        var name = TestDatabase.NewName(Now);

        Assert.StartsWith("npi_test_it_20261009220000_", name, StringComparison.Ordinal);
        Assert.True(name.Length <= 64);
        Assert.False(TestDatabase.IsStale(name, Now - TestDatabase.StaleAfter));
    }

    [Theory]
    [InlineData("npi_test_it_20261009194500_ab12cd34", true)]   // created before the cutoff (20:00)
    [InlineData("npi_test_it_20261009201500_ab12cd34", false)]  // a run in progress
    [InlineData("npi_test_it_ffbbbf68bca5", true)]              // the old naming, without a timestamp
    [InlineData("npi_test", false)]                              // never anything outside the prefix
    [InlineData("workplace", false)]
    public void Only_old_test_databases_are_stale(string name, bool stale) =>
        Assert.Equal(stale, TestDatabase.IsStale(name, Now - TestDatabase.StaleAfter));
}
