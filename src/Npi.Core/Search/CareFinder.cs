using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>A kind of care on the patient path, in everyday words, and the specialties it stands for.</summary>
/// <param name="Key">URL value, e.g. "family".</param>
/// <param name="Label">The button, e.g. "Family doctor".</param>
/// <param name="Plural">The results title, e.g. "Family doctors".</param>
/// <param name="Specialties">(category, subcategory) pairs of <see cref="SpecialtyCategories"/>; subcategory null = the whole category.</param>
/// <param name="EntityType">1 = clinicians, 2 = places (urgent care centres), null = both (a category picked under "Something else").</param>
/// <param name="HealthCenters">Also show HRSA health center sites nearby (primary care, children, dental, mental health: what health centers offer).</param>
public sealed record CareNeed(string Key, string Label, string Plural, IReadOnlyList<(string Category, string? Subcategory)> Specialties, int? EntityType = 1,
    bool HealthCenters = false);

/// <summary>What a patient asked for: the kind of care, the ZIP, and the optional preferences.</summary>
public sealed record CareRequest(CareNeed Need, string Zip, bool TakesMedicare = false, bool VideoVisits = false, string? Gender = null);

/// <summary>One provider near the patient: the nearest of its practice addresses, and a hospital it works with.</summary>
public sealed record CareResult(AreaProvider Nearest, string? Hospital, string? HospitalCcn);

/// <summary>The providers found, nearest first, within <see cref="RadiusMiles"/> of the ZIP.</summary>
/// <param name="More">True when a search stopped at its limit, so there are more than <see cref="Items"/> holds.</param>
public sealed record CareResults(IReadOnlyList<CareResult> Items, int RadiusMiles, bool More, double CenterLat, double CenterLon);

/// <summary>
/// The patient path (docs/PRODUCT_PLAN.md §2.4, step 1.8): providers of a kind of care nearest a ZIP, through the map
/// search (exact practice addresses, nearest first), widening from 2 to 50 miles until enough are found.
/// Excluded providers are always left out; "takes Medicare" also leaves out those who opted out.
/// </summary>
public sealed class CareFinder(string connectionString, SearchService search)
{
    private const double MilesPerDegreeLat = 69.0;

    /// <summary>The radii tried in turn, in miles.</summary>
    public static readonly int[] Radii = [2, 5, 10, 25, 50];

    /// <summary>Fewer results than this within a radius widens the search (three pages of ten). Starting small keeps dense cities fast.</summary>
    public const int Enough = 30;

    /// <summary>The kinds of care offered as buttons, in order. "Something else" picks any category or subcategory instead.</summary>
    public static readonly IReadOnlyList<CareNeed> Needs =
    [
        new("family", "Family doctor", "Family doctors", [("Doctors (MD, DO)", "Family medicine & general practice"), ("Doctors (MD, DO)", "Internal medicine")],
            HealthCenters: true),
        new("children", "Children's doctor", "Children's doctors", [("Doctors (MD, DO)", "Children & teens (pediatrics)")], HealthCenters: true),
        new("dentist", "Dentist", "Dentists", [("Dentists", "General dentists"), ("Dentists", "Children's dentists")], HealthCenters: true),
        new("eye", "Eye doctor", "Eye doctors", [("Eye care", "Optometrists"), ("Eye care", "Ophthalmologists")]),
        new("mental", "Mental health", "Mental health providers", [("Mental & behavioral health", null)], HealthCenters: true),
        new("skin", "Skin doctor", "Skin doctors", [("Doctors (MD, DO)", "Skin (dermatology)")]),
        new("back", "Back & joints", "Back and joint care",
            [("Doctors (MD, DO)", "Bones & joints (orthopedics)"), ("Chiropractic & complementary care", "Chiropractors"), ("Therapy & rehabilitation", "Physical therapists")]),
        new("women", "Women's health", "Women's health doctors", [("Doctors (MD, DO)", "Women's health (OB/GYN)"), ("Midwives & doulas", "Certified nurse midwives")]),
        new("heart", "Heart doctor", "Heart doctors", [("Doctors (MD, DO)", "Heart (cardiology)")]),
        new("urgent", "Urgent care", "Urgent care centers", [("Clinics & centers", "Urgent care & emergency centers")], EntityType: 2),
    ];

    /// <summary>A button's need, or a need for one category ("cat") or subcategory ("cat|sub") of <see cref="SpecialtyCategories"/>.</summary>
    public static CareNeed? Resolve(string? key, string? specialty)
    {
        if (Needs.FirstOrDefault(n => string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase)) is { } need)
        {
            return need;
        }

        if (string.IsNullOrWhiteSpace(specialty))
        {
            return null;
        }

        var parts = specialty.Split('|', 2);
        var category = SpecialtyCategories.Default.Categories.FirstOrDefault(c => c.Name == parts[0]);
        if (category is null)
        {
            return null;
        }

        if (parts.Length == 1 || parts[1].Length == 0)
        {
            return new("other", category.Singular, category.Title, [(category.Name, null)], EntityType: null);
        }

        var sub = category.Subcategories.FirstOrDefault(s => s.Name == parts[1]);
        return sub is null ? null : new("other", sub.Singular, sub.Title, [(category.Name, sub.Name)], EntityType: null);
    }

    /// <summary>Practice addresses a ZIP needs before their middle is used as its centre.</summary>
    public const int MinAddressesForCentre = 5;

    /// <summary>
    /// Where to measure from: the middle of the ZIP's placed practice addresses, else its Census centre; null when it has
    /// neither (PO-box ZIPs). A rural ZIP's Census point can lie far from its town (59101, Billings: 17 miles), while
    /// its practice addresses are in the town.
    /// </summary>
    public async Task<(double Lat, double Lon)?> ZipPointAsync(string zip, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        var addresses = await connection.QuerySingleAsync<(double? Lat, double? Lon, long Count)>(new CommandDefinition(
            """
            SELECT AVG(a.lat), AVG(a.lon), COUNT(*) FROM (
              SELECT DISTINCT m.addr_key, m.lat, m.lon FROM provider_location l
              JOIN provider_map m ON m.npi = l.npi AND m.addr_key = l.addr_key
              WHERE l.zip5 = @zip AND m.approximate = 0) a
            """, new { zip }, cancellationToken: ct));
        if (addresses is { Count: >= MinAddressesForCentre, Lat: { } lat, Lon: { } lon })
        {
            return (lat, lon);
        }

        return await connection.QuerySingleOrDefaultAsync<(double, double)?>(new CommandDefinition(
            "SELECT CAST(lat AS DOUBLE), CAST(lon AS DOUBLE) FROM zip_centroid WHERE zip5 = @zip", new { zip }, cancellationToken: ct));
    }

    /// <summary>The ZIP whose centre is nearest a point ("Use my location"), or null far from any.</summary>
    public async Task<string?> NearestZipAsync(double lat, double lon, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        for (var d = 0.1; d <= 1.6; d *= 2)
        {
            var zip = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                """
                SELECT zip5 FROM zip_centroid
                WHERE lat BETWEEN @lat - @d AND @lat + @d AND lon BETWEEN @lon - @d AND @lon + @d
                ORDER BY POW(lat - @lat, 2) + POW((lon - @lon) * COS(RADIANS(@lat)), 2) LIMIT 1
                """, new { lat, lon, d }, cancellationToken: ct));
            if (zip is not null)
            {
                return zip;
            }
        }

        return null;
    }

    /// <summary>Providers nearest the ZIP's centre; null when the ZIP has no Census centre.</summary>
    public async Task<CareResults?> FindAsync(CareRequest request, CancellationToken ct)
    {
        if (await ZipPointAsync(request.Zip, ct) is not { } center)
        {
            return null;
        }

        var filters = request.Need.Specialties.Select(s => new SearchFilter
        {
            Category = s.Category,
            Subcategory = s.Subcategory,
            EntityType = request.Need.EntityType,
            Gender = request.Need.EntityType == 2 ? null : request.Gender,
            AcceptsAssignment = request.TakesMedicare ? true : null,
            OptedOut = request.TakesMedicare ? false : null,
            Telehealth = request.VideoVisits ? true : null,
            Excluded = false,
            PageSize = 1,
        }).ToList();

        CareResults? results = null;
        foreach (var radius in Radii)
        {
            // Capped at the largest searchable map area (far north, 50 miles is wider than that); the distance check keeps the radius.
            var halfLat = Math.Min(radius / MilesPerDegreeLat, MapBounds.MaxLatSpan / 2 - 0.001);
            var halfLon = Math.Min(radius / (MilesPerDegreeLat * Math.Max(0.2, Math.Cos(center.Lat * Math.PI / 180))), MapBounds.MaxLonSpan / 2 - 0.001);
            var bounds = new MapBounds(center.Lat - halfLat, center.Lon - halfLon, center.Lat + halfLat, center.Lon + halfLon);
            var nearest = new Dictionary<string, AreaProvider>(StringComparer.Ordinal);
            var more = false;
            foreach (var filter in filters)
            {
                var area = await search.SearchAreaAsync(filter, bounds, ct);
                more |= area.Truncated;
                foreach (var item in area.Items.Where(i => i.DistanceMiles <= radius))
                {
                    if (!nearest.TryGetValue(item.Provider.Npi, out var known) || item.DistanceMiles < known.DistanceMiles)
                    {
                        nearest[item.Provider.Npi] = item;
                    }
                }
            }

            var items = nearest.Values.OrderBy(i => i.DistanceMiles).ThenBy(i => i.Provider.Name, StringComparer.Ordinal).ToList();
            results = new CareResults([.. items.Select(i => new CareResult(i, null, null))], radius, more, center.Lat, center.Lon);
            if (items.Count >= Enough)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>The results with, for each clinician, a hospital it works with (Care Compare affiliations; the best rated first).</summary>
    public async Task<IReadOnlyList<CareResult>> WithHospitalsAsync(IReadOnlyList<CareResult> results, CancellationToken ct)
    {
        var npis = results.Select(r => r.Nearest.Provider.Npi).ToList();
        if (npis.Count == 0)
        {
            return results;
        }

        await using var connection = new MySqlConnection(connectionString);
        var hospitals = (await connection.QueryAsync<(string Npi, string Ccn, string Name)>(new CommandDefinition(
                """
                SELECT a.npi, h.ccn, h.name FROM cc_facility_affiliation a JOIN cms_hospital h ON h.ccn = a.ccn
                WHERE a.npi IN @npis ORDER BY a.npi, h.overall_rating IS NULL, h.overall_rating DESC, h.name
                """, new { npis }, cancellationToken: ct)))
            .GroupBy(h => h.Npi).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return [.. results.Select(r => hospitals.TryGetValue(r.Nearest.Provider.Npi, out var h) ? r with { Hospital = h.Name, HospitalCcn = h.Ccn } : r)];
    }
}
