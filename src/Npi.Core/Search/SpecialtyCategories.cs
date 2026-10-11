using System.Reflection;
using CsvHelper;
using CsvHelper.Configuration;

namespace Npi.Core.Search;

/// <summary>A plain-language specialty group ("Doctors (MD, DO)") and its subcategories.</summary>
/// <param name="Title">What a list of them is called ("Doctors").</param>
/// <param name="Singular">One of them ("Doctor").</param>
public sealed record SpecialtyCategory(string Name, string Title, string Singular, IReadOnlyList<string> Synonyms,
    IReadOnlyList<SpecialtySubcategory> Subcategories)
{
    /// <summary>What they do, in plain words (written for this site).</summary>
    public string? Description { get; init; }

    /// <summary>Every taxonomy code of every subcategory.</summary>
    public IReadOnlySet<string> Codes { get; } = Subcategories.SelectMany(s => s.Codes).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public SpecialtySubcategory? Find(string? subcategory) =>
        subcategory is null ? null : Subcategories.FirstOrDefault(s => string.Equals(s.Name, subcategory.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>A subcategory ("Heart (cardiology)", list title "Cardiologists", one "Cardiologist") and the NUCC codes it stands for.</summary>
public sealed record SpecialtySubcategory(string Category, string Name, string Title, string Singular, IReadOnlyList<string> Synonyms,
    IReadOnlySet<string> Codes)
{
    /// <summary>What they do, in plain words (written for this site).</summary>
    public string? Description { get; init; }
}

/// <summary>Where a taxonomy code sits in the plain categories.</summary>
public sealed record SpecialtyPlacement(string Category, string Subcategory, string Singular);

/// <summary>
/// The plain specialty categories the site offers instead of NUCC's classifications (owner, 2026-10-11): about 25 categories
/// of about 200 subcategories, each a set of NUCC taxonomy codes. Hand-made lists embedded in Npi.Core:
/// <c>specialty_categories.csv</c> (category, subcategory, title, singular, synonyms, description; category rows have no subcategory; order
/// = display order) and <c>specialty_codes.csv</c> (code, category, subcategory; a code may sit in several subcategories, e.g.
/// psychiatrists under Doctors and under Mental health; its first row is where it is shown). A code missing from the list
/// (a new NUCC release) falls under Other › Other until the list is updated; the loader logs such codes.
/// </summary>
public sealed class SpecialtyCategories
{
    public const string FallbackCategory = "Other";
    public const string FallbackSubcategory = "Other";

    private readonly Dictionary<string, SpecialtyCategory> _byName;
    private readonly Dictionary<string, SpecialtyPlacement> _primary;

    private SpecialtyCategories(IReadOnlyList<SpecialtyCategory> categories, Dictionary<string, SpecialtyPlacement> primary)
    {
        Categories = categories;
        _byName = categories.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        _primary = primary;
        Fallback = _byName[FallbackCategory].Find(FallbackSubcategory)!;
    }

    /// <summary>The embedded lists.</summary>
    public static SpecialtyCategories Default { get; } = Load();

    public IReadOnlyList<SpecialtyCategory> Categories { get; }

    /// <summary>Where codes missing from the list are placed.</summary>
    public SpecialtySubcategory Fallback { get; }

    public SpecialtyCategory? Find(string? category) =>
        category is not null && _byName.TryGetValue(category.Trim(), out var found) ? found : null;

    public bool IsMapped(string code) => _primary.ContainsKey(code);

    /// <summary>Where a provider with this (primary) taxonomy code is shown; null for no code.</summary>
    public SpecialtyPlacement? Place(string? code) =>
        code is null ? null
        : _primary.TryGetValue(code, out var placement) ? placement
        : new SpecialtyPlacement(Fallback.Category, Fallback.Name, Fallback.Singular);

    /// <summary>
    /// Whether <paramref name="code"/> belongs to the category (and subcategory, when given). Unmapped codes belong to the
    /// fallback (Other › Other).
    /// </summary>
    public bool Contains(SpecialtyCategory category, SpecialtySubcategory? subcategory, string code) =>
        IsMapped(code)
            ? (subcategory?.Codes ?? category.Codes).Contains(code)
            : category.Name == Fallback.Category && (subcategory is null || ReferenceEquals(subcategory, Fallback));

    internal static SpecialtyCategories Parse(TextReader categoriesCsv, TextReader codesCsv)
    {
        var config = new CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture) { HasHeaderRecord = true };
        var codeRows = new List<(string Code, string Category, string Subcategory)>();
        using (var csv = new CsvReader(codesCsv, config))
        {
            csv.Read();
            csv.ReadHeader();
            while (csv.Read())
            {
                codeRows.Add((csv.GetField("code")!.Trim(), csv.GetField("category")!, csv.GetField("subcategory")!));
            }
        }

        var codesBySub = codeRows.GroupBy(r => (r.Category, r.Subcategory))
            .ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.Select(r => r.Code).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var categories = new List<SpecialtyCategory>();
        var pending = new List<(string Name, string Title, string Singular, string[] Synonyms, string? Description)>();
        var subs = new Dictionary<string, List<SpecialtySubcategory>>(StringComparer.Ordinal);
        using (var csv = new CsvReader(categoriesCsv, config))
        {
            csv.Read();
            csv.ReadHeader();
            while (csv.Read())
            {
                var category = csv.GetField("category")!;
                var subcategory = csv.GetField("subcategory")!;
                var title = csv.GetField("title")!;
                var singular = csv.GetField("singular")!;
                var synonyms = csv.GetField("synonyms")!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var description = csv.TryGetField<string>("description", out var d) && !string.IsNullOrWhiteSpace(d) ? d.Trim() : null;
                if (subcategory.Length == 0)
                {
                    pending.Add((category, title, singular, synonyms, description));
                    subs[category] = [];
                    continue;
                }

                if (!subs.TryGetValue(category, out var list))
                {
                    throw new InvalidDataException($"specialty_categories.csv: subcategory '{subcategory}' before its category '{category}'.");
                }

                list.Add(new SpecialtySubcategory(category, subcategory, title, singular, synonyms,
                    codesBySub.TryGetValue((category, subcategory), out var codes) ? codes : new HashSet<string>()) { Description = description });
            }
        }

        foreach (var (name, title, singular, synonyms, description) in pending)
        {
            categories.Add(new SpecialtyCategory(name, title, singular, synonyms, subs[name]) { Description = description });
        }

        var known = categories.SelectMany(c => c.Subcategories).Select(s => (s.Category, s.Name)).ToHashSet();
        var primary = new Dictionary<string, SpecialtyPlacement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, category, subcategory) in codeRows)
        {
            if (!known.Contains((category, subcategory)))
            {
                throw new InvalidDataException($"specialty_codes.csv: {code} names an unknown subcategory '{category} › {subcategory}'.");
            }

            if (!primary.ContainsKey(code))
            {
                var sub = categories.First(c => c.Name == category).Find(subcategory)!;
                primary[code] = new SpecialtyPlacement(category, subcategory, sub.Singular);
            }
        }

        return new SpecialtyCategories(categories, primary);
    }

    private static SpecialtyCategories Load()
    {
        var assembly = typeof(SpecialtyCategories).Assembly;
        using var categories = new StreamReader(Resource(assembly, "Npi.Core.specialty_categories.csv"));
        using var codes = new StreamReader(Resource(assembly, "Npi.Core.specialty_codes.csv"));
        return Parse(categories, codes);
    }

    private static Stream Resource(Assembly assembly, string name) =>
        assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
}
