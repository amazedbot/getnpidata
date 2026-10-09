namespace Npi.Loader;

/// <summary>The loader's top-level commands (CLAUDE.md §7, Stage 1).</summary>
public enum LoaderCommand
{
    Run,
    Migrate,
    Discover,
    LoadFile,
    Reference,
    Datasets,
    Project,
    Geocode,
    Publish,
}

/// <summary>A parsed command line: the command plus its positional arguments.</summary>
public sealed record LoaderInvocation(LoaderCommand Command, IReadOnlyList<string> Arguments);

public static class LoaderCommandLine
{
    private static readonly Dictionary<string, LoaderCommand> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["run"] = LoaderCommand.Run,
        ["migrate"] = LoaderCommand.Migrate,
        ["discover"] = LoaderCommand.Discover,
        ["load-file"] = LoaderCommand.LoadFile,
        ["reference"] = LoaderCommand.Reference,
        ["datasets"] = LoaderCommand.Datasets,
        ["project"] = LoaderCommand.Project,
        ["geocode"] = LoaderCommand.Geocode,
        ["publish"] = LoaderCommand.Publish,
    };

    public const string Usage = """
        Usage: Npi.Loader [command]

        Commands:
          run              Download and load everything new (default)
          migrate          Apply database migrations
          discover         List the NPPES files that run would process (dry run)
          load-file <zip>  Load one NPPES zip manually
          reference        Refresh NUCC / HUD / Census reference data
          datasets [name]  Reload the Stage 5.5 datasets (OIG, CMS, …), or just one
          project          Rebuild the search projection tables
          geocode [county] Geocode every new practice address (Census) and rebuild the map tables;
                           with a 5-digit county FIPS, only that county's addresses
          publish          Sync the search projection to Azure
        """;

    /// <summary>Parses the arguments. No arguments means <see cref="LoaderCommand.Run"/>.</summary>
    /// <returns>The invocation, or null with an error message when the arguments are invalid.</returns>
    public static LoaderInvocation? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        if (args.Count == 0)
        {
            return new LoaderInvocation(LoaderCommand.Run, []);
        }

        if (!Names.TryGetValue(args[0], out var command))
        {
            error = $"Unknown command '{args[0]}'.";
            return null;
        }

        var rest = args.Skip(1).ToArray();
        if (command == LoaderCommand.Datasets && rest.Length <= 1)
        {
            return new LoaderInvocation(command, rest);
        }

        if (command == LoaderCommand.Geocode && rest.Length == 1)
        {
            if (rest[0].Length != 5 || !rest[0].All(char.IsAsciiDigit))
            {
                error = "Command 'geocode' takes an optional 5-digit county FIPS code, e.g. 36103.";
                return null;
            }

            return new LoaderInvocation(command, rest);
        }

        var expected = command == LoaderCommand.LoadFile ? 1 : 0;
        if (rest.Length != expected)
        {
            error = expected == 0
                ? $"Command '{args[0]}' takes no arguments."
                : $"Command '{args[0]}' needs exactly one argument: the path to a zip file.";
            return null;
        }

        return new LoaderInvocation(command, rest);
    }
}
