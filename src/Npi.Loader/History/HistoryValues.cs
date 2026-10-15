namespace Npi.Loader.History;

/// <summary>
/// The value tables behind <see cref="NpiVersion"/>: states, countries and taxonomy codes as small numbers, and the distinct
/// sets of an NPI's taxonomy codes (persisted in npi_history_taxonomy_set, so a set keeps its id across runs). Lookups by
/// span, so reading a file allocates no string per row.
/// </summary>
public sealed class HistoryValues
{
    public StringTable States { get; } = new();

    public StringTable Countries { get; } = new();

    public StringTable Codes { get; } = new();

    // A set's key: its code numbers, sorted, one char each (exact, and looked up without allocating).
    private readonly Dictionary<string, int> _sets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _setLookup;
    private readonly List<string?> _setCodes = [null];
    private readonly List<int> _newSets = [];

    public HistoryValues()
    {
        _setLookup = _sets.GetAlternateLookup<ReadOnlySpan<char>>();
        _locationLookup = _locationSets.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>The id of the set of these codes (numbers from <see cref="Codes"/>; any order, repeats allowed), adding it when new; 0 for none.</summary>
    public int Set(Span<char> codeNumbers)
    {
        if (codeNumbers.Length == 0)
        {
            return 0;
        }

        codeNumbers.Sort();
        var distinct = 1;
        for (var i = 1; i < codeNumbers.Length; i++)
        {
            if (codeNumbers[i] != codeNumbers[distinct - 1])
            {
                codeNumbers[distinct++] = codeNumbers[i];
            }
        }

        var key = codeNumbers[..distinct];
        if (_setLookup.TryGetValue(key, out var id))
        {
            return id;
        }

        id = _setCodes.Count;
        var codes = new string[distinct];
        for (var i = 0; i < distinct; i++)
        {
            codes[i] = Codes[key[i]]!;
        }

        Array.Sort(codes, StringComparer.Ordinal);
        _sets.Add(new string(key), id);
        _setCodes.Add(string.Join(' ', codes));
        _newSets.Add(id);
        return id;
    }

    /// <summary>A stored set (its id from the database, its codes space-separated), when resuming.</summary>
    public void AddStoredSet(int id, string codes)
    {
        var numbers = codes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(c => (char)Codes.Add(c)).ToArray();
        Array.Sort(numbers);
        while (_setCodes.Count <= id)
        {
            _setCodes.Add(null);
        }

        _setCodes[id] = string.Join(' ', codes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        _sets[new string(numbers)] = id;
    }

    /// <summary>The codes of a set, space-separated and sorted; null for 0.</summary>
    public string? SetCodes(int id) => id > 0 && id < _setCodes.Count ? _setCodes[id] : null;

    /// <summary>The sets added since the last <see cref="TakeNewSets"/> (to write with the period's versions).</summary>
    public IReadOnlyList<(int Id, string Codes)> TakeNewSets()
    {
        var sets = _newSets.Select(id => (id, _setCodes[id]!)).ToList();
        _newSets.Clear();
        return sets;
    }

    public int SetCount => _sets.Count;

    // A location set's key: per (state, ZIP) pair, sorted and distinct, three chars: the state's number, then the ZIP (+1) in two.
    private readonly Dictionary<string, int> _locationSets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _locationLookup;
    private readonly List<(ushort State, int Zip)[]?> _locationPairs = [null];
    private readonly List<int> _newLocationSets = [];
    private char[] _locationKey = new char[48];

    /// <summary>
    /// The id of the set of these secondary locations ((state number, ZIP + 1 or 0) pairs, sorted by state then ZIP; repeats
    /// allowed), adding it when new; 0 for none.
    /// </summary>
    public int LocationSet(IReadOnlyList<(ushort State, int Zip)> sortedPairs)
    {
        if (sortedPairs.Count == 0)
        {
            return 0;
        }

        if (_locationKey.Length < sortedPairs.Count * 3)
        {
            _locationKey = new char[sortedPairs.Count * 3];
        }

        var length = 0;
        var distinct = new List<(ushort, int)>(sortedPairs.Count);
        for (var i = 0; i < sortedPairs.Count; i++)
        {
            var (state, zip) = sortedPairs[i];
            if (i > 0 && sortedPairs[i - 1] == (state, zip))
            {
                continue;
            }

            distinct.Add((state, zip));
            _locationKey[length++] = (char)state;
            _locationKey[length++] = (char)(zip >> 16);
            _locationKey[length++] = (char)(zip & 0xFFFF);
        }

        var key = _locationKey.AsSpan(0, length);
        if (_locationLookup.TryGetValue(key, out var id))
        {
            return id;
        }

        id = _locationPairs.Count;
        _locationSets.Add(new string(key), id);
        _locationPairs.Add([.. distinct]);
        _newLocationSets.Add(id);
        return id;
    }

    /// <summary>A stored location set (its id and (state, ZIP or "") rows from the database), when resuming.</summary>
    public void AddStoredLocationSet(int id, IEnumerable<(string State, string Zip)> rows)
    {
        var pairs = rows
            .Select(r => (State: States.Add(r.State), Zip: int.TryParse(r.Zip, out var zip) ? zip + 1 : 0))
            .Distinct()
            .OrderBy(p => p.State).ThenBy(p => p.Zip)
            .ToArray();
        while (_locationPairs.Count <= id)
        {
            _locationPairs.Add(null);
        }

        // Registered under its stored id, and not written again.
        var key = new string([.. pairs.SelectMany(p => new[] { (char)p.State, (char)(p.Zip >> 16), (char)(p.Zip & 0xFFFF) })]);
        _locationSets[key] = id;
        _locationPairs[id] = pairs;
    }

    /// <summary>The (state, ZIP or "") pairs of a location set; empty for 0.</summary>
    public IEnumerable<(string State, string Zip)> LocationPairs(int id) =>
        id > 0 && id < _locationPairs.Count && _locationPairs[id] is { } pairs
            ? pairs.Select(p => (States[p.State]!, p.Zip == 0 ? "" : (p.Zip - 1).ToString("D5", System.Globalization.CultureInfo.InvariantCulture)))
            : [];

    /// <summary>The location sets added since the last call (to write with the period's versions).</summary>
    public IReadOnlyList<int> TakeNewLocationSets()
    {
        var sets = _newLocationSets.ToList();
        _newLocationSets.Clear();
        return sets;
    }

    public int LocationSetCount => _locationSets.Count;

    /// <summary>The (state, ZIP) rows of every location set (what npi_history_location_set holds).</summary>
    public long LocationPairCount => _locationPairs.Sum(p => (long)(p?.Length ?? 0));

    /// <summary>The number of pairs in one location set.</summary>
    public int LocationPairCountOf(int id) => id > 0 && id < _locationPairs.Count ? _locationPairs[id]?.Length ?? 0 : 0;
}

/// <summary>Strings numbered from 1 (0 = none), looked up by span. At most 65,535.</summary>
public sealed class StringTable
{
    private readonly Dictionary<string, ushort> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ushort>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly List<string?> _values = [null];

    public StringTable() => _lookup = _ids.GetAlternateLookup<ReadOnlySpan<char>>();

    public int Count => _values.Count - 1;

    public string? this[int id] => id > 0 && id < _values.Count ? _values[id] : null;

    /// <summary>The number of <paramref name="value"/>, added when new; 0 for an empty value.</summary>
    public ushort Add(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return 0;
        }

        if (_lookup.TryGetValue(value, out var id))
        {
            return id;
        }

        if (_values.Count > ushort.MaxValue)
        {
            throw new InvalidDataException($"More than {ushort.MaxValue:N0} distinct values in one history column.");
        }

        id = (ushort)_values.Count;
        var text = value.ToString();
        _ids.Add(text, id);
        _values.Add(text);
        return id;
    }

    public ushort Add(string? value) => value is null ? (ushort)0 : Add(value.AsSpan());
}
