using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;
using Npi.Loader.Load;

namespace Npi.Loader.Datasets;

/// <summary>
/// HHS's open-data platform (opendata.hhs.gov; docs/PRODUCT_PLAN.md step 3.7). It has no catalog API: each dataset's page
/// (<c>/datasets/&lt;slug&gt;/</c>) carries its releases ("versions") with their files, and the files live in an Azure blob
/// folder per release (<c>…/datasets/&lt;slug&gt;/&lt;yyyy-MM-dd&gt;/dataset/&lt;slug&gt;.csv.zip</c>) with a SHA-256 beside each.
/// The newest release is the latest such folder; its version is the folder's date and the start of the checksum, so a file
/// republished under the same date reloads too. US government works, free of copyright; credited as HHS (T-MSIS).
/// </summary>
public static partial class HhsOpenData
{
    /// <summary>The newest release of <paramref name="slug"/>'s zipped CSV in the dataset page <paramref name="html"/>.</summary>
    /// <exception cref="InvalidDataException">The page lists no such file (HHS changed the page).</exception>
    public static DatasetRelease FindRelease(string html, string slug)
    {
        // The page's data is a JavaScript string, so its quotes come escaped.
        var text = html.Replace("\\\"", "\"", StringComparison.Ordinal);
        DatasetRelease? best = null;
        var bestDate = "";
        foreach (Match m in ZipUrl().Matches(text))
        {
            if (m.Groups["slug"].Value != slug || m.Groups["slug2"].Value != slug)
            {
                continue;
            }

            var date = m.Groups["date"].Value;
            if (string.CompareOrdinal(date, bestDate) <= 0)
            {
                continue;
            }

            var url = m.Value;
            var checksum = Regex.Match(text, Regex.Escape($"\"url\":\"{url}\"") + "\\s*,\\s*\"checksum\":\"sha256:(?<sum>[0-9a-f]{12})");
            bestDate = date;
            best = new DatasetRelease(checksum.Success ? $"{date} sha256 {checksum.Groups["sum"].Value}" : date, new Uri(url),
                PeriodEnd: date);
        }

        return best ?? throw new InvalidDataException($"The HHS open-data page of {slug} lists no {slug}.csv.zip.");
    }

    /// <summary>Fetches <paramref name="slug"/>'s page under <paramref name="baseUrl"/> and finds its newest release.</summary>
    public static async Task<DatasetRelease> FindLatestAsync(HttpClient http, string baseUrl, string slug, CancellationToken ct)
    {
        var page = await http.GetStringAsync(new Uri(new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), slug + "/"), ct);
        return FindRelease(page, slug);
    }

    [GeneratedRegex(@"https://[A-Za-z0-9.-]+/datasets/(?<slug>[a-z0-9-]+)/(?<date>\d{4}-\d{2}-\d{2})/dataset/(?<slug2>[a-z0-9-]+)\.csv\.zip")]
    private static partial Regex ZipUrl();

    /// <summary>The largest .csv entry of a zip, as a stream that is read as it unzips (never extracted).</summary>
    public static Stream OpenLargestCsv(ZipArchive zip, string what)
    {
        var entry = zip.Entries.Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        .MaxBy(e => e.Length)
                    ?? throw new InvalidDataException($"{what}: the zip holds no CSV.");
        return entry.Open();
    }

    /// <summary>
    /// Splits one CSV line into <paramref name="fields"/> (cleared first): commas outside double quotes separate, a quoted field
    /// loses its quotes and "" inside one is a quote. The files have no line breaks inside fields.
    /// </summary>
    public static void Split(string line, List<string> fields)
    {
        fields.Clear();
        var i = 0;
        while (true)
        {
            if (i < line.Length && line[i] == '"')
            {
                var value = new StringBuilder();
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            value.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    value.Append(line[i++]);
                }

                fields.Add(value.ToString());
                var comma = line.IndexOf(',', i);
                if (comma < 0)
                {
                    return;
                }

                i = comma + 1;
            }
            else
            {
                var comma = line.IndexOf(',', i);
                if (comma < 0)
                {
                    fields.Add(line[i..]);
                    return;
                }

                fields.Add(line[i..comma]);
                i = comma + 1;
            }
        }
    }

    /// <summary>The column of <paramref name="name"/> in a header row (case-insensitive, trimmed, a BOM ignored).</summary>
    /// <exception cref="InvalidDataException">The header lacks it: the publisher renamed a column.</exception>
    public static int Column(IReadOnlyList<string> header, string name, string what)
    {
        for (var i = 0; i < header.Count; i++)
        {
            if (string.Equals(header[i].Trim().TrimStart('﻿').Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new InvalidDataException($"{what} has no {name} column (header: {string.Join(", ", header.Take(12))}).");
    }
}

/// <summary>
/// Rows computed in memory loaded into a table: written to a tab-separated file in the work folder (\N = NULL; tabs, line breaks
/// and backslashes in values escaped) and bulk-loaded with LOAD DATA, any warning failing the load. The file is deleted after.
/// </summary>
public sealed class TsvRows : IAsyncDisposable
{
    private readonly string _path;
    private readonly StreamWriter _writer;
    private readonly StringBuilder _line = new();

    public TsvRows(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, $"{name}.{Guid.NewGuid():N}.tsv");
        _writer = new StreamWriter(_path, false, new UTF8Encoding(false), 1 << 20) { NewLine = "\n" };
    }

    public long Rows { get; private set; }

    /// <summary>Adds one row; null values are NULL, numbers and dates are written invariantly.</summary>
    public void Add(params object?[] values)
    {
        _line.Clear();
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                _line.Append('\t');
            }

            switch (values[i])
            {
                case null:
                    _line.Append("\\N");
                    break;
                case DateOnly d:
                    _line.Append(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    break;
                case IFormattable f:
                    _line.Append(f.ToString(null, CultureInfo.InvariantCulture));
                    break;
                case var v:
                    foreach (var c in v.ToString()!)
                    {
                        _line.Append(c switch { '\\' => "\\\\", '\t' => "\\t", '\n' => "\\n", '\r' => "\\r", _ => c.ToString() });
                    }

                    break;
            }
        }

        _writer.Write(_line);
        _writer.Write('\n');
        Rows++;
    }

    /// <summary>Loads the rows into <paramref name="table"/>'s <paramref name="columns"/>.</summary>
    public async Task<long> LoadAsync(MySqlConnection connection, string table, IReadOnlyList<string> columns, CancellationToken ct)
    {
        await _writer.FlushAsync(ct);
        _writer.Close();
        var loader = new MySqlBulkLoader(connection)
        {
            TableName = $"`{table}`",
            Local = true,
            FileName = _path,
            CharacterSet = "utf8mb4",
            FieldTerminator = "\t",
            LineTerminator = "\n",
            Timeout = 0,
        };
        foreach (var column in columns)
        {
            loader.Columns.Add($"`{column}`");
        }

        return await StagingLoader.WithWarningsAsFailures(connection, table, () => loader.LoadAsync(ct));
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        File.Delete(_path);
    }
}
