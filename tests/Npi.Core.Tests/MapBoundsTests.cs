using Npi.Core.Search;

namespace Npi.Core.Tests;

public class MapBoundsTests
{
    [Fact]
    public void Bbox_is_parsed_in_leaflet_order_west_south_east_north()
    {
        var b = MapBounds.Parse("-73.5,40.6,-73.3,40.8");
        Assert.Equal(new MapBounds(South: 40.6, West: -73.5, North: 40.8, East: -73.3), b);
        Assert.Null(b!.Problem);
        Assert.Equal((40.7, -73.4), (Math.Round(b.CenterLat, 6), Math.Round(b.CenterLon, 6)));
        Assert.Equal("POLYGON((-73.5 40.6, -73.3 40.6, -73.3 40.8, -73.5 40.8, -73.5 40.6))", b.Polygon);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("a,b,c,d")]
    [InlineData("1,2,3,4,5")]
    public void Malformed_bbox_is_null(string? value) => Assert.Null(MapBounds.Parse(value));

    [Theory]
    [InlineData("-73.3,40.6,-73.5,40.8", "invalid")]   // west > east
    [InlineData("-73.5,40.8,-73.3,40.6", "invalid")]   // south > north
    [InlineData("-73.5,95,-73.3,96", "invalid")]       // not on earth
    [InlineData("-100,30,-70,45", "Zoom in")]          // the eastern US
    [InlineData("-74,40,-70,41", "Zoom in")]           // 4 degrees of longitude
    public void Unsearchable_areas_say_why(string bbox, string problem) =>
        Assert.Contains(problem, MapBounds.Parse(bbox)!.Problem, StringComparison.Ordinal);

    [Fact]
    public void Cells_cover_the_area_on_a_tenth_of_a_degree_grid()
    {
        // Manhattan: rows FLOOR((lat + 90) * 10) = 1307..1308 (40.72, 40.81), columns FLOOR((lon + 180) * 10) = 1059..1060.
        Assert.Equal([1307 * 3600 + 1059, 1307 * 3600 + 1060, 1308 * 3600 + 1059, 1308 * 3600 + 1060], MapBounds.Parse("-74.02,40.72,-73.93,40.81")!.Cells);
        Assert.Equal(26 * 36, MapBounds.Parse("-75.25,40.05,-71.75,42.55")!.Cells.Count);
    }

    [Fact]
    public void Around_keeps_the_centre_and_scales_the_sides()
    {
        var b = MapBounds.Parse("-74,40,-73,41")!.Around(0.5);
        Assert.Equal((40.25, -73.75, 40.75, -73.25), (b.South, b.West, b.North, b.East));
    }
}
