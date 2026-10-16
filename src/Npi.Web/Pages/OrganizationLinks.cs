using MySqlConnector;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// Links from lists to an organization's page (docs/PRODUCT_PLAN.md step 3.9, owner 2026-10-16): a facility, a Medicare group or an
/// organization NPI goes to the full page of its organization when it has one, else to its own detail page.
/// </summary>
public sealed class OrganizationLinks
{
    public static readonly OrganizationLinks None = new(new Dictionary<string, ProviderOrganization>(), new Dictionary<string, ProviderOrganization>(),
        new Dictionary<string, ProviderOrganization>());

    private readonly IReadOnlyDictionary<string, ProviderOrganization> _ccns;
    private readonly IReadOnlyDictionary<string, ProviderOrganization> _pacIds;
    private readonly IReadOnlyDictionary<string, ProviderOrganization> _npis;

    public OrganizationLinks(IReadOnlyDictionary<string, ProviderOrganization> ccns, IReadOnlyDictionary<string, ProviderOrganization> pacIds,
        IReadOnlyDictionary<string, ProviderOrganization> npis)
    {
        _ccns = ccns;
        _pacIds = pacIds;
        _npis = npis;
    }

    /// <summary>Looks up the pages of these facilities, groups and NPIs in three queries; no links (the detail pages) when the lookup fails.</summary>
    public static async Task<OrganizationLinks> ForAsync(OrganizationGroupService? groups, IEnumerable<string>? ccns, IEnumerable<string>? pacIds,
        IEnumerable<string>? npis, CancellationToken ct)
    {
        if (groups is null)
        {
            return None;
        }

        try
        {
            var c = ccns?.Where(OrganizationService.IsCcn).ToList() ?? [];
            var p = pacIds?.Where(OrganizationService.IsPacId).ToList() ?? [];
            var n = npis?.Where(Npi.Core.InputFormats.IsNpi).ToList() ?? [];
            return new OrganizationLinks(
                c.Count == 0 ? new Dictionary<string, ProviderOrganization>() : await groups.GetPagesForCcnsAsync(c, ct),
                p.Count == 0 ? new Dictionary<string, ProviderOrganization>() : await groups.GetPagesForPacIdsAsync(p, ct),
                n.Count == 0 ? new Dictionary<string, ProviderOrganization>() : await groups.GetMembershipsAsync(n, ct));
        }
        catch (MySqlException)
        {
            return None; // before the tables exist: the detail pages
        }
    }

    public string Facility(string ccn) =>
        _ccns.TryGetValue(ccn.ToUpperInvariant(), out var o) ? "/organization/" + o.MainNpi : "/facility/" + Uri.EscapeDataString(ccn);

    public string Group(string pacId) => _pacIds.TryGetValue(pacId, out var o) ? "/organization/" + o.MainNpi : "/group/" + pacId;

    /// <summary>An NPI's organization page when it has one, else its provider page.</summary>
    public string Provider(string npi) => _npis.TryGetValue(npi, out var o) ? "/organization/" + o.MainNpi : "/provider/" + npi;

    public bool HasPage(string npi) => _npis.ContainsKey(npi);
}
