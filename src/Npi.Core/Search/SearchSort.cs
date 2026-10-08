namespace Npi.Core.Search;

public enum SearchSort
{
    Name,
    Npi,
    Credential,
    City,
    State,
    Zip,
    LastUpdate,
    Enumeration,
}

/// <summary>A whitelisted sort key and direction, parsed from "name", "-lastUpdate", etc.</summary>
public readonly record struct SearchSortOrder(SearchSort Key, bool Descending)
{
    public static readonly SearchSortOrder Default = new(SearchSort.Name, false);

    public static IReadOnlyList<string> Names { get; } = ["name", "npi", "credential", "city", "state", "zip", "lastUpdate", "enumeration"];

    public static bool TryParse(string? value, out SearchSortOrder order)
    {
        order = Default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var text = value.Trim();
        var descending = text.StartsWith('-');
        var name = descending ? text[1..] : text;
        var index = Names.ToList().FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        order = new SearchSortOrder((SearchSort)index, descending);
        return true;
    }

    public override string ToString() => (Descending ? "-" : "") + Names[(int)Key];
}
