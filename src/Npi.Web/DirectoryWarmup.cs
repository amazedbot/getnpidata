using Npi.Core.Search;

namespace Npi.Web;

/// <summary>
/// Builds the facility and group lists once after the site starts (docs/PRODUCT_PLAN.md step 3.6), so the first visitor of
/// /facilities or /organizations doesn't wait for them. A failure is logged; the first request then tries again.
/// </summary>
public sealed partial class DirectoryWarmup(FacilityDirectory facilities, OrganizationDirectory organizations, ILogger<DirectoryWarmup> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WarmAsync("facilities", facilities.WarmAsync, stoppingToken);
        await WarmAsync("groups and organizations", organizations.WarmAsync, stoppingToken);
    }

    private async Task WarmAsync(string what, Func<CancellationToken, Task> warm, CancellationToken ct)
    {
        try
        {
            await warm(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Failed(logger, what, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Building the {What} list failed; the first request will try again.")]
    private static partial void Failed(ILogger logger, string what, Exception ex);
}
