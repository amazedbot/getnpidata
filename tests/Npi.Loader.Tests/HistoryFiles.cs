using System.IO.Compression;
using System.Text;
using Npi.Loader.Csv;

namespace Npi.Loader.Tests;

/// <summary>
/// Tiny archived NPPES monthly files for the history tests (docs/PRODUCT_PLAN.md step 3.4), in each era's layout: the real
/// header rows of November 2007 (187 columns) and December 2016 (329) from tests/fixtures/history, 2011's 314 (2016's without the
/// 15 taxonomy group columns) and today's V2 (330). Values are written as CMS does: every field in double quotes.
/// </summary>
public sealed class HistoryFiles
{
    public enum Layout
    {
        Y2007,
        Y2011,
        Y2016,
        V2,
    }

    private readonly List<Dictionary<string, string>> _rows = [];
    private readonly List<(string Npi, string State, string Zip, string Country)> _secondary = [];

    public static IReadOnlyList<string> Header(Layout layout) => layout switch
    {
        Layout.Y2007 => CsvHeader.ParseLine(FirstLine("history/npidata_2007_fileheader.csv")),
        Layout.Y2016 => CsvHeader.ParseLine(FirstLine("history/npidata_2016_fileheader.csv")),
        Layout.Y2011 => CsvHeader.ParseLine(FirstLine("history/npidata_2016_fileheader.csv"))
            .Where(h => !h.StartsWith("Healthcare Provider Taxonomy Group", StringComparison.Ordinal)).ToList(),
        _ => CsvHeader.ParseLine(FirstLine("npidata_pfile_v2_fileheader.csv")),
    };

    private static string FirstLine(string fixture) => File.ReadLines(Fixtures.Path(fixture)).First();

    /// <summary>A provider: column (database name, e.g. Provider_Business_Practice_Location_Address_State_Name) → value.</summary>
    public HistoryFiles Row(string npi, params (string Column, string Value)[] values)
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["NPI"] = npi };
        foreach (var (column, value) in values)
        {
            row[column] = value;
        }

        _rows.Add(row);
        return this;
    }

    /// <summary>An active provider practising at <paramref name="zip"/>, with taxonomy codes (the first one primary unless <paramref name="primarySlot"/>).</summary>
    public HistoryFiles Active(string npi, string state, string zip, string enumeration, string lastUpdate, string[] codes, int primarySlot = 1,
        string entity = "1", string country = "US")
    {
        var values = new List<(string, string)>
        {
            ("Entity_Type_Code", entity), ("Provider_Business_Practice_Location_Address_State_Name", state),
            ("Provider_Business_Practice_Location_Address_Postal_Code", zip), ("Provider_Business_Practice_Location_Address_Country_Code", country),
            ("Provider_Enumeration_Date", enumeration), ("Last_Update_Date", lastUpdate), ("Provider_Last_Name_Legal_Name", "TESTER"),
        };
        for (var i = 0; i < codes.Length; i++)
        {
            values.Add(($"Healthcare_Provider_Taxonomy_Code_{i + 1}", codes[i]));
            values.Add(($"Healthcare_Provider_Primary_Taxonomy_Switch_{i + 1}", i + 1 == primarySlot ? "Y" : "N"));
        }

        return Row(npi, [.. values]);
    }

    /// <summary>A secondary practice location (pl_pfile, written when <see cref="Zip"/> is asked for locations).</summary>
    public HistoryFiles Secondary(string npi, string state, string zip, string country = "US")
    {
        _secondary.Add((npi, state, zip, country));
        return this;
    }

    /// <summary>The pl_pfile CSV (today's header) of the secondary locations.</summary>
    public string LocationCsv()
    {
        var csv = new StringBuilder(File.ReadLines(Fixtures.Path("pl_pfile_v2_fileheader.csv")).First()).Append('\n');
        foreach (var (npi, state, zip, country) in _secondary)
        {
            csv.Append($"\"{npi}\",\"1 MAIN ST\",\"\",\"TOWN\",\"{state}\",\"{zip}\",\"{country}\",\"\",\"\",\"\"\n");
        }

        return csv.ToString();
    }

    /// <summary>A deactivated NPI as the files list it from November 2012: the NPI and its deactivation date only.</summary>
    public HistoryFiles Deactivated(string npi, string date) => Row(npi, ("NPI_Deactivation_Date", date));

    public string Csv(Layout layout, string lineTerminator = "\n")
    {
        var header = Header(layout);
        var columns = header.Select(HeaderMapper.ToColumnName).ToList();
        var csv = new StringBuilder(string.Join(',', header.Select(h => $"\"{h}\""))).Append(lineTerminator);
        foreach (var row in _rows)
        {
            csv.AppendJoin(',', columns.Select(c => $"\"{row.GetValueOrDefault(c, "")}\"")).Append(lineTerminator);
        }

        return csv.ToString();
    }

    /// <summary>
    /// Writes the zip: <c>npidata_20050523-&lt;dataDate&gt;.csv</c> (in <paramref name="subfolder"/> when given) and its header file,
    /// and with <paramref name="locations"/> the <c>pl_pfile</c> of the secondary locations (as the files have from September 2018).
    /// </summary>
    public string Zip(string folder, string zipName, Layout layout, DateOnly dataDate, string? subfolder = null, bool pfile = false, bool locations = false)
    {
        var path = Path.Combine(folder, zipName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var prefix = (subfolder is null ? "" : subfolder + "/") + (pfile ? "npidata_pfile_" : "npidata_") + "20050523-" + dataDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var csv = Csv(layout);
        Add(zip, prefix + ".csv", csv);
        Add(zip, prefix + (layout == Layout.Y2007 ? "NPIDataDisseminationFileHeader.csv" : layout == Layout.V2 ? "_fileheader.csv" : "FileHeader.csv"),
            csv[..(csv.IndexOf('\n') + 1)]);
        Add(zip, "NPPES Data Dissemination - Readme.pdf", "%PDF-1.4 placeholder");
        if (locations)
        {
            var date = dataDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
            var pl = LocationCsv();
            Add(zip, $"pl_pfile_20050523-{date}.csv", pl);
            Add(zip, $"pl_pfile_20050523-{date}_FileHeader.csv", pl[..(pl.IndexOf('\n') + 1)]);
        }

        return path;
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
