using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Npi.Core.Search;

/// <summary>How current and trustworthy an NPPES record looks (docs/PRODUCT_PLAN.md Phase 3, step 3.1).</summary>
public enum QualityLabel
{
    /// <summary>Nothing found that suggests the record is out of date.</summary>
    Current = 1,

    /// <summary>Something conflicts or is missing; confirm before relying on the record.</summary>
    Check = 2,

    /// <summary>Probably out of date: old, with no sign of current practice anywhere in the public data.</summary>
    Stale = 3,
}

/// <summary>What the loader found about a record (<c>provider_quality.reasons</c>); the bit values are stored, never renumber them.</summary>
[Flags]
public enum QualityReason
{
    None = 0,

    /// <summary>NPPES last updated <see cref="RecordQuality.CheckYears"/> or more years before the data's as-of date.</summary>
    NotUpdated5Years = 1,

    /// <summary>NPPES last updated <see cref="RecordQuality.StaleYears"/> or more years before the data's as-of date (also sets the 5-year bit).</summary>
    NotUpdated10Years = 2,

    /// <summary>No sign of current practice (<see cref="QualityActivity"/> is none).</summary>
    NoActivity = 4,

    /// <summary>The primary US practice address couldn't be placed on a map, only at its ZIP code.</summary>
    NotMapped = 8,

    /// <summary>The primary practice address is a PO box (NPPES asks for a street address).</summary>
    PoBox = 16,

    /// <summary>No practice phone number.</summary>
    NoPhone = 32,

    /// <summary>
    /// Care Compare lists practices, all elsewhere: none in a ZIP code or city of the NPPES practice locations or within
    /// <see cref="RecordQuality.CareCompareMiles"/> miles of one (ZIP centres). A group's other office in the same area isn't a conflict.
    /// </summary>
    CareCompareAddress = 64,

    /// <summary>Care Compare's primary specialty is a profession none of the NPPES taxonomies belongs to (e.g. a doctor listed as a student).</summary>
    CareCompareSpecialty = 128,

    /// <summary>The state's licensing records (where we have them) show the license expired or inactive, and none current.</summary>
    LicenseLapsed = 256,

    /// <summary>A state licensing record lists the provider as deceased.</summary>
    Deceased = 512,

    /// <summary>The NPI was deactivated and later reactivated (for information).</summary>
    Reactivated = 1024,

    /// <summary>The primary practice address is in a residential building (for information: often a home office).</summary>
    HomeAddress = 2048,
}

/// <summary>Signs that the provider currently practises (<c>provider_quality.activity</c>); stored bits, never renumber them.</summary>
[Flags]
public enum QualityActivity
{
    None = 0,
    MedicareBilling = 1,
    Prescribing = 2,
    CareCompare = 4,
    MedicareEnrollment = 8,
    OrderRefer = 16,
    IndustryPayments = 32,
    CertifiedFacility = 64,
    CurrentLicense = 128,
}

/// <summary>One line of a record-quality explanation: <c>good</c>, <c>warn</c>, <c>bad</c> or <c>info</c>, and the text.</summary>
public sealed record QualityNote(string Kind, string Text);

/// <summary>
/// A provider's record-quality label with what it was based on. The API shows the label, the reasons and the signs of current
/// practice in words; the stored bits stay internal.
/// </summary>
public sealed record ProviderQuality(QualityLabel Label, [property: JsonIgnore] QualityReason Reasons, [property: JsonIgnore] QualityActivity Activity)
{
    /// <summary>The reasons in a few words each, most serious first ("Not updated in 10+ years", "No sign of current practice").</summary>
    [JsonPropertyName("reasons")]
    public IReadOnlyList<string> ReasonTexts => RecordQuality.ShortReasons(Reasons);

    /// <summary>The signs of current practice found ("billed Medicare", "enrolled in Medicare").</summary>
    public IReadOnlyList<string> Signs => RecordQuality.SignTexts(Activity);
}

/// <summary>
/// The record-quality rules (docs/PRODUCT_PLAN.md step 3.1), shared by the loader (which stores the label) and the site (which
/// explains it). <b>Stale:</b> not updated in NPPES for <see cref="StaleYears"/>+ years and no sign of current practice in the public
/// data (Medicare billing or prescribing, a Care Compare listing, Medicare enrollment or order-and-refer eligibility, industry
/// payments, a certified facility, a current state license), or a state record listing the provider as deceased. <b>Check:</b> not
/// updated for <see cref="CheckYears"/>+ years with no sign of practice, or a conflict or gap: Care Compare lists another address or
/// profession, a lapsed state license, an address that couldn't be mapped or is a PO box, no phone. <b>Current:</b> everything else.
/// A reactivated NPI and a home address are shown but don't change the label.
/// </summary>
public static class RecordQuality
{
    public const int CheckYears = 5;
    public const int StaleYears = 10;

    /// <summary>A Care Compare practice this close to an NPPES practice location (ZIP centres) agrees with the record.</summary>
    public const int CareCompareMiles = 25;

    /// <summary>Any one of these makes a record "check".</summary>
    public const QualityReason CheckAny = QualityReason.NotMapped | QualityReason.PoBox | QualityReason.NoPhone | QualityReason.CareCompareAddress |
                                          QualityReason.CareCompareSpecialty | QualityReason.LicenseLapsed;

    public static QualityLabel Classify(QualityReason r) =>
        (r & (QualityReason.NotUpdated10Years | QualityReason.NoActivity)) == (QualityReason.NotUpdated10Years | QualityReason.NoActivity)
        || (r & QualityReason.Deceased) != 0
            ? QualityLabel.Stale
            : (r & (QualityReason.NotUpdated5Years | QualityReason.NoActivity)) == (QualityReason.NotUpdated5Years | QualityReason.NoActivity)
              || (r & CheckAny) != 0
                ? QualityLabel.Check
                : QualityLabel.Current;

    /// <summary><see cref="Classify"/> as a SQL expression over an INT column of reasons, for the loader.</summary>
    public static string LabelSql(string reasons)
    {
        static int I(QualityReason r) => (int)r;
        var stale = I(QualityReason.NotUpdated10Years | QualityReason.NoActivity);
        var check = I(QualityReason.NotUpdated5Years | QualityReason.NoActivity);
        return string.Create(CultureInfo.InvariantCulture,
            $"CASE WHEN ({reasons} & {stale}) = {stale} OR ({reasons} & {I(QualityReason.Deceased)}) <> 0 THEN {(int)QualityLabel.Stale} " +
            $"WHEN ({reasons} & {check}) = {check} OR ({reasons} & {I(CheckAny)}) <> 0 THEN {(int)QualityLabel.Check} " +
            $"ELSE {(int)QualityLabel.Current} END");
    }

    /// <summary>The label as a badge or column says it.</summary>
    public static string Name(QualityLabel label) => label switch
    {
        QualityLabel.Stale => "Stale",
        QualityLabel.Check => "Check",
        _ => "Current",
    };

    // The search filter (`quality`): its values and their labels, in the order the form lists them.
    public const string FilterNotStale = "notStale";

    public static readonly IReadOnlyList<(string Value, string Label)> FilterOptions =
    [
        (FilterNotStale, "Hide stale records"), ("current", "Current only"), ("check", "Check only"), ("stale", "Stale only"),
    ];

    /// <summary>The canonical spelling of a filter value (any case), or null when it isn't one.</summary>
    public static string? FilterValue(string value) =>
        FilterOptions.Select(o => o.Value).FirstOrDefault(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>The labels a filter value keeps (<c>quality_search.label IN …</c>).</summary>
    public static IReadOnlyList<int> FilterLabels(string value) => value switch
    {
        FilterNotStale => [(int)QualityLabel.Current, (int)QualityLabel.Check],
        "current" => [(int)QualityLabel.Current],
        "check" => [(int)QualityLabel.Check],
        "stale" => [(int)QualityLabel.Stale],
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Not a record-quality filter value."),
    };

    /// <summary>The filter's condition on alias {0} (a table with an npi column). A provider without a label counts as not stale.</summary>
    internal static string FilterSql(string value) => value switch
    {
        FilterNotStale => $"NOT EXISTS (SELECT 1 FROM provider_quality q WHERE q.npi = {{0}}.npi AND q.label = {(int)QualityLabel.Stale})",
        "current" => $"EXISTS (SELECT 1 FROM provider_quality q WHERE q.npi = {{0}}.npi AND q.label = {(int)QualityLabel.Current})",
        "check" => $"EXISTS (SELECT 1 FROM provider_quality q WHERE q.npi = {{0}}.npi AND q.label = {(int)QualityLabel.Check})",
        "stale" => $"EXISTS (SELECT 1 FROM provider_quality q WHERE q.npi = {{0}}.npi AND q.label = {(int)QualityLabel.Stale})",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Not a record-quality filter value."),
    };

    private static readonly (QualityReason Reason, string Text)[] Short =
    [
        (QualityReason.Deceased, "Listed as deceased by a state board"),
        (QualityReason.NotUpdated10Years, $"Not updated in {StaleYears}+ years"),
        (QualityReason.NotUpdated5Years, $"Not updated in {CheckYears}+ years"),
        (QualityReason.NoActivity, "No sign of current practice"),
        (QualityReason.CareCompareSpecialty, "Medicare lists another profession"),
        (QualityReason.CareCompareAddress, "Medicare lists practices elsewhere"),
        (QualityReason.LicenseLapsed, "State license lapsed"),
        (QualityReason.PoBox, "PO box address"),
        (QualityReason.NotMapped, "Address not found on a map"),
        (QualityReason.NoPhone, "No phone"),
        (QualityReason.Reactivated, "NPI reactivated"),
        (QualityReason.HomeAddress, "Home address"),
    ];

    /// <summary>The reasons in a few words each, most serious first; the 5-year age is left out when the 10-year one applies.</summary>
    public static IReadOnlyList<string> ShortReasons(QualityReason reasons) =>
        Short.Where(s => (reasons & s.Reason) != 0
                         && !(s.Reason == QualityReason.NotUpdated5Years && (reasons & QualityReason.NotUpdated10Years) != 0))
            .Select(s => s.Text).ToList();

    private static readonly (QualityActivity Activity, string Text)[] Signs =
    [
        (QualityActivity.MedicareBilling, "billed Medicare"),
        (QualityActivity.Prescribing, "prescribed under Medicare Part D"),
        (QualityActivity.CareCompare, "listed in Medicare's Care Compare"),
        (QualityActivity.MedicareEnrollment, "enrolled in Medicare"),
        (QualityActivity.OrderRefer, "may order and refer for Medicare"),
        (QualityActivity.IndustryPayments, "received industry payments in the newest Open Payments year"),
        (QualityActivity.CertifiedFacility, "holds a Medicare-certified facility"),
        (QualityActivity.CurrentLicense, "holds a current state license"),
    ];

    /// <summary>The signs of current practice in words, e.g. ["billed Medicare", "enrolled in Medicare"].</summary>
    public static IReadOnlyList<string> SignTexts(QualityActivity activity) =>
        Signs.Where(s => (activity & s.Activity) != 0).Select(s => s.Text).ToList();

    /// <summary>
    /// The full explanation for a profile: the label's reasons first (bad, then warn), then what checked out (good), then notes (info).
    /// <paramref name="lastUpdate"/> gives the age in years; <paramref name="careCompare"/> adds what Care Compare lists.
    /// </summary>
    public static IReadOnlyList<QualityNote> Explain(ProviderQuality q, DateOnly? lastUpdate, DateOnly today, bool isPerson,
        QualityCareCompare? careCompare = null)
    {
        var r = q.Reasons;
        bool Has(QualityReason x) => (r & x) != 0;
        var notes = new List<QualityNote>();
        if (Has(QualityReason.Deceased))
        {
            notes.Add(new("bad", "A state licensing record lists the provider as deceased"));
        }

        var years = lastUpdate is { } u ? (today.DayNumber - u.DayNumber) / 365 : (int?)null;
        var since = lastUpdate?.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        if (Has(QualityReason.NotUpdated5Years))
        {
            notes.Add(new(Has(QualityReason.NoActivity) ? "bad" : "warn",
                $"Not updated in NPPES for {years ?? CheckYears} years (last update {since})"));
        }
        else if (since is not null)
        {
            notes.Add(new("good", $"Updated in NPPES {(years is 0 ? "within the last year" : $"in {since}")}"));
        }

        if (Has(QualityReason.NoActivity))
        {
            notes.Add(new(Has(QualityReason.NotUpdated5Years) ? "bad" : "info", isPerson
                ? "No sign of current practice in the public data: no Medicare billing, enrollment or Care Compare listing, no industry payments, no current state license on file"
                : "No sign of current operation in the public data: no Medicare enrollment or billing, no certified facility"));
        }
        else
        {
            var signs = SignTexts(q.Activity);
            notes.Add(new("good", "Signs of current practice: " + string.Join(", ", signs)));
        }

        if (Has(QualityReason.CareCompareSpecialty))
        {
            notes.Add(new("bad", "Medicare's Care Compare lists another profession" +
                                 (careCompare?.PrimarySpecialty is { } s ? $" ({CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant())})" : "")));
        }

        if (Has(QualityReason.CareCompareAddress))
        {
            var where = careCompare?.Locations.Select(l => string.Join(", ", new[] { l.Address1, l.City, l.State + " " + l.Zip5 }.Where(x => !string.IsNullOrWhiteSpace(x))))
                .Take(3).ToList();
            notes.Add(new("bad", $"Medicare's Care Compare lists practices only elsewhere ({CareCompareMiles}+ miles from every address here)" +
                                 (where is { Count: > 0 } ? ": " + string.Join("; ", where) : "")));
        }
        else if (careCompare is { Locations.Count: > 0 })
        {
            notes.Add(new("good", "Medicare's Care Compare lists a practice at or near an address here"));
        }

        if (Has(QualityReason.LicenseLapsed))
        {
            notes.Add(new("warn", "The state's licensing records show the license expired or inactive, and no current one"));
        }

        if (Has(QualityReason.PoBox))
        {
            notes.Add(new("warn", "The practice address is a PO box; NPPES asks for a street address"));
        }

        if (Has(QualityReason.NotMapped))
        {
            notes.Add(new("warn", "The practice address couldn't be found on a map (shown at its ZIP code)"));
        }

        if (Has(QualityReason.NoPhone))
        {
            notes.Add(new("warn", "No practice phone number"));
        }

        if (Has(QualityReason.Reactivated))
        {
            notes.Add(new("info", "The NPI was deactivated and later reactivated"));
        }

        if (Has(QualityReason.HomeAddress))
        {
            notes.Add(new("info", "The practice address is a residential building (often a home office)"));
        }

        return notes;
    }

    /// <summary>The kinds of Care Compare primary specialty → the plain categories (<see cref="SpecialtyCategories"/>) it may belong to.</summary>
    public static IReadOnlyList<(string Specialty, string Category)> CareCompareCategories { get; } = LoadCareCompareCategories();

    private static List<(string, string)> LoadCareCompareCategories()
    {
        const string name = "Npi.Core.care_compare_specialties.csv";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        var rows = new List<(string, string)>();
        reader.ReadLine(); // header: specialty,categories
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            // The specialty has no commas; the categories may (quoted, "Doctors (MD, DO)"), separated by "|".
            var comma = line.IndexOf(',', StringComparison.Ordinal);
            var specialty = line[..comma];
            foreach (var category in line[(comma + 1)..].Trim('"').Split('|'))
            {
                rows.Add((specialty, category));
            }
        }

        return rows;
    }
}

/// <summary>What Care Compare lists for a clinician, for the record-quality explanation.</summary>
public sealed record QualityCareCompare(string? PrimarySpecialty, IReadOnlyList<QualityCareCompareLocation> Locations);

/// <summary>A practice Care Compare lists (one per ZIP code).</summary>
public sealed record QualityCareCompareLocation(string? Address1, string? City, string? State, string Zip5);
