using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// What _OrganizationBanner shows (docs/PRODUCT_PLAN.md step 3.9): the organization page a detail page belongs to, the provider
/// page's own NPI (null on a facility or group page) and what the detail page itself is, in words.
/// </summary>
public sealed record OrganizationBanner(ProviderOrganization Organization, string? Npi, string ThisPage)
{
    public const string NpiPage = "the exact record of this one NPI";
    public const string FacilityPage = "the exact record of this one certified facility (CCN)";
    public const string GroupPage = "the exact record of this one Medicare group (PAC ID)";
}
