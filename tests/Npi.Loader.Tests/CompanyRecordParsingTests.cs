using System.Text.Json;
using Npi.Core.Search;
using Npi.Loader.Datasets;

namespace Npi.Loader.Tests;

/// <summary>Parsing of the company record sources (Stage 5.5 item 17 extras): openFDA files, OIG's agreement list, SEC's tickers.</summary>
public class CompanyRecordParsingTests
{
    private sealed record Row(string? recall_number, string? recalling_firm, string? product_description);

    [Fact]
    public async Task Json_array_stream_reads_only_the_top_level_results_array()
    {
        // meta.results is an object one level down; strings hold brackets, braces and escaped quotes.
        await using var file = File.OpenRead(Fixtures.Path("datasets/fda_enforcement_sample.json"));
        var rows = new List<Row>();
        await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable<Row>(new JsonArrayStream(file, "results"), cancellationToken: TestContext.Current.CancellationToken))
        {
            rows.Add(row!);
        }

        Assert.Equal(["D-0001-2026", "D-0002-2025", "D-0001-2026", "D-0100-2025", "Z-0200-2024", "Z-0201-2024"], rows.Select(r => r.recall_number));
        Assert.Equal("Tablets \"30 count\" [blister] {lot 1}", rows[0].product_description);
    }

    [Fact]
    public void Json_array_stream_fails_without_the_array()
    {
        using var json = new MemoryStream("""{"meta": {"results": {"total": 0}}}"""u8.ToArray());
        Assert.Throws<InvalidDataException>(() => new JsonArrayStream(json, "results").ReadByte());
    }

    [Fact]
    public void Oig_list_page_gives_each_agreement_and_the_last_page()
    {
        var page = new Uri("https://oig.test/compliance/corporate-integrity-agreements/browse-cias/");
        var (agreements, last) = OigCiaSource.ParsePage(File.ReadAllText(Fixtures.Path("datasets/oig_cia_page_sample.html")), page);

        Assert.Equal(17, last);
        Assert.Equal(3, agreements.Count);
        var snap = agreements[1];
        Assert.Equal(("snap-diagnostics-llc-and-gil-raviv", "SNAP Diagnostics, LLC and Gil Raviv", "Wheeling, IL", "Corporate Integrity Agreement", "Suspended",
            (DateTime?)new DateTime(2026, 10, 1)), (snap.Slug, snap.Name, snap.Location, snap.Type, snap.Status, snap.StatusDate));
        Assert.Equal("https://oig.test/compliance/corporate-integrity-agreements/browse-cias/snap-diagnostics-llc-and-gil-raviv/", snap.Url);
        Assert.Equal(("Integrity Agreement", "Closed"), (agreements[2].Type, agreements[2].Status));
    }

    [Fact]
    public void Oig_entities_are_the_whole_name_and_each_named_part()
    {
        Assert.Equal(
            [CompanyNames.Key("SNAP Diagnostics, LLC and Gil Raviv"), CompanyNames.Key("SNAP Diagnostics"), CompanyNames.Key("Gil Raviv")],
            OigCiaSource.EntityKeys("SNAP Diagnostics, LLC and Gil Raviv"));
        Assert.Contains(CompanyNames.Key("Ten Healthcare"), OigCiaSource.EntityKeys("Thyroid Specialty Laboratory, Inc. d.b.a. Ten Healthcare; 3890 Management, LLC"));
    }

    [Fact]
    public void Sec_ticker_file_keeps_registrants_with_a_ticker()
    {
        using var file = File.OpenRead(Fixtures.Path("datasets/sec_company_tickers_sample.json"));
        Assert.Equal([(78003, "PFIZER INC", "PFE", "NYSE"), (59478, "LILLY ELI & CO", "LLY", "NYSE")], SecCompanySource.Parse(file));
    }
}
