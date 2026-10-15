using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>The specialty and provider filter fields shared by the search page and the map page (_SpecialtyFields, _ProviderFields).</summary>
public sealed record FilterFields(SearchFilter Filter, IReadOnlyList<string> Classifications, IReadOnlyList<string> Specializations,
    IReadOnlyDictionary<string, string[]> Errors)
{
    /// <summary>The standardized credentials for the Credential dropdown, most common first (Stage 5.5 item 12).</summary>
    public IReadOnlyList<CredentialInfo> Credentials { get; init; } = [];

    /// <summary>The dropdown entry the Credential filter stands for ("M.D." in a link selects MD), else the filter as given.</summary>
    public string? SelectedCredential { get; init; }

    /// <summary>The "New providers" choices (Stage 5.5 item 11), plus <paramref name="current"/> when a link uses another day count.</summary>
    public static IEnumerable<(int Days, string Text)> NewChoices(int? current) =>
        WithCurrent([(30, "Last 30 days"), (90, "Last 90 days"), (365, "Last year")], current);

    /// <summary>The "Record updated" choices, plus <paramref name="current"/> when a link uses another day count.</summary>
    public static IEnumerable<(int Days, string Text)> UpdatedChoices(int? current) =>
        WithCurrent([(7, "Last 7 days"), (30, "Last 30 days"), (90, "Last 90 days")], current);

    private static (int Days, string Text)[] WithCurrent((int Days, string Text)[] choices, int? current) =>
        current is { } days and > 0 && choices.All(c => c.Days != days)
            ? [.. choices.Append((Days: days, Text: string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Last {days:N0} days"))).OrderBy(c => c.Days)]
            : choices;

    /// <summary>The plain specialty categories (<see cref="SpecialtyCategories"/>), in display order.</summary>
    public IReadOnlyList<string> Categories => TaxonomyCatalog.GetCategories();

    /// <summary>The chosen category's subcategories.</summary>
    public IReadOnlyList<string> Subcategories => TaxonomyCatalog.GetSubcategories(Filter.Category) ?? [];

    /// <summary>Open the exact NUCC fields when a link uses them.</summary>
    public bool NuccOpen => Filter.Classification is not null || Filter.TaxonomyCode is not null || Errors.ContainsKey(nameof(SearchFilter.Classification))
                            || Errors.ContainsKey(nameof(SearchFilter.Specialization));

    /// <summary>How many filters other than specialty and location are set (they sit in the "More filters" drawer).</summary>
    public int MoreCount => new object?[]
    {
        Filter.LastName, Filter.FirstName, Filter.OrgName, Filter.Npi, Filter.EntityType, Filter.Gender, Filter.Credential, Filter.Excluded,
        Filter.OptedOut, Filter.OrderRefer, Filter.AcceptsAssignment, Filter.Telehealth, Filter.MinYears, Filter.MedicareActive, Filter.Shortage,
        Filter.AddressType, Filter.Quality, Filter.NewWithinDays, Filter.UpdatedWithinDays,
    }.Count(v => v is not null);

    /// <summary>True when one of those filters is set or has an error: the drawer opens.</summary>
    public bool MoreOpen => MoreCount > 0 || Errors.Keys.Any(k => k is not (nameof(SearchFilter.Category) or nameof(SearchFilter.Subcategory)
        or nameof(SearchFilter.Classification) or nameof(SearchFilter.Specialization)));

    public string Error(string field) => Errors.TryGetValue(field, out var messages) ? string.Join(" ", messages) : "";
}
