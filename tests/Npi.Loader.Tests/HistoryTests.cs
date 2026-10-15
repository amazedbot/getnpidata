using System.Text;
using Npi.Loader.History;

namespace Npi.Loader.Tests;

/// <summary>
/// The NPPES history (docs/PRODUCT_PLAN.md step 3.4) without a database: archived file names of every era, the npidata
/// layouts of 2007 to now, the comparison of one file with the next, and which months to process or record as missing.
/// </summary>
public sealed class HistoryTests
{
    private const string Chiro = "111N00000X", Dentist = "122300000X", Family = "207Q00000X";
    private const string A = "1000000004", B = "1000000012", C = "1000000020", D = "1000000038";

    private static DateOnly M(int year, int month) => new(year, month, 1);

    [Theory]
    [InlineData("NPPES_Data_Dissemination_Nov_2007.zip", 2007, 11)]
    [InlineData("NPPES_Data_Dissemination_2011_Dec.zip", 2011, 12)]
    [InlineData("NPPES_Data_Dissemination_2012_Jan.zip", 2012, 1)]
    [InlineData("NPPES_Data_Dissemination_Sept_2015.zip", 2015, 9)]
    [InlineData("NPPES_Data_Dissemination_June_2015.zip", 2015, 6)]
    [InlineData("NPPES_Data_Dissemination_Jan_2016.zip", 2016, 1)]
    [InlineData("NPPES_Data_Dissemination_August_2016.zip", 2016, 8)]
    [InlineData("nppes_data_dissemination_march_2011.zip", 2011, 3)]
    [InlineData("NPPES_Data_Dissemination_January_2025_V2.zip", 2025, 1)]
    [InlineData("NPPES_Data_Disseminat_April_2021.zip", 2021, 4)]
    [InlineData("npi_raw_201701.zip", 2017, 1)]
    public void Monthly_file_names_of_every_era_give_their_month(string name, int year, int month) =>
        Assert.Equal(M(year, month), HistoryCatalog.PeriodOf(name));

    [Theory]
    [InlineData("NPPES_Data_Dissemination_011314_011914_Weekly.zip")]
    [InlineData("NPPES_Deactivated_NPI_Report_021013.zip")]
    [InlineData("npi200711.csv.zip")]
    [InlineData("NPPES_Data_Dissemination_Foo_2015.zip")]
    [InlineData("npi_raw_201713.zip")]
    public void Other_names_are_not_monthly_files(string name) => Assert.Null(HistoryCatalog.PeriodOf(name));

    [Fact]
    public void Cms_candidate_names_cover_the_spellings_used()
    {
        Assert.Equal("NPPES_Data_Dissemination_September_2016.zip", HistoryCatalog.CmsCandidates(M(2016, 9))[0]);
        Assert.Contains("NPPES_Data_Dissemination_Sept_2015.zip", HistoryCatalog.CmsCandidates(M(2015, 9)));
        Assert.Contains("NPPES_Data_Dissemination_June_2015.zip", HistoryCatalog.CmsCandidates(M(2015, 6)));
        Assert.Contains("NPPES_Data_Dissemination_Jan_2016.zip", HistoryCatalog.CmsCandidates(M(2016, 1)));
        // From 2025 both versions exist until March 2026: V2 (wider fields) first.
        Assert.Equal("NPPES_Data_Dissemination_January_2025_V2.zip", HistoryCatalog.CmsCandidates(M(2025, 1))[0]);
        Assert.Equal("NPPES_Data_Dissemination_December_2016.zip", HistoryCatalog.CmsCandidates(M(2016, 12))[0]);
    }

    [Fact]
    public void A_directory_listing_gives_the_monthly_zips_only()
    {
        var files = HistoryCatalog.ParseListing(Fixtures.Text("history/nber_listing.html"));
        Assert.Equal(
            [("NPPES_Data_Dissemination_2011_Dec.zip", M(2011, 12)), ("NPPES_Data_Dissemination_April_2011.zip", M(2011, 4)),
             ("NPPES_Data_Dissemination_Sept_2011.zip", M(2011, 9)), ("NPPES_Data_Dissemination_Nov_2011.zip", M(2011, 11))],
            files);
    }

    [Theory]
    [InlineData("npidata_20050523-20071112.csv", true)]
    [InlineData("npidata_20050523-20071112NPIDataDisseminationFileHeader.csv", false)]
    [InlineData("npidata_20050523-20161211FileHeader.csv", false)]
    [InlineData("SR 63782/npidata_20050523-20081110.csv", true)]
    [InlineData("20100110_2213/NPIDATA_20050523-20100110.CSV", true)]
    [InlineData("npidata_pfile_20050523-20180311.csv", true)]
    [InlineData("npidata_pfile_20050523-20180311_fileheader.csv", false)]
    [InlineData("othername_pfile_20050523-20180311.csv", false)]
    [InlineData("NPPES Data Dissemination - Readme.pdf", false)]
    public void The_data_entry_is_found_in_any_folder_and_case(string entry, bool data) => Assert.Equal(data, NppesHistoryReader.IsDataEntry(entry));

    [Fact]
    public void The_data_date_comes_from_the_csv_name()
    {
        Assert.Equal(new DateOnly(2007, 11, 12), NppesHistoryReader.DataDateOf("npidata_20050523-20071112.csv"));
        Assert.Equal(new DateOnly(2018, 3, 11), NppesHistoryReader.DataDateOf("x/npidata_pfile_20050523-20180311.csv"));
        Assert.Null(NppesHistoryReader.DataDateOf("npidata.csv"));
    }

    [Theory]
    [InlineData(HistoryFiles.Layout.Y2007, 187)]
    [InlineData(HistoryFiles.Layout.Y2011, 314)]
    [InlineData(HistoryFiles.Layout.Y2016, 329)]
    [InlineData(HistoryFiles.Layout.V2, 330)]
    public void Every_layout_maps_and_reads_the_same_fields(HistoryFiles.Layout layout, int columns)
    {
        Assert.Equal(columns, HistoryFiles.Header(layout).Count);
        var csv = new HistoryFiles()
            .Active(A, "NY", "117354321", "05/23/2005", "07/08/2011", [Chiro, Family], primarySlot: 2)
            .Active(B, "ny", "11201", "09/05/2011", "09/05/2011", [Dentist])
            .Active(C, "ON", "M5V 2T6", "01/01/2008", "01/01/2008", [Family], country: "CA", entity: "2")
            .Deactivated(D, "03/01/2011")
            .Csv(layout);
        var (rows, summary, values) = Read(csv);

        Assert.Equal((columns, 4L, 1L, 0L), (summary.Columns, summary.Rows, summary.DeactivatedRows, summary.BadRows));
        var a = rows[NpiVersion.Key(1000000004)];
        Assert.Equal((1, HistoryStatus.Active, "NY", 11735 + 1, Family), (a.Entity, a.Status, values.States[a.State], a.Zip, values.Codes[a.Primary]));
        Assert.Equal("111N00000X 207Q00000X", values.SetCodes(a.TaxonomySet));
        Assert.Equal((new DateOnly(2005, 5, 23), new DateOnly(2011, 7, 8)), (NpiVersion.Date(a.Enumeration), NpiVersion.Date(a.LastUpdate)));
        var b = rows[NpiVersion.Key(1000000012)];
        Assert.Equal(("NY", Dentist, 0), (values.States[b.State], values.Codes[b.Primary], b.Deactivation));
        var c = rows[NpiVersion.Key(1000000020)];
        Assert.Equal((2, "ON", "CA", 0), (c.Entity, values.States[c.State], values.Countries[c.Country], c.Zip));    // no ZIP outside the US
        var d = rows[NpiVersion.Key(1000000038)];
        Assert.Equal((HistoryStatus.Deactivated, (byte)0, 0, new DateOnly(2011, 3, 1)), (d.Status, d.Entity, d.TaxonomySet, NpiVersion.Date(d.Deactivation)));
    }

    [Fact]
    public void Quotes_commas_and_line_breaks_inside_values_and_crlf_lines_are_read()
    {
        var header = HistoryFiles.Header(HistoryFiles.Layout.Y2016);
        var columns = header.Select(Npi.Loader.Csv.HeaderMapper.ToColumnName).ToList();
        string Row(string npi, string name, string last) => string.Join(',', columns.Select(c => c switch
        {
            "NPI" => $"\"{npi}\"",
            "Provider_Organization_Name_Legal_Business_Name" => name,
            "Provider_Business_Practice_Location_Address_State_Name" => "\"NY\"",
            "Provider_Business_Practice_Location_Address_Postal_Code" => "\"11735\"",
            "Healthcare_Provider_Taxonomy_Code_1" => $"\"{Chiro}\"",
            "Healthcare_Provider_Taxonomy_Group_15" => last,
            _ => "\"\"",
        }));
        var csv = string.Join(',', header.Select(h => $"\"{h}\"")) + "\r\n"
                  + Row(A, "\"SMITH, JONES \"\"AND\"\" CO\"", "\"\"") + "\r\n"
                  + Row(B, "\"PLAIN\"", "\"a value\r\nacross two lines, with a \"\"quote\"\"\"") + "\r\n"
                  + Row(C, "UNQUOTED", "") + "\r\n";
        var (rows, summary, values) = Read(csv);
        Assert.Equal((3L, 0L), (summary.Rows, summary.BadRows));
        Assert.All(rows.Values, v => Assert.Equal(("NY", 11736, Chiro), (values.States[v.State], v.Zip, values.Codes[v.Primary])));
    }

    [Fact]
    public void A_row_longer_than_the_read_buffer_and_a_last_line_without_a_line_end_are_read()
    {
        var csv = new HistoryFiles().Active(A, "NY", "11735", "05/23/2005", "05/23/2005", [Chiro])
            .Row(B, ("Provider_Business_Practice_Location_Address_State_Name", "NY"), ("Provider_Other_Organization_Name", new string('X', 5 << 20)))
            .Csv(HistoryFiles.Layout.V2).TrimEnd('\n');
        var (rows, summary, _) = Read(csv);
        Assert.Equal(2L, summary.Rows);
        Assert.Equal(2, rows.Count);
    }

    private static (Dictionary<int, NpiVersion> Rows, HistoryFileSummary Summary, HistoryValues Values) Read(string csv)
    {
        var values = new HistoryValues();
        var rows = new Dictionary<int, NpiVersion>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var summary = NppesHistoryReader.Read(stream, "npidata_20050523-20111211.csv", values, (int key, in NpiVersion v) => rows[key] = v, CancellationToken.None);
        return (rows, summary, values);
    }

    // One file compared with the state: the rows given, read from an in-memory CSV.
    private static PeriodDiff Apply(HistoryState state, DateOnly date, HistoryFiles file, HistoryFiles.Layout layout = HistoryFiles.Layout.Y2016) =>
        state.Apply(date, handler =>
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(file.Csv(layout)));
            return NppesHistoryReader.Read(stream, "npidata.csv", state.Dictionary, handler, CancellationToken.None);
        }, minRowRatio: 0);

    [Fact]
    public void Each_file_adds_a_version_only_where_the_fields_changed()
    {
        var state = new HistoryState(new HistoryValues());
        var first = Apply(state, new DateOnly(2011, 9, 11), new HistoryFiles()
            .Active(A, "NY", "11735", "05/23/2005", "05/23/2005", [Chiro])
            .Active(B, "NY", "11201", "09/05/2011", "09/05/2011", [Dentist])
            .Active(C, "NY", "11735", "01/01/2008", "01/01/2008", [Dentist]), HistoryFiles.Layout.Y2011);
        Assert.Equal((3, 3, 0, 3), (first.Versions, first.NewNpis, first.Deactivated, first.Active));

        // A moves; B's primary taxonomy changes; C drops out of the file (deactivated before November 2012: inferred); D is new.
        var second = Apply(state, new DateOnly(2011, 11, 13), new HistoryFiles()
            .Active(A, "NY", "11201", "05/23/2005", "10/01/2011", [Chiro])
            .Active(B, "NY", "11201", "09/05/2011", "11/01/2011", [Dentist, Chiro], primarySlot: 2)
            .Active(D, "NY", "11735", "10/20/2011", "10/20/2011", [Chiro]), HistoryFiles.Layout.Y2011);
        Assert.Equal((4, 1, 1, 1, 1, 1, 3), (second.Versions, second.NewNpis, second.Moved, second.SpecialtyChanged, second.Deactivated, second.Gone, second.Active));
        Assert.True(state.TryGet(NpiVersion.Key(1000000020), out var c));
        Assert.Equal((HistoryStatus.Gone, new DateOnly(2011, 11, 13).DayNumber), (c.Status, c.FromPeriod));

        // The same file again: nothing changes.
        var again = Apply(state, new DateOnly(2011, 12, 11), new HistoryFiles()
            .Active(A, "NY", "11201", "05/23/2005", "10/01/2011", [Chiro])
            .Active(B, "NY", "11201", "09/05/2011", "11/01/2011", [Dentist, Chiro], primarySlot: 2)
            .Active(D, "NY", "11735", "10/20/2011", "10/20/2011", [Chiro]), HistoryFiles.Layout.Y2011);
        Assert.Equal(0, again.Versions);

        // From November 2012 deactivated NPIs are listed: C (gone) gets its date (a version, not a new deactivation) and D is
        // deactivated; B's codes in another order are the same set.
        var third = Apply(state, new DateOnly(2012, 12, 9), new HistoryFiles()
            .Active(A, "NY", "11201", "05/23/2005", "10/01/2011", [Chiro])
            .Active(B, "NY", "11201", "09/05/2011", "11/01/2011", [Chiro, Dentist], primarySlot: 1)
            .Deactivated(C, "10/15/2011")
            .Deactivated(D, "12/01/2012"));
        Assert.Equal((2, 0, 1, 0, 0), (third.Versions, third.NewNpis, third.Deactivated, third.Gone, third.Reactivated));
        Assert.True(state.TryGet(NpiVersion.Key(1000000012), out var b));
        Assert.Equal(Chiro, state.Dictionary.Codes[b.Primary]);                                  // the primary is the switched slot

        var fourth = Apply(state, new DateOnly(2013, 1, 13), new HistoryFiles()
            .Active(A, "NY", "11201", "05/23/2005", "10/01/2011", [Chiro])
            .Active(B, "NY", "11201", "09/05/2011", "11/01/2011", [Chiro, Dentist], primarySlot: 1)
            .Deactivated(C, "10/15/2011")
            .Row(D, ("Entity_Type_Code", "1"), ("Provider_Business_Practice_Location_Address_State_Name", "NY"),
                ("Provider_Business_Practice_Location_Address_Postal_Code", "11735"), ("NPI_Deactivation_Date", "12/01/2012"),
                ("NPI_Reactivation_Date", "01/02/2013"), ("Healthcare_Provider_Taxonomy_Code_1", Chiro)));
        Assert.Equal((1, 1, 3), (fourth.Versions, fourth.Reactivated, fourth.Active));
    }

    [Fact]
    public void A_second_row_for_an_npi_replaces_the_first_and_a_short_file_is_refused()
    {
        var state = new HistoryState(new HistoryValues());
        var diff = Apply(state, new DateOnly(2016, 12, 11), new HistoryFiles()
            .Active(A, "NY", "11735", "05/23/2005", "05/23/2005", [Chiro])
            .Active(A, "NY", "11201", "05/23/2005", "06/01/2016", [Chiro])
            .Active(B, "NY", "11201", "05/23/2005", "05/23/2005", [Chiro]));
        Assert.Equal(2, diff.Versions);
        Assert.True(state.TryGet(NpiVersion.Key(1000000004), out var a));
        Assert.Equal(11202, a.Zip);

        var error = Assert.Throws<InvalidDataException>(() => state.Apply(new DateOnly(2017, 1, 8), handler =>
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new HistoryFiles().Active(A, "NY", "11201", "05/23/2005", "06/01/2016", [Chiro]).Csv(HistoryFiles.Layout.Y2016)));
            return NppesHistoryReader.Read(stream, "npidata.csv", state.Dictionary, handler, CancellationToken.None);
        }));
        Assert.Contains("truncated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_version_is_written_as_a_tab_separated_line()
    {
        var values = new HistoryValues();
        var v = new NpiVersion
        {
            Entity = 1, Status = HistoryStatus.Active, State = values.States.Add("NY"), Country = values.Countries.Add("US"), Zip = 501 + 1,
            Primary = values.Codes.Add(Chiro), Enumeration = new DateOnly(2005, 5, 23).DayNumber, FromPeriod = new DateOnly(2007, 11, 12).DayNumber,
        };
        Span<char> codes = [(char)v.Primary];
        v.TaxonomySet = values.Set(codes);
        var line = new StringBuilder();
        HistoryBuilder.VersionLine(line, NpiVersion.Key(1000000004), v, values);
        Assert.Equal("1000000004\t2007-11-12\t0\t1\tNY\t00501\tUS\t111N00000X\t1\t\\N\t2005-05-23\t\\N\t\\N\t\\N\n", line.ToString());

        HistoryBuilder.VersionLine(line, NpiVersion.Key(1999999999), NpiVersion.GoneVersion() with { FromPeriod = 1 }, values);
        Assert.Equal("1999999999\t0001-01-02\t2\t\\N\t\\N\t\\N\t\\N\t\\N\t\\N\t\\N\t\\N\t\\N\t\\N\t\\N\n", line.ToString());

        v.LocationSet = values.LocationSet([(values.States.Add("NJ"), 7001), (values.States.Add("NY"), 0)]);
        HistoryBuilder.VersionLine(line, NpiVersion.Key(1000000004), v, values);
        Assert.Equal("1000000004\t2007-11-12\t0\t1\tNY\t00501\tUS\t111N00000X\t1\t1\t2005-05-23\t\\N\t\\N\t\\N\n", line.ToString());
    }

    private static Dictionary<DateOnly, string> Statuses(params (DateOnly Month, string Status)[] rows) => rows.ToDictionary(r => r.Month, r => r.Status);

    [Fact]
    public void Months_are_planned_in_order_completed_and_missing_ones_skipped()
    {
        Assert.Empty(HistoryPlan.Skip(Statuses()));
        var statuses = Statuses((M(2007, 11), HistoryPeriodStatus.Completed), (M(2007, 12), HistoryPeriodStatus.Missing),
            (M(2008, 5), HistoryPeriodStatus.Completed), (M(2008, 7), HistoryPeriodStatus.Failed));
        Assert.Equal(M(2008, 5), HistoryPlan.LastCompleted(statuses));
        var skip = HistoryPlan.Skip(statuses);
        Assert.Equal(7, skip.Count);                                                             // 2007-11 to 2008-05
        Assert.DoesNotContain(M(2008, 7), skip);                                                 // failed: tried again
        // 2008-01 to 2008-04 were never recorded: a later month is done, so they can't be added any more.
        Assert.Equal([M(2008, 1), M(2008, 2), M(2008, 3), M(2008, 4)], HistoryPlan.Superseded(statuses));

        // Files found for 2008-07 and 2008-11: the months between them have none; months after the newest may not be out yet.
        Assert.Equal([M(2008, 6), M(2008, 8), M(2008, 9), M(2008, 10)],
            HistoryPlan.Missing(new HashSet<DateOnly> { M(2008, 7), M(2008, 11) }, skip, HistoryPlan.LastCompleted(statuses)));
        Assert.Empty(HistoryPlan.Missing(new HashSet<DateOnly>(), skip, HistoryPlan.LastCompleted(statuses)));
    }

    [Fact]
    public void Runs_monthly_file_is_added_only_as_the_next_month()
    {
        Assert.Equal(HistoryPlan.Append.NotStarted, HistoryPlan.CanAppend(Statuses(), M(2026, 11), out _));
        var statuses = Statuses((M(2026, 9), HistoryPeriodStatus.Completed));
        Assert.Equal(HistoryPlan.Append.AlreadyDone, HistoryPlan.CanAppend(statuses, M(2026, 9), out _));
        Assert.Equal(HistoryPlan.Append.Next, HistoryPlan.CanAppend(statuses, M(2026, 10), out _));
        Assert.Equal(HistoryPlan.Append.Blocked, HistoryPlan.CanAppend(statuses, M(2026, 12), out var first));
        Assert.Equal([M(2026, 10), M(2026, 11)], first);
        statuses[M(2026, 10)] = HistoryPeriodStatus.Missing;
        statuses[M(2026, 11)] = HistoryPeriodStatus.Missing;
        Assert.Equal(HistoryPlan.Append.Next, HistoryPlan.CanAppend(statuses, M(2026, 12), out _));
    }

    [Fact]
    public void Secondary_locations_are_read_from_the_pl_pfile_and_kept_through_a_file_without_one()
    {
        var folder = Path.Combine(Path.GetTempPath(), "npi-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var state = new HistoryState(new HistoryValues());
            PeriodDiff Apply(string zip) => state.Apply(NppesHistoryReader.PeekDataDate(zip)!.Value,
                handler => NppesHistoryReader.Read(zip, state.Dictionary, handler, CancellationToken.None), minRowRatio: 0);

            // August 2018: no pl_pfile yet. A practises in NY only.
            var august = Apply(new HistoryFiles()
                .Active(A, "NY", "11735", "05/23/2005", "05/23/2005", [Chiro])
                .Active(B, "NY", "11201", "05/23/2005", "05/23/2005", [Dentist])
                .Zip(folder, "aug.zip", HistoryFiles.Layout.Y2016, new DateOnly(2018, 8, 12), pfile: true));
            Assert.False(august.File.HasLocations);
            Assert.True(state.TryGet(NpiVersion.Key(1000000004), out var a));
            Assert.Equal(0, a.LocationSet);

            // September 2018: the first pl_pfile. A also practises in NJ (twice, and a foreign row without a ZIP; an over-long state is left out).
            var september = Apply(new HistoryFiles()
                .Active(A, "NY", "11735", "05/23/2005", "05/23/2005", [Chiro])
                .Active(B, "NY", "11201", "05/23/2005", "05/23/2005", [Dentist])
                .Secondary(A, "NJ", "07001").Secondary(A, "nj", "070011234").Secondary(A, "ON", "K1A0B1", "CA").Secondary(A, "ONTARIO", "K1A0B1", "CA")
                .Zip(folder, "sep.zip", HistoryFiles.Layout.Y2016, new DateOnly(2018, 9, 9), pfile: true, locations: true));
            Assert.Equal((true, 4L, 1, 1, 1), (september.File.HasLocations, september.File.SecondaryRows, september.Versions, september.LocationsChanged, september.WithLocations));
            Assert.True(state.TryGet(NpiVersion.Key(1000000004), out a));
            Assert.Equal([("NJ", "07001"), ("ON", "")], state.Dictionary.LocationPairs(a.LocationSet).ToList());

            // A file without a pl_pfile after it: A keeps its locations, nothing changes.
            var october = Apply(new HistoryFiles()
                .Active(A, "NY", "11735", "05/23/2005", "05/23/2005", [Chiro])
                .Active(B, "NY", "11201", "05/23/2005", "05/23/2005", [Dentist])
                .Zip(folder, "oct.zip", HistoryFiles.Layout.Y2016, new DateOnly(2018, 10, 7), pfile: true));
            Assert.Equal(0, october.Versions);

            // B gains A's set (the same id), A drops NJ.
            var november = Apply(new HistoryFiles()
                .Active(A, "NY", "11735", "05/23/2005", "05/23/2005", [Chiro])
                .Active(B, "NY", "11201", "05/23/2005", "05/23/2005", [Dentist])
                .Secondary(A, "ON", "K1A0B1", "CA").Secondary(B, "NJ", "07001").Secondary(B, "ON", "", "CA")
                .Zip(folder, "nov.zip", HistoryFiles.Layout.Y2016, new DateOnly(2018, 11, 11), pfile: true, locations: true));
            Assert.Equal((2, 2), (november.Versions, november.LocationsChanged));
            Assert.True(state.TryGet(NpiVersion.Key(1000000012), out var b));
            Assert.Equal(a.LocationSet, b.LocationSet);
            Assert.Equal(2, state.Dictionary.LocationSetCount);
            Assert.Equal(2, state.Dictionary.TakeNewLocationSets().Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_stored_location_set_keeps_its_id_and_a_new_one_gets_the_next()
    {
        var values = new HistoryValues();
        values.AddStoredLocationSet(7, [("NY", "11735"), ("NJ", "")]);
        var ny = values.States.Add("NY");
        var nj = values.States.Add("NJ");
        Assert.Equal(7, values.LocationSet(new List<(ushort, int)> { (ny, 11736), (nj, 0) }.OrderBy(p => p.Item1).ToList()));
        Assert.Empty(values.TakeNewLocationSets());
        Assert.Equal(8, values.LocationSet([(ny, 11736)]));
        Assert.Equal([8], values.TakeNewLocationSets());
    }
}
