using System.IO.Compression;
using System.Text;
using Npi.Loader.Datasets;

namespace Npi.Loader.Tests;

/// <summary>The T-MSIS Medicaid files and Florida's Provider Master List (docs/PRODUCT_PLAN.md step 3.7), parsed from fixtures with the real headers.</summary>
public sealed class MedicaidSourceTests
{
    private static StreamReader Csv(string fixture)
    {
        var zip = new ZipArchive(File.OpenRead(Fixtures.Path(fixture)), ZipArchiveMode.Read);
        return new StreamReader(HhsOpenData.OpenLargestCsv(zip, fixture), Encoding.UTF8);
    }

    [Fact]
    public void The_dataset_page_gives_the_newest_release_with_its_checksum()
    {
        var segments = HhsOpenData.FindRelease(Fixtures.Text("datasets/hhs_medicaid-provider-enrollment-segments_page_sample.html"), MedicaidEnrollmentSource.Slug);
        Assert.Equal("2026-07-24 sha256 b70930a9a017", segments.Version);
        Assert.Equal("https://stopendataprod.blob.core.windows.net/datasets/medicaid-provider-enrollment-segments/2026-07-24/dataset/medicaid-provider-enrollment-segments.csv.zip",
            segments.Url.AbsoluteUri);
        Assert.Equal("2026-07-24", segments.PeriodEnd);
        var spending = HhsOpenData.FindRelease(Fixtures.Text("datasets/hhs_medicaid-provider-spending_page_sample.html"), MedicaidSpendingSource.Slug);
        Assert.Equal("2026-02-09 sha256 3387c48ea960", spending.Version);

        // A newer release folder wins; another dataset's file on the page doesn't count; no zip at all is an error.
        const string page = """
            \"url\":\"https://blob.test/datasets/medicaid-provider-spending/2026-02-09/dataset/medicaid-provider-spending.csv.zip\",\"checksum\":\"sha256:aaaaaaaaaaaaaaaa\"
            \"url\":\"https://blob.test/datasets/medicaid-provider-spending/2027-03-01/dataset/medicaid-provider-spending.csv.zip\",\"checksum\":\"sha256:bbbbbbbbbbbbbbbb\"
            \"url\":\"https://blob.test/datasets/medicaid-provider-spending-ndc/2028-01-01/dataset/medicaid-provider-spending-ndc.csv.zip\"
            """;
        Assert.Equal("2027-03-01 sha256 bbbbbbbbbbbb", HhsOpenData.FindRelease(page, MedicaidSpendingSource.Slug).Version);
        Assert.Throws<InvalidDataException>(() => HhsOpenData.FindRelease("<html></html>", MedicaidSpendingSource.Slug));
    }

    [Fact]
    public void Csv_lines_split_at_commas_outside_quotes()
    {
        var fields = new List<string>();
        HhsOpenData.Split("1000000038,TX,2024-12-01,,75,\"TERM - STATE EXCLUSION/ DEBARMENT, ETC.\",1,\"A \"\"B\"\"\"", fields);
        Assert.Equal(["1000000038", "TX", "2024-12-01", "", "75", "TERM - STATE EXCLUSION/ DEBARMENT, ETC.", "1", "A \"B\""], fields);
        HhsOpenData.Split("", fields);
        Assert.Equal([""], fields);
        HhsOpenData.Split("a,", fields);
        Assert.Equal(["a", ""], fields);
    }

    [Fact]
    public void Enrollment_segments_become_periods_per_npi_and_state()
    {
        var rows = new List<MedicaidEnrollmentRow>();
        using var reader = Csv("datasets/medicaid_enrollment_segments_sample.csv.zip");
        var summary = MedicaidEnrollmentSegments.Summarize(reader, new DateOnly(2026, 7, 24), rows.Add);

        Assert.Equal(
            [
                // Monthly segments and their CHIP twins, then an open one: one period, still enrolled.
                new MedicaidEnrollmentRow("1000000004", "NY", 1, new DateOnly(2024, 8, 1), null, null),
                // Ended by the termination that followed it.
                new MedicaidEnrollmentRow("1000000004", "OH", 1, new DateOnly(2015, 3, 1), new DateOnly(2019, 6, 30), "TERM - LICENSE EXPIRED"),
                // 1900-01-01 is no real start; more than a month apart is a second period; pending isn't enrolled.
                new MedicaidEnrollmentRow("1000000012", "CA", 1, null, new DateOnly(2020, 12, 31), null),
                new MedicaidEnrollmentRow("1000000012", "CA", 2, new DateOnly(2022, 1, 1), new DateOnly(2023, 5, 31), null),
                new MedicaidEnrollmentRow("1000000038", "TX", 1, new DateOnly(2018, 1, 1), new DateOnly(2024, 11, 30), "TERM - STATE EXCLUSION/ DEBARMENT, ETC."),
                // An open active segment cut by a termination inside it.
                new MedicaidEnrollmentRow("1000000053", "FL", 1, new DateOnly(2016, 5, 1), new DateOnly(2020, 1, 31), "TERM - PROVIDER DECEASED"),
            ],
            rows);
        Assert.Equal(19, summary.Lines);
        Assert.Equal(4, summary.SkippedNpis); // blank, 0000000000, an M-number, a bad check digit
        Assert.Equal(6, summary.Periods);
        // The months covered, from the data: the earliest end of a segment (the file holds those overlapping its window) and the
        // latest start (2018-01 and 2024-12 in the real file).
        Assert.Equal((new DateOnly(2019, 6, 1), new DateOnly(2024, 12, 1)), (summary.FirstMonth, summary.LastMonth));
        Assert.Equal(new Dictionary<string, int> { ["CA"] = 1, ["FL"] = 1, ["NY"] = 1, ["OH"] = 1, ["TX"] = 1 }, summary.NpisByState);
    }

    [Fact]
    public void Enrollment_periods_join_segments_that_touch_and_end_after_the_data_counts_as_still_enrolled()
    {
        const string header = "NPI,STATE_CD,ENRL_START_DT,ENRL_END_DT,ENRL_STATUS_CD,ENRL_STATUS_DESC,ENRL_PLAN_CD,ENRL_PLAN_DESC,PRVDR_TYPE,PRVDR_TYPE_DESC\n";
        var csv = header +
                  // Out of order in the file, overlapping and 20 days apart: one period, ending after the data (2024-12): still enrolled.
                  "1000000020,WA,2023-02-20,2026-06-30,03,ACTIVE - ACTIVE,1,MEDICAID,01,FACILITY\n" +
                  "1000000020,WA,2019-01-01,2021-12-31,03,ACTIVE - ACTIVE,1,MEDICAID,01,FACILITY\n" +
                  "1000000020,WA,2021-06-01,2023-01-31,01,ACTIVE - ACTIVE DO NOT PAY,1,MEDICAID,01,FACILITY\n" +
                  // A termination long after the period ended doesn't name its end; 1970-01-01 is no real start.
                  "1000000061,OR,1970-01-01,2019-03-31,03,ACTIVE - ACTIVE,1,MEDICAID,03,INDIVIDUAL\n" +
                  "1000000061,OR,2022-01-01,,76,TERM - NO CLAIMS ACTIVITY,1,MEDICAID,03,INDIVIDUAL\n" +
                  "1000000079,GU,2024-12-01,2024-12-31,03,ACTIVE - ACTIVE,1,MEDICAID,03,INDIVIDUAL\n";
        var rows = new List<MedicaidEnrollmentRow>();
        var summary = MedicaidEnrollmentSegments.Summarize(new StringReader(csv), new DateOnly(2026, 7, 24), rows.Add);
        Assert.Equal(
            [
                new MedicaidEnrollmentRow("1000000020", "WA", 1, new DateOnly(2019, 1, 1), null, null),
                new MedicaidEnrollmentRow("1000000061", "OR", 1, null, new DateOnly(2019, 3, 31), null),
                new MedicaidEnrollmentRow("1000000079", "GU", 1, new DateOnly(2024, 12, 1), new DateOnly(2024, 12, 31), null),
            ],
            rows);
        Assert.Equal(new DateOnly(2024, 12, 1), summary.LastMonth);

        // A file whose columns were renamed fails with the missing column named.
        var renamed = Assert.Throws<InvalidDataException>(() =>
            MedicaidEnrollmentSegments.Summarize(new StringReader(header.Replace("ENRL_END_DT", "END_DT", StringComparison.Ordinal)), new DateOnly(2026, 7, 24), rows.Add));
        Assert.Contains("ENRL_END_DT", renamed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Medicaid_spending_is_summed_per_npi_and_year_and_the_newest_years_procedures_ranked()
    {
        var years = new List<MedicaidBillingRow>();
        var procedures = new List<MedicaidProcedureRow>();
        using var reader = Csv("datasets/medicaid_spending_sample.csv.zip");
        var summary = MedicaidSpending.Summarize(reader, years.Add, procedures.Add);

        Assert.Equal(
            [
                // Served under the group's billing NPI in 2024 (an adjustment included); billed and served itself in 2021.
                new MedicaidBillingRow("1000000004", 2021, 14, 700m, 14, 700m, 14, 700m),
                new MedicaidBillingRow("1000000004", 2024, 62, 3450.25m, 0, 0m, 62, 3450.25m),
                new MedicaidBillingRow("1000000012", 2019, 12, 300m, 12, 300m, 12, 300m),
                // The group: billing all, serving once itself; a cell with it as both counts once in the totals.
                new MedicaidBillingRow("1000000038", 2023, 12, 600m, 12, 600m, 0, 0m),
                new MedicaidBillingRow("1000000038", 2024, 462, 15450.50m, 462, 15450.50m, 400, 12000.25m),
                new MedicaidBillingRow("1000000046", 2018, 12, 120m, 12, 120m, 0, 0m),
            ],
            years);
        Assert.Equal(
            [
                new MedicaidProcedureRow("1000000004", 1, 2024, "98940", 20, 2000m),
                new MedicaidProcedureRow("1000000004", 2, 2024, "99213", 42, 1450.25m),
                new MedicaidProcedureRow("1000000038", 1, 2024, "T1019", 400, 12000.25m),
                new MedicaidProcedureRow("1000000038", 2, 2024, "98940", 20, 2000m),
                new MedicaidProcedureRow("1000000038", 3, 2024, "99213", 42, 1450.25m),
            ],
            procedures);
        Assert.Equal((10L, 1L, 1L, 6L, 4L), (summary.Lines, summary.SkippedNpis, summary.SkippedAmounts, summary.NpiYears, summary.Npis));
        Assert.Equal((new DateOnly(2018, 1, 1), new DateOnly(2024, 6, 1)), (summary.FirstMonth, summary.LastMonth));
        Assert.Equal((2L, 18900.75m), summary.ByYear[2024]);
    }

    [Fact]
    public void Floridas_list_keeps_current_enrollments_and_limited_enrollments_only()
    {
        using var file = File.OpenRead(Fixtures.Path("datasets/fl_provider_master_list_sample.zip"));
        var rows = FloridaMedicaidList.Read(file, new DateOnly(2026, 10, 17)).ToList();
        // Not: an order/refer registration (ROPA), a registration without a Medicaid ID, an inactive enrollment, eligibility that
        // ended, a blank NPI, a failed check digit, a provider's other service location.
        Assert.Equal(
            [
                new FloridaMedicaidEnrollment("1000000004", new DateOnly(2008, 6, 13), false),
                new FloridaMedicaidEnrollment("1000000004", new DateOnly(2015, 1, 1), true),
                new FloridaMedicaidEnrollment("1000000020", new DateOnly(2021, 3, 1), true),
            ],
            rows);
    }
}
