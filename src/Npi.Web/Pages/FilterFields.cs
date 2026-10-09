using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>The specialty and provider filter fields shared by the search page and the map page (_SpecialtyFields, _ProviderFields).</summary>
public sealed record FilterFields(SearchFilter Filter, IReadOnlyList<string> Classifications, IReadOnlyList<string> Specializations,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public string Error(string field) => Errors.TryGetValue(field, out var messages) ? string.Join(" ", messages) : "";
}
