using Serilog;

namespace Npi.Loader;

/// <summary>
/// The loader's log format. Steps that run side by side (the datasets, the states of the state licenses) log through
/// <see cref="ForStep"/>, so each of their lines starts with the step's name, e.g. "[open_payments] Downloaded …".
/// </summary>
public static class LoaderLog
{
    /// <summary>The property that names the step; lines without it have no prefix.</summary>
    public const string StepProperty = "Step";

    /// <summary>Serilog's default console format, with the step's name before the message.</summary>
    public const string OutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {" + StepProperty + ":l}{Message:lj}{NewLine}{Exception}";

    /// <summary>Serilog's default file format, with the step's name before the message.</summary>
    public const string FileOutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {" + StepProperty + ":l}{Message:lj}{NewLine}{Exception}";

    /// <summary>A logger whose lines start with "[<paramref name="step"/>] ".</summary>
    public static ILogger ForStep(ILogger log, string step) => log.ForContext(StepProperty, $"[{step}] ");
}
