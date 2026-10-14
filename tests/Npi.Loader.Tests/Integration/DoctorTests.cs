namespace Npi.Loader.Tests.Integration;

/// <summary>The doctor's database checks against a migrated test database. Skipped unless NPI_TEST_MYSQL is set.</summary>
public sealed class DoctorTests
{
    [Fact]
    public async Task A_migrated_database_passes_the_database_checks()
    {
        await using var db = await TestDatabase.CreateAsync();
        var checks = await Doctor.CheckDatabaseAsync(db.ConnectionString, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(checks, c => c.Status == Doctor.Status.Fail);
        Assert.Equal(["MySQL", "local_infile", "SESSION_VARIABLES_ADMIN", "Privileges", "Migrations", "Data"], checks.Select(c => c.Name));
        Assert.Equal(Doctor.Status.Info, checks[^1].Status); // nothing loaded
    }

    [Fact]
    public async Task A_missing_connection_string_says_how_to_set_it()
    {
        var check = Assert.Single(await Doctor.CheckDatabaseAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(Doctor.Status.Fail, check.Status);
        Assert.Contains("ConnectionStrings:LocalMySql", check.Fix, StringComparison.Ordinal);
    }
}
