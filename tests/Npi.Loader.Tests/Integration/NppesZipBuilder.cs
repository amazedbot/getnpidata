using System.IO.Compression;
using System.Text;
using Npi.Loader.Csv;

namespace Npi.Loader.Tests.Integration;

/// <summary>
/// Builds small NPPES data zips for loader tests, using the real V2 header rows from tests/fixtures.
/// Values are written exactly as CMS does: every field in double quotes, nothing escaped.
/// </summary>
internal sealed class NppesZipBuilder
{
    private readonly List<Dictionary<string, string>> _providers = [];
    private readonly List<string[]> _otherNames = [];
    private readonly List<string[]> _locations = [];

    /// <param name="values">Column name (as in the database, e.g. Provider_Last_Name_Legal_Name) → value.</param>
    public NppesZipBuilder Provider(string npi, params (string Column, string Value)[] values)
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["NPI"] = npi };
        foreach (var (column, value) in values)
        {
            row[column] = value;
        }

        _providers.Add(row);
        return this;
    }

    public NppesZipBuilder OtherName(string npi, string name, string typeCode, string createdDate)
    {
        _otherNames.Add([npi, name, typeCode, createdDate]);
        return this;
    }

    public NppesZipBuilder Location(string npi, string line1, string line2 = "", string city = "", string state = "", string postalCode = "",
        string country = "", string phone = "", string extension = "", string fax = "")
    {
        _locations.Add([npi, line1, line2, city, state, postalCode, country, phone, extension, fax]);
        return this;
    }

    /// <returns>The path of the written zip, named <paramref name="zipName"/>.</returns>
    public string Write(string folder, string zipName, string lineTerminator = "\n")
    {
        var path = Path.Combine(folder, zipName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var npiHeader = HeaderLine("npidata_pfile_v2_fileheader.csv");
        var columns = CsvHeader.ParseLine(npiHeader).Select(HeaderMapper.ToColumnName).ToList();
        var npiRows = _providers.Select(p => columns.Select(c => p.GetValueOrDefault(c, "")).ToArray());

        AddCsv(zip, "npidata_pfile_20260101-20260107", npiHeader, npiRows, lineTerminator);
        AddCsv(zip, "othername_pfile_20260101-20260107", HeaderLine("othername_pfile_v2_fileheader.csv"), _otherNames, lineTerminator);
        AddCsv(zip, "pl_pfile_20260101-20260107", HeaderLine("pl_pfile_v2_fileheader.csv"), _locations, lineTerminator);
        AddCsv(zip, "endpoint_pfile_20260101-20260107", "\"NPI\",\"Endpoint\"", [["1000000004", "https://example.org"]], lineTerminator);
        AddText(zip, "NPPES_Data_Dissemination_Readme_v.2.pdf", "%PDF-1.4 placeholder");
        return path;
    }

    private static string HeaderLine(string fixture) => File.ReadAllLines(Fixtures.Path(fixture))[0];

    private static void AddCsv(ZipArchive zip, string baseName, string header, IEnumerable<string[]> rows, string lineTerminator)
    {
        var csv = new StringBuilder(header).Append(lineTerminator);
        foreach (var row in rows)
        {
            csv.AppendJoin(',', row.Select(v => $"\"{v}\"")).Append(lineTerminator);
        }

        AddText(zip, baseName + ".csv", csv.ToString());
        AddText(zip, baseName + "_fileheader.csv", header + lineTerminator);
    }

    private static void AddText(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
