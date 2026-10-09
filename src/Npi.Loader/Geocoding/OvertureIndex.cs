namespace Npi.Loader.Geocoding;

/// <summary>How a practice address was placed by Overture data, best first.</summary>
public static class PointSource
{
    /// <summary>An address point (Overture addresses, from the US DOT National Address Database): the building or parcel.</summary>
    public const string Address = "address";

    /// <summary>The address of an Overture place (e.g. a hospital's "101 Nicolls Rd"): where that place is.</summary>
    public const string Place = "place";

    /// <summary>An address without a house number matched to an Overture place by name ("STONY BROOK UNIVERSITY HOSPITAL").</summary>
    public const string PlaceName = "place_name";
}

/// <summary>Where an address was placed, and what it matched (the address point or the place name).</summary>
public sealed record PointMatch(string Source, double Lat, double Lon, string Matched);

/// <summary>
/// The Overture address points and places of one area, indexed for matching practice addresses (CLAUDE.md §7 Stage 5.5
/// item 10): (1) an address point with the same house number, street and ZIP (or city); (2) the address of a place,
/// for addresses that are mailing addresses of large sites rather than points on a street; (3) for addresses that are
/// only a building name, a place in the same ZIP (or city) whose name contains the address's words.
/// </summary>
public sealed class OvertureIndex
{
    /// <summary>Share of an address's name words a place name must contain, and the least number of shared words.</summary>
    public const double MinNameCoverage = 0.75;

    public const int MinSharedWords = 2;

    private readonly Dictionary<(string, string, string), (double Lat, double Lon)> _pointsByZip = [];
    private readonly Dictionary<(string, string, string), (double Lat, double Lon)> _pointsByCity = [];
    private readonly Dictionary<(string, string, string), List<(double Lat, double Lon, string Name)>> _placesByZip = [];
    private readonly Dictionary<(string, string, string), List<(double Lat, double Lon, string Name)>> _placesByCity = [];
    // Places by ZIP, and by city for places without a ZIP: name matching only compares places in the address's ZIP (or city).
    private readonly Dictionary<string, List<(IReadOnlySet<string> Tokens, double Lat, double Lon, double Confidence, string Name)>> _namesByZip = [];
    private readonly Dictionary<string, List<(IReadOnlySet<string> Tokens, double Lat, double Lon, double Confidence, string Name)>> _namesByCity = [];

    // With candidates given, only points and places they can match are kept: a state has millions of address points
    // but only thousands of practice addresses.
    private readonly HashSet<(string, string)>? _wantedStreets;
    private readonly HashSet<string>? _wantedNameZips;
    private readonly HashSet<string>? _wantedNameCities;

    /// <param name="candidates">The practice addresses to be matched (address line 1, city, ZIP); null keeps everything.</param>
    public OvertureIndex(IEnumerable<(string? Address1, string? City, string Zip5)>? candidates = null)
    {
        if (candidates is null)
        {
            return;
        }

        _wantedStreets = [];
        _wantedNameZips = new(StringComparer.Ordinal);
        _wantedNameCities = new(StringComparer.Ordinal);
        foreach (var (address1, city, zip5) in candidates)
        {
            if (AddressNormalizer.Split(address1) is var (number, street))
            {
                _wantedStreets.Add((number, street));
            }
            else
            {
                _wantedNameZips.Add(zip5);
                if (!string.IsNullOrWhiteSpace(city))
                {
                    _wantedNameCities.Add(city.Trim().ToUpperInvariant());
                }
            }
        }
    }

    public int Points => _pointsByZip.Count + _pointsByCity.Count;

    public int Places { get; private set; }

    public void AddPoint(string? number, string? street, string? zip, string? city, double lat, double lon)
    {
        if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(street))
        {
            return;
        }

        var n = number.Trim().ToUpperInvariant();
        var s = AddressNormalizer.Street(street);
        if (_wantedStreets is not null && !_wantedStreets.Contains((n, s)))
        {
            return;
        }

        if (Zip5(zip) is { } z)
        {
            _pointsByZip.TryAdd((n, s, z), (lat, lon));
        }

        if (!string.IsNullOrWhiteSpace(city))
        {
            _pointsByCity.TryAdd((n, s, city.Trim().ToUpperInvariant()), (lat, lon));
        }
    }

    public void AddPlace(string? name, string? freeformAddress, string? zip, string? city, double lat, double lon, double confidence)
    {
        var z = Zip5(zip) ?? "";
        var c = city?.Trim().ToUpperInvariant() ?? "";
        if (AddressNormalizer.Split(freeformAddress) is var (number, street) && (_wantedStreets is null || _wantedStreets.Contains((number, street))))
        {
            var entry = (lat, lon, name ?? "");
            if (z.Length > 0)
            {
                Add(_placesByZip, (number, street, z), entry);
            }

            if (c.Length > 0)
            {
                Add(_placesByCity, (number, street, c), entry);
            }
        }

        Places++;
        var wantedName = _wantedNameZips is null || _wantedNameZips.Contains(z) || (z.Length == 0 && _wantedNameCities!.Contains(c));
        var tokens = wantedName ? AddressNormalizer.NameTokens(name) : new HashSet<string>();
        if (tokens.Count >= MinSharedWords)
        {
            if (z.Length > 0)
            {
                Add(_namesByZip, z, (tokens, lat, lon, confidence, name!));
            }
            else if (c.Length > 0)
            {
                Add(_namesByCity, c, (tokens, lat, lon, confidence, name!));
            }
        }
    }

    /// <summary>The best placement of a practice address in this area, or null.</summary>
    public PointMatch? Match(string? address1, string? city, string zip5)
    {
        var c = city?.Trim().ToUpperInvariant() ?? "";
        if (AddressNormalizer.Split(address1) is var (number, street))
        {
            if (_pointsByZip.TryGetValue((number, street, zip5), out var p) || _pointsByCity.TryGetValue((number, street, c), out p))
            {
                return new PointMatch(PointSource.Address, p.Lat, p.Lon, $"{number} {street}");
            }

            if (_placesByZip.TryGetValue((number, street, zip5), out var places) || _placesByCity.TryGetValue((number, street, c), out places))
            {
                // Several places often share one address (departments of a hospital): use their middle.
                return new PointMatch(PointSource.Place, Median(places.Select(x => x.Lat)), Median(places.Select(x => x.Lon)), places[0].Name);
            }

            return null; // a house number that no data knows: Census interpolation (or the ZIP centre) is the better guess
        }

        var wanted = AddressNormalizer.NameTokens(address1);
        if (wanted.Count < MinSharedWords)
        {
            return null;
        }

        var best = _namesByZip.GetValueOrDefault(zip5, []).Concat(_namesByCity.GetValueOrDefault(c, []))
            .Select(n => (n, Shared: n.Tokens.Count(wanted.Contains)))
            .Where(x => x.Shared >= MinSharedWords && (double)x.Shared / wanted.Count >= MinNameCoverage)
            .OrderByDescending(x => (double)x.Shared / wanted.Count)
            .ThenByDescending(x => (double)x.Shared / x.n.Tokens.Count) // the closest name: fewest extra words
            .ThenByDescending(x => x.n.Confidence)
            .Select(x => x.n)
            .FirstOrDefault();
        return best.Name is null ? null : new PointMatch(PointSource.PlaceName, best.Lat, best.Lon, best.Name);
    }

    private static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(value);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static string? Zip5(string? zip) =>
        zip is { Length: >= 5 } && zip.AsSpan(0, 5).IndexOfAnyExceptInRange('0', '9') < 0 ? zip[..5] : null;
}
