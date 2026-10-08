namespace Npi.Client;

/// <summary>One search result: the summary columns, showing the location that matched the search.</summary>
public sealed class ProviderSummary
{
    /// <summary>Ten-digit National Provider Identifier.</summary>
    public string Npi { get; set; } = "";

    /// <summary>1 = individual, 2 = organization.</summary>
    public int EntityType { get; set; }

    /// <summary>"Individual" or "Organization".</summary>
    public string EntityTypeName { get; set; } = "";

    /// <summary>"LAST, FIRST MIDDLE SUFFIX" for individuals, the legal business name for organizations.</summary>
    public string Name { get; set; } = "";

    public string? Credential { get; set; }

    /// <summary>"Classification – Specialization" of the primary taxonomy.</summary>
    public string? PrimarySpecialty { get; set; }

    public string? Address1 { get; set; }

    public string? Address2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    /// <summary>ZIP or ZIP+4 ("11702-2218"); the raw postal code outside the US.</summary>
    public string? Zip { get; set; }

    public string? County { get; set; }

    /// <summary>Digits only, as published by NPPES.</summary>
    public string? Phone { get; set; }

    /// <summary>F or M (individuals only).</summary>
    public string? Gender { get; set; }

    public DateTime? EnumerationDate { get; set; }

    public DateTime? LastUpdateDate { get; set; }
}

/// <summary>One page of search results.</summary>
public sealed class ProviderPage
{
    public IReadOnlyList<ProviderSummary> Items { get; set; } = [];

    public int Page { get; set; }

    public int PageSize { get; set; }

    /// <summary>Every match, not just this page. Paging stops at the first 10,000; download the CSV for more.</summary>
    public long TotalCount { get; set; }

    /// <summary>Newest NPPES update date in the data being served.</summary>
    public DateTime? DataAsOf { get; set; }
}

/// <summary>One taxonomy (specialty) of a provider, with its license.</summary>
public sealed class ProviderTaxonomy
{
    /// <summary>NPPES slot 1–15.</summary>
    public int Slot { get; set; }

    /// <summary>NUCC taxonomy code, e.g. 111N00000X.</summary>
    public string Code { get; set; } = "";

    public string? Classification { get; set; }

    public string? Specialization { get; set; }

    public bool IsPrimary { get; set; }

    public string? LicenseNumber { get; set; }

    public string? LicenseState { get; set; }
}

/// <summary>A practice location (primary or secondary).</summary>
public sealed class ProviderLocation
{
    public bool IsPrimary { get; set; }

    public string? Address1 { get; set; }

    public string? Address2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Zip { get; set; }

    /// <summary>"US", or the country code of a foreign address.</summary>
    public string? CountryCode { get; set; }

    public string? Phone { get; set; }
}

/// <summary>Everything published for one provider.</summary>
public sealed class ProviderDetail
{
    public string Npi { get; set; } = "";

    public int EntityType { get; set; }

    public string EntityTypeName { get; set; } = "";

    public string Name { get; set; } = "";

    public string? NamePrefix { get; set; }

    public string? Credential { get; set; }

    public string? Gender { get; set; }

    public DateTime? EnumerationDate { get; set; }

    public DateTime? LastUpdateDate { get; set; }

    public IReadOnlyList<ProviderTaxonomy> Taxonomies { get; set; } = [];

    public IReadOnlyList<ProviderLocation> Locations { get; set; } = [];

    public IReadOnlyList<string> OtherNames { get; set; } = [];
}

/// <summary>A state or territory.</summary>
public sealed class StateInfo
{
    /// <summary>Two-letter code, e.g. NY.</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
}

/// <summary>A county, by five-digit FIPS code (use it as <see cref="ProviderSearch.County"/>).</summary>
public sealed class CountyInfo
{
    public string Fips { get; set; } = "";

    public string Name { get; set; } = "";
}

/// <summary>Where the served data comes from.</summary>
public sealed class ApiMeta
{
    /// <summary>Newest NPPES update date in the data.</summary>
    public DateTime? DataAsOf { get; set; }

    public string? MonthlyFile { get; set; }

    public string? WeeklyFile { get; set; }

    public string? DeactivationFile { get; set; }

    public string? NuccVersion { get; set; }

    public string? HudVersion { get; set; }

    public string? CensusVersion { get; set; }

    /// <summary>Active providers in the data.</summary>
    public int ProviderCount { get; set; }

    public DateTimeOffset ProjectedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
}
