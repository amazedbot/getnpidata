using Npi.Loader.Load;

namespace Npi.Loader.Tests;

public class DeactivationReportTests
{
    // Legacy defect #6: the VB loader skipped exactly two header rows. The real report (Sep 2026) has a
    // title row and a column header row; the reader must find the data wherever it starts.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Data_starts_at_the_first_npi_row(int headerRows)
    {
        var rows = Enumerable.Range(0, headerRows).Select(i => new object?[] { i == 0 ? "NPPES Deactivated Records as of Sep 14 2026" : "NPI", "NPPES Deactivation Date" })
            .Concat([["1730979303", "09/14/2026"], ["1386161024", "09/13/2026"]]);

        var report = DeactivationReport.Read(new MemoryStream(XlsxWriter.Write(rows)));

        Assert.Equal(
        [
            new DeactivatedNpi("1730979303", new DateOnly(2026, 9, 14)),
            new DeactivatedNpi("1386161024", new DateOnly(2026, 9, 13)),
        ], report);
    }

    [Fact]
    public void Numeric_cells_and_excel_dates_are_accepted()
    {
        var xlsx = XlsxWriter.Write([["NPI", "Date"], [1730979303d, new DateTime(2005, 5, 23).ToOADate()]]);

        var report = DeactivationReport.Read(new MemoryStream(xlsx));

        Assert.Equal(new DeactivatedNpi("1730979303", new DateOnly(2005, 5, 23)), Assert.Single(report));
    }

    [Fact]
    public void Blank_rows_are_skipped_and_duplicates_kept_once()
    {
        var xlsx = XlsxWriter.Write([["1730979303", "09/14/2026"], [null, null], ["1730979303", "09/14/2026"], ["1386161024", "09/13/2026"]]);

        Assert.Equal(2, DeactivationReport.Read(new MemoryStream(xlsx)).Count);
    }

    [Theory]
    [InlineData("173097930", "09/14/2026")]   // 9 digits
    [InlineData("1730979303", "not a date")]
    [InlineData("1730979303", "")]
    public void An_invalid_row_after_the_data_starts_rejects_the_report(string npi, string date)
    {
        var xlsx = XlsxWriter.Write([["NPI", "Date"], ["1386161024", "09/13/2026"], [npi, date]]);

        Assert.Throws<InvalidDataException>(() => DeactivationReport.Read(new MemoryStream(xlsx)));
    }

    [Fact]
    public void A_report_without_npis_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => DeactivationReport.Read(new MemoryStream(XlsxWriter.Write([["NPI", "Date"]]))));
    }
}
