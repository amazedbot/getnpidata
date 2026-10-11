using System.Globalization;
using System.Text;

namespace Npi.Core.Search;

/// <summary>
/// A specialty a phrase stands for: a plain category, optionally narrowed to a subcategory (<see cref="SpecialtyCategories"/>), or
/// else a NUCC classification, optionally narrowed to one of its specializations.
/// </summary>
public sealed record SpecialtyTerm(string? Classification, string? Specialization)
{
    public string? Category { get; init; }

    public string? Subcategory { get; init; }

    /// <summary>What a list of them is called ("Cardiologists"); set for plain categories.</summary>
    public string? Title { get; init; }

    public string Label => Title ?? (Specialization is null ? Classification ?? "" : $"{Classification} – {Specialization}");

    public static SpecialtyTerm For(SpecialtyCategory category, SpecialtySubcategory? subcategory = null) =>
        new(null, null) { Category = category.Name, Subcategory = subcategory?.Name, Title = subcategory?.Title ?? category.Title };
}

public sealed record CountyTerm(string Fips, string State, string Name, long Population);

/// <summary>A USPS city name in a state, with how many ZIPs USPS gives it (a size proxy to pick among same-named cities).</summary>
public sealed record CityTerm(string City, string State, long Zips);

/// <summary>What the search box understood one piece of a query as, and the query parameters that carry it.</summary>
public sealed record QueryPart(string Label, string Value, IReadOnlyList<string> Parameters);

public enum QueryKind
{
    /// <summary>Nothing searchable.</summary>
    Empty,

    /// <summary>An NPI: open its page.</summary>
    Npi,

    /// <summary>A specialty and/or a place (and maybe a name): a list.</summary>
    List,

    /// <summary>Only a name: people and organizations.</summary>
    Names,
}

public sealed record QueryInterpretation(QueryKind Kind, SearchFilter Filter, IReadOnlyList<QueryPart> Parts, string? NameText, string? Npi,
    IReadOnlyList<string> Notes)
{
    /// <summary>"near me" was typed: only the map can ask for the visitor's location.</summary>
    public bool NearMe { get; init; }
}

/// <summary>
/// The words the search box understands (docs/PRODUCT_PLAN.md step 1.3): the plain specialty categories (names, titles,
/// everyday words such as "heart doctor"; <see cref="SpecialtyCategories"/>), then NUCC taxonomy names, display names and
/// plurals; states, counties and USPS city names.
/// </summary>
public sealed class SearchVocabulary
{
    /// <summary>Parenthesized words in subcategory names that are too vague on their own ("Mental health (psychiatric)").</summary>
    private static readonly HashSet<string> VagueTerms = new(StringComparer.Ordinal) { "psychiatric" };

    private static readonly string[] CountySuffixes = ["county", "parish", "borough", "census area", "municipality", "city and borough", "municipio"];

    public SearchVocabulary(IEnumerable<TaxonomyEntry> taxonomy, IEnumerable<CountyTerm> counties, IEnumerable<CityTerm> cities,
        IReadOnlyDictionary<string, string> stateNames)
    {
        var entries = taxonomy.Where(e => e.Classification is not null).ToList();
        var classifications = entries.Select(e => e.Classification!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        void Add(string phrase, SpecialtyTerm term, bool overwrite)
        {
            foreach (var form in Forms(QueryText.Normalize(phrase)))
            {
                if (form.Length > 1 && (overwrite || !Specialties.ContainsKey(form)))
                {
                    Specialties[form] = term;
                }
            }
        }

        // Specializations first (only multi-word ones or display names: single words like "Family" are too vague), then
        // classifications and everyday words, which win.
        var specializationCounts = entries.Where(e => e.Specialization is not null)
            .GroupBy(e => QueryText.Normalize(e.Specialization!)).ToDictionary(g => g.Key, g => g.Select(e => e.Classification).Distinct().Count());
        foreach (var e in entries.Where(e => e.Specialization is not null))
        {
            var key = QueryText.Normalize(e.Specialization!);
            if (key.Contains(' ', StringComparison.Ordinal) && specializationCounts[key] == 1)
            {
                Add(e.Specialization!, new SpecialtyTerm(e.Classification!, e.Specialization), overwrite: false);
            }

            if (e.DisplayName is { Length: > 0 } display)
            {
                Add(display, new SpecialtyTerm(e.Classification!, e.Specialization), overwrite: false);
            }
        }

        foreach (var c in classifications)
        {
            Add(c, new SpecialtyTerm(entries.First(e => string.Equals(e.Classification, c, StringComparison.OrdinalIgnoreCase)).Classification!, null), overwrite: true);
        }

        // The plain categories (owner, 2026-10-11) win over NUCC's names. Within them, first come first served: explicit
        // synonyms ("heart doctor"), then subcategory titles, singulars and the term in parentheses ("cardiologists",
        // "cardiologist", "cardiology"), then category names and titles. "Other …" subcategories and the Other category add none.
        var plain = new Dictionary<string, SpecialtyTerm>(StringComparer.Ordinal);
        void AddPlain(string phrase, SpecialtyTerm term)
        {
            foreach (var form in Forms(QueryText.Normalize(phrase)))
            {
                if (form.Length > 1)
                {
                    plain.TryAdd(form, term);
                }
            }
        }

        var categories = SpecialtyCategories.Default.Categories.Where(c => c.Name != SpecialtyCategories.FallbackCategory).ToList();
        foreach (var category in categories)
        {
            category.Synonyms.ToList().ForEach(x => AddPlain(x, SpecialtyTerm.For(category)));
            foreach (var sub in category.Subcategories)
            {
                sub.Synonyms.ToList().ForEach(x => AddPlain(x, SpecialtyTerm.For(category, sub)));
            }
        }

        foreach (var category in categories)
        {
            foreach (var sub in category.Subcategories.Where(s => !s.Name.StartsWith("Other", StringComparison.Ordinal)))
            {
                AddPlain(sub.Title, SpecialtyTerm.For(category, sub));
                AddPlain(sub.Singular, SpecialtyTerm.For(category, sub));
                if (Parenthesized(sub.Name) is { } term)
                {
                    AddPlain(term, SpecialtyTerm.For(category, sub));
                }
            }
        }

        foreach (var category in categories)
        {
            AddPlain(category.Name, SpecialtyTerm.For(category));
            AddPlain(category.Title, SpecialtyTerm.For(category));
            AddPlain(category.Singular, SpecialtyTerm.For(category));
            if (Parenthesized(category.Name) is { } term)
            {
                AddPlain(term, SpecialtyTerm.For(category));
            }
        }

        foreach (var (phrase, term) in plain)
        {
            Specialties[phrase] = term;
        }

        foreach (var county in counties)
        {
            var key = CountyKey(county.Name);
            foreach (var variant in SaintVariants(key))
            {
                (CountiesByName.TryGetValue(variant, out var list) ? list : CountiesByName[variant] = []).Add(county);
            }
        }

        foreach (var city in cities)
        {
            foreach (var variant in SaintVariants(QueryText.Normalize(city.City)))
            {
                (CitiesByName.TryGetValue(variant, out var list) ? list : CitiesByName[variant] = []).Add(city);
            }
        }

        foreach (var (code, name) in stateNames)
        {
            StateCodes.Add(code.ToUpperInvariant());
            StatesByName[QueryText.Normalize(name)] = code.ToUpperInvariant();
            StateNames[code.ToUpperInvariant()] = name;
        }
    }

    public Dictionary<string, SpecialtyTerm> Specialties { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<CountyTerm>> CountiesByName { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<CityTerm>> CitiesByName { get; } = new(StringComparer.Ordinal);

    public HashSet<string> StateCodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> StatesByName { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> StateNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Suffolk County" → "suffolk", "St. Louis city" stays "st louis city".</summary>
    public static string CountyKey(string name)
    {
        var key = QueryText.Normalize(name);
        foreach (var suffix in CountySuffixes)
        {
            if (key.EndsWith(" " + suffix, StringComparison.Ordinal))
            {
                return key[..^(suffix.Length + 1)];
            }
        }

        return key;
    }

    // "Heart (cardiology)" → "cardiology"; nothing for a list ("(LPN, LVN)") or a vague word.
    private static string? Parenthesized(string name)
    {
        var open = name.LastIndexOf('(');
        if (open < 0 || !name.EndsWith(')'))
        {
            return null;
        }

        var inner = name[(open + 1)..^1];
        return inner.Contains(',', StringComparison.Ordinal) || VagueTerms.Contains(QueryText.Normalize(inner)) ? null : inner;
    }

    // "st johns" also as "saint johns", and the other way round.
    private static IEnumerable<string> SaintVariants(string key)
    {
        yield return key;
        if (key.StartsWith("st ", StringComparison.Ordinal))
        {
            yield return "saint " + key[3..];
        }
        else if (key.StartsWith("saint ", StringComparison.Ordinal))
        {
            yield return "st " + key[6..];
        }
    }

    // The phrase and its plural ("chiropractor" → "chiropractors", "pharmacy" → "pharmacies").
    private static IEnumerable<string> Forms(string phrase)
    {
        yield return phrase;
        if (phrase.EndsWith('y') && phrase.Length > 3 && !"aeiou".Contains(phrase[^2], StringComparison.Ordinal))
        {
            yield return phrase[..^1] + "ies";
        }
        else if (phrase.EndsWith('s') || phrase.EndsWith("sh", StringComparison.Ordinal) || phrase.EndsWith("ch", StringComparison.Ordinal) || phrase.EndsWith('x'))
        {
            yield return phrase + "es";
        }
        else
        {
            yield return phrase + "s";
        }
    }
}

/// <summary>Text helpers shared by the interpreter and the vocabulary.</summary>
public static class QueryText
{
    /// <summary>Lower-case, accents dropped, "&amp;" read as "and", apostrophes dropped, other punctuation as spaces, single spaces.</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '\'' or '’')
            {
                continue;
            }

            if (c == '&')
            {
                sb.Append(" and ");
            }
            else
            {
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>
/// Reads what someone typed in the search box (docs/PRODUCT_PLAN.md step 1.3): an NPI; or a specialty and/or a place
/// (state, county, USPS city, ZIP, "within N miles of"), with any other words as a name; or just a name.
/// </summary>
public static class QueryInterpreter
{
    private static readonly HashSet<string> Connectors = new(StringComparer.Ordinal)
    {
        "in", "near", "around", "at", "from", "the", "a", "an", "of", "for", "with", "and", "within", "miles", "mile", "mi", "close", "to", "by", "me", "find",
        "search", "list", "all", "show",
    };

    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "doctor", "doctors", "provider", "providers", "physician", "physicians", "specialist", "specialists", "practitioner", "practitioners",
    };

    private static readonly HashSet<string> PlaceLead = new(StringComparer.Ordinal) { "in", "near", "around", "at", "from" };

    private static readonly HashSet<string> CountyWords = new(StringComparer.Ordinal) { "county", "parish", "borough", "counties" };

    private static readonly HashSet<string> OrganizationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "llc", "inc", "pc", "pllc", "llp", "ltd", "corp", "corporation", "co", "company", "hospital", "hospitals", "clinic", "clinics", "center",
        "centre", "centers", "group", "associates", "assoc", "medical", "health", "healthcare", "care", "pharmacy", "lab", "labs", "laboratory",
        "laboratories", "services", "practice", "partners", "dental", "chiropractic", "therapy", "university", "system", "foundation", "institute",
        "home", "hospice", "ambulance", "imaging", "radiology", "surgery", "surgical", "urgent", "rehab", "rehabilitation", "nursing", "supply",
    };

    public static QueryInterpretation Interpret(string? text, SearchVocabulary vocabulary)
    {
        var raw = (text ?? "").Trim();
        var notes = new List<string>();
        var nearMe = false;
        var empty = new QueryInterpretation(QueryKind.Empty, new SearchFilter(), [], null, null, notes);
        if (raw.Length == 0)
        {
            return empty;
        }

        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 10 && raw.All(c => char.IsAsciiDigit(c) || c is ' ' or '-'))
        {
            return new QueryInterpretation(QueryKind.Npi, new SearchFilter { Npi = digits }, [], null, digits, notes);
        }

        var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokens = QueryText.Normalize(raw).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var used = new bool[tokens.Length];
        var parts = new List<QueryPart>();
        string? state = null, countyFips = null, city = null, zip = null;
        int? radius = null;
        SpecialtyTerm? specialty = null;

        // "near me" needs the visitor's location, which only the map asks for.
        for (var i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i] == "near" && tokens[i + 1] == "me")
            {
                used[i] = used[i + 1] = true;
                nearMe = true;
                notes.Add("“Near me” works on the map page, which can ask for your location.");
            }
        }

        // Radius: "within 25 miles", "25 mi".
        for (var i = 0; i + 1 < tokens.Length; i++)
        {
            if (int.TryParse(tokens[i], NumberStyles.None, CultureInfo.InvariantCulture, out var miles) && tokens[i + 1] is "miles" or "mile" or "mi"
                && tokens[i].Length <= 3)
            {
                radius = Math.Clamp(miles, 1, 100);
                used[i] = used[i + 1] = true;
                if (i > 0 && tokens[i - 1] == "within")
                {
                    used[i - 1] = true;
                }
            }
        }

        // ZIP (a ZIP+4 reads as two tokens, "11701 4321").
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!used[i] && tokens[i].Length == 5 && tokens[i].All(char.IsAsciiDigit))
            {
                zip = tokens[i];
                used[i] = true;
                if (i + 1 < tokens.Length && tokens[i + 1].Length == 4 && tokens[i + 1].All(char.IsAsciiDigit))
                {
                    used[i + 1] = true;
                }

                if (radius is null && i > 0 && tokens[i - 1] is "near" or "around")
                {
                    radius = 10; // "near 11701": a short drive
                }

                break;
            }
        }

        // "New York City" / "NYC".
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!used[i] && tokens[i] == "nyc")
            {
                (state, city, used[i]) = ("NY", "New York", true);
            }
            else if (!used[i] && i + 2 < tokens.Length && tokens[i] == "new" && tokens[i + 1] == "york" && tokens[i + 2] == "city")
            {
                (state, city) = ("NY", "New York");
                used[i] = used[i + 1] = used[i + 2] = true;
            }
        }

        // State: a two-letter code only as the last word (so "in", "or", "me" stay words), or a state name anywhere.
        var last = LastUnused(used);
        if (state is null && last >= 0 && tokens[last].Length == 2 && vocabulary.StateCodes.Contains(tokens[last]) && (last > 0 || tokens.Length == 1))
        {
            state = tokens[last].ToUpperInvariant();
            used[last] = true;
        }

        if (state is null && Longest(tokens, used, 3, p => vocabulary.StatesByName.ContainsKey(p)) is { } stateMatch)
        {
            // "New York" before "County"/a city name could be part of something else; take it as the state only when nothing else claims it.
            state = vocabulary.StatesByName[stateMatch.Phrase];
            Mark(used, stateMatch);
        }

        // County: words followed by "county"/"parish"/"borough".
        for (var i = 1; i < tokens.Length && countyFips is null; i++)
        {
            if (used[i] || !CountyWords.Contains(tokens[i]))
            {
                continue;
            }

            for (var len = Math.Min(4, i); len >= 1; len--)
            {
                var start = i - len;
                if (Enumerable.Range(start, len).Any(j => used[j]))
                {
                    continue;
                }

                var phrase = string.Join(' ', tokens[start..i]);
                if (!vocabulary.CountiesByName.TryGetValue(phrase, out var counties))
                {
                    continue;
                }

                var inState = state is null ? counties : counties.Where(c => string.Equals(c.State, state, StringComparison.OrdinalIgnoreCase)).ToList();
                if (inState.Count == 0)
                {
                    continue;
                }

                var county = inState.OrderByDescending(c => c.Population).First();
                if (state is null && inState.Count > 1)
                {
                    notes.Add($"There's more than one {county.Name}: we used {county.State}. Add the state to choose another ("
                              + string.Join(", ", inState.Skip(1).Select(c => c.State).Distinct()) + ").");
                }

                countyFips = county.Fips;
                state ??= county.State.ToUpperInvariant();
                for (var j = start; j <= i; j++)
                {
                    used[j] = true;
                }

                parts.Add(new QueryPart("County", $"{county.Name}, {county.State}", ["county", "city"]));
                break;
            }
        }

        // Specialty: the longest phrase.
        Match? specialtyWords = null;
        if (Longest(tokens, used, 6, p => vocabulary.Specialties.ContainsKey(p)) is { } specialtyMatch)
        {
            specialty = vocabulary.Specialties[specialtyMatch.Phrase];
            specialtyWords = specialtyMatch;
            Mark(used, specialtyMatch);
        }

        for (var i = 0; i < tokens.Length; i++)
        {
            if (!used[i] && Generic.Contains(tokens[i]))
            {
                used[i] = true;
            }
        }

        // City: only with context (a specialty, a state, or "in"/"near" before it), so a surname isn't read as a town.
        // An organization word as the specialty ("hospital") gives no context: "Stony Brook University Hospital" is a name, "hospitals in stony brook" a list.
        // A generic word as the specialty ("doctor") gives none either: "doctor john smith" is a name, not doctors in Smith, NV.
        var orgWordSpecialty = specialtyWords is { } ow && tokens[ow.Start..(ow.Start + ow.Length)].All(t => OrganizationWords.Contains(t) || Generic.Contains(t));
        var cityAllowed = (specialty is not null && !orgWordSpecialty) || state is not null || countyFips is not null;
        if (city is null && zip is null)
        {
            var cityMatch = Longest(tokens, used, 4, p => vocabulary.CitiesByName.TryGetValue(p, out var list)
                && (state is null || list.Any(c => string.Equals(c.State, state, StringComparison.OrdinalIgnoreCase))),
                start => cityAllowed || (start > 0 && PlaceLead.Contains(tokens[start - 1])));
            if (cityMatch is { } m)
            {
                var candidates = vocabulary.CitiesByName[m.Phrase].Where(c => state is null || string.Equals(c.State, state, StringComparison.OrdinalIgnoreCase));
                var best = candidates.OrderByDescending(c => c.Zips).First();
                city = CityCatalog.TitleCase(best.City);
                state ??= best.State.ToUpperInvariant();
                Mark(used, m);
            }
        }

        // "Stony Brook University Hospital": an organization word used as the specialty, with other words and no place, is a name.
        // "Doctor John Smith": a generic word ("doctor") as the specialty, with other words and no place, is dropped.
        var noPlace = state is null && countyFips is null && city is null && zip is null;
        if (noPlace && specialtyWords is { } sw && Enumerable.Range(0, tokens.Length).Any(i => !used[i] && !Connectors.Contains(tokens[i])))
        {
            var specialtyTokens = tokens[sw.Start..(sw.Start + sw.Length)];
            if (specialtyTokens.All(OrganizationWords.Contains))
            {
                specialty = null;
                for (var j = sw.Start; j < sw.Start + sw.Length; j++)
                {
                    used[j] = false;
                }
            }
            else if (specialtyTokens.All(Generic.Contains))
            {
                specialty = null;
            }
        }

        // Leftover words: connectors drop out once something structured was found; the rest is a name.
        var structured = specialty is not null || state is not null || countyFips is not null || city is not null || zip is not null;
        var nameWords = new List<string>();
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!used[i] && !(structured && Connectors.Contains(tokens[i])))
            {
                nameWords.Add(tokens[i]);
            }
        }

        if (!structured)
        {
            // Only a name: keep what was typed (apart from a leading "Dr.").
            var name = string.Join(' ', words.SkipWhile((w, i) => i == 0 && QueryText.Normalize(w) == "dr")).Trim();
            if (QueryText.Normalize(name).Length == 0)
            {
                return empty with { Notes = notes };
            }

            return new QueryInterpretation(QueryKind.Names, new SearchFilter(), [new QueryPart("Name", name, ["lastName", "firstName", "orgName", "nameMatch"])],
                name, null, notes);
        }

        var filter = new SearchFilter
        {
            Category = specialty?.Category,
            Subcategory = specialty?.Subcategory,
            Classification = specialty?.Classification,
            Specialization = specialty?.Specialization,
            State = state,
            CountyFips = countyFips,
            City = city,
            Zip5 = zip,
            RadiusMiles = zip is null ? null : radius,
        };
        if (specialty is not null)
        {
            parts.Insert(0, new QueryPart("Specialty", specialty.Label,
                specialty.Category is not null ? ["category", "subcategory"] : ["classification", "specialization"]));
        }

        if (state is not null)
        {
            parts.Insert(parts.Count(p => p.Label == "Specialty"),
                new QueryPart("State", vocabulary.StateNames.GetValueOrDefault(state, state), ["state", "county", "city"]));
        }

        if (city is not null)
        {
            parts.Add(new QueryPart("City", $"{city}, {state}", ["city"]));
        }

        if (zip is not null)
        {
            parts.Add(filter.RadiusMiles is { } r ? new QueryPart("Near", $"within {r} miles of {zip}", ["zip", "radius"]) : new QueryPart("ZIP", zip, ["zip", "radius"]));
        }

        string? nameText = null;
        if (nameWords.Count > 0)
        {
            nameText = string.Join(' ', nameWords);
            filter = nameWords.Any(OrganizationWords.Contains)
                ? filter with { OrgName = nameText, NameMatch = NameSearch.Similar }
                : nameWords.Count == 1
                    ? filter with { LastName = nameWords[0], NameMatch = NameSearch.Similar }
                    : filter with { FirstName = string.Join(' ', nameWords[..^1]), LastName = nameWords[^1], NameMatch = NameSearch.Similar };
            parts.Add(new QueryPart("Name", nameText, ["lastName", "firstName", "orgName", "nameMatch"]));
        }

        if (zip is null && radius is not null)
        {
            notes.Add("A distance needs a ZIP code, e.g. “dentists within 10 miles of 11701”.");
        }

        return new QueryInterpretation(QueryKind.List, filter, parts, nameText, null, notes) { NearMe = nearMe };
    }

    private sealed record Match(int Start, int Length, string Phrase);

    private static int LastUnused(bool[] used)
    {
        for (var i = used.Length - 1; i >= 0; i--)
        {
            if (!used[i])
            {
                return i;
            }
        }

        return -1;
    }

    // The longest run of unused words (up to maxLength) that matches, leftmost first among equals.
    private static Match? Longest(string[] tokens, bool[] used, int maxLength, Func<string, bool> matches, Func<int, bool>? allowedAt = null)
    {
        for (var len = Math.Min(maxLength, tokens.Length); len >= 1; len--)
        {
            for (var start = 0; start + len <= tokens.Length; start++)
            {
                if (Enumerable.Range(start, len).Any(j => used[j]) || (allowedAt is not null && !allowedAt(start)))
                {
                    continue;
                }

                var phrase = string.Join(' ', tokens[start..(start + len)]);
                if (matches(phrase))
                {
                    return new Match(start, len, phrase);
                }
            }
        }

        return null;
    }

    private static void Mark(bool[] used, Match m)
    {
        for (var j = m.Start; j < m.Start + m.Length; j++)
        {
            used[j] = true;
        }
    }
}
