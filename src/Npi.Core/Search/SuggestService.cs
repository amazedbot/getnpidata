using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>
/// One type-ahead suggestion for the search box (docs/PRODUCT_PLAN.md step 1.3). <see cref="Url"/> opens a page (a provider);
/// <see cref="Text"/> completes the box (a specialty, or a place after "in").
/// </summary>
public sealed record Suggestion(string Kind, string Label, string? Detail, string? Url, string? Text);

/// <summary>Type-ahead for the search box: specialties and places from the vocabulary, providers by name prefix.</summary>
public sealed class SuggestService(string connectionString, SearchVocabularyCatalog vocabularies)
{
    public const int MaxPerKind = 5;

    private static readonly string[] PlaceLeads = [" in ", " near ", " around "];

    private sealed record ProviderRow(string Npi, string SortName, sbyte EntityType, string? Classification, string? City, string? State);

    public async Task<IReadOnlyList<Suggestion>> SuggestAsync(string? text, CancellationToken ct)
    {
        var typed = (text ?? "").TrimStart();
        if (QueryText.Normalize(typed).Length < 2)
        {
            return [];
        }

        var vocabulary = await vocabularies.GetAsync(ct);
        var result = new List<Suggestion>();

        // A place after "in"/"near": complete the place, keep what came before.
        var lead = PlaceLeads.Select(l => (Lead: l, At: typed.LastIndexOf(l, StringComparison.OrdinalIgnoreCase))).Where(x => x.At >= 0).OrderByDescending(x => x.At).FirstOrDefault();
        if (lead.Lead is not null)
        {
            var before = typed[..(lead.At + lead.Lead.Length)];
            var fragment = QueryText.Normalize(typed[(lead.At + lead.Lead.Length)..]);
            if (fragment.Length >= 2)
            {
                result.AddRange(Places(vocabulary, fragment).Select(p => p with { Text = before + p.Label }));
            }

            return result;
        }

        var normalized = QueryText.Normalize(typed);
        // Phrases starting with the text first ("cardio" → Cardiologists), then phrases with a word starting with it; the plain
        // categories before NUCC's names; one per specialty. A plain category shows its list title and where it sits.
        var specialties = vocabulary.Specialties
            .Select(kv => (Phrase: kv.Key, Term: kv.Value, Rank: kv.Key.StartsWith(normalized, StringComparison.Ordinal) ? 0
                : kv.Key.Contains(" " + normalized, StringComparison.Ordinal) ? 1 : 2))
            .Where(x => x.Rank < 2)
            .OrderBy(x => x.Rank).ThenBy(x => x.Term.Category is null ? 1 : 0).ThenBy(x => x.Phrase.EndsWith('s') ? 1 : 0)
            .ThenBy(x => x.Term.Specialization is null ? 0 : 1).ThenBy(x => x.Phrase.Length)
            .DistinctBy(x => x.Term)
            .Take(MaxPerKind)
            .Select(x =>
            {
                if (x.Term.Category is { } category)
                {
                    var where = x.Term.Subcategory is { } sub ? $"{category} › {sub}" : "Category";
                    return new Suggestion("specialty", x.Term.Label, where, null, x.Term.Label + " in ");
                }

                var shown = x.Rank == 0 && !string.Equals(x.Phrase, QueryText.Normalize(x.Term.Label), StringComparison.Ordinal)
                    ? CultureInfoTitle(x.Phrase) : x.Term.Label;
                return new Suggestion("specialty", shown, shown == x.Term.Label ? "NUCC specialty" : x.Term.Label, null, shown + " in ");
            });
        result.AddRange(specialties);
        result.AddRange(Places(vocabulary, normalized).Take(3).Select(p => p with { Text = p.Label }));
        result.AddRange(await ProvidersAsync(typed, ct));
        return result;
    }

    private static string CultureInfoTitle(string phrase) => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(phrase);

    private static IEnumerable<Suggestion> Places(SearchVocabulary vocabulary, string fragment)
    {
        var states = vocabulary.StatesByName.Where(kv => kv.Key.StartsWith(fragment, StringComparison.Ordinal))
            .Select(kv => new Suggestion("place", vocabulary.StateNames[kv.Value], "State", null, null));
        var counties = vocabulary.CountiesByName.Where(kv => kv.Key.StartsWith(fragment, StringComparison.Ordinal))
            .SelectMany(kv => kv.Value).DistinctBy(c => c.Fips).OrderByDescending(c => c.Population).Take(MaxPerKind)
            .Select(c => new Suggestion("place", $"{c.Name}, {c.State}", "County", null, null));
        var cities = vocabulary.CitiesByName.Where(kv => kv.Key.StartsWith(fragment, StringComparison.Ordinal))
            .SelectMany(kv => kv.Value).OrderByDescending(c => c.Zips).Take(MaxPerKind)
            .Select(c => new Suggestion("place", $"{CityCatalog.TitleCase(c.City)}, {c.State}", "City", null, null));
        return states.Concat(counties).Concat(cities).DistinctBy(s => s.Label).Take(MaxPerKind);
    }

    // Names that start with the text: "smith j" and "john smith" both find "SMITH, JOHN".
    private async Task<IEnumerable<Suggestion>> ProvidersAsync(string typed, CancellationToken ct)
    {
        // As typed (names keep apostrophes and hyphens: O'BRIEN) and normalized (punctuation dropped).
        var prefixes = new List<string>();
        foreach (var words in new[]
                 {
                     typed.ToUpperInvariant().Replace(",", " ", StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                     QueryText.Normalize(typed).ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries),
                 })
        {
            prefixes.Add(SearchQuery.Prefix(string.Join(' ', words)));
            if (words.Length == 2)
            {
                prefixes.Add(SearchQuery.Prefix($"{words[0]}, {words[1]}"));
                prefixes.Add(SearchQuery.Prefix($"{words[1]}, {words[0]}"));
            }
        }

        prefixes = [.. prefixes.Distinct(StringComparer.Ordinal)];

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var rows = new List<ProviderRow>();
        foreach (var prefix in prefixes)
        {
            rows.AddRange(await connection.QueryAsync<ProviderRow>(new CommandDefinition(
                """
                SELECT p.npi AS Npi, p.sort_name AS SortName, p.entity_type AS EntityType, t.Classification,
                       (SELECT l.city FROM provider_location l WHERE l.npi = p.npi ORDER BY l.is_primary DESC, l.id LIMIT 1) AS City,
                       (SELECT l.state FROM provider_location l WHERE l.npi = p.npi ORDER BY l.is_primary DESC, l.id LIMIT 1) AS State
                FROM (SELECT npi, sort_name, entity_type, primary_taxonomy_code FROM provider WHERE sort_name LIKE @prefix ORDER BY sort_name LIMIT @take) p
                LEFT JOIN taxonomy_codes t ON t.Taxonomy_Code = p.primary_taxonomy_code
                ORDER BY p.sort_name
                """, new { prefix, take = MaxPerKind }, commandTimeout: 10, cancellationToken: ct)));
        }

        return rows.DistinctBy(r => r.Npi).OrderBy(r => r.SortName, StringComparer.OrdinalIgnoreCase).Take(MaxPerKind)
            .Select(r => new Suggestion(r.EntityType == 2 ? "organization" : "provider", r.SortName,
                string.Join(" · ", new[] { r.Classification, r.City is null ? null : $"{CityCatalog.TitleCase(r.City)}, {r.State}" }.Where(x => x is not null)),
                "/provider/" + r.Npi, null));
    }
}
