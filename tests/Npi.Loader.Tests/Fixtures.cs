namespace Npi.Loader.Tests;

/// <summary>Paths to files in tests/fixtures (copied next to the test assembly).</summary>
internal static class Fixtures
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public static string Text(string name) => File.ReadAllText(Path(name));
}
