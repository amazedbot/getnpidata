namespace Npi.Loader;

/// <summary>Loader settings (CLAUDE.md §5). Secrets come from user-secrets or environment variables.</summary>
public sealed class LoaderOptions
{
    /// <summary>Downloads and temporary files. Default: %ProgramData%\getnpidata\work.</summary>
    public string WorkFolder { get; set; } = "";

    /// <summary>Rolling log files. Default: %ProgramData%\getnpidata\logs.</summary>
    public string LogFolder { get; set; } = "";

    /// <summary>Keep the most recently loaded zip in the work folder (older ones are deleted).</summary>
    public bool KeepLastZip { get; set; } = true;

    public string NppesPageUrl { get; set; } = "https://download.cms.gov/nppes/NPI_Files.html";

    /// <summary>A full replace (monthly, deactivation report) must have at least this share of the current rows.</summary>
    public double MinRowRatio { get; set; } = 0.95;

    public int DownloadAttempts { get; set; } = 4;

    public string ResolvedWorkFolder => Resolve(WorkFolder, "work");

    public string ResolvedLogFolder => Resolve(LogFolder, "logs");

    private static string Resolve(string configured, string leaf) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "getnpidata", leaf)
            : Environment.ExpandEnvironmentVariables(configured);
}
