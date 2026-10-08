using System.Text;
using System.Text.RegularExpressions;
using Npi.Loader.Csv;
using Npi.Loader.Db;

namespace Npi.Loader.Tests;

public partial class CsvHeaderTests
{
    [Theory]
    [InlineData("\"NPI\",\"Created Date\"\n\"1\",\"2\"\n", "\n")]
    [InlineData("\"NPI\",\"Created Date\"\r\n\"1\",\"2\"\r\n", "\r\n")]
    public void Header_and_line_terminator_are_detected(string csv, string terminator)
    {
        var header = CsvHeader.Read(new MemoryStream(Encoding.UTF8.GetBytes(csv)));

        Assert.Equal(["NPI", "Created Date"], header.Columns);
        Assert.Equal(terminator, header.LineTerminator);
    }

    [Fact]
    public void Quoted_fields_may_contain_commas_and_doubled_quotes()
    {
        Assert.Equal(["a,b", "say \"hi\"", "", "plain"], CsvHeader.ParseLine("\"a,b\",\"say \"\"hi\"\"\",\"\",plain"));
    }

    [Fact]
    public void A_file_without_a_complete_header_line_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => CsvHeader.Read(new MemoryStream(Encoding.UTF8.GetBytes("\"NPI\",\"x\""))));
    }

    [Theory]
    [InlineData("Provider Organization Name (Legal Business Name)", "Provider_Organization_Name_Legal_Business_Name")]
    [InlineData("Employer Identification Number (EIN)", "Employer_Identification_Number_EIN")]
    [InlineData("Provider Last Name (Legal Name)", "Provider_Last_Name_Legal_Name")]
    [InlineData("Healthcare Provider Taxonomy Code_1", "Healthcare_Provider_Taxonomy_Code_1")]
    [InlineData("Provider Business Mailing Address Country Code (If outside U.S.)", "Provider_Business_Mailing_Address_Country_Code")]
    [InlineData("Provider Sex Code", "Provider_Gender_Code")]
    [InlineData("Provider Secondary Practice Location Address-  Address Line 2", "Provider_Secondary_Practice_Location_Address_Line_2")]
    [InlineData("Provider Secondary Practice Location Address - City Name", "Provider_Secondary_Practice_Location_Address_City_Name")]
    [InlineData("Created Date", "Created_Date")]
    public void Headers_map_to_column_names(string header, string column) => Assert.Equal(column, HeaderMapper.ToColumnName(header));

    // The real V2 headers (copied from the 092826_100426 weekly) must map onto the baseline tables.
    [Theory]
    [InlineData("npidata_pfile_v2_fileheader.csv", "npidata", 330)]
    [InlineData("othername_pfile_v2_fileheader.csv", "other_names", 4)]
    [InlineData("pl_pfile_v2_fileheader.csv", "practice_locations", 10)]
    public void Real_v2_headers_map_onto_the_baseline_schema(string fixture, string table, int expectedColumns)
    {
        var header = CsvHeader.Read(File.OpenRead(Fixtures.Path(fixture)));
        var columns = BaselineColumns(table);

        var mapped = HeaderMapper.MapToTable(header.Columns, columns, table);

        Assert.Equal(expectedColumns, mapped.Count);
        Assert.Equal("\n", header.LineTerminator);
        if (table == "npidata")
        {
            Assert.Equal(columns, mapped); // same order as the table
        }
    }

    [Fact]
    public void An_unknown_header_fails_the_load_instead_of_being_dropped()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            HeaderMapper.MapToTable(["NPI", "Brand New CMS Field"], ["NPI"], "npidata"));
        Assert.Contains("Brand_New_CMS_Field", ex.Message);
    }

    [Fact]
    public void Two_headers_mapping_to_one_column_fail()
    {
        Assert.Throws<InvalidDataException>(() => HeaderMapper.MapToTable(["Created Date", "Created-Date"], ["Created_Date"], "t"));
    }

    private static List<string> BaselineColumns(string table)
    {
        var baseline = MigrationRunner.LoadEmbedded().Single(m => m.Version == 1).Sql;
        var body = Regex.Match(baseline, $@"CREATE TABLE IF NOT EXISTS `{table}` \((?<body>.*?)\n\) ENGINE", RegexOptions.Singleline).Groups["body"].Value;
        return ColumnLine().Matches(body).Select(m => m.Groups[1].Value).Where(c => c != "ID").ToList();
    }

    [GeneratedRegex(@"^\s+`([^`]+)`", RegexOptions.Multiline)]
    private static partial Regex ColumnLine();
}
