namespace Npi.Core;

/// <summary>
/// Where the loader and the site keep their files unless configured: %ProgramData%\getnpidata on Windows; elsewhere
/// $XDG_DATA_HOME/getnpidata, else ~/.local/share/getnpidata (the shared data folder there, /usr/share, isn't writable).
/// </summary>
public static class DataFolders
{
    public static string DefaultRoot => DefaultRootFor(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string DefaultRootFor(bool windows, string? xdgDataHome, string programData, string home) =>
        windows ? Path.Combine(programData, "getnpidata")
        : !string.IsNullOrWhiteSpace(xdgDataHome) ? Path.Combine(xdgDataHome, "getnpidata")
        : Path.Combine(home, ".local", "share", "getnpidata");
}
