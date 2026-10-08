using Npi.Loader.Nppes;

namespace Npi.Loader.Tests;

public class NppesFileTests
{
    [Fact]
    public void Monthly_file_is_dated_the_first_of_its_month()
    {
        var file = NppesFileClassifier.Classify("NPPES_Data_Dissemination_September_2026_V2.zip");

        Assert.Equal(new NppesFile("NPPES_Data_Dissemination_September_2026_V2.zip", NppesFileKind.Monthly, new DateOnly(2026, 9, 1)), file);
    }

    [Fact]
    public void Weekly_file_is_dated_the_end_of_its_week()
    {
        var file = NppesFileClassifier.Classify("NPPES_Data_Dissemination_092826_100426_Weekly_V2.zip");

        Assert.NotNull(file);
        Assert.Equal(NppesFileKind.Weekly, file.Kind);
        Assert.Equal(new DateOnly(2026, 10, 4), file.FileDate);
        Assert.Equal(new DateOnly(2026, 9, 28), file.WeekStart);
    }

    [Fact]
    public void Deactivation_report_is_dated_its_report_date()
    {
        var file = NppesFileClassifier.Classify("NPPES_Deactivated_NPI_Report_091426_V2.zip");

        Assert.Equal(NppesFileKind.Deactivation, file?.Kind);
        Assert.Equal(new DateOnly(2026, 9, 14), file?.FileDate);
    }

    [Theory]
    [InlineData("nppes_data_dissemination_september_2026_v2.ZIP")]
    [InlineData("NPPES_DATA_DISSEMINATION_092826_100426_WEEKLY_V2.zip")]
    public void Names_match_case_insensitively(string name) => Assert.NotNull(NppesFileClassifier.Classify(name));

    // Legacy defect #8: V1 files are retired; only _V2 names are accepted.
    [Theory]
    [InlineData("NPPES_Data_Dissemination_September_2026.zip")]
    [InlineData("NPPES_Data_Dissemination_092826_100426_Weekly.zip")]
    [InlineData("NPPES_Deactivated_NPI_Report_091426.zip")]
    [InlineData("NPPES_Data_Dissemination_Septembr_2026_V2.zip")]   // not a month
    [InlineData("NPPES_Data_Dissemination_100426_092826_Weekly_V2.zip")] // ends before it starts
    [InlineData("NPPES_Data_Dissemination_133126_100426_Weekly_V2.zip")] // month 13
    [InlineData("NPPES_Data_Dissemination_September_2026_V2.zip.part")]
    [InlineData("NPI-What-You-Need-To-Know.pdf")]
    public void Other_names_are_not_recognised(string name) => Assert.Null(NppesFileClassifier.Classify(name));

    // Legacy defect #7: "_FileHeader" vs "_fileheader" was matched case-sensitively.
    [Theory]
    [InlineData("npidata_pfile_20260928-20261004.csv", NppesEntryKind.NpiData)]
    [InlineData("NPIDATA_PFILE_20260928-20261004.CSV", NppesEntryKind.NpiData)]
    [InlineData("npidata_pfile_20260928-20261004_fileheader.csv", NppesEntryKind.FileHeader)]
    [InlineData("npidata_pfile_20260928-20261004_FileHeader.csv", NppesEntryKind.FileHeader)]
    [InlineData("othername_pfile_20260928-20261004.csv", NppesEntryKind.OtherName)]
    [InlineData("pl_pfile_20260928-20261004.csv", NppesEntryKind.PracticeLocation)]
    [InlineData("endpoint_pfile_20260928-20261004.csv", NppesEntryKind.Endpoint)]
    [InlineData("NPPES_Data_Dissemination_Readme_v.2.pdf", NppesEntryKind.Readme)]
    [InlineData("subfolder/othername_pfile_x.csv", NppesEntryKind.OtherName)]
    [InlineData("notes.txt", NppesEntryKind.Unknown)]
    [InlineData("taxonomy.csv", NppesEntryKind.Unknown)]
    public void Zip_entries_are_classified_case_insensitively(string name, NppesEntryKind expected) =>
        Assert.Equal(expected, NppesEntryClassifier.Classify(name));
}
