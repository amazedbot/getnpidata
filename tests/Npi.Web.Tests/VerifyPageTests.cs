using Npi.Core.Search;
using Npi.Web.Pages;

namespace Npi.Web.Tests;

public class VerifyPageTests
{
    [Fact]
    public void Npis_are_every_ten_digit_number_in_order_without_duplicates()
    {
        const string upload = "NPI,Name\r\n1003000126,Smith\r\n\"1234567893\",Jones\r\n1003000126,Smith again\r\nphone 63155501001,zip 117011234\r\n";

        Assert.Equal(["1003000126", "1234567893"], VerifyModel.ExtractNpis(upload));
        Assert.Empty(VerifyModel.ExtractNpis("no numbers here, 12345"));
    }

    [Fact]
    public void A_file_with_an_npi_column_contributes_only_that_column()
    {
        string?[][] rows = [["Name", "Provider NPI", "Phone", "Billing npi"], ["Smith", "1003000126", "6315550100", "1234567893"], ["Short row"]];
        Assert.Equal(["1003000126", "1234567893"], VerifyModel.ExtractNpis(VerifyModel.NpiColumnsText(rows)!)); // not the phone
        Assert.Null(VerifyModel.NpiColumnsText([["1003000126", "Smith"]]));                                      // no header: every number
        Assert.Null(VerifyModel.NpiColumnsText([]));
    }

    private static LookupRow Active(bool excluded = false, bool optedOut = false, bool orderRefer = false, int entityType = 1) =>
        new("1234567893", LookupStatus.Found,
            new ProviderSummary("1234567893", entityType, "SMITH, DANA", "NP", null, null, null, null, null, null, null, null, null, null, null)
            {
                Flags = ProviderFlags.None with { Excluded = excluded, OptedOutOfMedicare = optedOut, CanOrderAndRefer = orderRefer },
            });

    [Fact]
    public void Each_row_says_what_was_found_and_lands_in_its_tabs()
    {
        var deactivated = new LookupRow("1003000126", LookupStatus.Deactivated, null) { DeactivationDate = new DateOnly(2025, 3, 1) };
        Assert.Equal([("Deactivated on March 1, 2025", "alert")], VerifyModel.Results(deactivated));
        Assert.Equal([("Deactivated", "alert")], VerifyModel.Results(deactivated with { DeactivationDate = null }));
        Assert.Equal([("Not found in NPPES", "warn")], VerifyModel.Results(new LookupRow("1234567893", LookupStatus.NotFound, null)));
        Assert.StartsWith("Invalid NPI", VerifyModel.Results(new LookupRow("1234567890", LookupStatus.Invalid, null))[0].Text, StringComparison.Ordinal);

        Assert.Equal([("Active", "ok"), ("Can order and refer", null)], VerifyModel.Results(Active(orderRefer: true)));
        Assert.Equal([("Active", "ok")], VerifyModel.Results(Active(orderRefer: true, entityType: 2))); // organizations don't order
        Assert.Equal([("On the OIG exclusion list since November 20, 2024", "alert"), ("Opted out of Medicare until June 30, 2027", "warn")],
            VerifyModel.Results(Active(excluded: true, optedOut: true) with { ExclusionDate = new DateOnly(2024, 11, 20), OptOutEndDate = new DateOnly(2027, 6, 30) }));

        string[] Tabs(LookupRow row) => [.. VerifyModel.Tabs.Select(t => t.Key).Where(t => VerifyModel.InTab(t, row))];
        Assert.Equal(["all", "active"], Tabs(Active()));
        Assert.Equal(["attention", "all", "active", "excluded"], Tabs(Active(excluded: true)));
        Assert.Equal(["attention", "all", "active", "optedout"], Tabs(Active(optedOut: true)));
        Assert.Equal(["attention", "all", "deactivated"], Tabs(deactivated));
        Assert.Equal(["attention", "all", "notfound"], Tabs(new LookupRow("1234567893", LookupStatus.NotFound, null)));
        Assert.Equal(["attention", "all", "invalid"], Tabs(new LookupRow("1234567890", LookupStatus.Invalid, null)));
    }
}
