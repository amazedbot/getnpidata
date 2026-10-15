using System.Text;
using System.Text.Json;
using Npi.Loader.Datasets;

namespace Npi.Loader.Tests;

/// <summary>Older Medicare years (docs/PRODUCT_PLAN.md Phase 3): which catalog files are loaded, and how each year's header is read.</summary>
public class MedicareHistoryTests
{
    private static readonly string PartB = MedicareHistoryPart.PartB.Title;
    private static readonly string PartD = MedicareHistoryPart.PartD.Title;

    /// <summary>A data.cms.gov catalog (data.json) listing these releases: title, data year, CSV URL and modified date.</summary>
    internal static string CatalogJson(IEnumerable<(string Title, int Year, string Url, string Modified)> releases) =>
        JsonSerializer.Serialize(new
        {
            dataset = releases.Select(r => new Dictionary<string, object>
            {
                ["title"] = $"{r.Title} : {r.Year}-12-01",
                ["modified"] = r.Modified,
                ["temporal"] = new[] { new { startDate = $"{r.Year}-01-01", endDate = $"{r.Year}-12-31" } },
                ["distribution"] = new object[]
                {
                    new { downloadURL = r.Url, mediaType = "text/csv" },
                    new { accessURL = "https://cms.test/data-api/v1/dataset/x/data", format = "API" },
                },
            }),
        });

    private static CmsCatalog Catalog(params (string Title, int Year, string Url, string Modified)[] releases) =>
        CmsCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(CatalogJson(releases))));

    [Fact]
    public void Every_year_but_the_newest_is_history_from_2013()
    {
        var catalog = Catalog(
            (PartB, 2012, "https://cms.test/files/2025-11/a/MUP_PHY_D12_Prov.csv", "2025-03-26"),
            (PartB, 2013, "https://cms.test/files/2025-11/b/MUP_PHY_D13_Prov.csv", "2025-03-26"),
            (PartB, 2014, "https://cms.test/files/2025-11/c/MUP_PHY_D14_Prov.csv", "2025-03-26"),
            (PartB, 2014, "https://cms.test/files/2024-06/old/MUP_PHY_D14_Prov.csv", "2024-06-01"), // an older listing of the same year
            (PartB, 2024, "https://cms.test/files/2026-05/d/MUP_PHY_D24_Prov.csv", "2026-05-21"),
            (PartD, 2013, "https://cms.test/files/2026-08/e/mup_dpr_dy13_npi.csv", "2026-08-03"),
            (PartD, 2023, "https://cms.test/files/2026-08/f/mup_dpr_dy23_npi.csv", "2026-08-03"),
            (PartD, 2024, "https://cms.test/files/2026-08/g/mup_dpr_dy24_npi.csv", "2026-08-03"),
            (PartB + " and Service", 2015, "https://cms.test/files/x/MUP_PHY_D15_Prov_Svc.csv", "2025-03-26"));

        Assert.Equal([2012, 2013, 2014, 2024], catalog.FindYears(PartB).Select(r => r.DataYear!.Value));
        Assert.Equal("/files/2025-11/c/MUP_PHY_D14_Prov.csv", catalog.FindYears(PartB)[2].Version); // the newest listing, its folder included

        var files = MedicareHistorySource.Files(catalog);
        Assert.Equal([('B', 2013), ('B', 2014), ('D', 2013), ('D', 2023)], files.Select(f => (f.Part.Code, f.Year)));
        Assert.Matches("^2013-2023 [0-9a-f]{12}$", MedicareHistorySource.Version(files));

        // Another file for one year changes the version; the same files in another order don't.
        var republished = MedicareHistorySource.Files(Catalog(
            (PartB, 2013, "https://cms.test/files/2026-12/z/MUP_PHY_D13_Prov.csv", "2026-12-01"),
            (PartB, 2014, "https://cms.test/files/2025-11/c/MUP_PHY_D14_Prov.csv", "2025-03-26"),
            (PartB, 2024, "https://cms.test/files/2026-05/d/MUP_PHY_D24_Prov.csv", "2026-05-21"),
            (PartD, 2023, "https://cms.test/files/2026-08/f/mup_dpr_dy23_npi.csv", "2026-08-03"),
            (PartD, 2013, "https://cms.test/files/2026-08/e/mup_dpr_dy13_npi.csv", "2026-08-03"),
            (PartD, 2024, "https://cms.test/files/2026-08/g/mup_dpr_dy24_npi.csv", "2026-08-03")));
        Assert.NotEqual(MedicareHistorySource.Version(files), MedicareHistorySource.Version(republished));
        Assert.Equal(MedicareHistorySource.Version(files), MedicareHistorySource.Version([.. files.Reverse()]));
        Assert.Equal("no older years", MedicareHistorySource.Version([]));
    }

    [Fact]
    public void A_new_newest_year_moves_the_previous_one_into_the_history()
    {
        var files = MedicareHistorySource.Files(Catalog(
            (PartB, 2023, "https://cms.test/files/a/MUP_PHY_D23_Prov.csv", "2025-09-09"),
            (PartB, 2024, "https://cms.test/files/b/MUP_PHY_D24_Prov.csv", "2026-05-21"),
            (PartB, 2025, "https://cms.test/files/c/MUP_PHY_D25_Prov.csv", "2027-05-21")));
        Assert.Equal([2023, 2024], files.Select(f => f.Year));
    }

    [Theory]
    [InlineData('B', "Rndrng_NPI,Rndrng_Prvdr_Last_Org_Name,Rndrng_Prvdr_Type,Tot_HCPCS_Cds,Tot_Benes,Tot_Srvcs,Tot_Mdcr_Pymt_Amt", "Rndrng_NPI", "Tot_Benes")]
    [InlineData('B', "﻿npi,nppes_provider_last_org_name,total_services,total_unique_benes,total_medicare_payment_amt", "npi", "total_unique_benes")]
    [InlineData('D', "Prscrbr_NPI,Prscrbr_Last_Org_Name,Prscrbr_Type,Tot_Clms,Tot_30day_Fills,Tot_Drug_Cst,Tot_Benes", "Prscrbr_NPI", "Tot_Clms")]
    [InlineData('D', "NPI,NPPES_PROVIDER_LAST_ORG_NAME,SPECIALTY_DESCRIPTION,BENE_COUNT,TOTAL_CLAIM_COUNT", "npi", "total_claim_count")]
    public void Each_years_header_is_read_by_its_layout(char part, string header, string npi, string count)
    {
        var columns = (part == 'B' ? MedicareHistoryPart.PartB : MedicareHistoryPart.PartD).ColumnsFor(header.Split(','), "x.csv");

        Assert.Equal([(npi, "npi", CsvValue.Npi), (count, "value", CsvValue.OptionalNumber)], columns.Select(c => (c.Header, c.Column, c.Kind)));
    }

    [Fact]
    public void An_unknown_layout_fails_with_the_header()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            MedicareHistoryPart.PartD.ColumnsFor(["Prescriber_NPI", "Total_Claims", "Total_Cost"], "mup_dpr_dy13_npi.csv"));

        Assert.Contains("mup_dpr_dy13_npi.csv (Part D) has an unknown layout", error.Message, StringComparison.Ordinal);
        Assert.Contains("Prescriber_NPI, Total_Claims, Total_Cost", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_newest_year_sources_and_the_history_read_the_same_titles()
    {
        // The history must leave out exactly the year MedicareUtilizationSource and PartDPrescriberSource load.
        Assert.Equal("Medicare Physician & Other Practitioners - by Provider", PartB);
        Assert.Equal("Medicare Part D Prescribers - by Provider", PartD);
        Assert.Contains(DatasetLoader.AllSources, s => s is MedicareHistorySource);
    }
}
