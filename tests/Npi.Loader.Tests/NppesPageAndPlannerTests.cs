using Npi.Loader.Nppes;

namespace Npi.Loader.Tests;

public class NppesPageAndPlannerTests
{
    private static readonly Uri PageUrl = new("https://download.cms.gov/nppes/NPI_Files.html");

    private static IReadOnlyList<NppesLink> FixtureLinks() => NppesPage.ParseZipLinks(Fixtures.Text("NPI_Files.html"), PageUrl);

    [Fact]
    public void Page_links_are_resolved_and_deduplicated()
    {
        var links = FixtureLinks();

        Assert.Equal(
        [
            "NPPES_Data_Dissemination_September_2026_V2.zip",
            "NPPES_Deactivated_NPI_Report_091426_V2.zip",
            "NPPES_Data_Dissemination_090726_091326_Weekly_V2.zip",
            "NPPES_Data_Dissemination_091426_092026_Weekly_V2.zip",
            "NPPES_Data_Dissemination_092126_092726_Weekly_V2.zip",
            "NPPES_Data_Dissemination_092826_100426_Weekly_V2.zip",
            "NPPES_Data_Dissemination_August_2026.zip",
        ], links.Select(l => l.FileName));
        Assert.All(links, l => Assert.Equal($"https://download.cms.gov/nppes/{l.FileName}", l.Url.AbsoluteUri));
    }

    [Fact]
    public void Plan_loads_monthly_then_weeklies_in_order_then_deactivations()
    {
        var plan = LoadPlanner.Plan(FixtureLinks(), new HashSet<string>());

        Assert.Equal(
        [
            "NPPES_Data_Dissemination_September_2026_V2.zip",
            "NPPES_Data_Dissemination_090726_091326_Weekly_V2.zip",
            "NPPES_Data_Dissemination_091426_092026_Weekly_V2.zip",
            "NPPES_Data_Dissemination_092126_092726_Weekly_V2.zip",
            "NPPES_Data_Dissemination_092826_100426_Weekly_V2.zip",
            "NPPES_Deactivated_NPI_Report_091426_V2.zip",
        ], plan.Files.Select(f => f.File.FileName));
        Assert.Contains(plan.Skipped, s => s.FileName == "NPPES_Data_Dissemination_August_2026.zip");
    }

    [Fact]
    public void Plan_skips_completed_files()
    {
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NPPES_Data_Dissemination_September_2026_V2.zip",
            "NPPES_Data_Dissemination_090726_091326_Weekly_V2.zip",
            "NPPES_Deactivated_NPI_Report_091426_V2.zip",
        };

        var plan = LoadPlanner.Plan(FixtureLinks(), completed);

        Assert.Equal(
        [
            "NPPES_Data_Dissemination_091426_092026_Weekly_V2.zip",
            "NPPES_Data_Dissemination_092126_092726_Weekly_V2.zip",
            "NPPES_Data_Dissemination_092826_100426_Weekly_V2.zip",
        ], plan.Files.Select(f => f.File.FileName));
    }

    // Legacy defect #3: the VB loader logged a file before processing it, so a crash skipped it forever.
    // Here only Completed counts; a Failed/Loading/Legacy file is simply not in the completed set.
    [Fact]
    public void Failed_or_interrupted_files_are_planned_again()
    {
        var plan = LoadPlanner.Plan(FixtureLinks(), new HashSet<string> { "NPPES_Data_Dissemination_091426_092026_Weekly_V2.zip" });
        var retry = LoadPlanner.Plan(FixtureLinks(), new HashSet<string>());

        Assert.DoesNotContain(plan.Files, f => f.File.FileName == "NPPES_Data_Dissemination_091426_092026_Weekly_V2.zip");
        Assert.Contains(retry.Files, f => f.File.FileName == "NPPES_Data_Dissemination_091426_092026_Weekly_V2.zip");
    }

    [Fact]
    public void Only_the_newest_monthly_and_deactivation_report_are_planned()
    {
        var links = new[]
        {
            Link("NPPES_Data_Dissemination_August_2026_V2.zip"),
            Link("NPPES_Data_Dissemination_September_2026_V2.zip"),
            Link("NPPES_Deactivated_NPI_Report_081026_V2.zip"),
            Link("NPPES_Deactivated_NPI_Report_091426_V2.zip"),
        };

        var plan = LoadPlanner.Plan(links, new HashSet<string>());

        Assert.Equal(["NPPES_Data_Dissemination_September_2026_V2.zip", "NPPES_Deactivated_NPI_Report_091426_V2.zip"],
            plan.Files.Select(f => f.File.FileName));
        Assert.Contains(plan.Skipped, s => s.FileName == "NPPES_Data_Dissemination_August_2026_V2.zip" && s.Reason.StartsWith("superseded"));
    }

    [Fact]
    public void A_monthly_older_than_a_completed_one_is_not_reloaded()
    {
        var plan = LoadPlanner.Plan([Link("NPPES_Data_Dissemination_August_2026_V2.zip")],
            new HashSet<string> { "NPPES_Data_Dissemination_September_2026_V2.zip" });

        Assert.Empty(plan.Files);
        Assert.Contains("older than", Assert.Single(plan.Skipped).Reason);
    }

    private static NppesLink Link(string name) => new(name, new Uri(PageUrl, name));
}
