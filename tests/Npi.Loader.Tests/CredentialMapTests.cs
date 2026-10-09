using Npi.Loader.Projection;

namespace Npi.Loader.Tests;

public class CredentialMapTests
{
    // A small slice of the real distribution (provider counts as of Oct 2026, some scaled down).
    private static readonly (string, long)[] Sample =
    [
        ("MD", 621865), ("M.D.", 438080), ("M.D", 17397), ("PHD", 33554), ("PH.D.", 43121), ("MD, PHD", 1942), ("MD-PHD", 300),
        ("MS", 15861), ("M.S.", 11289), ("CCC-SLP", 13648), ("MS, CCC-SLP", 8406), ("MSCCC-SLP", 120), ("CCC/SLP", 90),
        ("PA-C", 113991), ("PAC", 900), ("PT", 71785), ("MSPT", 4000), ("PHARMD", 98377), ("PHARM D", 9916),
        ("ZQX", 3), ("NONE", 500),
    ];

    [Fact]
    public void Known_credentials_are_learned_from_the_data_with_their_most_used_spelling()
    {
        var map = CredentialBuilder.Map(Sample);

        Assert.Equal(["PA-C"], map.Known["PAC"]);       // "PA-C" is used far more than "PAC"
        Assert.Equal(["PhD"], map.Known["PHD"]);        // the conventional spelling of a degree
        Assert.Equal(["MS", "PT"], map.Known["MSPT"]);  // two more common credentials run together
        Assert.False(map.Known.ContainsKey("ZQX"));    // too rare to be a credential
    }

    [Fact]
    public void Every_raw_value_maps_to_its_standard_credentials()
    {
        var map = CredentialBuilder.Map(Sample);

        Assert.Equal(["MD"], map.ByRaw["M.D."]);
        Assert.Equal(["MD", "PhD"], map.ByRaw["MD-PHD"]);
        Assert.Equal(["MS", "CCC-SLP"], map.ByRaw["MSCCC-SLP"]);
        Assert.Equal(["CCC-SLP"], map.ByRaw["CCC/SLP"]);
        Assert.Equal(["PharmD"], map.ByRaw["PHARM D"]);
        Assert.Equal(["ZQX"], map.ByRaw["ZQX"]);      // unknown: kept as written, just not in the dropdown
        Assert.Empty(map.ByRaw["NONE"]);
    }
}
