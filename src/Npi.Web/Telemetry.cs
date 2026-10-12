using System.Diagnostics;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Instrumentation.AspNetCore;

namespace Npi.Web;

/// <summary>
/// Azure Application Insights for the site and the API (docs/PRODUCT_PLAN.md decision 7, step 1.9), through the Azure Monitor
/// OpenTelemetry distro. What is sent is described on /privacy: requests (path, route, status, duration), dependencies
/// (database calls), errors and performance counters, never query strings or client addresses.
/// </summary>
public static class Telemetry
{
    /// <summary>The configured connection string (APPLICATIONINSIGHTS_CONNECTION_STRING or ApplicationInsights:ConnectionString), or null: telemetry off.</summary>
    public static string? ConnectionString(IConfiguration configuration) =>
        new[] { configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"], configuration["ApplicationInsights:ConnectionString"] }
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    /// <summary>Tags that could identify a visitor or what they searched for.</summary>
    public static readonly string[] RemovedTags = ["url.query", "client.address", "client.port", "user_agent.original"];

    public static void Add(IServiceCollection services, string connectionString)
    {
        services.AddOpenTelemetry().UseAzureMonitor(o => o.ConnectionString = connectionString);
        services.Configure<AspNetCoreTraceInstrumentationOptions>(o =>
        {
            o.Filter = http => !http.Request.Path.StartsWithSegments("/health");
            o.EnrichWithHttpRequest = (activity, _) => Scrub(activity);
            o.EnrichWithHttpResponse = (activity, _) => Scrub(activity);
        });
    }

    /// <summary>Removes <see cref="RemovedTags"/> from a request's activity.</summary>
    public static void Scrub(Activity activity)
    {
        foreach (var tag in RemovedTags)
        {
            activity.SetTag(tag, null);
        }
    }
}
