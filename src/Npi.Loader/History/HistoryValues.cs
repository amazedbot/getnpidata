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

    public HistoryValues() => _setLookup = _sets.GetAlternateLookup<ReadOnlySpan<char>>();

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
