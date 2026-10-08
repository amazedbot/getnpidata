using System.Globalization;
using Microsoft.AspNetCore.Http.Extensions;
using Npi.Core.Search;

namespace Npi.Web.Search;

/// <summary>
/// Converts between query strings and <see cref="SearchFilter"/>. The search page, /export.csv and
/// /api/v1/providers all use these parameter names (CLAUDE.md §7 Stage 5), so a search URL can be
/// shared, paged, sorted or downloaded without changing its meaning.
/// </summary>
public static class SearchQueryString
{
    /// <returns>The filter, plus problems with values that are not even the right type (e.g. radius=abc).</returns>
    public static (SearchFilter Filter, Dictionary<string, string[]> Errors) Parse(IQueryCollection q)
    {
        var errors = new Dictionary<string, string[]>();
        int? Int(string key, string field)
        {
            var raw = Text(q, key);
            if (raw is null)
            {
                return null;
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            errors[field] = [$"{key} must be a whole number."];
            return null;
        }

        var filter = new SearchFilter
        {
            Classification = Text(q, "classification"),
            Specialization = Text(q, "specialization"),
            TaxonomyCode = Text(q, "taxonomy"),
            State = Text(q, "state"),
            CountyFips = Text(q, "county"),
            City = Text(q, "city"),
            Zip5 = Text(q, "zip"),
            RadiusMiles = Int("radius", nameof(SearchFilter.RadiusMiles)),
            LastName = Text(q, "lastName"),
            FirstName = Text(q, "firstName"),
            OrgName = Text(q, "orgName"),
            Npi = Text(q, "npi"),
            EntityType = Int("entityType", nameof(SearchFilter.EntityType)),
            Gender = Text(q, "gender"),
            Credential = Text(q, "credential"),
            Sort = Text(q, "sort"),
            Page = Int("page", nameof(SearchFilter.Page)) ?? 1,
            PageSize = Int("pageSize", nameof(SearchFilter.PageSize)) ?? SearchFilter.DefaultPageSize,
        };
        return (filter, errors);
    }

    /// <summary>One query parameter: its name, whether it is a whole number, the <see cref="SearchFilter"/> property and a description.</summary>
    public sealed record Parameter(string Name, bool IsInteger, string Field, string Description);

    /// <summary>Every search parameter, in display order. Feeds the OpenAPI document and maps validation errors back to parameter names.</summary>
    public static readonly IReadOnlyList<Parameter> Parameters =
    [
        new("classification", false, nameof(SearchFilter.Classification), "NUCC classification, e.g. \"Chiropractor\" (GET /api/v1/taxonomy/classifications). Matches any of the provider's 15 taxonomies."),
        new("specialization", false, nameof(SearchFilter.Specialization), "NUCC specialization within the classification (GET /api/v1/taxonomy/classifications/{c}/specializations)."),
        new("taxonomy", false, nameof(SearchFilter.TaxonomyCode), "NUCC taxonomy code, e.g. 111N00000X."),
        new("state", false, nameof(SearchFilter.State), "Two-letter state code of any practice location."),
        new("county", false, nameof(SearchFilter.CountyFips), "Five-digit county FIPS code (GET /api/v1/states/{st}/counties). A ZIP matches every county it overlaps."),
        new("city", false, nameof(SearchFilter.City), "Practice location city (exact, case-insensitive)."),
        new("zip", false, nameof(SearchFilter.Zip5), "Five-digit practice location ZIP."),
        new("radius", true, nameof(SearchFilter.RadiusMiles), "Miles around zip (1-100). Needs a ZIP with a Census ZCTA centroid."),
        new("lastName", false, nameof(SearchFilter.LastName), "Last name prefix (individuals)."),
        new("firstName", false, nameof(SearchFilter.FirstName), "First name prefix (individuals)."),
        new("orgName", false, nameof(SearchFilter.OrgName), "Organization name prefix."),
        new("npi", false, nameof(SearchFilter.Npi), "Ten-digit NPI."),
        new("entityType", true, nameof(SearchFilter.EntityType), "1 = individual, 2 = organization."),
        new("gender", false, nameof(SearchFilter.Gender), "F or M (individuals)."),
        new("credential", false, nameof(SearchFilter.Credential), "Credential, punctuation ignored (\"M.D.\" = \"MD\")."),
        new("sort", false, nameof(SearchFilter.Sort), "name (default), npi, credential, city, state, zip, lastUpdate or enumeration; prefix - for descending."),
        new("page", true, nameof(SearchFilter.Page), "Page number, from 1. Paging stops at the first 10,000 matches; use the CSV beyond that."),
        new("pageSize", true, nameof(SearchFilter.PageSize), "Results per page, 1-200 (default 50)."),
    ];

    private static readonly Dictionary<string, string> FieldToParameter =
        Parameters.ToDictionary(p => p.Field, p => p.Name, StringComparer.Ordinal);

    /// <summary>Validation errors keyed by query parameter name ("radius") instead of filter property ("RadiusMiles"). Other keys, like "filter", are kept.</summary>
    public static Dictionary<string, string[]> ByParameterName(IEnumerable<KeyValuePair<string, string[]>> errors) =>
        errors.GroupBy(e => FieldToParameter.TryGetValue(e.Key, out var name) ? name : e.Key)
            .ToDictionary(g => g.Key, g => g.SelectMany(e => e.Value).ToArray());

    /// <summary>True when the query string contains any search filter (paging/sorting alone don't count).</summary>
    public static bool HasAnyFilter(IQueryCollection q) => FilterKeys.Any(k => Text(q, k) is not null);

    private static readonly string[] FilterKeys =
        ["classification", "taxonomy", "state", "county", "city", "zip", "lastName", "firstName", "orgName", "npi", "entityType", "gender", "credential"];

    /// <summary>"?classification=…&amp;state=…" for the filter, optionally with a different page/sort.</summary>
    public static string ToQueryString(SearchFilter f, int? page = null, string? sort = null, bool includePaging = true)
    {
        var qb = new QueryBuilder();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                qb.Add(key, value);
            }
        }

        Add("classification", f.Classification);
        Add("specialization", f.Specialization);
        Add("taxonomy", f.TaxonomyCode);
        Add("state", f.State);
        Add("county", f.CountyFips);
        Add("city", f.City);
        Add("zip", f.Zip5);
        Add("radius", f.RadiusMiles?.ToString(CultureInfo.InvariantCulture));
        Add("lastName", f.LastName);
        Add("firstName", f.FirstName);
        Add("orgName", f.OrgName);
        Add("npi", f.Npi);
        Add("entityType", f.EntityType?.ToString(CultureInfo.InvariantCulture));
        Add("gender", f.Gender);
        Add("credential", f.Credential);
        Add("sort", sort ?? f.Sort);
        if (includePaging)
        {
            var p = page ?? f.Page;
            if (p != 1)
            {
                Add("page", p.ToString(CultureInfo.InvariantCulture));
            }

            if (f.PageSize != SearchFilter.DefaultPageSize)
            {
                Add("pageSize", f.PageSize.ToString(CultureInfo.InvariantCulture));
            }
        }

        return qb.ToQueryString().Value ?? "";
    }

    private static string? Text(IQueryCollection q, string key)
    {
        var value = q[key].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
