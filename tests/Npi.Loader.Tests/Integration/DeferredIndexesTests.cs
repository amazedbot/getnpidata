using Npi.Loader.Db;
using Serilog.Core;

namespace Npi.Loader.Tests.Integration;

/// <summary>Staging tables filled without their secondary indexes get exactly those indexes back (speed item 12). Skipped unless NPI_TEST_MYSQL is set.</summary>
public sealed class DeferredIndexesTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Indexes_dropped_for_a_load_come_back_as_they_were()
    {
        await using var db = await TestDatabase.CreateAsync();
        // provider: eight B-tree indexes; provider_org_name: a full-text index and one starting with npi; provider_map: a spatial one.
        string[] tables = ["provider", "provider_org_name", "provider_map"];
        foreach (var table in tables)
        {
            await db.ExecuteAsync($"CREATE TABLE `{table}_staging` LIKE `{table}`");
        }

        var before = await IndexLinesAsync(db, tables);
        await using var connection = await db.Database.OpenAsync(_ct);
        var dropped = await DeferredIndexes.DropAsync(connection, tables.Select(t => t + "_staging"), _ct);

        Assert.Equal([("provider_staging", 8)], dropped.Select(d => (d.Table, d.Indexes.Count)));
        var during = await IndexLinesAsync(db, tables);
        // Kept: the full-text and spatial indexes (cheaper to fill than to add) and those starting with npi (later fill steps join by NPI).
        Assert.Equal(before["provider_org_name"], during["provider_org_name"]);
        Assert.Equal(before["provider_map"], during["provider_map"]);
        Assert.Empty(during["provider"]);

        await db.ExecuteAsync("INSERT INTO provider_org_name_staging (npi, name) VALUES ('1000000004', 'NORTH SHORE MEDICAL')");
        await DeferredIndexes.RestoreAsync(connection, dropped, Logger.None, _ct);
        Assert.Equal(before, await IndexLinesAsync(db, tables));
        Assert.Equal(1L, await db.ScalarAsync<long>(
            "SELECT COUNT(*) FROM provider_org_name_staging WHERE MATCH(name) AGAINST('+north* +shore*' IN BOOLEAN MODE)"));
    }

    /// <summary>Each staging table's secondary index lines from SHOW CREATE TABLE, sorted (re-added indexes are listed last).</summary>
    private static async Task<Dictionary<string, string[]>> IndexLinesAsync(TestDatabase db, string[] tables)
    {
        var lines = new Dictionary<string, string[]>();
        foreach (var table in tables)
        {
            var create = (await db.QueryAsync<(string Table, string Create)>($"SHOW CREATE TABLE `{table}_staging`")).Single().Create;
            lines[table] = create.Split('\n').Select(l => l.Trim().TrimEnd(','))
                .Where(l => l.StartsWith("KEY ", StringComparison.Ordinal) || l.StartsWith("FULLTEXT ", StringComparison.Ordinal) || l.StartsWith("SPATIAL ", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToArray();
        }

        return lines;
    }
}
