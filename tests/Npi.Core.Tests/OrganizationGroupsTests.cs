using Npi.Core.Search;

namespace Npi.Core.Tests;

/// <summary>Combined organization profiles (docs/PRODUCT_PLAN.md step 3.8): the shared rules.</summary>
public class OrganizationGroupsTests
{
    [Theory]
    [InlineData("330014", "330014")]   // a hospital
    [InlineData("33S014", "330014")]   // its psychiatric unit
    [InlineData("33t014", "330014")]   // its rehabilitation unit, lower-case
    [InlineData("33U014", "330014")]   // swing beds
    [InlineData("33Z305", "331305")]   // a critical access hospital's swing beds
    [InlineData("33M305", "331305")]   // its psychiatric unit
    [InlineData("335001", "335001")]   // a nursing home
    [InlineData("33D001", "33D001")]   // not a unit letter
    [InlineData(" 1234 ", "1234")]     // not a CCN of six characters
    public void A_unit_ccn_stands_for_its_hospital(string ccn, string expected)
    {
        Assert.Equal(expected, OrganizationGroups.FacilityKey(ccn));
        Assert.Equal(expected != ccn.Trim().ToUpperInvariant(), OrganizationGroups.IsUnitCcn(ccn));
    }

    [Theory]
    [InlineData("33S014", "hospital", "Psychiatric unit")]
    [InlineData("33T014", "hospital", "Rehabilitation unit")]
    [InlineData("33Z305", "hospital", "Swing beds")]
    [InlineData("330014", "hospital", "Hospital")]
    [InlineData("335001", "nursing_home", "Nursing home")]
    public void Facility_names_are_plain(string ccn, string kind, string expected) =>
        Assert.Equal(expected, OrganizationGroups.FacilityName(ccn, kind));

    [Fact]
    public void Roles_are_in_plain_words()
    {
        var main = new OrganizationRoleInputs(true, "330014", "hospital", "282N00000X", QualityActivity.MedicareBilling);
        Assert.Equal("Main NPI: Hospital (CCN 330014)", OrganizationGroups.Role(main, main));
        Assert.Equal("Main NPI", OrganizationGroups.Role(main with { Ccn = null }, main));

        // A unit, by its CCN; an NPI with the main NPI's specialty and no sign of practice; another specialty.
        Assert.Equal("Psychiatric unit (CCN 33S014)", OrganizationGroups.Role(new(false, "33S014", "hospital", "282N00000X", QualityActivity.None), main));
        Assert.Equal("Possible duplicate registration", OrganizationGroups.Role(new(false, null, null, "282N00000X", QualityActivity.None), main));
        Assert.Equal("General hospital", OrganizationGroups.Role(new(false, null, null, "282N00000X", QualityActivity.MedicareEnrollment), main));
        Assert.Equal("Retail pharmacy", OrganizationGroups.Role(new(false, null, null, "3336C0003X", QualityActivity.None), main));
        Assert.Equal("Other NPI", OrganizationGroups.Role(new(false, null, null, "999999999X", QualityActivity.None), main));
        Assert.Equal("Other NPI", OrganizationGroups.Role(new(false, null, null, null, QualityActivity.None), main));
    }

    [Fact]
    public void Ties_are_listed_in_plain_words()
    {
        Assert.Equal("same name and address, same Medicare enrollment (PAC ID)",
            OrganizationGroups.Ties(OrganizationTie.MedicareEnrollment | OrganizationTie.NameAddress));
        Assert.Equal("", OrganizationGroups.Ties(0));
    }

    [Theory]
    [InlineData("2007-01-01", "2023-10-11", true)]   // updated three years before the as-of date: flagged
    [InlineData("2007-01-01", "2025-10-11", false)]  // updated a year before: spared
    [InlineData("2025-10-11", "2025-10-11", false)]  // issued a year before: spared
    [InlineData("2025-10-11", "2023-10-11", false)]  // issued a year before, whatever the update
    [InlineData("2007-01-01", "2024-10-10", true)]   // a day over two years
    [InlineData("2007-01-01", "2024-10-11", false)]  // exactly two years: not before the limit, as the other age rules count
    [InlineData(null, null, true)]                   // no dates: counts as old
    public void Another_current_npi_spares_records_issued_or_updated_in_the_last_two_years(string? enumerated, string? updated, bool applies)
    {
        static DateOnly? D(string? s) => s is null ? null : DateOnly.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(applies, RecordQuality.OtherNpiCurrentApplies(D(enumerated), D(updated), new DateOnly(2026, 10, 11)));
        Assert.Equal("COALESCE(p.e < @l, 1) AND COALESCE(p.u < @l, 1)", RecordQuality.OtherNpiCurrentSql("p.e", "p.u", "@l"));
    }

    [Fact]
    public void Another_current_npi_names_that_npi_and_links_to_it()
    {
        var reasons = QualityReason.OtherNpiCurrent | QualityReason.NoActivity;
        Assert.Contains("Another NPI of this organization is current: 1234567893", RecordQuality.ShortReasons(reasons, "1234567893"));
        Assert.Contains(RecordQuality.OtherNpiCurrentText, RecordQuality.ShortReasons(reasons));

        var quality = new ProviderQuality(QualityLabel.Stale, reasons, QualityActivity.None) { OtherCurrentNpi = "1234567893" };
        Assert.Equal(QualityLabel.Stale, RecordQuality.Classify(reasons));
        var note = Assert.Single(RecordQuality.Explain(quality, null, new DateOnly(2026, 10, 12), isPerson: false), n => n.Href is not null);
        Assert.Equal("/provider/1234567893", note.Href);
        Assert.Equal("1234567893", note.LinkText);
        Assert.Equal("bad", note.Kind);
    }
}
