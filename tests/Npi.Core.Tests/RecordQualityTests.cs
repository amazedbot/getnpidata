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
    [InlineData(QualityReason.ListedClosed | QualityReason.NoActivity, QualityLabel.Stale)]
    [InlineData(QualityReason.FacilityClosed | QualityReason.NoActivity, QualityLabel.Stale)]
    [InlineData(QualityReason.Reactivated | QualityReason.HomeAddress, QualityLabel.Current)] // for information only
    public void Labels_follow_the_rules(QualityReason reasons, QualityLabel expected) => Assert.Equal(expected, RecordQuality.Classify(reasons));

    [Fact]
    public void The_SQL_rule_uses_the_same_bits()
    {
        Assert.Equal("CASE WHEN (r & 6) = 6 OR (r & 512) <> 0 OR ((r & 12288) <> 0 AND (r & 4) <> 0) THEN 3 WHEN (r & 5) = 5 OR (r & 12792) <> 0 THEN 2 ELSE 1 END", RecordQuality.LabelSql("r"));
    }

    [Fact]
    public void Reasons_read_most_serious_first_without_repeating_the_age()
    {
        Assert.Equal(["Not updated in 10+ years", "No sign of current practice"], RecordQuality.ShortReasons(Old10 | QualityReason.NoActivity));
        Assert.Equal(["Not updated in 5+ years", "Medicare lists practices elsewhere", "No phone", "Home address"],
            RecordQuality.ShortReasons(Old5 | QualityReason.NoPhone | QualityReason.CareCompareAddress | QualityReason.HomeAddress));
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

        var alone = Assert.Throws<SearchValidationException>(() => SearchValidation.Normalize(new SearchFilter { Quality = "stale" }));
        Assert.Equal([nameof(SearchFilter.Quality)], alone.Errors.Keys);
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
        Assert.DoesNotContain("provider_quality", located.From, StringComparison.Ordinal);
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
    }
}
