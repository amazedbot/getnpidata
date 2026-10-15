using Npi.Loader.Geocoding;
using Npi.Loader.Projection;

namespace Npi.Loader.Tests;

/// <summary>Which open Overture listings confirm that a business operates (owner, 2026-10-15).</summary>
public class BusinessListingTests
{
    [Theory]
    [InlineData("meta 2026-09-14", true)]                          // kept by the business
    [InlineData("AllThePlaces 2026-09-09", true)]                  // read from the business's own site
    [InlineData("PinMeTo 2026-09-15", true)]
    [InlineData("Microsoft 2024-06-28", true)]                     // seen by Bing within 3 years
    [InlineData("Microsoft 2011-08-22", false)]                    // last seen 15 years ago
    [InlineData("Foursquare 2023-09-23", true)]
    [InlineData("Foursquare 2023-09-22", false)]
    [InlineData("BrightQuery 2026-09-17", false)]                  // a business registration: 3 Village Chiropractic, Setauket
    [InlineData("BrightQuery 2026-09-17;Microsoft 2025-10-06", true)]
    [InlineData("Microsoft ", false)]                              // no date
    [InlineData("SomethingNew 2026-09-01", false)]
    [InlineData("", false)]
    [InlineData(null, false)]                                      // sources not recorded yet (before migration 099)
    public void A_listing_counts_only_when_a_source_keeps_up_with_the_business(string? sources, bool confirms) =>
        Assert.Equal(confirms, BusinessListing.Confirms(sources, "2026-09-23.1"));

    [Fact]
    public void The_listing_date_is_the_newest_of_its_own_sources()
    {
        Assert.Equal(new DateOnly(2025, 10, 6), OvertureMatcher.PlaceUpdated("Microsoft 2011-08-22;Foursquare 2025-10-06"));
        Assert.Null(OvertureMatcher.PlaceUpdated("Microsoft "));
        Assert.Null(OvertureMatcher.PlaceUpdated(null));
    }
}
