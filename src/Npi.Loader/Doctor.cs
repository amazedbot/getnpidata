using System.Globalization;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Npi.Loader.Db;

namespace Npi.Loader;

/// <summary>
/// The <c>doctor</c> command: checks everything the loader and the site need on this computer (the .NET runtime, the work
/// folders and disk space, MySQL's version, settings and logins, the migrations, the network, the optional settings) and
/// prints, for each problem, what to do. Changes nothing. Exit code 0 when nothing failed.
/// </summary>
public sealed class Doctor(LoaderOptions options, IConfiguration configuration, HttpClient http, TextWriter output)
{
    public enum Status { Ok, Info, Warn, Fail }

    /// <summary>One check: what was found and, for a warning or failure, how to fix it.</summary>
    public sealed record Check(Status Status, string Name, string Detail, string? Fix = null);

    /// <summary>A full load needs room for the database (~35 GB) and the largest downloads (up to ~9 GB at a time).</summary>
    public const long RecommendedFreeBytes = 100L * 1024 * 1024 * 1024;

    // Boards whose lists the owner downloads by hand; the loader skips a state without files.
    private static readonly string[] HandDownloadedStates = ["FL", "AL", "MI", "CA"];

    private const string LoaderSecrets = "dotnet user-secrets --project src/Npi.Loader set";

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var checks = new List<Check>
        {
            new(Status.Ok, ".NET", $"runtime {Environment.Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}"),
        };
        checks.AddRange(CheckFolders());
        checks.AddRange(await CheckDatabaseAsync(configuration.GetConnectionString("LocalMySql"), ct));
        checks.Add(await CheckSiteLoginAsync(ct));
        checks.AddRange(await CheckNetworkAsync(ct));
        checks.AddRange(CheckOptionalSettings());

        foreach (var check in checks)
        {
            await output.WriteLineAsync($"  {Label(check.Status),-5} {check.Name}: {check.Detail}");
            if (check.Fix is not null)
            {
                await output.WriteLineAsync($"        fix: {check.Fix}");
            }
        }

        var failed = checks.Count(c => c.Status == Status.Fail);
        var warned = checks.Count(c => c.Status == Status.Warn);
        await output.WriteLineAsync();
        await output.WriteLineAsync(failed > 0 ? $"{failed} problem(s) to fix before the loader can run; {warned} warning(s)."
            : warned > 0 ? $"Ready, with {warned} warning(s)." : "Ready.");
        return failed > 0 ? 1 : 0;
    }

    private static string Label(Status status) => status switch
    {
        Status.Ok => "OK",
        Status.Info => "INFO",
        Status.Warn => "WARN",
        _ => "FAIL",
    };

    private IEnumerable<Check> CheckFolders()
    {
        foreach (var (name, folder) in new[]
                 {
                     ("Work folder", options.ResolvedWorkFolder),
                     ("Log folder", options.ResolvedLogFolder),
                     ("State license files folder", options.ResolvedStateLicenseFilesFolder),
                 })
        {
            Check check;
            try
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, $".doctor-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                check = new Check(Status.Ok, name, folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                check = new Check(Status.Fail, name, $"{folder} is not writable ({ex.Message})",
                    $"choose another folder: {LoaderSecrets} \"{(name.StartsWith("Work", StringComparison.Ordinal) ? "WorkFolder" : name.StartsWith("Log", StringComparison.Ordinal) ? "LogFolder" : "StateLicenseFilesFolder")}\" \"<folder>\"");
            }

            yield return check;
        }

        var root = Path.GetPathRoot(Path.GetFullPath(options.ResolvedWorkFolder));
        if (root is not null && new DriveInfo(root) is { IsReady: true } drive)
        {
            var free = drive.AvailableFreeSpace;
            yield return free >= RecommendedFreeBytes
                ? new Check(Status.Ok, "Disk space", $"{Gb(free)} free on {root}")
                : new Check(Status.Warn, "Disk space", $"{Gb(free)} free on {root}; a full load needs about 100 GB (the database ~35 GB, if MySQL keeps it on this drive, and downloads of up to ~9 GB at a time)",
                    "free some space, or point WorkFolder at a larger drive");
        }
    }

    private static string Gb(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("N0", CultureInfo.InvariantCulture) + " GB";

    /// <summary>The loader's database: reachable, MySQL 8.4, local_infile, the privileges it needs, migrated, loaded.</summary>
    public static async Task<IReadOnlyList<Check>> CheckDatabaseAsync(string? connectionString, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return [new Check(Status.Fail, "Loader database", "the connection string LocalMySql is not set",
                $"{LoaderSecrets} \"ConnectionStrings:LocalMySql\" \"Server=localhost;Database=npi;User ID=npi_loader;Password=…\" (scripts/setup-secrets.ps1 asks for it)")];
        }

        var checks = new List<Check>();
        var database = new Database(connectionString);
        MySqlConnection connection;
        try
        {
            connection = await database.OpenAsync(ct);
        }
        catch (MySqlException ex)
        {
            return [new Check(Status.Fail, "Loader database", $"cannot connect to {Describe(connectionString)}: {ex.Message}",
                "start MySQL (docker compose up -d, or the MySQL84 service on Windows), and check the server, database, user and password in LocalMySql")];
        }

        await using (connection)
        {
            var version = await connection.ExecuteScalarAsync<string>(new CommandDefinition("SELECT VERSION()", cancellationToken: ct)) ?? "";
            var major = Version.TryParse(version.Split('-')[0], out var v) ? v : new Version(0, 0);
            checks.Add(major >= new Version(8, 4) ? new Check(Status.Ok, "MySQL", $"{version} at {Describe(connectionString)}")
                : major >= new Version(8, 0) ? new Check(Status.Warn, "MySQL", $"{version}: the project is built and tested on MySQL 8.4 LTS", "install MySQL 8.4 (8.0 reached end of life in April 2026)")
                : new Check(Status.Fail, "MySQL", $"{version}: MySQL 8.4 is needed", "install MySQL 8.4"));

            var localInfile = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT @@GLOBAL.local_infile", cancellationToken: ct));
            checks.Add(localInfile == 1 ? new Check(Status.Ok, "local_infile", "ON (bulk loading works)")
                : new Check(Status.Fail, "local_infile", "OFF, so the loader can't bulk-load files",
                    "add local_infile=ON under [mysqld] in my.ini (Windows: C:\\ProgramData\\MySQL\\MySQL Server 8.4\\my.ini) or my.cnf and restart MySQL; docker-compose.yml already sets it"));

            try
            {
                await connection.ExecuteAsync(new CommandDefinition("SET SESSION innodb_strict_mode = OFF", cancellationToken: ct));
                checks.Add(new Check(Status.Ok, "SESSION_VARIABLES_ADMIN", "granted (needed to create the 330-column npidata table)"));
            }
            catch (MySqlException)
            {
                checks.Add(new Check(Status.Fail, "SESSION_VARIABLES_ADMIN", "not granted to this login",
                    "as root: GRANT SESSION_VARIABLES_ADMIN ON *.* TO '<loader user>'@'%';"));
            }

            var grants = (await connection.QueryAsync<string>(new CommandDefinition("SHOW GRANTS FOR CURRENT_USER()", cancellationToken: ct))).ToList();
            var name = database.DatabaseName;
            var all = grants.Any(g => g.Contains("ALL PRIVILEGES ON *.*", StringComparison.OrdinalIgnoreCase)
                                      || g.Contains($"ALL PRIVILEGES ON `{name}`.*", StringComparison.OrdinalIgnoreCase)
                                      || g.Contains($"ALL PRIVILEGES ON `{name.Replace("_", "\\_", StringComparison.Ordinal)}`.*", StringComparison.OrdinalIgnoreCase));
            checks.Add(all ? new Check(Status.Ok, "Privileges", $"all privileges on {name}")
                : new Check(Status.Warn, "Privileges", $"no ALL PRIVILEGES grant on {name} found ({string.Join("; ", grants)})",
                    $"as root: GRANT ALL PRIVILEGES ON `{name}`.* TO '<loader user>'@'%';"));

            var pending = await MigrationRunner.GetPendingAsync(connection, ct);
            if (pending.Count > 0)
            {
                checks.Add(new Check(Status.Warn, "Migrations", $"{pending.Count} not applied (newest {pending[^1].Version:D3}_{pending[^1].Name})",
                    "dotnet run --project src/Npi.Loader -- migrate"));
                return checks;
            }

            checks.Add(new Check(Status.Ok, "Migrations", "all applied"));
            var providers = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT `provider_count` FROM `data_version` WHERE `id` = 1", cancellationToken: ct));
            checks.Add(providers is > 0
                ? new Check(Status.Ok, "Data", $"{providers:N0} active providers in the search tables")
                : new Check(Status.Info, "Data", "nothing loaded yet",
                    "dotnet run --project src/Npi.Loader -- run (everything; many hours) or -- sample (one weekly file, minutes)"));
        }

        return checks;
    }

    private static string Describe(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        return $"{builder.Server}:{builder.Port}/{builder.Database} as {builder.UserID}";
    }

    // The site's read-only login, from Npi.Web's own user-secrets or environment (ConnectionStrings__RemoteMySql).
    private static async Task<Check> CheckSiteLoginAsync(CancellationToken ct)
    {
        var web = new ConfigurationBuilder().AddUserSecrets("getnpidata-web").AddEnvironmentVariables().Build();
        var connectionString = web.GetConnectionString("RemoteMySql");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new Check(Status.Warn, "Site database", "the site's connection string RemoteMySql is not set, so the site won't start",
                "dotnet user-secrets --project src/Npi.Web set \"ConnectionStrings:RemoteMySql\" \"Server=localhost;Database=npi;User ID=npi_web;Password=…\" (scripts/setup-secrets.ps1 asks for it)");
        }

        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(ct);
            var tables = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'data_version'", cancellationToken: ct));
            return tables > 0 ? new Check(Status.Ok, "Site database", $"{Describe(connectionString)} can read the search tables")
                : new Check(Status.Warn, "Site database", $"{Describe(connectionString)} connects, but sees no search tables",
                    "point RemoteMySql at the loader's database, migrate it, and grant the site login SELECT on it");
        }
        catch (MySqlException ex)
        {
            return new Check(Status.Warn, "Site database", $"cannot connect to {Describe(connectionString)}: {ex.Message}",
                "check RemoteMySql (Npi.Web user-secrets); the site login needs SELECT on the loader's database");
        }
    }

    private async Task<IEnumerable<Check>> CheckNetworkAsync(CancellationToken ct)
    {
        var checks = new List<Check>();
        foreach (var (name, url, required) in new[]
                 {
                     ("CMS NPPES files", options.NppesPageUrl, true),
                     ("DuckDB extensions (Overture and FEMA steps)", "https://extensions.duckdb.org/", false),
                 })
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                checks.Add(new Check(Status.Ok, name, $"{url} answers ({(int)response.StatusCode})"));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                checks.Add(new Check(required ? Status.Fail : Status.Warn, name, $"{url} can't be reached ({ex.Message})", "check the internet connection, proxy or firewall"));
            }
        }

        return checks;
    }

    private IEnumerable<Check> CheckOptionalSettings()
    {
        yield return string.IsNullOrWhiteSpace(options.HudApiToken)
            ? new Check(Status.Warn, "HudApiToken", "not set: county search and the area pages stay empty",
                $"get a free token at https://www.huduser.gov/portal/dataset/uspszip-api.html, then {LoaderSecrets} \"HudApiToken\" \"<token>\"")
            : new Check(Status.Ok, "HudApiToken", "set");
        yield return string.IsNullOrWhiteSpace(options.SecUserAgent)
            ? new Check(Status.Info, "SecUserAgent", "not set: SEC listings on the company pages are skipped",
                $"{LoaderSecrets} \"SecUserAgent\" \"getnpidata <your e-mail>\" (SEC's fair-access rule)")
            : new Check(Status.Ok, "SecUserAgent", "set");
        yield return string.IsNullOrWhiteSpace(options.OpenFdaApiKey)
            ? new Check(Status.Info, "OpenFdaApiKey", "not set: product adverse-event counts fill 900 a day instead of 20,000 a run",
                $"get a free key at https://open.fda.gov/apis/authentication/, then {LoaderSecrets} \"OpenFdaApiKey\" \"<key>\"")
            : new Check(Status.Ok, "OpenFdaApiKey", "set");

        // Boards whose lists the owner downloads by hand (CLAUDE.md §7 Stage 5.5 item 15); the loader skips a state without files.
        var folder = options.ResolvedStateLicenseFilesFolder;
        var states = HandDownloadedStates
            .Where(s => Directory.Exists(Path.Combine(folder, s)) && Directory.EnumerateFiles(Path.Combine(folder, s)).Any()).ToList();
        yield return new Check(Status.Info, "Hand-downloaded state licenses",
            states.Count == 0 ? "none (FL, AL, MI and CA are skipped; the other 11 states load by themselves)" : $"files for {string.Join(", ", states)} in {folder}",
            states.Count == 4 ? null : $"optional: see README, \"State license files\", for where each board's files come from (into {Path.Combine(folder, "<state>")})");
        if (states.Contains("CA") && !OperatingSystem.IsWindows())
        {
            yield return new Check(Status.Info, "California licenses", "the CA file is a Microsoft Access database, read only on Windows: skipped here");
        }
    }
}
