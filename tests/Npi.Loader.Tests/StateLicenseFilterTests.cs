using System.Text;
using Npi.Loader.Datasets;

namespace Npi.Loader.Tests;

public class StateLicenseFilterTests
{
    [Theory]
    [InlineData("C1-0024514", "10024514", "24514")]
    [InlineData("D0012345", "12345", "12345")]
    [InlineData("MD 05878", "5878", "5878")]
    [InlineData("000000", null, null)]
    [InlineData("PENDING", null, null)]
    [InlineData(null, null, null)]
    public void Keys_follow_the_matching_rules(string? number, string? all, string? last) =>
        Assert.Equal((all, last), (LicenseKeys.All(number), LicenseKeys.Last(number)));

    [Fact]
    public void A_number_could_match_by_all_its_digits_or_its_last_run()
    {
        var keys = new LicenseKeys();
        keys.Add("12345", "12345");
        keys.Add("10024514", "24514"); // NPPES "1-0024514"
        Assert.True(keys.CouldMatch("D0012345"));
        Assert.True(keys.CouldMatch("C9-0024514")); // other prefix, same last digits
        Assert.False(keys.CouldMatch("D0012346"));
        Assert.False(keys.CouldMatch("000000"));
        Assert.False(keys.CouldMatch(null));
    }

    [Fact]
    public async Task Only_rows_with_a_known_license_number_are_kept_after_the_header()
    {
        var dataset = StateLicenseSource.Datasets.First(d => d.State == "MD"); // a letterhead before the header
        var folder = Directory.CreateTempSubdirectory("npi-filter-").FullName;
        try
        {
            var source = Path.Combine(folder, "in.csv");
            var target = Path.Combine(folder, "out.csv");
            await File.WriteAllTextAsync(source,
                "﻿\"Maryland Board of Physicians\",,,,,,,\n\n" +
                "\"License #\",\"First Name\",\"Last Name\",\"Middle Name\",\"License Status\",\"Expiration Date\",\"Discipline\",\n" +
                "\"D0012345\",\"RAJ\",\"PATEL\",\"\",\"A\",\"09/30/2027\",\"N\",\n" +
                "\"D0099999\",\"ANN\",\"OTHER\",\"\",\"A\",\"09/30/2027\",\"N\",\n" +
                "\"D0054321\",\"JOSÉ\",\"O'NEIL, JR\",\"\",\"A\",\"09/30/2027\",\"Y\",\n", Encoding.Latin1);
            var keys = new LicenseKeys();
            keys.Add("12345", "12345");
            keys.Add("54321", "54321");

            Assert.Equal((3L, 2L), await StateLicenseFilter.FilterAsync(source, target, dataset, keys, TestContext.Current.CancellationToken));
            Assert.Equal(
                "\"License #\",\"First Name\",\"Last Name\",\"Middle Name\",\"License Status\",\"Expiration Date\",\"Discipline\",\"\"\n" +
                "\"D0012345\",\"RAJ\",\"PATEL\",\"\",\"A\",\"09/30/2027\",\"N\",\"\"\n" +
                "\"D0054321\",\"JOSÉ\",\"O'NEIL, JR\",\"\",\"A\",\"09/30/2027\",\"Y\",\"\"\n",
                await File.ReadAllTextAsync(target, Encoding.Latin1));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
