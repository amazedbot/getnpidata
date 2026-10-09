using Npi.Loader.Geocoding;

namespace Npi.Loader.Tests;

public class OvertureIndexTests
{
    [Theory]
    [InlineData("101 Nicolls Road, Suite 2", "101", "NICOLLS RD")]
    [InlineData("101 NICOLLS RD STE 2", "101", "NICOLLS RD")]
    [InlineData("10 W 46th Street #1402", "10", "W 46TH ST")]
    [InlineData("1000 Northern Blvd., Floor 3", "1000", "NORTHERN BLVD")]
    [InlineData("3 TINKER LANE", "3", "TINKER LN")]
    [InlineData("104-23 Roosevelt Avenue", "104-23", "ROOSEVELT AVE")]
    [InlineData("4802 Tenth Ave", "4802", "10TH AVE")]
    public void Addresses_split_into_house_number_and_normalized_street(string address, string number, string street) =>
        Assert.Equal((number, street), AddressNormalizer.Split(address));

    [Theory]
    [InlineData("STONY BROOK UNIVERSITY HOSPITAL")]
    [InlineData("HEALTH SCIENCES CENTER L4 #060")]
    [InlineData("")]
    [InlineData(null)]
    public void Building_names_have_no_house_number(string? address) => Assert.Null(AddressNormalizer.Split(address));

    [Fact]
    public void Name_tokens_expand_abbreviations_and_drop_unit_codes() =>
        Assert.Equal(new[] { "CENTER", "HEALTH", "SCIENCES" }, AddressNormalizer.NameTokens("HSC Level 4, Rm 060").Order());

    private static OvertureIndex StonyBrook()
    {
        var index = new OvertureIndex();
        index.AddPoint("3", "Tinker Lane", "11733", "Setauket", 40.95574, -73.09634);
        index.AddPoint("100", "Nicolls Road", "11790", "Stony Brook", 40.91422, -73.11633);
        index.AddPlace("Stony Brook University Department of Psychiatry", "101 Nicolls Rd", "11794", "Stony Brook", 40.9092, -73.11513, 1.0);
        index.AddPlace("Stony Brook University Hospital Blood Bank", "101 Nicolls Rd #5000", "11794", null, 40.90904, -73.11526, 0.97);
        index.AddPlace("Stony Brook Children's Hospital", "101 Nicolls Rd", "11794", null, 40.90915, -73.11642, 0.97);
        index.AddPlace("Stonybrook University Hospital", "Nichols Rd", "11794", null, 40.90574, -73.11733, 0.85);
        index.AddPlace("Stony Brook University Hospital", null, "11794", null, 40.9090, -73.1155, 0.9);
        index.AddPlace("Corner Animal Hospital", "24 Woods Corner Rd", "11733", null, 40.9261, -73.11907, 0.92);
        return index;
    }

    [Fact]
    public void An_address_point_wins_over_everything()
    {
        var match = StonyBrook().Match("3 TINKER LN", "SETAUKET", "11733");
        Assert.Equal(new PointMatch(PointSource.Address, 40.95574, -73.09634, "3 TINKER LN"), match);
    }

    [Fact]
    public void A_mailing_address_of_a_large_site_is_placed_at_its_places()
    {
        // 101 Nicolls Rd is no point on the street, but several places use it: the middle of them.
        var match = StonyBrook().Match("101 NICOLLS RD RM 20", "STONY BROOK", "11794");
        Assert.Equal(PointSource.Place, match?.Source);
        Assert.Equal((40.90915, -73.11526), (match!.Lat, match.Lon));
    }

    [Fact]
    public void A_building_name_is_matched_to_the_closest_place_name_in_the_same_zip()
    {
        var index = StonyBrook();
        Assert.Equal("Stony Brook University Hospital", index.Match("STONY BROOK UNIVERSITY HOSPITAL", "STONY BROOK", "11794")?.Matched);
        var l4 = index.Match("UNIVERSITY HOSPITAL, L4", "STONY BROOK", "11794");
        Assert.Equal(PointSource.PlaceName, l4?.Source);
        Assert.Contains("University Hospital", l4!.Matched, StringComparison.Ordinal); // the shortest name with both words

        Assert.Null(index.Match("UNIVERSITY HOSPITAL", "SETAUKET", "11733"));   // a different ZIP: no guessing
        Assert.Null(index.Match("HOSPITAL", "STONY BROOK", "11794"));           // one word is too little to go on
        Assert.Null(index.Match("WESTCHESTER HALL", "STONY BROOK", "11794"));   // nothing by that name
    }

    [Fact]
    public void An_unknown_house_number_is_left_to_the_census_geocoder() =>
        Assert.Null(StonyBrook().Match("99 TINKER LN", "SETAUKET", "11733"));

    [Fact]
    public void With_candidates_only_the_points_and_places_they_can_use_are_kept()
    {
        var index = new OvertureIndex([("3 TINKER LANE", "SETAUKET", "11733"), ("UNIVERSITY HOSPITAL", "STONY BROOK", "11794")]);
        index.AddPoint("3", "Tinker Ln", "11733", "Setauket", 40.95574, -73.09634);
        index.AddPoint("5", "Tinker Ln", "11733", "Setauket", 40.9560, -73.0965);                      // no practice there
        index.AddPlace("Stony Brook University Hospital", null, "11794", null, 40.9090, -73.1155, 0.9);
        index.AddPlace("Corner Animal Hospital", "24 Woods Corner Rd", "11733", null, 40.9261, -73.11907, 0.92); // not a wanted ZIP for names

        Assert.Equal(2, index.Points); // 3 Tinker Ln, by ZIP and by city
        Assert.Equal(PointSource.Address, index.Match("3 TINKER LN", "SETAUKET", "11733")?.Source);
        Assert.Equal(PointSource.PlaceName, index.Match("UNIVERSITY HOSPITAL", "STONY BROOK", "11794")?.Source);
    }
}
