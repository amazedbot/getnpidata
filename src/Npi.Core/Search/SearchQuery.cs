using System.Globalization;
using System.Text;
using Dapper;

namespace Npi.Core.Search;

/// <summary>Centre of a radius search, resolved from the ZIP's Census centroid.</summary>
public readonly record struct GeoPoint(double Lat, double Lon);

/// <summary>
/// Builds the parameterized SQL for a validated <see cref="SearchFilter"/> (CLAUDE.md §7 Stage 3.3).
/// User input only ever travels as parameters; the SQL text comes from fixed fragments and the sort
/// whitelist. One row per NPI; a provider matches when ANY of its taxonomy slots matches and ANY of
/// its practice locations matches all location filters.
/// <para>
/// Performance (Stage 3.5): the most selective filter becomes a <em>driver</em>, a derived table of
/// candidate NPIs, and MySQL is told to start from it (JOIN_ORDER). Specialty + location uses
/// provider_search, a (taxonomy_code, state, city, zip5, npi) table, so the main use case is one
/// index range read. Everything else filters the candidates. The page query also returns the total
/// via COUNT(*) OVER (), so the matching work is done once per page.
/// </para>
/// </summary>
public sealed class SearchQuery
{
    private const double EarthRadiusMiles = 3958.8;
    private const double MilesPerDegreeLat = 69.0;

    private readonly SearchSortOrder _sort;
    private readonly string? _driver;
    private readonly List<string> _remaining = [];
    private readonly bool _hasLocationFilter;
    private readonly List<string> _locationTemplates = []; // "{0}" = table alias

    /// <param name="taxonomyCodes">The codes the Classification/Specialization/TaxonomyCode filters resolved to, or null for no specialty filter.</param>
    /// <param name="radiusCenter">The ZIP centroid for a radius search.</param>
    public SearchQuery(SearchFilter filter, IReadOnlyCollection<string>? taxonomyCodes, GeoPoint? radiusCenter)
    {
        Filter = filter;
        _sort = SearchSortOrder.TryParse(filter.Sort, out var sort)
            ? sort
            : throw new ArgumentException($"Unknown sort '{filter.Sort}'; validate the filter first.", nameof(filter));

        // Provider-level conditions on alias "{0}" (p in the outer query, d in a name/credential driver).
        var providerTemplates = new List<(string Kind, string Template)>();
        if (filter.Npi is not null)
        {
            Parameters.Add("npi", filter.Npi);
        }

        if (filter.LastName is not null)
        {
            Parameters.Add("lastName", Prefix(filter.LastName));
            providerTemplates.Add(("name", "{0}.last_name LIKE @lastName"));
        }

        if (filter.FirstName is not null)
        {
            Parameters.Add("firstName", Prefix(filter.FirstName));
            providerTemplates.Add(("name", "{0}.first_name LIKE @firstName"));
        }

        if (filter.OrgName is not null)
        {
            Parameters.Add("orgName", Prefix(filter.OrgName));
            providerTemplates.Add(("name", "{0}.org_name LIKE @orgName"));
        }

        if (filter.Credential is not null)
        {
            var key = new string(filter.Credential.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
            Parameters.Add("credential", Prefix(key));
            providerTemplates.Add(("credential", "{0}.credential_key LIKE @credential"));
        }

        if (filter.EntityType is not null)
        {
            Parameters.Add("entityType", filter.EntityType);
            providerTemplates.Add(("attribute", "{0}.entity_type = @entityType"));
        }

        if (filter.Gender is not null)
        {
            Parameters.Add("gender", filter.Gender);
            providerTemplates.Add(("attribute", "{0}.gender = @gender"));
        }

        if (taxonomyCodes is not null)
        {
            // An empty list can't match anything; keep the query valid instead of emitting "IN ()".
            Parameters.Add("taxonomyCodes", taxonomyCodes.Count > 0 ? taxonomyCodes.ToArray() : ["-"]);
        }

        AddLocationFilters(filter, radiusCenter);
        _hasLocationFilter = _locationTemplates.Count > 0;

        // Pick the driver: the filter expected to match the fewest providers.
        var drivenBy = new HashSet<string>();
        if (filter.Npi is not null)
        {
            _driver = "SELECT @npi AS npi";
        }
        else if (taxonomyCodes is not null && _hasLocationFilter)
        {
            _driver = $"SELECT DISTINCT s.npi FROM provider_search s WHERE s.taxonomy_code IN @taxonomyCodes AND {Location("s")}";
            drivenBy.Add("taxonomy");
            drivenBy.Add("location");
        }
        else if (taxonomyCodes is not null)
        {
            _driver = "SELECT DISTINCT t.npi FROM provider_taxonomy t WHERE t.taxonomy_code IN @taxonomyCodes";
            drivenBy.Add("taxonomy");
        }
        else if (providerTemplates.Any(t => t.Kind == "name"))
        {
            _driver = "SELECT d.npi FROM provider d WHERE " + string.Join(" AND ", providerTemplates.Where(t => t.Kind == "name").Select(t => Format(t.Template, "d")));
            drivenBy.Add("name");
        }
        else if (_hasLocationFilter)
        {
            _driver = $"SELECT DISTINCT l.npi FROM provider_location l WHERE {Location("l")}";
            drivenBy.Add("location");
        }
        else if (providerTemplates.Any(t => t.Kind == "credential"))
        {
            _driver = "SELECT d.npi FROM provider d WHERE " + Format(providerTemplates.First(t => t.Kind == "credential").Template, "d");
            drivenBy.Add("credential");
        }

        _remaining.AddRange(providerTemplates.Where(t => !drivenBy.Contains(t.Kind)).Select(t => Format(t.Template, "p")));
        if (taxonomyCodes is not null && !drivenBy.Contains("taxonomy"))
        {
            _remaining.Add("EXISTS (SELECT 1 FROM provider_taxonomy t WHERE t.npi = p.npi AND t.taxonomy_code IN @taxonomyCodes)");
        }

        if (_hasLocationFilter && !drivenBy.Contains("location"))
        {
            _remaining.Add($"EXISTS (SELECT 1 FROM provider_location l WHERE l.npi = p.npi AND {Location("l")})");
        }

        Parameters.Add("take", filter.PageSize);
        Parameters.Add("skip", (filter.Page - 1) * filter.PageSize);
    }

    public SearchFilter Filter { get; }

    public DynamicParameters Parameters { get; } = new();

    /// <summary>Location conditions on alias <c>l</c> (provider_location), "1 = 1" when there are none.</summary>
    public string LocationWhere => Location("l");

    /// <summary>FROM … WHERE … selecting matching providers as alias <c>p</c>.</summary>
    public string From
    {
        get
        {
            var where = _remaining.Count > 0 ? " WHERE " + string.Join(" AND ", _remaining) : "";
            return _driver is null
                ? $"FROM provider p{where}"
                : $"FROM ({_driver}) c JOIN provider p ON p.npi = c.npi{where}";
        }
    }

    private string Hint => _driver is null ? "" : "/*+ JOIN_ORDER(c, p) */ ";

    public string CountSql => $"SELECT {Hint}COUNT(*) {From}";

    /// <summary>The NPIs of the requested page in sort order, each with the total match count (column Total).</summary>
    public string PageSql => $"SELECT {Hint}p.npi AS Npi, COUNT(*) OVER () AS Total {From} ORDER BY {OrderBy} LIMIT @take OFFSET @skip";

    /// <summary>All matching NPIs in sort order (CSV export; no paging).</summary>
    public string AllSql => $"SELECT {Hint}p.npi {From} ORDER BY {OrderBy}";

    /// <summary>The matching locations of the given NPIs (parameter @npis), best first per NPI.</summary>
    public string LocationsSql =>
        $"SELECT l.npi AS Npi, l.address1 AS Address1, l.address2 AS Address2, l.city AS City, l.state AS State, l.zip5 AS Zip5, l.zip4 AS Zip4, " +
        $"l.postal_code AS PostalCode, l.country_code AS CountryCode, l.phone AS Phone FROM provider_location l " +
        $"WHERE l.npi IN @npis AND {LocationWhere} ORDER BY l.npi, l.is_primary DESC, l.id";

    public string OrderBy
    {
        get
        {
            var dir = _sort.Descending ? "DESC" : "ASC";
            return _sort.Key switch
            {
                SearchSort.Name => $"p.sort_name {dir}, p.npi",
                SearchSort.Npi => $"p.npi {dir}",
                SearchSort.Credential => $"p.credential_key {dir}, p.sort_name, p.npi",
                SearchSort.LastUpdate => $"p.last_update_date {dir}, p.npi",
                SearchSort.Enumeration => $"p.enumeration_date {dir}, p.npi",
                SearchSort.City => $"{MatchingLocation("city")} {dir}, p.sort_name, p.npi",
                SearchSort.State => $"{MatchingLocation("state")} {dir}, p.sort_name, p.npi",
                SearchSort.Zip => $"{MatchingLocation("zip5")} {dir}, p.sort_name, p.npi",
                _ => throw new InvalidOperationException($"Unknown sort {_sort.Key}"),
            };
        }
    }

    // The value of the location the grid shows: the first matching location, primary first.
    private string MatchingLocation(string column) =>
        $"(SELECT ml.{column} FROM provider_location ml WHERE ml.npi = p.npi AND {Location("ml")} ORDER BY ml.is_primary DESC, ml.id LIMIT 1)";

    private string Location(string alias) =>
        _locationTemplates.Count > 0 ? string.Join(" AND ", _locationTemplates.Select(t => Format(t, alias))) : "1 = 1";

    private void AddLocationFilters(SearchFilter filter, GeoPoint? radiusCenter)
    {
        if (filter.State is not null)
        {
            Parameters.Add("state", filter.State);
            _locationTemplates.Add("{0}.state = @state");
        }

        if (filter.City is not null)
        {
            Parameters.Add("city", filter.City);
            _locationTemplates.Add("{0}.city = @city");
        }

        if (filter.CountyFips is not null)
        {
            Parameters.Add("countyFips", filter.CountyFips);
            _locationTemplates.Add("{0}.zip5 IN (SELECT z.zip5 FROM zip_county z WHERE z.county_fips = @countyFips)");
        }

        if (filter.Zip5 is not null && filter.RadiusMiles is null)
        {
            Parameters.Add("zip5", filter.Zip5);
            _locationTemplates.Add("{0}.zip5 = @zip5");
        }

        if (filter.RadiusMiles is not null)
        {
            var center = radiusCenter ?? throw new ArgumentException("A radius search needs the ZIP centroid.", nameof(radiusCenter));
            var miles = filter.RadiusMiles.Value;
            var dLat = miles / MilesPerDegreeLat;
            var dLon = miles / (MilesPerDegreeLat * Math.Max(Math.Cos(center.Lat * Math.PI / 180), 0.01));
            Parameters.Add("latMin", center.Lat - dLat);
            Parameters.Add("latMax", center.Lat + dLat);
            Parameters.Add("lonMin", center.Lon - dLon);
            Parameters.Add("lonMax", center.Lon + dLon);
            Parameters.Add("lat", center.Lat);
            Parameters.Add("lon", center.Lon);
            Parameters.Add("radius", (double)miles);
            // Bounding box (uses the (lat, lon) index), then the exact great-circle distance.
            _locationTemplates.Add(string.Create(CultureInfo.InvariantCulture,
                $"{{0}}.zip5 IN (SELECT c.zip5 FROM zip_centroid c WHERE c.lat BETWEEN @latMin AND @latMax AND c.lon BETWEEN @lonMin AND @lonMax " +
                $"AND {EarthRadiusMiles} * 2 * ASIN(SQRT(POWER(SIN(RADIANS(c.lat - @lat) / 2), 2) " +
                $"+ COS(RADIANS(@lat)) * COS(RADIANS(c.lat)) * POWER(SIN(RADIANS(c.lon - @lon) / 2), 2))) <= @radius)"));
        }
    }

    private static string Format(string template, string alias) => template.Replace("{0}", alias, StringComparison.Ordinal);

    /// <summary>"O'Br" → "O'Br%", with LIKE wildcards in the input escaped (no leading wildcards, CLAUDE.md §7 Stage 3.3).</summary>
    internal static string Prefix(string value)
    {
        var escaped = new StringBuilder(value.Length + 1);
        foreach (var c in value)
        {
            if (c is '\\' or '%' or '_')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.Append('%').ToString();
    }
}
