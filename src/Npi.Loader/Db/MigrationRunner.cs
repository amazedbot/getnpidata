using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Serilog;

namespace Npi.Loader.Db;

public sealed record Migration(int Version, string Name, string Sql)
{
    /// <summary>SHA-256 of the script with line endings normalised (git may check files out with CRLF).</summary>
    public string Checksum { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql.ReplaceLineEndings("\n"))));
}

/// <summary>
/// Applies the numbered scripts in db/migrations (embedded in this assembly) and records them in
/// <c>schema_migrations</c>. Each script runs once; an applied script whose text changed is an error.
/// </summary>
public sealed partial class MigrationRunner(Database database, ILogger log)
{
    private const string CreateTrackingTable = """
        CREATE TABLE IF NOT EXISTS `schema_migrations` (
          `version` INT NOT NULL,
          `name` VARCHAR(255) NOT NULL,
          `sha256` CHAR(64) NOT NULL,
          `applied_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
          PRIMARY KEY (`version`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
        """;

    [GeneratedRegex(@"^migrations/(?<v>\d{3})_(?<name>[A-Za-z0-9_]+)\.sql$")]
    private static partial Regex ResourceName();

    public static IReadOnlyList<Migration> LoadEmbedded()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var migrations = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var m = ResourceName().Match(resource);
            if (!m.Success)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            migrations.Add(new Migration(int.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture), m.Groups["name"].Value, reader.ReadToEnd()));
        }

        var duplicate = migrations.GroupBy(x => x.Version).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null
            ? migrations.OrderBy(x => x.Version).ToList()
            : throw new InvalidOperationException($"Two migrations share version {duplicate.Key:D3}.");
    }

    /// <summary>Migrations not yet applied. Throws if an applied migration's script changed.</summary>
    public async Task<IReadOnlyList<Migration>> GetPendingAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await GetPendingAsync(connection, ct);
    }

    public async Task<int> ApplyAsync(CancellationToken ct)
    {
        var pending = await GetPendingAsync(ct);
        if (pending.Count == 0)
        {
            log.Information("Database {Database} is up to date", database.DatabaseName);
            return 0;
        }

        foreach (var migration in pending)
        {
            // A fresh connection per migration, so session settings (001 turns innodb_strict_mode off) don't leak into the next one.
            await using var connection = await database.OpenAsync(ct);
            var started = DateTime.UtcNow;
            log.Information("Applying migration {Version:D3}_{Name}", migration.Version, migration.Name);
            await Database.ExecuteAsync(connection, migration.Sql, ct);
            await Database.ExecuteAsync(connection,
                "INSERT INTO `schema_migrations` (`version`, `name`, `sha256`) VALUES (@Version, @Name, @Checksum)", ct,
                param: migration);
            log.Information("Applied migration {Version:D3}_{Name} in {Seconds:N1}s", migration.Version, migration.Name, (DateTime.UtcNow - started).TotalSeconds);
        }

        return pending.Count;
    }

    private static async Task<IReadOnlyList<Migration>> GetPendingAsync(MySqlConnection connection, CancellationToken ct)
    {
        await Database.ExecuteAsync(connection, CreateTrackingTable, ct);
        var applied = (await connection.QueryAsync<(int Version, string Sha256)>(
                new CommandDefinition("SELECT `version`, `sha256` FROM `schema_migrations`", cancellationToken: ct)))
            .ToDictionary(x => x.Version, x => x.Sha256);

        var pending = new List<Migration>();
        foreach (var migration in LoadEmbedded())
        {
            if (!applied.TryGetValue(migration.Version, out var checksum))
            {
                pending.Add(migration);
            }
            else if (!string.Equals(checksum, migration.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Migration {migration.Version:D3}_{migration.Name} was changed after it was applied. Add a new migration instead of editing it.");
            }
        }

        return pending;
    }
}
