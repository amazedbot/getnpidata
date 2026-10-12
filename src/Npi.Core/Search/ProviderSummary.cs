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

    /// <summary>The plain category of the primary taxonomy (<see cref="SpecialtyCategories"/>), e.g. "Doctors (MD, DO)".</summary>
    public string? Category { get; init; }

    /// <summary>The plain subcategory of the primary taxonomy, e.g. "Heart (cardiology)".</summary>
    public string? Subcategory { get; init; }

    /// <summary>The primary specialty in plain words, e.g. "Cardiologist" (<see cref="PrimarySpecialty"/> keeps NUCC's wording).</summary>
    public string? PlainSpecialty { get; init; }

    /// <summary>How current and trustworthy the record looks (docs/PRODUCT_PLAN.md step 3.1); null when not labelled yet.</summary>
    public ProviderQuality? Quality { get; init; }

    /// <summary>The kind of building at the shown location (<see cref="AddressTypes"/>, Stage 5.5 item 18); null when not labelled.</summary>
    public string? AddressType { get; init; }
}

/// <summary>The status of one NPI in a bulk lookup.</summary>
public enum LookupStatus
{
    /// <summary>An active provider; <see cref="LookupRow.Provider"/> is set.</summary>
    Found,

    /// <summary>Well-formed but not in NPPES.</summary>
    NotFound,

    /// <summary>Not 10 digits, or the check digit is wrong (a typo).</summary>
    Invalid,

    /// <summary>Deactivated in NPPES; <see cref="LookupRow.DeactivationDate"/> says when (docs/PRODUCT_PLAN.md decision 2).</summary>
    Deactivated,
}

/// <summary>One requested NPI and what the lookup found for it.</summary>
public sealed record LookupRow(string Npi, LookupStatus Status, ProviderSummary? Provider)
{
    /// <summary>When NPPES deactivated the NPI (status Deactivated).</summary>
    public DateOnly? DeactivationDate { get; init; }

    /// <summary>The earliest OIG exclusion date of an excluded provider.</summary>
    public DateOnly? ExclusionDate { get; init; }

    /// <summary>When the provider's current Medicare opt-out ends; null when not opted out or open-ended.</summary>
    public DateOnly? OptOutEndDate { get; init; }
}

public sealed record SearchResult(IReadOnlyList<ProviderSummary> Items, int Page, int PageSize, long TotalCount, DateOnly? DataAsOf);
