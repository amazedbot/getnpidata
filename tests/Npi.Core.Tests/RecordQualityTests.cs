using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Npi.Core.Search;

namespace Npi.Core.Tests;

/// <summary>The record-quality rules (docs/PRODUCT_PLAN.md step 3.1).</summary>
public class RecordQualityTests
{
    private const QualityReason Old5 = QualityReason.NotUpdated5Years;
    private const QualityReason Old10 = QualityReason.NotUpdated5Years | QualityReason.NotUpdated10Years;

    [Theory]
    [InlineData(QualityReason.None, QualityLabel.Current)]
    [InlineData(Old10, QualityLabel.Current)]                                        // old, but practising
    [InlineData(QualityReason.NoActivity, QualityLabel.Current)]                     // recent, but no Medicare etc.
    [InlineData(Old5 | QualityReason.NoActivity, QualityLabel.Check)]
    [InlineData(Old10 | QualityReason.NoActivity, QualityLabel.Stale)]
    [InlineData(QualityReason.Deceased, QualityLabel.Stale)]
    [InlineData(QualityReason.CareCompareAddress, QualityLabel.Check)]
    [InlineData(QualityReason.CareCompareSpecialty, QualityLabel.Check)]
    [InlineData(QualityReason.LicenseLapsed, QualityLabel.Check)]
    [InlineData(QualityReason.PoBox, QualityLabel.Check)]
    [InlineData(QualityReason.NotMapped, QualityLabel.Check)]
    [InlineData(QualityReason.NoPhone, QualityLabel.Check)]
    [InlineData(QualityReason.FacilityClosed, QualityLabel.Check)]                  // closed, but still active elsewhere
    [InlineData(QualityReason.ListedClosed, QualityLabel.Check)]
    [InlineData(Old10 | QualityReason.ListingOnly, QualityLabel.Check)]                 // old, and only a map listing says it is open
    [InlineData(QualityReason.ListedClosed | QualityReason.NoActivity, QualityLabel.Stale)]
    [InlineData(QualityReason.FacilityClosed | QualityReason.NoActivity, QualityLabel.Stale)]
    [InlineData(QualityReason.Reactivated | QualityReason.HomeAddress, QualityLabel.Current)] // for information only
    [InlineData(QualityReason.MedicareStopped, QualityLabel.Current)]                   // stopped billing Medicare, but practising
    [InlineData(QualityReason.MedicareStopped | QualityReason.NoActivity, QualityLabel.Check)] // counts like the 5-year age
    [InlineData(Old5 | QualityReason.MedicareStopped | QualityReason.NoActivity, QualityLabel.Check)] // never stale on its own
    [InlineData(Old10 | QualityReason.MedicareStopped | QualityReason.NoActivity, QualityLabel.Stale)]
    [InlineData(QualityReason.MedicareStopped | QualityReason.PoBox, QualityLabel.Check)]
    [InlineData(QualityReason.OtherNpiCurrent | QualityReason.NoActivity, QualityLabel.Stale)] // another NPI of its organization is current
    [InlineData(QualityReason.OtherNpiCurrent | QualityReason.NoActivity | QualityReason.NoPhone, QualityLabel.Stale)]
    public void Labels_follow_the_rules(QualityReason reasons, QualityLabel expected) => Assert.Equal(expected, RecordQuality.Classify(reasons));

    [Fact]
    public void The_SQL_rule_uses_the_same_bits()
    {
        Assert.Equal("CASE WHEN (r & 6) = 6 OR (r & 66048) <> 0 OR ((r & 12288) <> 0 AND (r & 4) <> 0) THEN 3 " +
                     "WHEN ((r & 4) <> 0 AND (r & 32769) <> 0) OR (r & 29176) <> 0 THEN 2 ELSE 1 END", RecordQuality.LabelSql("r"));
        Assert.Equal(32768, (int)QualityReason.MedicareStopped); // a stored bit
    }

    [Fact]
    public void The_SQL_rule_gives_the_same_label_as_Classify_for_every_combination()
    {
        // LabelSql's conditions evaluated for every combination of the reason bits: each "(r & m)" replaced by its value, the
        // rest (=, <>, AND, OR, parentheses) read by DataTable.Compute. The MySQL tests run the SQL itself on stored labels.
        var sql = RecordQuality.LabelSql("r");
        var whens = Regex.Matches(sql, @"WHEN (.+?) THEN (\d)").Select(m => (Condition: m.Groups[1].Value, Label: (QualityLabel)int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToList();
        var otherwise = (QualityLabel)int.Parse(Regex.Match(sql, @"ELSE (\d) END$").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Equal(2, whens.Count);

        var bits = Enum.GetValues<QualityReason>().Where(r => r != QualityReason.None).Select(r => (int)r).ToArray();
        using var table = new DataTable();
        for (var mask = 0; mask < 1 << bits.Length; mask++)
        {
            var r = bits.Where((_, i) => (mask & (1 << i)) != 0).Aggregate(0, (a, b) => a | b);
            var label = whens.FirstOrDefault(w => (bool)table.Compute(
                Regex.Replace(w.Condition, @"\(r & (\d+)\)", m => (r & int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture)),
                null)).Label;
            Assert.Equal(RecordQuality.Classify((QualityReason)r), label == 0 ? otherwise : label);
        }
    }

    [Fact]
    public void Reasons_read_most_serious_first_without_repeating_the_age()
    {
        Assert.Equal(["Not updated in 10+ years", "No sign of current practice"], RecordQuality.ShortReasons(Old10 | QualityReason.NoActivity));
        Assert.Equal(["Not updated in 5+ years", "Medicare lists practices elsewhere", "No phone", "Home address"],
            RecordQuality.ShortReasons(Old5 | QualityReason.NoPhone | QualityReason.CareCompareAddress | QualityReason.HomeAddress));
        Assert.Equal(["No sign of current practice", "Stopped billing Medicare", "No phone"],
            RecordQuality.ShortReasons(QualityReason.NoActivity | QualityReason.MedicareStopped | QualityReason.NoPhone));
        Assert.Equal(["billed Medicare", "holds a current state license"],
            RecordQuality.SignTexts(QualityActivity.CurrentLicense | QualityActivity.MedicareBilling));
    }

    [Fact]
    public void Filter_values_are_canonical()
    {
        Assert.Equal("notStale", RecordQuality.FilterValue("NOTSTALE"));
        Assert.Equal("stale", RecordQuality.FilterValue("Stale"));
        Assert.Null(RecordQuality.FilterValue("fresh"));
        Assert.Equal("notStale", SearchValidation.Normalize(new SearchFilter { State = "NY", Quality = " notstale " }).Quality);

        Assert.Equal("stale", SearchValidation.Normalize(new SearchFilter { Quality = "stale" }).Quality); // alone too
        Assert.Contains(nameof(SearchFilter.Quality),
            Assert.Throws<SearchValidationException>(() => SearchValidation.Normalize(new SearchFilter { State = "NY", Quality = "fresh" })).Errors.Keys);
    }

    [Fact]
    public void The_filter_is_an_npi_level_check()
    {
        var query = new SearchQuery(SearchValidation.Normalize(new SearchFilter { Classification = "Chiropractor", State = "NY", Quality = "notStale" }),
            ["111N00000X"], null);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM provider_quality q WHERE q.npi = p.npi AND q.label = 3)", query.From, StringComparison.Ordinal);

        // With only a location, the label drives from quality_search (one index range), and isn't checked again.
        var located = new SearchQuery(SearchValidation.Normalize(new SearchFilter { State = "CA", Quality = "stale" }), null, null);
        Assert.StartsWith("FROM (SELECT DISTINCT qs.npi FROM quality_search qs WHERE qs.label IN @qualityLabels AND ", located.From, StringComparison.Ordinal);
        Assert.DoesNotContain("provider_quality q WHERE q.npi = p.npi", located.From, StringComparison.Ordinal); // (the main NPI of an organization is checked, pm)
        Assert.Equal([3], located.Parameters.Get<IReadOnlyList<int>>("qualityLabels"));
        Assert.False(located.MayBeBroad); // sorting a sparse label's candidates beats walking the name index (CA + stale: 20 s → 3.8 s)
        Assert.True(new SearchQuery(SearchValidation.Normalize(new SearchFilter { State = "CA", Quality = "notStale" }), null, null).MayBeBroad);
        Assert.Equal([1, 2], RecordQuality.FilterLabels("notStale"));
    }

    [Fact]
    public void Care_Compare_specialties_map_to_existing_categories()
    {
        var names = SpecialtyCategories.Default.Categories.Select(c => c.Name).ToHashSet();
        Assert.All(RecordQuality.CareCompareCategories, r => Assert.Contains(r.Category, names));
        Assert.Equal(["Dentists", "Doctors (MD, DO)"],
            RecordQuality.CareCompareCategories.Where(r => r.Specialty == "ORAL SURGERY").Select(r => r.Category).Order());
        Assert.Contains(("CARDIOVASCULAR DISEASE (CARDIOLOGY)", "Doctors (MD, DO)"), RecordQuality.CareCompareCategories);
    }

    [Fact]
    public void A_profile_explains_the_label()
    {
        var stale = new ProviderQuality(QualityLabel.Stale, Old10 | QualityReason.NoActivity | QualityReason.Reactivated, QualityActivity.None);
        Assert.Equal(
        [
            new QualityNote("bad", "Not updated in NPPES for 14 years (last update March 2012)"),
            new QualityNote("bad", "No sign of current practice in the public data: no Medicare billing, enrollment or Care Compare listing, no industry payments, no current state license on file"),
            new QualityNote("info", "The NPI was deactivated and later reactivated"),
        ], RecordQuality.Explain(stale, new DateOnly(2012, 3, 1), new DateOnly(2026, 10, 12), isPerson: true));

        var current = new ProviderQuality(QualityLabel.Current, QualityReason.None, QualityActivity.MedicareBilling | QualityActivity.CareCompare);
        Assert.Equal(
        [
            new QualityNote("good", "Updated in NPPES in May 2025"),
            new QualityNote("good", "Signs of current practice: billed Medicare, listed in Medicare's Care Compare"),
            new QualityNote("good", "Medicare's Care Compare lists a practice at or near an address here"),
        ], RecordQuality.Explain(current, new DateOnly(2025, 5, 2), new DateOnly(2026, 10, 12), isPerson: true,
            new QualityCareCompare("FAMILY PRACTICE", [new QualityCareCompareLocation("1 MAIN ST", "BABYLON", "NY", "11702")])));

        // Medicare billing stopped: with no sign of practice it counts against the record; with one, it is a note.
        var history = new MedicareHistory(2013, 2024,
            [new(2013, true, true, 120, 900), new(2014, true, false, 98, null), new(2015, true, false, null, null), new(2016, true, false, 40, null)]);
        var check = new ProviderQuality(QualityLabel.Check, Old5 | QualityReason.NoActivity | QualityReason.MedicareStopped, QualityActivity.None);
        Assert.Contains(new QualityNote("bad", "Last billed Medicare in 2016 (Part B); last Part D prescription in 2013; none in the CMS data for 2017–2024"),
            RecordQuality.Explain(check, new DateOnly(2016, 3, 1), new DateOnly(2026, 10, 12), isPerson: true, medicare: history));
        var licensed = new ProviderQuality(QualityLabel.Current, QualityReason.MedicareStopped, QualityActivity.CurrentLicense);
        Assert.Contains(new QualityNote("info", "Billed Medicare in an earlier year, but not in the newest CMS data"),
            RecordQuality.Explain(licensed, new DateOnly(2025, 3, 1), new DateOnly(2026, 10, 12), isPerson: true));
        Assert.DoesNotContain(RecordQuality.Explain(current, new DateOnly(2025, 5, 2), new DateOnly(2026, 10, 12), isPerson: true, medicare: history),
            n => n.Text.Contains("Last billed", StringComparison.Ordinal)); // only with the reason
    }

    [Fact]
    public void Medicare_history_reads_as_years_and_the_last_billing()
    {
        var gaps = new MedicareHistory(2013, 2024,
        [
            new(2013, true, false, 30, null), new(2014, true, false, 31, null), new(2015, true, false, 33, null), new(2016, true, true, 35, 20),
            new(2019, false, true, null, 40), new(2021, true, false, 12, null), new(2022, true, false, 14, null), new(2023, true, false, 15, null),
            new(2024, true, false, 18, null),
        ]);
        Assert.Equal("2013–2016, 2019, 2021–2024", gaps.Spans);
        Assert.False(gaps.Stopped);
        Assert.Equal((2013, 2024), (gaps.FirstYear, gaps.LastYear));
        Assert.Equal("Last billed Medicare in 2024 (Part B); last Part D prescription in 2019", gaps.LastBilledText);

        var prescriber = new MedicareHistory(2013, 2024, [new(2015, true, false, 11, null), new(2018, false, true, null, 77)]);
        Assert.True(prescriber.Stopped);
        Assert.Equal("Last prescribed under Medicare Part D in 2018; last billed Part B in 2015", prescriber.LastBilledText);
        Assert.Equal("Last prescribed under Medicare Part D in 2018; last billed Part B in 2015; none in the CMS data for 2019–2024",
            RecordQuality.MedicareStoppedText(prescriber));
        Assert.Equal("Last billed Medicare in 2023 (Part B and Part D); none in the CMS data for 2024",
            RecordQuality.MedicareStoppedText(new MedicareHistory(2013, 2024, [new(2023, true, true, 50, 60)])));
        Assert.Null(new MedicareHistory(2013, 2024, []).LastBilledText);
    }
}
