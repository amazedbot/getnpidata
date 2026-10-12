using Npi.Core.Search;
using Npi.Web.Pages;

namespace Npi.Web.Tests;

/// <summary>The Companies &amp; products pages (docs/PRODUCT_PLAN.md Phase 9, owner 2026-10-12): wording and the chart.</summary>
public class IndustryPageTests
{
    [Fact]
    public void Amounts_are_shortened_for_headlines()
    {
        Assert.Equal(["$5.12B", "$20.8B", "$812M", "$1.90M", "$15.2M", "$950K", "$640"],
            new[] { 5.12e9, 20.8e9, 812e6, 1.9e6, 15.2e6, 950e3, 640 }.Select(Format.ShortMoney));
        Assert.Equal(["4.42M", "12.0M", "332,888"], new long[] { 4_420_000, 12_000_000, 332_888 }.Select(Format.ShortNumber));
    }

    [Fact]
    public void Payment_kinds_read_plainly()
    {
        Assert.Equal("Speaking and faculty fees", Format.Nature(
            "Compensation for services other than consulting, including serving as faculty or as a speaker at a venue other than a continuing education program"));
        Assert.Equal("Consulting", Format.Nature("Consulting Fee"));
        Assert.Equal("Food and beverage", Format.Nature("Food and Beverage"));
        Assert.Equal("Grant", Format.Nature("Grant"));
    }

    [Fact]
    public void Columns_scale_to_the_tallest()
    {
        var bars = new YearBars("x", [("2024", 50), ("2025", 100), ("2026", 0.5)]);
        Assert.Equal(["50", "100", "2"], bars.Columns.Select(c => bars.Height(c.Value)));
        Assert.Equal("0", new YearBars("x", [("2025", 0)]).Height(0));
    }

    private static CompanyDetail Company(params CompanyProduct[] products) => new("100000000001", "PFIZER INC.", [], "NY", "United States", 500e6, 4.6e9, 0, 0,
        2019, 2025, 332_888, [], 2025, [], products, [], [], []);

    [Fact]
    public void A_company_is_described_by_what_its_payments_name()
    {
        var model = new CompanyModel(null!);
        typeof(CompanyModel).GetProperty(nameof(CompanyModel.Company))!.SetValue(model, Company(new CompanyProduct("ELIQUIS", "Drug", null, 100, 1)));
        Assert.Equal("Drug maker", model.Kind);
        Assert.Equal("Drug maker in NY, United States that paid US providers $5.10B in general and research payments from 2019 to 2025, reaching 332,888 providers.",
            model.Summary);
        Assert.Equal("2019–2025", model.Years);

        typeof(CompanyModel).GetProperty(nameof(CompanyModel.Company))!.SetValue(model,
            Company(new CompanyProduct("ELIQUIS", "Drug", null, 50, 1), new CompanyProduct("PUMP", "Device", null, 50, 1)));
        Assert.Equal("Drug and device maker", model.Kind);
        typeof(CompanyModel).GetProperty(nameof(CompanyModel.Company))!.SetValue(model, Company());
        Assert.Equal("Company", model.Kind);
    }

    [Fact]
    public void Shares_read_as_percentages()
    {
        Assert.Equal(("73%", "23.0%", "—"), (ProductModel.Percent(0.733), ProductModel.Percent(0.2301, 1), ProductModel.Percent(null)));
        Assert.Equal("one two …", ProductModel.Clip("one two three", 8));
    }
}
