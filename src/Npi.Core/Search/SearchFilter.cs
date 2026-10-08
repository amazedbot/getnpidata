namespace Npi.Core.Search;

/// <summary>
/// A provider search (CLAUDE.md §7 Stage 3.2). Every filter is optional, but at least one is
/// required. The web page, the CSV export and /api/v1 all build one of these, so results match.
/// </summary>
public sealed record SearchFilter
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>Paging is meant for browsing; past this many rows, use the CSV export.</summary>
    public const int MaxResultWindow = 10_000;

    /// <summary>A location-only search with at least this many matches is paged in sort-index order (see SearchQuery.MayBeBroad).</summary>
    public const int BroadSearchThreshold = 200_000;

    /// <summary>A page that can't be found within this many seconds is reported as too broad, not left hanging.</summary>
    public const int SearchTimeoutSeconds = 30;

    /// <summary>NUCC Classification, e.g. "Chiropractor". Matches any of the provider's 15 taxonomy slots.</summary>
    public string? Classification { get; init; }

    /// <summary>NUCC Specialization within <see cref="Classification"/>.</summary>
    public string? Specialization { get; init; }

    /// <summary>A single NUCC taxonomy code, e.g. "111N00000X".</summary>
    public string? TaxonomyCode { get; init; }

    /// <summary>Two-letter state of any practice location.</summary>
    public string? State { get; init; }

    /// <summary>Five-digit county FIPS; matches locations whose ZIP overlaps the county (HUD crosswalk).</summary>
    public string? CountyFips { get; init; }

    public string? City { get; init; }

    public string? Zip5 { get; init; }

    /// <summary>With <see cref="Zip5"/>: locations whose ZIP centroid is within this many miles (1–100).</summary>
    public int? RadiusMiles { get; init; }

    /// <summary>Prefix of the individual's last name.</summary>
    public string? LastName { get; init; }

    /// <summary>Prefix of the individual's first name.</summary>
    public string? FirstName { get; init; }

    /// <summary>Prefix of the organization's legal business name.</summary>
    public string? OrgName { get; init; }

    public string? Npi { get; init; }

    /// <summary>1 = individual, 2 = organization.</summary>
    public int? EntityType { get; init; }

    /// <summary>NPPES sex code (M or F).</summary>
    public string? Gender { get; init; }

    /// <summary>Prefix of the credential, ignoring punctuation ("MD" matches "M.D.").</summary>
    public string? Credential { get; init; }

    /// <summary>Sort key (<see cref="SearchSort"/>), optionally prefixed with "-" for descending. Default "name".</summary>
    public string? Sort { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = DefaultPageSize;

    public bool HasLocationFilter => State is not null || CountyFips is not null || City is not null || Zip5 is not null;
}
