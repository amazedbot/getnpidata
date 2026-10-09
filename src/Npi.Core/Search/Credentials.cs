using System.Text.RegularExpressions;

namespace Npi.Core.Search;

/// <summary>A standardized credential and how many active providers hold it.</summary>
public sealed record CredentialInfo(string Credential, int Providers);

/// <summary>
/// Standardized credentials (CLAUDE.md §7 Stage 5.5 item 12). NPPES credentials are free text: 4.85M providers wrote
/// 173k different values ("M.D.", "MD", "M. D.", "MD, PHD", "MS CCC-SLP", "NURSE PRACTITIONER" …). A value is split into
/// pieces at separators, each piece cleaned (punctuation and spacing), and each piece resolved against the known
/// credentials: the spellings that many providers use on their own, compared without punctuation ("PA-C" = "PAC").
/// The loader builds the known set from the data (CredentialBuilder); the site resolves typed filters with it.
/// </summary>
public static partial class Credentials
{
    /// <summary>
    /// The list entry for every credential too rare to be listed (held by fewer than <see cref="CredentialCatalog.MinProviders"/>
    /// providers): provider_credential has an extra "Other" row (ord <see cref="OtherOrd"/>) for each provider holding one.
    /// </summary>
    public const string Other = "Other";

    /// <summary>The <c>ord</c> of the "Other" rows in provider_credential; display skips them.</summary>
    public const int OtherOrd = 100;

    /// <summary>Spelled-out titles that stand for a credential.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["DOCTOR OF MEDICINE"] = "MD", ["MEDICAL DOCTOR"] = "MD", ["DOCTOR OF OSTEOPATHY"] = "DO", ["DOCTOR OF OSTEOPATHIC MEDICINE"] = "DO",
        ["DOCTOR OF CHIROPRACTIC"] = "DC", ["CHIROPRACTOR"] = "DC", ["DOCTOR OF DENTAL SURGERY"] = "DDS", ["DOCTOR OF DENTAL MEDICINE"] = "DMD",
        ["DOCTOR OF PHARMACY"] = "PharmD", ["PHARMACIST"] = "RPh", ["DOCTOR OF OPTOMETRY"] = "OD", ["OPTOMETRIST"] = "OD",
        ["DOCTOR OF PODIATRIC MEDICINE"] = "DPM", ["PODIATRIST"] = "DPM", ["DOCTOR OF PHYSICAL THERAPY"] = "DPT", ["PHYSICAL THERAPIST"] = "PT",
        ["PHYSICAL THERAPIST ASSISTANT"] = "PTA", ["OCCUPATIONAL THERAPIST"] = "OT", ["OCCUPATIONAL THERAPI"] = "OT", ["NURSE PRACTITIONER"] = "NP",
        ["REGISTERED NURSE"] = "RN", ["LICENSED PRACTICAL NURSE"] = "LPN", ["LICENSED VOCATIONAL NURSE"] = "LVN", ["PHYSICIAN ASSISTANT"] = "PA",
        ["LICENSED CLINICAL SOCIAL WORKER"] = "LCSW", ["REGISTERED DIETITIAN"] = "RD", ["CERTIFIED REGISTERED NURSE ANESTHETIST"] = "CRNA",
        ["DOCTOR OF AUDIOLOGY"] = "AuD", ["DOCTOR OF PSYCHOLOGY"] = "PsyD", ["DOCTOR OF PHILOSOPHY"] = "PhD", ["CERTIFIED NURSE MIDWIFE"] = "CNM",
    };

    /// <summary>Conventional mixed-case spellings of degrees, by key.</summary>
    private static readonly Dictionary<string, string> Casing = new(StringComparer.Ordinal)
    {
        ["PHD"] = "PhD", ["PHARMD"] = "PharmD", ["PSYD"] = "PsyD", ["EDD"] = "EdD", ["DRPH"] = "DrPH", ["RPH"] = "RPh", ["AUD"] = "AuD",
        ["LAC"] = "LAc", ["DSC"] = "DSc", ["MSC"] = "MSc", ["BSC"] = "BSc", ["SCD"] = "ScD", ["THD"] = "ThD", ["DMIN"] = "DMin", ["MED"] = "MEd", ["DPHIL"] = "DPhil",
    };

    /// <summary>Values that mean "no credential".</summary>
    private static readonly HashSet<string> Empty = new(StringComparer.Ordinal)
    {
        "", "NONE", "NA", "N/A", "NULL", "NO", "OTHER", "X", "XX", "0", "UNKNOWN", "NOT APPLICABLE", "N", "-", "--",
    };

    /// <summary>"M.D." → "MD", "PA-C" → "PAC": letters and digits only, upper case. Two spellings are the same credential when their keys are equal.</summary>
    public static string Key(string value) => NonAlphanumerics().Replace(value.ToUpperInvariant(), "");

    /// <summary>The conventional spelling for a key, if it has one ("PHD" → "PhD").</summary>
    public static string? ConventionalSpelling(string key) => Casing.GetValueOrDefault(key);

    /// <summary>
    /// The cleaned pieces of a raw value: "M.D., PH.D." → ["MD", "PHD"]; "MS CCC-SLP" → ["MS CCC-SLP"] (one piece, split later
    /// by <see cref="Resolve"/>); "OTR/L" stays one piece (a slash before a single letter is part of the credential).
    /// </summary>
    public static IReadOnlyList<string> Pieces(string? raw)
    {
        if (raw is null)
        {
            return [];
        }

        var text = raw.ToUpperInvariant().Trim();
        if (Empty.Contains(text))
        {
            return [];
        }

        text = SlashBeforeLetter().Replace(text, "\u0001"); // OTR/L, COTA/L
        var pieces = new List<string>();
        foreach (var part in Separators().Split(text))
        {
            var piece = Clean(part.Replace('\u0001', '/'));
            if (!Empty.Contains(piece))
            {
                pieces.Add(piece);
            }
        }

        return pieces;
    }

    /// <summary>
    /// The credentials a cleaned piece stands for: a spelled-out title ("NURSE PRACTITIONER" → NP), or the longest runs of words
    /// that are known credentials ("MS CCC SLP" → MS, CCC-SLP; "PHARM D" → PharmD). A piece with a word that is no credential
    /// stays as written ("LPC ASSOCIATE", "BEHAVIOR TECHNICIAN").
    /// </summary>
    /// <param name="known">
    /// The standard credential(s) of a known key, or null: usually one ("MD"), two for a spelling that is two credentials run
    /// together ("MSPT" → MS, PT).
    /// </param>
    public static IReadOnlyList<string> Resolve(string piece, Func<string, IReadOnlyList<string>?> known)
    {
        if (Aliases.TryGetValue(piece, out var alias))
        {
            return [alias];
        }

        var words = piece.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        for (var i = 0; i < words.Length;)
        {
            IReadOnlyList<string>? found = null;
            var length = 0;
            for (var end = words.Length; end > i && found is null; end--)
            {
                length = end - i;
                found = length == 1 ? known(Key(words[i])) : JoinedWords(words[i..end], known);
            }

            if (found is null)
            {
                return [piece];
            }

            result.AddRange(found);
            i += length;
        }

        return result;
    }

    private const int MaxSpacedAbbreviation = 8;

    // Several words read as one credential: a spelled-out title, or a short abbreviation written with spaces ("PHARM D",
    // "CCC SLP"). A long one is a phrase someone also wrote without spaces ("CASE MANAGER"): keep the words.
    private static IReadOnlyList<string>? JoinedWords(string[] words, Func<string, IReadOnlyList<string>?> known)
    {
        if (Aliases.TryGetValue(string.Join(' ', words), out var alias))
        {
            return [alias];
        }

        return known(Key(string.Concat(words))) is [var single] && single.Length <= MaxSpacedAbbreviation ? [single] : null;
    }

    /// <summary>The standardized credentials of a raw value, in order, without duplicates.</summary>
    public static IReadOnlyList<string> Standardize(string? raw, Func<string, IReadOnlyList<string>?> known)
    {
        // Rejoin two pieces that are one credential split at a separator ("CCC/SLP" → CCC-SLP).
        var pieces = Pieces(raw).ToList();
        for (var i = 0; i + 1 < pieces.Count; i++)
        {
            if (!pieces[i].Contains(' ', StringComparison.Ordinal) && !pieces[i + 1].Contains(' ', StringComparison.Ordinal)
                && known(Key(pieces[i] + pieces[i + 1])) is [var joined])
            {
                pieces[i] = joined;
                pieces.RemoveAt(i + 1);
            }
        }

        return pieces.SelectMany(p => Resolve(p, known)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Upper case, periods and apostrophes removed, brackets to spaces, spaces collapsed: "PH. D." → "PH D".</summary>
    public static string Clean(string value) =>
        Spaces().Replace(value.ToUpperInvariant().Replace(".", "", StringComparison.Ordinal).Replace("'", "", StringComparison.Ordinal)
            .Replace('(', ' ').Replace(')', ' '), " ").Trim();

    [GeneratedRegex("[^A-Z0-9]+")]
    private static partial Regex NonAlphanumerics();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s*(?:[,;&+|]|/|\bAND\b)\s*")]
    private static partial Regex Separators();

    [GeneratedRegex(@"/(?=[A-Z](?![A-Z0-9]))")]
    private static partial Regex SlashBeforeLetter();
}
