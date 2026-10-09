using Npi.Web.Pages;

namespace Npi.Web.Tests;

public class LookupPageTests
{
    [Fact]
    public void Npis_are_every_ten_digit_number_in_order_without_duplicates()
    {
        const string upload = "NPI,Name\r\n1003000126,Smith\r\n\"1234567893\",Jones\r\n1003000126,Smith again\r\nphone 63155501001,zip 117011234\r\n";

        Assert.Equal(["1003000126", "1234567893"], LookupModel.ExtractNpis(upload));
        Assert.Empty(LookupModel.ExtractNpis("no numbers here, 12345"));
    }
}
