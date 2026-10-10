using System.Text;

namespace Npi.Core.Search;

/// <summary>
/// "Similar" name matching (CLAUDE.md §7 Stage 5.5 item 9). People: a last or first name matches by prefix or by
/// sound (Soundex of the accent-folded name, so "Smiht" finds SMITH and "Nunez" finds NUÑEZ). Organizations: every
/// word of the query may appear anywhere in the legal or another (DBA) name, as a word prefix (FULLTEXT on
/// provider_org_name), so "shore north" finds NORTH SHORE UNIVERSITY HOSPITAL.
/// </summary>
public static class NameSearch
{
    /// <summary>The <c>nameMatch</c> value for the default: names start with what was typed.</summary>
    public const string Prefix = "prefix";

    /// <summary>The <c>nameMatch</c> value for typo-tolerant people and word-anywhere organization names.</summary>
    public const string Similar = "similar";

    /// <summary>Accented capitals folded before Soundex (MySQL's SOUNDEX only knows A–Z and treats the rest as vowels).</summary>
    private static readonly (string From, string To)[] Folds =
    [
        ("À", "A"), ("Á", "A"), ("Â", "A"), ("Ã", "A"), ("Ä", "A"), ("Å", "A"), ("Ç", "C"), ("È", "E"), ("É", "E"), ("Ê", "E"), ("Ë", "E"),
        ("Ì", "I"), ("Í", "I"), ("Î", "I"), ("Ï", "I"), ("Ñ", "N"), ("Ò", "O"), ("Ó", "O"), ("Ô", "O"), ("Õ", "O"), ("Ö", "O"), ("Ø", "O"),
        ("Ù", "U"), ("Ú", "U"), ("Û", "U"), ("Ü", "U"), ("Ý", "Y"),
    ];

    /// <summary>
    /// The SQL for a name's phonetic key: the standard 4-character Soundex of the upper-cased, accent-folded name.
    /// Migration 056 stores exactly this expression (over `last_name` and `first_name`) as indexed virtual columns, and
    /// searches apply it to the typed name, so both sides always agree; a test checks the migration text.
    /// </summary>
    public static string PhoneticSql(string expression)
    {
        var sql = $"UPPER({expression})";
        foreach (var (from, to) in Folds)
        {
            sql = $"REPLACE({sql}, '{from}', '{to}')";
        }

        return $"LEFT(SOUNDEX({sql}), 4)";
    }

    /// <summary>True when a phonetic key can be computed (the name has a letter A–Z once folded); otherwise only the prefix matches.</summary>
    public static bool CanSoundLike(string name) =>
        name.ToUpperInvariant().Any(c => c is >= 'A' and <= 'Z' || Folds.Any(f => f.From[0] == c));

    /// <summary>Only this many leading characters of a typed name count towards <see cref="DistanceSql"/>.</summary>
    public const int MaxScoredLength = 20;

    /// <summary>
    /// How far a name is from what was typed, for ranking "similar" people (lower = closer; 0 = the same name):
    /// letters that differ position by position, plus twice the difference in length, plus twice each typed letter the
    /// name lacks (so swapped letters, a common typo, cost less than a wrong or extra letter). "SMIHT": SMITH 2, SMIDT 3,
    /// SMIDTH 3, SMOOT 6. A rough edit distance in plain SQL (MySQL has none); comparisons use the
    /// column's accent- and case-insensitive collation. <paramref name="parameter"/> holds the typed name, of
    /// <paramref name="typedLength"/> characters; only numbers from C# enter the SQL text.
    /// </summary>
    public static string DistanceSql(string column, string parameter, int typedLength)
    {
        var n = Math.Clamp(typedLength, 1, MaxScoredLength);
        var positions = string.Join(" + ", Enumerable.Range(1, n).Select(i => $"(SUBSTRING({column}, {i}, 1) <> SUBSTRING({parameter}, {i}, 1))"));
        // NOT LIKE, not LOCATE: LOCATE is accent-sensitive even under an accent-insensitive collation (Í ≠ I).
        var missing = string.Join(" + ", Enumerable.Range(1, n).Select(i => $"({column} NOT LIKE CONCAT('%', SUBSTRING({parameter}, {i}, 1), '%'))"));
        return $"(COALESCE({positions} + 2 * ABS(CHAR_LENGTH({column}) - CHAR_LENGTH({parameter})) + 2 * ({missing}), {MaxScoredLength * 4}))";
    }

    // InnoDB's default FULLTEXT stopwords of 3+ letters (shorter words are never indexed): a required stopword would match nothing.
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "are", "com", "for", "from", "how", "that", "the", "this", "was", "what", "when", "where", "who", "will", "with", "und", "www",
    };

    /// <summary>InnoDB's default innodb_ft_min_token_size: shorter words are not in the index.</summary>
    public const int MinWordLength = 3;

    /// <summary>
    /// The FULLTEXT boolean query for an organization name: every indexable word required, each as a prefix
    /// ("north shore" → "+north* +shore*"). Null when no word is indexable ("NY", "THE"): only the prefix match applies.
    /// </summary>
    public static string? OrganizationWords(string name)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in name + " ")
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (word.Length >= MinWordLength && !Stopwords.Contains(word.ToString()) && !words.Contains(word.ToString()))
            {
                words.Add(word.ToString());
            }

            word.Clear();
        }

        return words.Count == 0 ? null : string.Join(" ", words.Select(w => "+" + w + "*"));
    }
}
