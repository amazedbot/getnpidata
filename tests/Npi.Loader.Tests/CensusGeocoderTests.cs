using Npi.Loader.Geocoding;

namespace Npi.Loader.Tests;

public class CensusGeocoderTests
{
    private static readonly GeocodeRequest[] Batch =
    [
        new("k1", "1 DAVISON AVE W", "OCEANSIDE", "NY", "11572"),
        new("k2", "10 W 46TH ST STE 1402", "NEW YORK", "NY", "10036"),
        new("k3", "1000 NORTHERN BLVD, STE \"265\"", null, "NY", "11021"),
        new("k4", "166 BEACH 121ST ST", "ROCKAWAY PARK", "NY", "11694"),
    ];

    [Fact]
    public void Request_csv_numbers_rows_and_quotes_commas_and_quotes()
    {
        var lines = CensusGeocoder.WriteBatch(Batch).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
        [
            "1,1 DAVISON AVE W,OCEANSIDE,NY,11572",
            "2,10 W 46TH ST STE 1402,NEW YORK,NY,10036",
            "3,\"1000 NORTHERN BLVD, STE \"\"265\"\"\",,NY,11021",
            "4,166 BEACH 121ST ST,ROCKAWAY PARK,NY,11694",
        ], lines);
    }

    [Fact]
    public void Answers_map_back_to_keys_with_coordinates_only_for_matches()
    {
        // Real Census output (Oct 2026), in its own order; plus a line with an unknown id and a garbled one.
        const string body = """
            "2","10 W 46TH ST STE 1402, NEW YORK, NY, 10036","Match","Exact","10 W 46TH ST, NEW YORK, NY, 10036","-73.979385108515,40.756117910986","59657140","L"
            "1","1 DAVISON AVE W, OCEANSIDE, NY, 11572","Match","Non_Exact","1 DAVISON AVE W, OCEANSIDE, NY, 11572","-73.641442555307,40.641860135598","147681047","R"
            "3","1000 NORTHERN BLVD, STE 265, , NY, 11021","No_Match"
            "4","166 BEACH 121ST ST, ROCKAWAY PARK, NY, 11694","Tie"
            "9","SOMEWHERE ELSE","Match","Exact","X","-70,40","1","L"
            "x","garbled
            """;

        var results = CensusGeocoder.ParseResults(body, Batch).OrderBy(r => r.Key).ToList();

        Assert.Equal(["k1", "k2", "k3", "k4"], results.Select(r => r.Key));
        Assert.Equal(new GeocodeResult("k1", "Match", "Non_Exact", "1 DAVISON AVE W, OCEANSIDE, NY, 11572", 40.641860135598, -73.641442555307), results[0]);
        Assert.Equal((40.756117910986, -73.979385108515), (results[1].Lat!.Value, results[1].Lon!.Value));
        Assert.Equal(new GeocodeResult("k3", "No_Match", null, null, null, null), results[2]);
        Assert.Equal(new GeocodeResult("k4", "Tie", null, null, null, null), results[3]);
    }
}
