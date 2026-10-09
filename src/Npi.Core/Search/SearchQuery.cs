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
    private readonly List<string> _remainingNpiOnly = []; // dataset flags: need only the NPI, so the count can skip the provider join
    private readonly bool _hasLocationFilter;
    private readonly List<string> _locationTemplates = []; // "{0}" = table alias
    private readonly List<string> _broadWhere = [];          // all conditions on p, for the sort-index plan
    private string? _driverKind;
    private readonly List<string> _relevance = [];          // "similar" names: best matches first (default sort only)

    /// <param name="taxonomyCodes">The codes the Classification/Specialization/TaxonomyCode filters resolved to, or null for no specialty filter.</param>
    /// <param name="radiusCenter">The ZIP centroid for a radius search.</param>
    /// <param name="areaSearch">
    /// A map search (<see cref="AreaSql"/>): a specialty is then looked up by grid cell in provider_map_specialty
    /// (parameter @cells), which is far more selective than every provider of the specialty nationwide.
    /// </param>
    /// <param name="credential">
    /// The standardized credential the Credential filter resolved to (Stage 5.5 item 12): matched exactly against
    /// provider_credential. Null: a prefix match on the raw credential ("M" → MD, MS …).
    /// </param>
    public SearchQuery(SearchFilter filter, IReadOnlyCollection<string>? taxonomyCodes, GeoPoint? radiusCenter, bool areaSearch = false, string? credential = null)
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

        // Names (Stage 3.3 prefix match; Stage 5.5 item 9 "similar": people also by sound, organizations by words anywhere).
        var similar = filter.NameMatch == NameSearch.Similar;
        var relevance = new List<string>();
        void PersonName(string? value, string column, string parameter)
        {
            if (value is null)
            {
                return;
            }

            Parameters.Add(parameter, Prefix(value));
            if (similar && NameSearch.CanSoundLike(value))
            {
                Parameters.Add(parameter + "Sound", value);
                providerTemplates.Add(("name",
                    $"({{0}}.{column}_name LIKE @{parameter} OR {{0}}.{column}_phonetic = {NameSearch.PhoneticSql("@" + parameter + "Sound")})"));
                relevance.Add($"(p.{column}_name LIKE @{parameter}) DESC"); // what was typed, before what sounds like it
            }
            else
            {
                providerTemplates.Add(("name", $"{{0}}.{column}_name LIKE @{parameter}"));
            }
        }

        PersonName(filter.LastName, "last", "lastName");
        PersonName(filter.FirstName, "first", "firstName");

        string? orgDriver = null;
        if (filter.OrgName is not null)
        {
            Parameters.Add("orgName", Prefix(filter.OrgName));
            if (similar && NameSearch.OrganizationWords(filter.OrgName) is { } words)
            {
                // Any legal or other name containing every word (FULLTEXT), or the legal name starting with the text.
                // As the driver it also scores each NPI: a prefix match first, then FULLTEXT relevance.
                Parameters.Add("orgWords", words);
                const string match = "MATCH(o.name) AGAINST (@orgWords IN BOOLEAN MODE)";
                providerTemplates.Add(("org", $"({{0}}.org_name LIKE @orgName OR {{0}}.npi IN (SELECT o.npi FROM provider_org_name o WHERE {match}))"));
                orgDriver = $"SELECT x.npi, MAX(x.score) AS score FROM (SELECT o.npi, {match} AS score FROM provider_org_name o WHERE {match} " +
                            "UNION ALL SELECT d.npi, 1000 AS score FROM provider d WHERE d.org_name LIKE @orgName) x GROUP BY x.npi";
            }
            else
            {
                providerTemplates.Add(("name", "{0}.org_name LIKE @orgName"));
            }
        }

        if (credential is not null)
        {
            Parameters.Add("credentialExact", credential);
            providerTemplates.Add(("credential", "EXISTS (SELECT 1 FROM provider_credential pc WHERE pc.npi = {0}.npi AND pc.credential = @credentialExact)"));
        }
        else if (filter.Credential is not null)
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

        // Dataset flags (Stage 5.5): true = EXISTS, false = NOT EXISTS, on the shared fragments.
        foreach (var (value, exists) in new[]
                 {
                     (filter.Excluded, EnrichmentSql.Excluded), (filter.OptedOut, EnrichmentSql.OptedOut), (filter.OrderRefer, EnrichmentSql.OrderRefer),
                     (filter.AcceptsAssignment, EnrichmentSql.AcceptsAssignment), (filter.Telehealth, EnrichmentSql.Telehealth),
                     (filter.MedicareActive, EnrichmentSql.BilledMedicare),
                 })
        {
            if (value is not null)
            {
                providerTemplates.Add(("flag", value.Value ? exists : "NOT " + exists));
            }
        }

        if (filter.MinYears is not null)
        {
            Parameters.Add("maxGraduationYear", DateTime.UtcNow.Year - filter.MinYears.Value);
            providerTemplates.Add(("flag", EnrichmentSql.MinYears));
        }

        // New / recently updated (Stage 5.5 item 11), counted back from today (UTC).
        var dates = new List<string>();
        if (filter.NewWithinDays is { } newDays)
        {
            Parameters.Add("enumeratedSince", Since(newDays));
            providerTemplates.Add(("date", "{0}.enumeration_date >= @enumeratedSince"));
            dates.Add("d.enumeration_date >= @enumeratedSince");
        }

        if (filter.UpdatedWithinDays is { } updatedDays)
        {
            Parameters.Add("updatedSince", Since(updatedDays));
            providerTemplates.Add(("date", "{0}.last_update_date >= @updatedSince"));
            dates.Add("d.last_update_date >= @updatedSince");
        }

        if (taxonomyCodes is not null)
        {
            // An empty list can't match anything; keep the query valid instead of emitting "IN ()".
            Parameters.Add("taxonomyCodes", taxonomyCodes.Count > 0 ? taxonomyCodes.ToArray() : ["-"]);
        }

        AddLocationFilters(filter, radiusCenter);
        _hasLocationFilter = _locationTemplates.Count > 0;

        // For a possibly huge search (no specialty, name, NPI or credential), the sort-index plan needs
        // every condition on p itself.
        _broadWhere.AddRange(providerTemplates.Select(t => Format(t.Template, "p")));
        if (_hasLocationFilter)
        {
            // NO_SEMIJOIN: check each provider as the sort index is walked. Otherwise MySQL materializes
            // every matching NPI (1.2M for CA) and sorts them, which is what this plan exists to avoid.
            _broadWhere.Add($"EXISTS (SELECT /*+ NO_SEMIJOIN() */ 1 FROM provider_location l WHERE l.npi = p.npi AND {Location("l")})");
        }

        // Positive Care Compare filters, on alias cc (cc_clinician). They narrow a location driver, or drive on their own.
        var careCompare = new List<string>();
        if (filter.AcceptsAssignment == true)
        {
            careCompare.Add("cc.accepts_assignment = 1");
        }

        if (filter.Telehealth == true)
        {
            careCompare.Add("cc.telehealth = 1");
        }

        if (filter.MinYears is not null)
        {
            careCompare.Add("cc.graduation_year <= @maxGraduationYear");
        }

        // When the dates drive (measured on full data, Oct 2026):
        // - no location: always; the search counts first (MayBeBroad) and walks a sort index when the window is long
        //   (a year of enumerations: 650k matches, 0.4 s);
        // - a whole state: up to a year of enumerations or 120 days of updates (NY + a year: 6 s, against 13 s from
        //   the state's locations; CA 6 s against 25 s);
        // - a county, city or ZIP: never; their locations are few (Suffolk + any window ≤ 0.8 s);
        // - the map: up to 90 days of enumerations or 31 of updates; a year of new providers in Manhattan took 9 s
        //   from the candidates and 1 s from the points in view.
        var hasDate = filter.NewWithinDays is not null || filter.UpdatedWithinDays is not null;
        var narrowLocation = filter.CountyFips is not null || filter.City is not null || filter.Zip5 is not null;
        var selectiveDate = areaSearch
            ? filter.NewWithinDays <= MapSelectiveNewDays || filter.UpdatedWithinDays <= MapSelectiveUpdatedDays
            : hasDate && (!_hasLocationFilter
                          || (!narrowLocation && (filter.NewWithinDays <= StateSelectiveNewDays || filter.UpdatedWithinDays <= StateSelectiveUpdatedDays)));

        // Pick the driver: the filter expected to match the fewest providers.
        var drivenBy = new HashSet<string>();
        if (filter.Npi is not null)
        {
            _driver = "SELECT @npi AS npi";
            _driverKind = "npi";
        }
        else if (filter.Excluded == true || filter.OptedOut == true)
        {
            // A few thousand NPIs at most: start there and check everything else per candidate.
            _driver = filter.Excluded == true ? EnrichmentSql.ExcludedNpis : EnrichmentSql.OptedOutNpis;
            _driverKind = "flag";
        }
        else if (taxonomyCodes is not null && areaSearch)
        {
            _driver = "SELECT DISTINCT s.npi FROM provider_map_specialty s WHERE s.taxonomy_code IN @taxonomyCodes AND s.cell IN @cells";
            _driverKind = "taxonomy";
            drivenBy.Add("taxonomy");
        }
        else if (taxonomyCodes is not null && _hasLocationFilter)
        {
            _driver = $"SELECT DISTINCT s.npi FROM provider_search s WHERE s.taxonomy_code IN @taxonomyCodes AND {Location("s")}";
            drivenBy.Add("taxonomy");
            drivenBy.Add("location");
            _driverKind = "search";
        }
        else if (taxonomyCodes is not null)
        {
            _driver = "SELECT DISTINCT t.npi FROM provider_taxonomy t WHERE t.taxonomy_code IN @taxonomyCodes";
            _driverKind = "taxonomy";
            drivenBy.Add("taxonomy");
        }
        else if (credential is not null && areaSearch)
        {
            // A map search for a credential reads only the grid cells in view, as for a specialty.
            _driver = "SELECT DISTINCT c.npi FROM provider_map_credential c WHERE c.credential = @credentialExact AND c.cell IN @cells";
            drivenBy.Add("credential");
            _driverKind = "credential";
        }
        else if (credential is not null && _hasLocationFilter && !areaSearch)
        {
            // Like specialty + location: one index range on (credential, state, city, zip5).
            _driver = $"SELECT DISTINCT s.npi FROM credential_search s WHERE s.credential = @credentialExact AND {Location("s")}";
            drivenBy.Add("credential");
            drivenBy.Add("location");
            _driverKind = "search";
        }
        else if (selectiveDate)
        {
            // Both date conditions, when both are set: the provider rows are read anyway. DISTINCT keeps MySQL from
            // merging the derived table into the outer query, which would void JOIN_ORDER (a map search then scanned
            // every point in the area: 5 s instead of 0.1 s).
            _driver = "SELECT DISTINCT d.npi FROM provider d WHERE " + string.Join(" AND ", dates);
            drivenBy.Add("date");
            _driverKind = "date";
        }
        else if (orgDriver is not null)
        {
            _driver = orgDriver;
            drivenBy.Add("org");
            _driverKind = "org";
        }
        else if (providerTemplates.Any(t => t.Kind == "name"))
        {
            _driver = "SELECT d.npi FROM provider d WHERE " + string.Join(" AND ", providerTemplates.Where(t => t.Kind == "name").Select(t => Format(t.Template, "d")));
            drivenBy.Add("name");
            _driverKind = "name";
        }
        else if (_hasLocationFilter)
        {
            // With Care Compare filters, join them into the location scan: checking them afterwards means a
            // provider join per location match (950k for NY) instead of one cc_clinician lookup each.
            _driver = careCompare.Count == 0
                ? $"SELECT DISTINCT l.npi FROM provider_location l WHERE {Location("l")}"
                : $"SELECT DISTINCT l.npi FROM provider_location l JOIN cc_clinician cc ON cc.npi = l.npi WHERE {Location("l")} AND {string.Join(" AND ", careCompare)}";
            _driverKind = "location";
            drivenBy.Add("location");
        }
        else if (providerTemplates.Any(t => t.Kind == "credential"))
        {
            _driver = credential is not null
                ? "SELECT pc.npi FROM provider_credential pc WHERE pc.credential = @credentialExact"
                : "SELECT d.npi FROM provider d WHERE " + Format(providerTemplates.First(t => t.Kind == "credential").Template, "d");
            drivenBy.Add("credential");
            _driverKind = "credential";
        }
        else if (careCompare.Count > 0)
        {
            // At most the 1.6M Care Compare clinicians, instead of every provider.
            _driver = "SELECT cc.npi FROM cc_clinician cc WHERE " + string.Join(" AND ", careCompare);
            _driverKind = "carecompare";
        }

        _remaining.AddRange(providerTemplates.Where(t => !drivenBy.Contains(t.Kind)).Select(t => Format(t.Template, "p")));
        // NPI-level checks (dataset flags, the standardized credential) don't need the provider row, so a count can skip that join.
        _remainingNpiOnly.AddRange(providerTemplates.Where(t => (t.Kind == "flag" || (t.Kind == "credential" && credential is not null)) && !drivenBy.Contains(t.Kind))
            .Select(t => t.Template));
        if (taxonomyCodes is not null && !drivenBy.Contains("taxonomy"))
        {
            _remaining.Add("EXISTS (SELECT 1 FROM provider_taxonomy t WHERE t.npi = p.npi AND t.taxonomy_code IN @taxonomyCodes)");
        }

        if (_hasLocationFilter && !drivenBy.Contains("location"))
        {
            _remaining.Add($"EXISTS (SELECT 1 FROM provider_location l WHERE l.npi = p.npi AND {Location("l")})");
        }

        if (similar && filter.Sort is null)
        {
            // Best matches first, then the normal name order. Only the default sort: an explicit sort is honoured as asked.
            if (orgDriver is not null)
            {
                relevance.Insert(0, _driverKind == "org" ? "c.score DESC" : "(p.org_name LIKE @orgName) DESC");
            }

            _relevance.AddRange(relevance);
        }

        Parameters.Add("take", filter.PageSize);
        Parameters.Add("skip", (filter.Page - 1) * filter.PageSize);
    }

    public SearchFilter Filter { get; }

    /// <summary>
    /// True when the driver is a small candidate set (an NPI, the exclusion/opt-out lists, a specialty, a name or a
    /// credential). The map search then starts from the candidates and checks whether each is inside the area;
    /// otherwise it starts from the points in the area (spatial index) and checks the filters.
    /// </summary>
    public bool HasSelectiveDriver => _driverKind is "npi" or "flag" or "search" or "taxonomy" or "name" or "org" or "credential" or "date";

    /// <summary>With a state (and no narrower location), "new within" windows up to this many days drive (see the constructor).</summary>
    public const int StateSelectiveNewDays = 400;

    /// <summary>The same for "updated within": updates are far more frequent than enumerations.</summary>
    public const int StateSelectiveUpdatedDays = 120;

    /// <summary>Map searches: "new within" windows up to this many days drive.</summary>
    public const int MapSelectiveNewDays = 90;

    /// <summary>Map searches: "updated within" windows up to this many days drive.</summary>
    public const int MapSelectiveUpdatedDays = 31;

    private static DateTime Since(int days) => DateTime.UtcNow.Date.AddDays(-days);

    /// <summary>
    /// Map search (Stage 5.5 item 10): the provider_map rows (alias m) inside the area <paramref name="boxParameter"/>
    /// (a WKT polygon parameter) that match the filter. The caller appends ORDER BY / LIMIT.
    /// </summary>
    public string AreaSql(string boxParameter)
    {
        const string select = "m.npi AS Npi, m.addr_key AS AddrKey, m.lat AS Lat, m.lon AS Lon, m.approximate AS Approximate, m.source AS Source";
        var joinProvider = _remaining.Count > 0 ? " JOIN provider p ON p.npi = m.npi" : "";
        var remaining = _remaining.Count > 0 ? " AND " + string.Join(" AND ", _remaining) : "";
        var inArea = $"MBRContains(ST_GeomFromText({boxParameter}, 0), m.pt)";
        return HasSelectiveDriver
            ? $"SELECT /*+ JOIN_ORDER(c, m{(joinProvider.Length > 0 ? ", p" : "")}) */ {select} FROM ({_driver}) c JOIN provider_map m ON m.npi = c.npi{joinProvider} WHERE {inArea}{remaining}"
            : $"SELECT {select} FROM provider_map m{joinProvider} WHERE {inArea}{(_driver is null ? "" : $" AND m.npi IN ({_driver})")}{remaining}";
    }

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

    /// <summary>
    /// The match count. A location-only search with nothing else to check counts straight from the
    /// location index: every provider_location row belongs to a projected provider, so joining
    /// provider (a million random lookups for a whole state) adds nothing.
    /// </summary>
    public string CountSql
    {
        get
        {
            if (_driverKind == "location" && _remaining.Count == 0)
            {
                return $"SELECT COUNT(DISTINCT l.npi) FROM provider_location l WHERE {Location("l")}";
            }

            // Only NPI-level checks left (dataset flags): count the candidates without joining provider,
            // which costs a random lookup per candidate (950k for a whole state). Only for drivers drawn from
            // projection tables; the NPI, flag and Care Compare drivers can list NPIs that aren't projected.
            if (_driverKind is "location" or "search" or "taxonomy" or "name" or "org" or "credential" or "date" && _remaining.Count == _remainingNpiOnly.Count)
            {
                var flags = _remainingNpiOnly.Count > 0 ? " WHERE " + string.Join(" AND ", _remainingNpiOnly.Select(t => Format(t, "c"))) : "";
                return $"SELECT COUNT(*) FROM ({_driver}) c{flags}";
            }

            return $"SELECT {Hint}COUNT(*) {From}";
        }
    }

    /// <summary>
    /// True when the search can match a large share of all providers: only location and/or
    /// gender/entity filters (§11 item 6). Count first; above <see cref="SearchFilter.BroadSearchThreshold"/>
    /// use <see cref="IndexOrderPageSql"/>.
    /// </summary>
    public bool MayBeBroad => _driverKind is null or "location" || (_driverKind == "date" && !_hasLocationFilter);

    /// <summary>
    /// The page read in sort-index order (name or NPI sorts only, else null). Fast when matches are
    /// dense: walking the index finds 50 of them quickly. Slow when they are sparse, hence the count first.
    /// </summary>
    public string? IndexOrderPageSql
    {
        get
        {
            var index = _sort.Key switch
            {
                SearchSort.Name => "ix_provider_sort",
                SearchSort.Npi => "PRIMARY",
                SearchSort.Enumeration => "ix_provider_enumeration",
                SearchSort.LastUpdate => "ix_provider_last_update",
                _ => null,
            };
            if (index is null || _relevance.Count > 0)
            {
                return null;
            }

            var where = _broadWhere.Count > 0 ? " WHERE " + string.Join(" AND ", _broadWhere) : "";
            return $"SELECT p.npi FROM provider p FORCE INDEX FOR ORDER BY ({index}){where} ORDER BY {OrderBy} LIMIT @take OFFSET @skip";
        }
    }

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
            var relevance = _relevance.Count > 0 ? string.Join(", ", _relevance) + ", " : "";
            return relevance + _sort.Key switch
            {
                SearchSort.Name => $"p.sort_name {dir}, p.npi",
                SearchSort.Npi => $"p.npi {dir}",
                SearchSort.Credential => $"p.credential_key {dir}, p.sort_name, p.npi",
                // The NPI follows the direction too, so the date indexes (date, npi) can give this order (IndexOrderPageSql).
                SearchSort.LastUpdate => $"p.last_update_date {dir}, p.npi {dir}",
                SearchSort.Enumeration => $"p.enumeration_date {dir}, p.npi {dir}",
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

        if (filter.Shortage is not null)
        {
            Parameters.Add("shortage", AreaService.Disciplines[filter.Shortage]);
            _locationTemplates.Add("{0}.zip5 IN (SELECT z.zip5 FROM zip_county z JOIN county_shortage cs ON cs.county_fips = z.county_fips WHERE cs.discipline = @shortage)");
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
