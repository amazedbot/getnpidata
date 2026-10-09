using Npi.Core.Search;

namespace Npi.Core.Tests;

public class MapServiceTests
{
    private static ProviderSummary Provider(string npi, string? zip, string name = "SMITH, JANE") =>
        new(npi, 1, name, "MD", "Chiropractor", "1 MAIN ST", null, "AMITYVILLE", "NY", zip, null, null, "F", null, null);

    [Theory]
    [InlineData("11701", "11701")]
    [InlineData("11701-1234", "11701")]
    [InlineData("117011234", null)] // the summary formats ZIP+4 with a dash
    [InlineData("1170", null)]
    [InlineData("A1701", null)]
    [InlineData(null, null)]
    public void Zip5_takes_the_five_digit_zip(string? zip, string? expected) => Assert.Equal(expected, MapService.Zip5(zip));

    [Theory]
    [InlineData(40.7, -73.4, true)]
    [InlineData(-90, 180, true)]
    [InlineData(90.01, 0, false)]
    [InlineData(0, -180.5, false)]
    [InlineData(double.NaN, 0, false)]
    [InlineData(0, double.PositiveInfinity, false)]
    public void Coordinates_must_be_on_earth(double lat, double lon, bool valid) => Assert.Equal(valid, MapService.IsValidCoordinate(lat, lon));

    [Fact]
    public void Pins_group_the_page_by_zip_in_page_order_and_skip_providers_without_a_point()
    {
        var centroids = new Dictionary<string, ZipPoint>
        {
            ["11701"] = new("11701", 40.68, -73.41),
            ["11201"] = new("11201", 40.69, -73.99),
        };
        ProviderSummary[] page =
        [
            Provider("1000000004", "11201-1234", "O'BRIEN, <JOSÉ>"),
            Provider("1000000012", "11701"),
            Provider("1000000020", "99999"),   // no centroid
            Provider("1000000038", null),      // no ZIP (non-US)
            Provider("1000000046", "11201"),
        ];

        var pins = MapService.GroupPins(page, centroids);

        Assert.Equal(["11201", "11701"], pins.Select(p => p.Zip));
        Assert.Equal(["1000000004", "1000000046"], pins[0].Providers.Select(p => p.Npi));
        Assert.Equal("O'BRIEN, <JOSÉ>", pins[0].Providers[0].Name);
        Assert.Equal("1 MAIN ST, AMITYVILLE", pins[0].Providers[0].Address);
        Assert.Equal((40.69, -73.99), (pins[0].Lat, pins[0].Lon));
        Assert.Equal(3, pins.Sum(p => p.Providers.Count));
    }
}
