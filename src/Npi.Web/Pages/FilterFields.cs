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

    public string Error(string field) => Errors.TryGetValue(field, out var messages) ? string.Join(" ", messages) : "";
}
