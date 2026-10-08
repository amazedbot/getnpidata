namespace Npi.Web.Api;

/// <summary>The "Api" configuration section (CLAUDE.md §7 Stage 5).</summary>
public sealed class ApiOptions
{
    public const string Section = "Api";

    /// <summary>When true, every /api/v1 request needs an <c>X-Api-Key</c> header matching one of <see cref="Keys"/>.</summary>
    public bool RequireKey { get; set; }

    /// <summary>Accepted API keys. Secrets: set them in user-secrets / App Service configuration (Api__Keys__0, …), never in appsettings.json.</summary>
    public string[] Keys { get; set; } = [];

    /// <summary>Requests allowed per client IP per window, for all of /api/v1 together.</summary>
    public int PermitLimit { get; set; } = 60;

    /// <summary>Length of the fixed rate-limit window in seconds.</summary>
    public int WindowSeconds { get; set; } = 60;
}
