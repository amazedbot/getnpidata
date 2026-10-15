using Npi.Core.Search;

namespace Npi.Core.Tests;

/// <summary>An NPI's record over the years (docs/PRODUCT_PLAN.md step 3.4): each change in plain words, and the lookup's "changed since" columns.</summary>
public class ProviderHistoryTests
{
    private const string Chiro = "111N00000X", Dentist = "122300000X";

    private static ProviderHistoryVersion V(DateOnly from, string status = ProviderHistoryReader.Active, string? state = "NY", string? zip = "11735",
        string? city = "FARMINGDALE", string primary = Chiro, string[]? codes = null, HistoryPlace[]? secondary = null, int entity = 1,
        DateOnly? lastUpdate = null, DateOnly? deactivation = null, DateOnly? reactivation = null) =>
        new(from, null, status, status == ProviderHistoryReader.NotListed ? null : entity,
            status == ProviderHistoryReader.Active ? new HistoryPlace(state, zip, city, "US") : null, secondary ?? [],
            status == ProviderHistoryReader.Active ? primary : null, status == ProviderHistoryReader.Active ? primary + " name" : null,
            status == ProviderHistoryReader.Active ? codes ?? [primary] : [], new DateOnly(2005, 5, 23), lastUpdate, deactivation, reactivation, "CMS NPPES monthly file", []);

    private static IReadOnlyList<string> Texts(ProviderHistoryVersion? before, ProviderHistoryVersion now, DateOnly? secondaryFrom = null, bool inFirstFile = false) =>
        ProviderHistoryReader.Describe(before, now, secondaryFrom, inFirstFile).Select(c => c.Text).ToList();

    [Fact]
    public void A_first_version_says_whether_it_was_in_the_archive_s_first_file()
    {
        Assert.Equal(["In the archive's first file (NPI issued May 23, 2005)"], Texts(null, V(new DateOnly(2007, 11, 12)), inFirstFile: true));
        Assert.Equal(["First listed (NPI issued May 23, 2005)"], Texts(null, V(new DateOnly(2009, 3, 8))));
        Assert.Equal(["First listed as deactivated (on March 1, 2013)"],
            Texts(null, V(new DateOnly(2013, 4, 7), ProviderHistoryReader.Deactivated, deactivation: new DateOnly(2013, 3, 1))));
    }

    [Fact]
    public void Moves_specialties_and_status_are_described_in_plain_words()
    {
        var first = V(new DateOnly(2011, 9, 11), lastUpdate: new DateOnly(2005, 5, 23));
        var moved = V(new DateOnly(2011, 11, 13), zip: "11201", city: "BROOKLYN", primary: Dentist, codes: [Dentist, Chiro], lastUpdate: new DateOnly(2011, 10, 1));
        Assert.Equal(
        [
            "Primary practice location: Farmingdale, NY 11735 → Brooklyn, NY 11201",
            "Primary specialty: 111N00000X name → 122300000X name",
            "Updated in NPPES on October 1, 2011",
        ], Texts(first, moved));

        // Only the update date: said so.
        Assert.Equal(["Record updated in NPPES on June 1, 2012 (no change in the location, specialty or status)"],
            Texts(moved, moved with { ListedFrom = new DateOnly(2012, 6, 10), LastUpdateDate = new DateOnly(2012, 6, 1) }));

        var gone = V(new DateOnly(2012, 1, 8), ProviderHistoryReader.NotListed);
        Assert.Equal(["No longer listed: deactivated (until November 2012 the files left deactivated NPIs out)"], Texts(moved, gone));
        var listed = V(new DateOnly(2012, 12, 9), ProviderHistoryReader.Deactivated, deactivation: new DateOnly(2011, 12, 15));
        Assert.Equal(["Listed again as deactivated on December 15, 2011"], Texts(gone, listed));
        var back = V(new DateOnly(2014, 2, 9), reactivation: new DateOnly(2014, 1, 20));
        Assert.Equal(["Reactivated on January 20, 2014, practising in Farmingdale, NY 11735"], Texts(listed, back));
        Assert.Equal(["Deactivated on March 3, 2015"], Texts(back, V(new DateOnly(2015, 4, 12), ProviderHistoryReader.Deactivated, deactivation: new DateOnly(2015, 3, 3))));
    }

    [Fact]
    public void Secondary_locations_are_new_in_the_first_file_that_lists_them_and_then_added_or_dropped()
    {
        var step = new DateOnly(2018, 9, 9);
        var nj = new HistoryPlace("NJ", "07001", "AVENEL", null);
        var ct = new HistoryPlace("CT", null, null, null);
        var before = V(new DateOnly(2018, 8, 12));
        var first = V(step, secondary: [nj]);
        Assert.Equal(["Secondary practice locations listed for the first time (the files list them from September 2018): Avenel, NJ 07001"], Texts(before, first, step));
        Assert.Equal(["Practice location added: CT", "Practice location dropped: Avenel, NJ 07001"], Texts(first, V(new DateOnly(2018, 10, 7), secondary: [ct]), step));
        Assert.Equal("outside the US (CA)", new HistoryPlace(null, null, null, "CA").ToString());
    }

    [Fact]
    public void Lookup_rows_carry_the_changes_as_two_columns_when_asked()
    {
        var row = new LookupRow("1234567893", LookupStatus.Deactivated, null)
        {
            DeactivationDate = new DateOnly(2025, 3, 1),
            ChangedSince = new DateOnly(2020, 1, 1),
            Changes = [new HistoryChange(new DateOnly(2021, 5, 9), HistoryChangeKinds.Moved, "Primary practice location: NY 11735 → NY 11201")],
        };
        Assert.Equal(ProviderCsv.LookupHeader.Length + 2, ProviderCsv.LookupChangesHeader.Length);
        Assert.Equal(["1234567893", "Deactivated", "2025-03-01", null, null, "2020-01-01", "2021-05-09 Primary practice location: NY 11735 → NY 11201"],
            ProviderCsv.LookupValues(row, withChanges: true).ToList());
        Assert.Equal(5, ProviderCsv.LookupValues(row).Count());
        Assert.Equal("none", ProviderCsv.ChangesText(row with { Changes = [] }));
        Assert.Null(ProviderCsv.ChangesText(row with { Changes = null }));
        Assert.Equal("moved", HistoryChangeKinds.Brief(HistoryChangeKinds.Moved));
        Assert.Equal("moved", HistoryChangeKinds.Brief("address"));
        Assert.Equal("state license renewed", HistoryChangeKinds.Brief("license_renewed"));
    }
}
