using Npi.Loader;

// Exit codes: 0 = everything completed, 1 = a step failed, 2 = bad command line.
var invocation = LoaderCommandLine.Parse(args, out var error);
if (invocation is null)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(LoaderCommandLine.Usage);
    return 2;
}

// Commands are implemented in Stage 1 onward (CLAUDE.md §7).
Console.Error.WriteLine($"Command '{invocation.Command}' is not implemented yet.");
return 1;
