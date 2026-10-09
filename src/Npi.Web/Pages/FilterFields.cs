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

    public string Error(string field) => Errors.TryGetValue(field, out var messages) ? string.Join(" ", messages) : "";
}
