using System.Text.RegularExpressions;

namespace Npi.Core.Search;

/// <summary>The search was invalid. <see cref="Errors"/> maps a filter name to its problems (for RFC 7807 responses).</summary>
public sealed class SearchValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("Invalid search: " + string.Join(" ", errors.SelectMany(e => e.Value)))
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>Validates and normalises a <see cref="SearchFilter"/> (CLAUDE.md §7 Stage 3.4).</summary>
public static partial class SearchValidation
{
    private const int MaxTextLength = 60;

    /// <summary>Returns the filter with values trimmed/upper-cased, or throws <see cref="SearchValidationException"/>.</summary>
    /// <param name="requireFilter">False for the map search, where the map area itself is the filter.</param>
    public static SearchFilter Normalize(SearchFilter filter, bool requireFilter = true)
    {
        var errors = new Dictionary<string, List<string>>();
        void Error(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
            {
                errors[field] = list = [];
            }

            list.Add(message);
        }

        string? Clean(string? value, string field, bool upper = false)
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (v is not null && v.Length > MaxTextLength)
            {
                Error(field, $"{field} must be at most {MaxTextLength} characters.");
            }

            return upper ? v?.ToUpperInvariant() : v;
        }

        var f = filter with
        {
            Classification = Clean(filter.Classification, nameof(SearchFilter.Classification)),
            Specialization = Clean(filter.Specialization, nameof(SearchFilter.Specialization)),
            TaxonomyCode = Clean(filter.TaxonomyCode, nameof(SearchFilter.TaxonomyCode), upper: true),
            State = Clean(filter.State, nameof(SearchFilter.State), upper: true),
            CountyFips = Clean(filter.CountyFips, nameof(SearchFilter.CountyFips)),
            City = Clean(filter.City, nameof(SearchFilter.City)),
            Zip5 = Clean(filter.Zip5, nameof(SearchFilter.Zip5)),
            LastName = Clean(filter.LastName, nameof(SearchFilter.LastName)),
            FirstName = Clean(filter.FirstName, nameof(SearchFilter.FirstName)),
            OrgName = Clean(filter.OrgName, nameof(SearchFilter.OrgName)),
            Npi = Clean(filter.Npi, nameof(SearchFilter.Npi)),
            Gender = Clean(filter.Gender, nameof(SearchFilter.Gender), upper: true),
            Credential = Clean(filter.Credential, nameof(SearchFilter.Credential)),
            Shortage = Clean(filter.Shortage, nameof(SearchFilter.Shortage)),
            Sort = Clean(filter.Sort, nameof(SearchFilter.Sort)),
            NameMatch = Clean(filter.NameMatch, nameof(SearchFilter.NameMatch))?.ToLowerInvariant() is { } match && match != NameSearch.Prefix ? match : null,
        };

        if (requireFilter && f is { Classification: null, TaxonomyCode: null, State: null, CountyFips: null, City: null, Zip5: null,
                LastName: null, FirstName: null, OrgName: null, Npi: null, EntityType: null, Gender: null, Credential: null,
                Excluded: null, OptedOut: null, OrderRefer: null, AcceptsAssignment: null, Telehealth: null, MinYears: null,
                MedicareActive: null, Shortage: null, NewWithinDays: null, UpdatedWithinDays: null })
        {
            Error("filter", "Enter at least one search filter.");
        }

        if (f.Specialization is not null && f.Classification is null)
        {
            Error(nameof(SearchFilter.Specialization), "Specialization requires a classification.");
        }

        if (f.TaxonomyCode is not null && !TaxonomyCode().IsMatch(f.TaxonomyCode))
        {
            Error(nameof(SearchFilter.TaxonomyCode), "Taxonomy code must be 10 letters/digits, e.g. 111N00000X.");
        }

        if (f.State is not null && !StateCode().IsMatch(f.State))
        {
            Error(nameof(SearchFilter.State), "State must be a 2-letter code, e.g. NY.");
        }

        if (f.CountyFips is not null && !(f.CountyFips.Length == 5 && f.CountyFips.All(char.IsAsciiDigit)))
        {
            Error(nameof(SearchFilter.CountyFips), "County must be a 5-digit FIPS code, e.g. 36103.");
        }

        if (f.Zip5 is not null && !InputFormats.IsZip5(f.Zip5))
        {
            Error(nameof(SearchFilter.Zip5), "ZIP must be 5 digits.");
        }

        if (f.RadiusMiles is not null)
        {
            if (f.RadiusMiles is < 1 or > 100)
            {
                Error(nameof(SearchFilter.RadiusMiles), "Radius must be between 1 and 100 miles.");
            }

            if (f.Zip5 is null)
            {
                Error(nameof(SearchFilter.RadiusMiles), "Radius search needs a ZIP.");
            }
        }

        if (f.Npi is not null && !InputFormats.IsNpi(f.Npi))
        {
            Error(nameof(SearchFilter.Npi), "NPI must be 10 digits.");
        }

        if (f.EntityType is not null and not (1 or 2))
        {
            Error(nameof(SearchFilter.EntityType), "Entity type must be 1 (individual) or 2 (organization).");
        }

        if (f.Gender is not null and not ("M" or "F"))
        {
            Error(nameof(SearchFilter.Gender), "Gender must be M or F.");
        }

        if (f.Shortage is not null && !AreaService.Disciplines.ContainsKey(f.Shortage))
        {
            Error(nameof(SearchFilter.Shortage), "Shortage must be primaryCare, dental or mentalHealth.");
        }

        if (f.MinYears is < 1 or > 70)
        {
            Error(nameof(SearchFilter.MinYears), "Years in practice must be between 1 and 70.");
        }

        if (f.NewWithinDays is < 1 or > SearchFilter.MaxWithinDays)
        {
            Error(nameof(SearchFilter.NewWithinDays), $"New within must be between 1 and {SearchFilter.MaxWithinDays:N0} days.");
        }

        if (f.UpdatedWithinDays is < 1 or > SearchFilter.MaxWithinDays)
        {
            Error(nameof(SearchFilter.UpdatedWithinDays), $"Updated within must be between 1 and {SearchFilter.MaxWithinDays:N0} days.");
        }

        if (f.NameMatch is not null and not NameSearch.Similar)
        {
            Error(nameof(SearchFilter.NameMatch), "Name match must be prefix or similar.");
        }

        if (f.Page < 1)
        {
            Error(nameof(SearchFilter.Page), "Page must be 1 or more.");
        }

        if (f.PageSize is < 1 or > SearchFilter.MaxPageSize)
        {
            Error(nameof(SearchFilter.PageSize), $"Page size must be between 1 and {SearchFilter.MaxPageSize}.");
        }
        else if ((long)f.Page * f.PageSize > SearchFilter.MaxResultWindow)
        {
            Error(nameof(SearchFilter.Page), $"Paging is limited to the first {SearchFilter.MaxResultWindow:N0} results; narrow the search or download the CSV.");
        }

        if (f.Sort is not null && !SearchSortOrder.TryParse(f.Sort, out _))
        {
            Error(nameof(SearchFilter.Sort), $"Sort must be one of: {string.Join(", ", SearchSortOrder.Names)} (prefix '-' for descending).");
        }

        if (errors.Count > 0)
        {
            throw new SearchValidationException(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }

        return f;
    }

    [GeneratedRegex("^[0-9A-Z]{10}$")]
    private static partial Regex TaxonomyCode();

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex StateCode();
}
