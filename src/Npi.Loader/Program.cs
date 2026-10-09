using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using Npi.Loader;
using Npi.Loader.Db;
using Serilog;

// Exit codes: 0 = everything completed, 1 = a step failed, 2 = bad command line.
var invocation = LoaderCommandLine.Parse(args, out var error);
if (invocation is null)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(LoaderCommandLine.Usage);
    return 2;
}

// User-secrets are loaded in every environment: the loader runs from Task Scheduler as the owner's
// Windows account, which is where they live (CLAUDE.md §5).
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddUserSecrets<LoaderOptions>(optional: true)
    .AddEnvironmentVariables("NPI_")
    .Build();
var options = configuration.Get<LoaderOptions>() ?? new LoaderOptions();

Directory.CreateDirectory(options.ResolvedLogFolder);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .WriteTo.File(Path.Combine(options.ResolvedLogFolder, "getnpidata-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 60, formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Log.Warning("Cancelling…");
    cts.Cancel();
};

try
{
    Log.Information("Npi.Loader {Command} {Arguments}", invocation.Command, string.Join(" ", invocation.Arguments));
    if (invocation.Command is LoaderCommand.Publish)
    {
        Log.Error("Command {Command} is not implemented yet (CLAUDE.md §7, Stage 6)", invocation.Command);
        return 1;
    }

    var database = new Database(configuration.GetConnectionString("LocalMySql") ?? "");
    using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        // Covers connecting and the response headers; the body of a 1 GB monthly can stream for longer.
        Timeout = TimeSpan.FromMinutes(5),
    };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("getnpidata-loader/1.0 (+https://github.com/amazedbot/getnpidata)");

    var app = new LoaderApp(options, database, http, Log.Logger);
    return invocation.Command switch
    {
        LoaderCommand.Migrate => await app.MigrateAsync(cts.Token),
        LoaderCommand.Discover => await app.DiscoverAsync(cts.Token),
        LoaderCommand.Run => await app.RunAsync(cts.Token),
        LoaderCommand.LoadFile => await app.LoadFileAsync(invocation.Arguments[0], cts.Token),
        LoaderCommand.Reference => await app.ReferenceAsync(cts.Token),
        LoaderCommand.Datasets => await app.DatasetsAsync(invocation.Arguments.Count > 0 ? invocation.Arguments[0] : null, cts.Token),
        LoaderCommand.Project => await app.ProjectAsync(cts.Token),
        _ => throw new InvalidOperationException($"Unhandled command {invocation.Command}"),
    };
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Log.Warning("Cancelled");
    return 1;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Npi.Loader {Command} failed", invocation.Command);
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
