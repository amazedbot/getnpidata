using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>Providers of one specialty (or of all, when <see cref="Category"/> is empty) in an area and per 100,000 people.</summary>
public sealed record AreaCount(string Category, string Subcategory, int Providers, double? Per100k);

/// <summary>A sub-area (a state of the US, a county of a state) with its counts for the chosen specialty.</summary>
public sealed record AreaChild(string Id, string Name, long? Population, int Providers, double? Per100k, IReadOnlyList<string> Shortages);

/// <summary>A city in a county with how many providers practise there, and roughly where it is.</summary>
public sealed record AreaCity(string City, int Providers, double? Latitude, double? Longitude);

/// <summary>A hospital in a county.</summary>
public sealed record AreaHospital(string Ccn, string Name, string? City, int? Rating, bool? Emergency);

/// <summary>A change recorded for a provider practising in the area.</summary>
public sealed record AreaChange(DateTime DetectedAt, string Npi, string Name, string Type, string? OldValue, string? NewValue);

/// <summary>
/// An area page (docs/PRODUCT_PLAN.md step 1.6): the US, a state or a county, optionally narrowed to a plain specialty: how many
/// providers, per 100,000 people compared with the state and the US, the specialties, the sub-areas, shortage areas, cities,
/// hospitals and recorded changes.
/// </summary>
public sealed record AreaProfile(string Type, string Id, string Name, string? State, long? Population, AreaCount Selected,
    double? ParentPer100k, double? UsPer100k, IReadOnlyList<AreaCount> Specialties, IReadOnlyList<AreaCount> ParentSpecialties,
    IReadOnlyList<AreaChild> Children, IReadOnlyList<CountyShortage> Shortages, IReadOnlyList<AreaCity> Cities,
    IReadOnlyList<AreaHospital> Hospitals, IReadOnlyList<AreaChange> Changes, int ChangeCount, int? PopulationYear)
{
    /// <summary>Providers (of the specialty) whose NPI was issued in the <see cref="AreaProfileService.NewDays"/> days up to the data's as-of date.</summary>
    public int NewProviders { get; init; }

    /// <summary>The first day "new" counts from; null without a data version.</summary>
    public DateOnly? NewSince { get; init; }

    /// <summary>"New York" for a county of NY; "United States" for a state; null for the US.</summary>
    public string? ParentName => Type switch { "county" => State is null ? null : GeographyCatalog.StateName(State), "state" => "the US", _ => null };
}

/// <summary>Reads area pages from the precomputed <c>area_provider_count</c> (migration 079) and the area tables.</summary>
public sealed class AreaProfileService(string connectionString)
{
    public const int NewDays = 90;

    private sealed record CountRow(string Category, string Subcategory, int Providers, int NewProviders);

    private sealed record ChildRow(string Id, string Name, long? Population, int? Providers);

    private sealed record ChangeRow(DateTime DetectedAt, string Npi, string? SortName, string ChangeType, string? OldValue, string? NewValue);

    /// <summary>The page for the US (<paramref name="state"/> null), a state, or a county of it; null when unknown.</summary>
    public async Task<AreaProfile?> GetAsync(string? state, string? countyFips, string? category, string? subcategory, CancellationToken ct)
    {
        var (type, id) = countyFips is not null ? ("county", countyFips) : state is not null ? ("state", state.ToUpperInvariant()) : ("us", "US");
        var specialty = SpecialtyCategories.Default.Find(category);
        var sub = specialty?.Find(subcategory);
        var cat = specialty?.Name ?? "";
        var subName = sub?.Name ?? "";

        // Malformed or unknown areas are refused before any query.
        if ((countyFips is not null && (countyFips.Length != 5 || !countyFips.All(char.IsAsciiDigit)))
            || (state is not null && !GeographyCatalog.AllStateNames.ContainsKey(state)))
        {
            return null;
        }

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);

        string name;
        string? stateCode = null;
        long? population;
        int? year;
        switch (type)
        {
            case "county":
                if (countyFips is not { Length: 5 } || !countyFips.All(char.IsAsciiDigit))
                {
                    return null;
                }

                var county = await connection.QuerySingleOrDefaultAsync<(string Name, string State, long? Population, long? Year)?>(new CommandDefinition(
                    """
                    SELECT c.county_name, c.state, CAST(p.population AS SIGNED), CAST(p.year AS SIGNED) FROM county c LEFT JOIN county_population p ON p.county_fips = c.county_fips
                    WHERE c.county_fips = @id
                    """, new { id }, cancellationToken: ct));
                if (county is not { } c || (state is not null && !string.Equals(c.State, state, StringComparison.OrdinalIgnoreCase)))
                {
                    return null;
                }

                (name, stateCode, population, year) = (c.Name, c.State, c.Population, (int?)c.Year);
                break;
            case "state":
                if (!GeographyCatalog.AllStateNames.TryGetValue(id, out var stateName))
                {
                    return null;
                }

                (name, stateCode) = (stateName, id);
                (population, year) = await PopulationAsync(connection, "WHERE c.state = @id", id, ct);
                break;
            default:
                name = "United States";
                (population, year) = await PopulationAsync(connection, "", id, ct);
                break;
        }

        var counts = (await connection.QueryAsync<CountRow>(new CommandDefinition(
            """
            SELECT category AS Category, subcategory AS Subcategory, providers AS Providers, new_providers AS NewProviders FROM area_provider_count
            WHERE area_type = @type AND area_id = @id
            """, new { type, id }, cancellationToken: ct))).ToList();
        var selected = counts.FirstOrDefault(r => r.Category == cat && r.Subcategory == subName);
        var selectedCount = new AreaCount(cat, subName, selected?.Providers ?? 0, Per100k(selected?.Providers ?? 0, population));
        var specialties = Breakdown(counts, cat, population);

        // The parent: the state of a county, the US of a state.
        double? parentPer100k = null;
        IReadOnlyList<AreaCount> parentSpecialties = [];
        if (type != "us")
        {
            var (parentType, parentId) = type == "county" ? ("state", stateCode!) : ("us", "US");
            var (parentPopulation, _) = await PopulationAsync(connection, parentType == "state" ? "WHERE c.state = @id" : "", parentId, ct);
            var parentCounts = (await connection.QueryAsync<CountRow>(new CommandDefinition(
                """
                SELECT category AS Category, subcategory AS Subcategory, providers AS Providers, new_providers AS NewProviders FROM area_provider_count
                WHERE area_type = @parentType AND area_id = @parentId
                """, new { parentType, parentId }, cancellationToken: ct))).ToList();
            parentPer100k = Per100k(parentCounts.FirstOrDefault(r => r.Category == cat && r.Subcategory == subName)?.Providers ?? 0, parentPopulation);
            parentSpecialties = Breakdown(parentCounts, cat, parentPopulation);
        }

        double? usPer100k = null;
        if (type == "county")
        {
            var (usPopulation, _) = await PopulationAsync(connection, "", "US", ct);
            var us = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT providers FROM area_provider_count WHERE area_type = 'us' AND area_id = 'US' AND category = @cat AND subcategory = @subName",
                new { cat, subName }, cancellationToken: ct));
            usPer100k = Per100k(us ?? 0, usPopulation);
        }
        else if (type == "state")
        {
            usPer100k = parentPer100k;
        }

        var children = type == "county" ? [] : await ChildrenAsync(connection, type, id, cat, subName, ct);
        IReadOnlyList<CountyShortage> shortages = type == "county" ? await ShortagesAsync(connection, id, ct) : [];
        // A county's ZIPs as a list: index ranges on provider_location and provider_search instead of a join MySQL may start from the wrong end.
        List<string> zips = type == "county"
            ? (await connection.QueryAsync<string>(new CommandDefinition("SELECT zip5 FROM zip_county WHERE county_fips = @id", new { id }, cancellationToken: ct))).ToList()
            : [];
        var cities = type == "county" && zips.Count > 0 ? await CitiesAsync(connection, zips, cat, subName, ct) : [];
        var hospitals = type == "county" ? await HospitalsAsync(connection, id, ct) : [];
        var (changes, changeCount) = type == "us" || (type == "county" && zips.Count == 0) ? ([], 0) : await ChangesAsync(connection, type, id, zips, ct);

        var asOf = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition("SELECT as_of_date FROM data_version WHERE id = 1", cancellationToken: ct));
        return new AreaProfile(type, id, name, stateCode, population, selectedCount, parentPer100k, usPer100k, specialties, parentSpecialties,
            children, shortages, cities, hospitals, changes, changeCount, year)
        {
            NewProviders = selected?.NewProviders ?? 0,
            NewSince = asOf is { } d ? DateOnly.FromDateTime(d).AddDays(-NewDays) : null,
        };
    }

    private static double? Per100k(int providers, long? population) =>
        population is > 0 ? providers * 100_000.0 / population.Value : null;

    // The categories (no category chosen) or the chosen category's subcategories, largest first.
    private static List<AreaCount> Breakdown(List<CountRow> counts, string category, long? population) =>
        counts.Where(r => category.Length == 0 ? r.Category.Length > 0 && r.Subcategory.Length == 0 : r.Category == category && r.Subcategory.Length > 0)
            .OrderByDescending(r => r.Providers)
            .Select(r => new AreaCount(r.Category, r.Subcategory, r.Providers, Per100k(r.Providers, population)))
            .ToList();

    private static async Task<(long? Population, int? Year)> PopulationAsync(MySqlConnection connection, string where, string id, CancellationToken ct)
    {
        var row = await connection.QuerySingleAsync<(decimal? Population, long? Year)>(new CommandDefinition(
            $"SELECT SUM(p.population), CAST(MAX(p.year) AS SIGNED) FROM county_population p JOIN county c ON c.county_fips = p.county_fips {where}",
            new { id }, cancellationToken: ct));
        return (row.Population is null ? null : (long)row.Population.Value, (int?)row.Year);
    }

    private static async Task<IReadOnlyList<AreaChild>> ChildrenAsync(MySqlConnection connection, string type, string id, string category,
        string subcategory, CancellationToken ct)
    {
        List<ChildRow> rows;
        if (type == "state")
        {
            rows = (await connection.QueryAsync<ChildRow>(new CommandDefinition(
                """
                SELECT c.county_fips AS Id, c.county_name AS Name, CAST(p.population AS SIGNED) AS Population, a.providers AS Providers
                FROM county c
                LEFT JOIN county_population p ON p.county_fips = c.county_fips
                LEFT JOIN area_provider_count a ON a.area_type = 'county' AND a.area_id = c.county_fips AND a.category = @category AND a.subcategory = @subcategory
                WHERE c.state = @id
                ORDER BY c.county_name
                """, new { id, category, subcategory }, cancellationToken: ct))).ToList();
            var shortages = (await connection.QueryAsync<(string Fips, string Discipline)>(new CommandDefinition(
                "SELECT s.county_fips, s.discipline FROM county_shortage s JOIN county c ON c.county_fips = s.county_fips WHERE c.state = @id",
                new { id }, cancellationToken: ct))).ToLookup(s => s.Fips, s => AreaService.DisciplineName(s.Discipline));
            return rows.Select(r => new AreaChild(r.Id, r.Name, r.Population, r.Providers ?? 0, Per100k(r.Providers ?? 0, r.Population), [.. shortages[r.Id]]))
                .ToList();
        }

        rows = (await connection.QueryAsync<ChildRow>(new CommandDefinition(
            """
            SELECT s.state AS Id, s.state AS Name, CAST(s.population AS SIGNED) AS Population, a.providers AS Providers
            FROM (SELECT c.state, SUM(p.population) AS population FROM county c LEFT JOIN county_population p ON p.county_fips = c.county_fips GROUP BY c.state) s
            LEFT JOIN area_provider_count a ON a.area_type = 'state' AND a.area_id = s.state AND a.category = @category AND a.subcategory = @subcategory
            """, new { category, subcategory }, cancellationToken: ct))).ToList();
        return rows.Where(r => GeographyCatalog.AllStateNames.ContainsKey(r.Id))
            .Select(r => new AreaChild(r.Id, GeographyCatalog.StateName(r.Id), r.Population, r.Providers ?? 0, Per100k(r.Providers ?? 0, r.Population), []))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<IReadOnlyList<CountyShortage>> ShortagesAsync(MySqlConnection connection, string fips, CancellationToken ct) =>
        (await connection.QueryAsync<(string Discipline, sbyte WholeCounty, int HpsaCount, int? MaxScore)>(new CommandDefinition(
            "SELECT discipline, whole_county, hpsa_count, max_score FROM county_shortage WHERE county_fips = @fips ORDER BY FIELD(discipline, 'PC', 'DH', 'MH')",
            new { fips }, cancellationToken: ct)))
        .Select(s => new CountyShortage(s.Discipline, AreaService.DisciplineName(s.Discipline), s.WholeCounty != 0, s.HpsaCount, s.MaxScore))
        .ToList();

    // The ten cities with the most providers (of the specialty), placed at the average centre of their ZIPs.
    private static async Task<IReadOnlyList<AreaCity>> CitiesAsync(MySqlConnection connection, IReadOnlyList<string> zips, string category,
        string subcategory, CancellationToken ct)
    {
        var sql = category.Length == 0
            ? """
              SELECT l.city AS City, COUNT(DISTINCT l.npi) AS Providers, AVG(zc.lat) AS Latitude, AVG(zc.lon) AS Longitude
              FROM provider_location l
              LEFT JOIN zip_centroid zc ON zc.zip5 = l.zip5
              WHERE l.zip5 IN @zips AND l.city IS NOT NULL
              GROUP BY l.city ORDER BY Providers DESC LIMIT 10
              """
            : $"""
              SELECT s.city AS City, COUNT(DISTINCT s.npi) AS Providers, AVG(zc.lat) AS Latitude, AVG(zc.lon) AS Longitude
              FROM provider_search s
              JOIN specialty_code_category m ON m.code = s.taxonomy_code AND m.category = @category {(subcategory.Length > 0 ? "AND m.subcategory = @subcategory" : "")}
              LEFT JOIN zip_centroid zc ON zc.zip5 = s.zip5
              WHERE s.zip5 IN @zips AND s.city <> ''
              GROUP BY s.city ORDER BY Providers DESC LIMIT 10
              """;
        return (await connection.QueryAsync<(string City, long Providers, double? Latitude, double? Longitude)>(new CommandDefinition(
                sql, new { zips, category, subcategory }, cancellationToken: ct)))
            .Select(c => new AreaCity(CityCatalog.TitleCase(c.City), (int)c.Providers, c.Latitude, c.Longitude))
            .ToList();
    }

    private static async Task<IReadOnlyList<AreaHospital>> HospitalsAsync(MySqlConnection connection, string fips, CancellationToken ct) =>
        (await connection.QueryAsync<(string Ccn, string Name, string? City, long? Rating, sbyte? Emergency)>(new CommandDefinition(
            """
            SELECT h.ccn, h.name, h.city, CAST(h.overall_rating AS SIGNED), h.emergency_services
            FROM cms_hospital h
            WHERE LEFT(h.zip, 5) IN (SELECT zip5 FROM zip_county WHERE county_fips = @fips)
            ORDER BY h.name
            """, new { fips }, cancellationToken: ct)))
        .Select(h => new AreaHospital(h.Ccn, CityCatalog.TitleCase(h.Name), h.City is null ? null : CityCatalog.TitleCase(h.City), (int?)h.Rating,
            h.Emergency is null ? null : h.Emergency != 0))
        .ToList();

    // Name, credential, specialty and address changes of providers who practise in the area now, newest first.
    private static async Task<(IReadOnlyList<AreaChange>, int)> ChangesAsync(MySqlConnection connection, string type, string id,
        IReadOnlyList<string> zips, CancellationToken ct)
    {
        var inArea = type == "county"
            ? "EXISTS (SELECT 1 FROM provider_location l WHERE l.npi = c.npi AND l.zip5 IN @zips)"
            : "EXISTS (SELECT 1 FROM provider_location l WHERE l.npi = c.npi AND l.state = @id)";
        const string kinds = "c.change_type IN ('name', 'credential', 'specialty', 'address', 'added')";
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM provider_change c WHERE {kinds} AND {inArea}", new { id, zips }, cancellationToken: ct));
        var rows = await connection.QueryAsync<ChangeRow>(new CommandDefinition(
            $"""
            SELECT c.detected_at AS DetectedAt, c.npi AS Npi, p.sort_name AS SortName, c.change_type AS ChangeType,
                   IF(c.change_type = 'specialty', COALESCE(CONCAT_WS(' – ', o.Classification, o.Specialization), c.old_value), c.old_value) AS OldValue,
                   IF(c.change_type = 'specialty', COALESCE(CONCAT_WS(' – ', n.Classification, n.Specialization), c.new_value), c.new_value) AS NewValue
            FROM provider_change c
            JOIN provider p ON p.npi = c.npi
            LEFT JOIN taxonomy_codes o ON c.change_type = 'specialty' AND o.Taxonomy_Code = c.old_value
            LEFT JOIN taxonomy_codes n ON c.change_type = 'specialty' AND n.Taxonomy_Code = c.new_value
            WHERE {kinds} AND {inArea}
            ORDER BY c.detected_at DESC, c.id DESC LIMIT 10
            """, new { id, zips }, cancellationToken: ct));
        return (rows.Select(r => new AreaChange(r.DetectedAt, r.Npi, r.SortName ?? r.Npi, r.ChangeType, r.OldValue, r.NewValue)).ToList(), (int)total);
    }
}
