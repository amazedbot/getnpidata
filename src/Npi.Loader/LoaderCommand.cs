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
    Credentials,
    AddressTypes,
    Quality,
    Areas,
    AreaHistory,
    Geocode,
    Overture,
    Publish,
    Doctor,
    Sample,
    Export,
    Directories,
    History,
    OrgGroups,
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
        ["credentials"] = LoaderCommand.Credentials,
        ["address-types"] = LoaderCommand.AddressTypes,
        ["quality"] = LoaderCommand.Quality,
        ["areas"] = LoaderCommand.Areas,
        ["area-history"] = LoaderCommand.AreaHistory,
        ["geocode"] = LoaderCommand.Geocode,
        ["overture"] = LoaderCommand.Overture,
        ["publish"] = LoaderCommand.Publish,
        ["doctor"] = LoaderCommand.Doctor,
        ["sample"] = LoaderCommand.Sample,
        ["export"] = LoaderCommand.Export,
        ["directories"] = LoaderCommand.Directories,
        ["history"] = LoaderCommand.History,
        ["org-groups"] = LoaderCommand.OrgGroups,
    };

    public const string Usage = """
        Usage: Npi.Loader [command]

        Commands:
          run              Download and load everything new (default)
          doctor           Check this computer's setup (MySQL, logins, folders, network, settings) and say what to fix
          sample           Quick try: load only the newest weekly file (~50,000 providers) and build the site's tables
          migrate          Apply database migrations
          discover         List the NPPES files that run would process (dry run)
          load-file <zip>  Load one NPPES zip manually
          reference        Refresh NUCC / HUD / Census reference data
          datasets [name]  Reload the Stage 5.5 datasets (OIG, CMS, …), or just one
          project [changed] Rebuild the search projection tables (and the tables built on them); with
                           "changed", only the NPIs the loads queued, when they are few (as run does)
          credentials      Rebuild the standardized credential tables
          address-types    Relabel the practice addresses (commercial, residential) from the FEMA buildings
          quality          Relabel every record current, check or stale (record quality)
          export           Write the ready-made file of every active provider (run does it after a load
                           changed its data); the site serves it for an export with no filter
          areas            Recount the providers per area (US, states, counties) and specialty for the area pages
          directories      Rebuild the facility, Medicare group and organization tables of the directory pages
          org-groups       Rebuild the organization groups (the NPIs of one hospital or organization, for its combined
                           profile and one search row), then relabel the records whose group changed
          org-groups measure [csv] Group the organizations in memory only and log what each kind of tie adds;
                           with a file name, write every grouped NPI to it as CSV
          area-history [full] Update the monthly area figures (moves, new and deactivated NPIs, changes);
                           with "full", compare every provider's locations, not only the queued ones
          history [yyyy-MM] Build the NPPES history from the archived monthly files (CMS from June 2015, NBER
                           before; ~137 GB, each zip deleted after use), up to that month or the newest, and the
                           area figures back to November 2007; a stopped run goes on where it stopped
          history discover List where each month's archived file is and the months without one (dry run)
          history measure <zip> [<zip> …]  Read local monthly zips in order and compare them in memory only
          geocode [county] Geocode every new practice address (Census) and rebuild the map tables;
                           with a 5-digit county FIPS, only that county's addresses
          overture [area]  Place practice addresses at their building with Overture Maps data (address
                           points, places), then rebuild the map tables: every state, or one state
                           (NY) or county (5-digit FIPS)
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

        if (command == LoaderCommand.Project && rest.Length == 1)
        {
            if (!string.Equals(rest[0], "changed", StringComparison.OrdinalIgnoreCase))
            {
                error = "Command 'project' takes only the word 'changed' (rebuild only the queued NPIs), or nothing for a full rebuild.";
                return null;
            }

            return new LoaderInvocation(command, ["changed"]);
        }

        if (command == LoaderCommand.AreaHistory && rest.Length == 1)
        {
            if (!string.Equals(rest[0], "full", StringComparison.OrdinalIgnoreCase))
            {
                error = "Command 'area-history' takes only the word 'full' (compare every provider's locations), or nothing.";
                return null;
            }

            return new LoaderInvocation(command, ["full"]);
        }

        if (command == LoaderCommand.History && rest.Length >= 1)
        {
            if (string.Equals(rest[0], "measure", StringComparison.OrdinalIgnoreCase) && rest.Length >= 2)
            {
                return new LoaderInvocation(command, ["measure", .. rest.Skip(1)]);
            }

            if (rest.Length == 1 && string.Equals(rest[0], "discover", StringComparison.OrdinalIgnoreCase))
            {
                return new LoaderInvocation(command, ["discover"]);
            }

            if (rest.Length == 1 && Npi.Core.Search.AreaHistoryMath.ParseMonth(rest[0]) is not null)
            {
                return new LoaderInvocation(command, rest);
            }

            error = "Command 'history' takes a month (yyyy-MM: process up to it), 'discover', or 'measure' and one or more zip files, or nothing.";
            return null;
        }

        if (command == LoaderCommand.OrgGroups && rest.Length >= 1)
        {
            if (string.Equals(rest[0], "measure", StringComparison.OrdinalIgnoreCase) && rest.Length <= 2)
            {
                return new LoaderInvocation(command, ["measure", .. rest.Skip(1)]);
            }

            error = "Command 'org-groups' takes nothing, or 'measure' and an optional CSV file name.";
            return null;
        }

        if (command == LoaderCommand.Overture && rest.Length == 1 && rest[0].Length == 2 && rest[0].All(char.IsAsciiLetter))
        {
            return new LoaderInvocation(command, [rest[0].ToUpperInvariant()]);
        }

        if (command is LoaderCommand.Geocode or LoaderCommand.Overture && rest.Length == 1)
        {
            if (rest[0].Length != 5 || !rest[0].All(char.IsAsciiDigit))
            {
                error = command == LoaderCommand.Overture
                    ? "Command 'overture' takes a state code (NY) or a 5-digit county FIPS code (36103), or nothing for every state."
                    : "Command 'geocode' takes an optional 5-digit county FIPS code, e.g. 36103.";
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
