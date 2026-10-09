namespace Npi.Core.Search;

/// <summary>One search result: the grid and CSV summary columns (CLAUDE.md §7.3), showing the matching location.</summary>
public sealed record ProviderSummary(
    string Npi,
    int EntityType,
    string Name,
    string? Credential,
    string? PrimarySpecialty,
    string? Address1,
    string? Address2,
    string? City,
    string? State,
    string? Zip,
    string? County,
    string? Phone,
    string? Gender,
    DateOnly? EnumerationDate,
    DateOnly? LastUpdateDate)
{
    public string EntityTypeName => EntityType == 2 ? "Organization" : "Individual";

    /// <summary>Badges from the Stage 5.5 datasets (exclusion, Medicare opt-out, order/refer eligibility).</summary>
    public ProviderFlags Flags { get; init; } = ProviderFlags.None;
}

public sealed record SearchResult(IReadOnlyList<ProviderSummary> Items, int Page, int PageSize, long TotalCount, DateOnly? DataAsOf);
