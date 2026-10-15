using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Npi.Core.Search;

/// <summary>
/// Writes search results as an Excel workbook (.xlsx, one sheet, a bold header row, text cells) without a spreadsheet
/// library (docs/PRODUCT_PLAN.md step 1.3). Excel holds 1,048,576 rows, so at most <see cref="MaxRows"/> providers;
/// the CSV has no limit.
/// </summary>
public static class ProviderXlsx
{
    public const int MaxRows = 1_048_575;

    /// <param name="output">Written synchronously by the zip archive: a file or memory stream, not a response body.</param>
    /// <returns>The number of providers written.</returns>
    public static Task<long> WriteAsync(IAsyncEnumerable<ProviderSummary> rows, Stream output, IReadOnlyList<ProviderColumn> columns, CancellationToken ct) =>
        WriteAsync(rows, output, columns.Select(c => c.Header).ToList(), r => columns.Select(c => c.Value(r)), ct);

    /// <summary>Bulk lookup results (docs/PRODUCT_PLAN.md step 1.7) with the CSV's columns (<see cref="ProviderCsv.LookupHeader"/>).</summary>
    public static Task<long> WriteLookupAsync(IAsyncEnumerable<LookupRow> rows, Stream output, CancellationToken ct, bool withChanges = false) =>
        WriteAsync(rows, output, withChanges ? ProviderCsv.LookupChangesHeader : ProviderCsv.LookupHeader, r => ProviderCsv.LookupValues(r, withChanges), ct);

    private static async Task<long> WriteAsync<T>(IAsyncEnumerable<T> rows, Stream output, IReadOnlyList<string> header, Func<T, IEnumerable<string?>> values,
        CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Part(zip, "[Content_Types].xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
        Part(zip, "_rels/.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        Part(zip, "xl/workbook.xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Providers" sheetId="1" r:id="rId1"/></sheets></workbook>""");
        Part(zip, "xl/_rels/workbook.xml.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
        Part(zip, "xl/styles.xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf fontId="0"/><xf fontId="1" applyFont="1"/></cellXfs></styleSheet>""");

        var sheet = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest);
        long count = 0;
        await using (var stream = sheet.Open())
        using (var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = false }))
        {
            const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            xml.WriteStartDocument(standalone: true);
            xml.WriteStartElement("worksheet", ns);
            xml.WriteStartElement("sheetViews", ns);
            xml.WriteStartElement("sheetView", ns);
            xml.WriteAttributeString("workbookViewId", "0");
            xml.WriteStartElement("pane", ns);
            xml.WriteAttributeString("ySplit", "1");
            xml.WriteAttributeString("topLeftCell", "A2");
            xml.WriteAttributeString("state", "frozen");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteStartElement("sheetData", ns);
            Row(xml, ns, header, header: true);
            await foreach (var r in rows.WithCancellation(ct))
            {
                if (count == MaxRows)
                {
                    break;
                }

                Row(xml, ns, values(r), header: false);
                count++;
            }

            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return count;
    }

    private static void Row(XmlWriter xml, string ns, IEnumerable<string?> values, bool header)
    {
        xml.WriteStartElement("row", ns);
        foreach (var value in values)
        {
            xml.WriteStartElement("c", ns);
            xml.WriteAttributeString("t", "inlineStr");
            if (header)
            {
                xml.WriteAttributeString("s", "1");
            }

            xml.WriteStartElement("is", ns);
            xml.WriteElementString("t", ns, Clean(value));
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        xml.WriteEndElement();
    }

    // XML can't hold control characters; drop them rather than fail the export.
    private static string Clean(string? value) =>
        value is null ? "" : value.Any(c => c < ' ' && c is not '\t' and not '\n' and not '\r') ? new string(value.Where(c => c >= ' ' || c is '\t' or '\n' or '\r').ToArray()) : value;

    private static void Part(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes);
    }
}
