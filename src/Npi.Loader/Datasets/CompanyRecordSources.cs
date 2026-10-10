using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Npi.Core.Search;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>Shared helpers of the company record sources (Stage 5.5 item 17 extras).</summary>
internal static class CompanyRecords
{
    /// <summary>Inserts <paramref name="rows"/> in one transaction (a few tens of thousands of small rows).</summary>
    public static async Task InsertAsync<T>(MySqlConnection connection, string sql, IEnumerable<T> rows, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct);
        foreach (var chunk in rows.Chunk(1000))
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, chunk, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var t && t.Length > max ? t[..max] : value.Trim();

    /// <summary>"20260820" → 2026-08-20; anything else → null.</summary>
    public static DateTime? Date(string? yyyymmdd) =>
        DateTime.TryParseExact(yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>
/// openFDA drug and device enforcement reports: every recall FDA classified (Class I most serious) since 2004, with the
/// recalling firm, product, reason and status. Bulk files listed in openFDA's download index; the export dates are the version.
/// </summary>
public sealed class FdaEnforcementSource : DatasetSource
{
    public static readonly (string Category, string ProductType)[] Kinds = [("drug", "Drugs"), ("device", "Devices")];

    public override string Name => "fda_enforcement";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    private sealed record Partition(string Category, string ProductType, Uri Url, string ExportDate);

    private static async Task<IReadOnlyList<Partition>> FindAsync(DatasetContext context, CancellationToken ct)
    {
        await using var stream = await context.Http.GetStreamAsync(new Uri(context.Options.FdaDownloadIndexUrl), ct);
        return ParseIndex(stream);
    }

    private static List<Partition> ParseIndex(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("results");
        var partitions = new List<Partition>();
        foreach (var (category, productType) in Kinds)
        {
            var enforcement = results.GetProperty(category).GetProperty("enforcement");
            var exportDate = enforcement.GetProperty("export_date").GetString() ?? "";
            foreach (var p in enforcement.GetProperty("partitions").EnumerateArray())
            {
                partitions.Add(new Partition(category, productType, new Uri(p.GetProperty("file").GetString()!), exportDate));
            }
        }

        return partitions.Count > 0 ? partitions : throw new InvalidDataException("The openFDA download index lists no enforcement files.");
    }

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var partitions = await FindAsync(context, ct);
        return new DatasetRelease(string.Join(" | ", partitions.Select(p => $"{p.Category} {p.ExportDate}").Distinct()), partitions[0].Url);
    }

    private sealed class Report
    {
        [JsonPropertyName("recall_number")] public string? RecallNumber { get; set; }
        [JsonPropertyName("recalling_firm")] public string? Firm { get; set; }
        [JsonPropertyName("classification")] public string? Classification { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("recall_initiation_date")] public string? InitiationDate { get; set; }
        [JsonPropertyName("report_date")] public string? ReportDate { get; set; }
        [JsonPropertyName("voluntary_mandated")] public string? VoluntaryMandated { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("product_description")] public string? ProductDescription { get; set; }
        [JsonPropertyName("reason_for_recall")] public string? Reason { get; set; }
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var partitions = await FindAsync(context, ct);
        await using var connection = await context.Database.OpenAsync(ct);
        var counts = await TableSwap.ReplaceAsync(connection, ["fda_enforcement"], context.Options.MinRowRatio, async () =>
        {
            foreach (var partition in partitions)
            {
                var zipPath = await context.DownloadAsync(new DatasetRelease(release.Version, partition.Url), $"fda_{partition.Category}_enforcement.zip", ct);
                try
                {
                    using var zip = ZipFile.OpenRead(zipPath);
                    var entry = zip.Entries.Single(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
                    await using var json = entry.Open();
                    var rows = new List<object>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    await foreach (var r in JsonSerializer.DeserializeAsyncEnumerable<Report>(new JsonArrayStream(json, "results"), cancellationToken: ct))
                    {
                        if (r?.RecallNumber is not { Length: > 0 } number || !seen.Add(number))
                        {
                            continue;
                        }

                        rows.Add(new
                        {
                            number = CompanyRecords.Trim(number, 30), type = partition.ProductType, firm = CompanyRecords.Trim(r.Firm, 255),
                            key = CompanyNames.Key(r.Firm), cls = CompanyRecords.Trim(r.Classification, 30), status = CompanyRecords.Trim(r.Status, 30),
                            initiated = CompanyRecords.Date(r.InitiationDate), reported = CompanyRecords.Date(r.ReportDate),
                            vm = CompanyRecords.Trim(r.VoluntaryMandated, 60), city = CompanyRecords.Trim(r.City, 100), state = CompanyRecords.Trim(r.State, 40),
                            country = CompanyRecords.Trim(r.Country, 100), product = CompanyRecords.Trim(r.ProductDescription, 1000),
                            reason = CompanyRecords.Trim(r.Reason, 1000),
                        });
                    }

                    await CompanyRecords.InsertAsync(connection,
                        """
                        INSERT INTO `fda_enforcement_staging` (`recall_number`, `product_type`, `firm`, `firm_key`, `classification`, `status`, `initiation_date`,
                          `report_date`, `voluntary_mandated`, `city`, `state`, `country`, `product_description`, `reason`)
                        VALUES (@number, @type, @firm, @key, @cls, @status, @initiated, @reported, @vm, @city, @state, @country, @product, @reason)
                        """, rows, ct);
                    context.Log.Information("FDA {Category} enforcement reports: {Rows:N0}", partition.Category, rows.Count);
                }
                finally
                {
                    File.Delete(zipPath);
                }
            }
        }, ct);
        return counts["fda_enforcement"];
    }
}

/// <summary>
/// SEC EDGAR's list of registrants with a ticker (CIK, name, ticker, exchange). SEC's fair-access policy asks every
/// automated client to identify itself with a contact e-mail, so the source runs only when <see cref="LoaderOptions.SecUserAgent"/>
/// is set (user-secrets; it is private). Without it the source records the version "not configured" and loads nothing
/// (the run doesn't fail); setting it later changes the version, so the next run loads the list.
/// </summary>
public sealed class SecCompanySource : DatasetSource
{
    public override string Name => "sec_companies";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    private static HttpRequestMessage Request(DatasetContext context, HttpMethod method)
    {
        if (string.IsNullOrWhiteSpace(context.Options.SecUserAgent))
        {
            throw new InvalidOperationException(
                "SEC EDGAR needs a contact in the User-Agent: set SecUserAgent to \"getnpidata <your e-mail>\" with dotnet user-secrets --project src/Npi.Loader set \"SecUserAgent\" \"…\".");
        }

        var request = new HttpRequestMessage(method, context.Options.SecCompanyTickersUrl);
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", context.Options.SecUserAgent);
        return request;
    }

    public const string NotConfigured = "not configured";

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.Options.SecUserAgent))
        {
            return new DatasetRelease(NotConfigured, new Uri(context.Options.SecCompanyTickersUrl));
        }

        using var request = Request(context, HttpMethod.Head);
        using var response = await context.Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var modified = response.Content.Headers.LastModified?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            ?? response.Headers.ETag?.Tag ?? $"checked {DateTime.UtcNow:yyyy-MM-dd}";
        return new DatasetRelease(modified, new Uri(context.Options.SecCompanyTickersUrl));
    }

    /// <summary>company_tickers_exchange.json: {"fields": ["cik", "name", "ticker", "exchange"], "data": [[320193, "Apple Inc.", "AAPL", "Nasdaq"], …]}.</summary>
    public static IReadOnlyList<(int Cik, string Name, string Ticker, string? Exchange)> Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var fields = doc.RootElement.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ToList();
        int cik = fields.IndexOf("cik"), name = fields.IndexOf("name"), ticker = fields.IndexOf("ticker"), exchange = fields.IndexOf("exchange");
        if (cik < 0 || name < 0 || ticker < 0)
        {
            throw new InvalidDataException("The SEC ticker file has no cik, name or ticker field.");
        }

        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(row => row.EnumerateArray().ToList())
            .Where(row => row[cik].ValueKind == JsonValueKind.Number && row[ticker].GetString() is { Length: > 0 })
            .Select(row => (row[cik].GetInt32(), row[name].GetString() ?? "", row[ticker].GetString()!,
                exchange >= 0 && row[exchange].ValueKind == JsonValueKind.String ? row[exchange].GetString() : null))
            .ToList();
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.Options.SecUserAgent))
        {
            context.Log.Warning("SEC EDGAR skipped: set SecUserAgent (\"getnpidata <contact e-mail>\") in the loader's user-secrets to load public company tickers");
            return 0;
        }

        using var request = Request(context, HttpMethod.Get);
        using var response = await context.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        IReadOnlyList<(int Cik, string Name, string Ticker, string? Exchange)> companies;
        await using (var stream = await response.Content.ReadAsStreamAsync(ct))
        {
            companies = Parse(stream);
        }

        await using var connection = await context.Database.OpenAsync(ct);
        var counts = await TableSwap.ReplaceAsync(connection, ["sec_company"], context.Options.MinRowRatio, () =>
            CompanyRecords.InsertAsync(connection,
                "INSERT IGNORE INTO `sec_company_staging` (`cik`, `ticker`, `name`, `name_key`, `exchange`) VALUES (@cik, @ticker, @name, @key, @exchange)",
                companies.Select(c => new { cik = c.Cik, ticker = CompanyRecords.Trim(c.Ticker, 20), name = CompanyRecords.Trim(c.Name, 255) ?? "", key = CompanyNames.Key(c.Name), exchange = CompanyRecords.Trim(c.Exchange, 40) }),
                ct), ct);
        return counts["sec_company"];
    }
}

/// <summary>
/// HHS-OIG's Corporate Integrity Agreements (and other integrity agreements): companies that settled federal health care
/// fraud cases and agreed to OIG oversight, with the status (Effective, Closed, …) and its date. Read from OIG's list
/// pages (about 20 agreements a page); the version is a hash of the whole list.
/// </summary>
public sealed partial class OigCiaSource : DatasetSource
{
    public override string Name => "oig_cia";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    public const int MaxPages = 200;

    public sealed record Agreement(string Slug, string Name, string? Location, string? Type, string? Status, DateTime? StatusDate, string Url);

    [GeneratedRegex("""<li class="usa-card card--list[^"]*">(?<card>.*?)</li>""", RegexOptions.Singleline)]
    private static partial Regex Card();

    [GeneratedRegex("""<a[^>]*href="(?<href>[^"]*/browse-cias/(?<slug>[^"/]+)/)"[^>]*>\s*(?<name>.*?)\s*</a>""", RegexOptions.Singleline)]
    private static partial Regex Heading();

    [GeneratedRegex("""<div class="pep-metadata[^"]*"[^>]*>(?<meta>.*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex Metadata();

    [GeneratedRegex("""<span>(?<text>.*?)</span>""", RegexOptions.Singleline)]
    private static partial Regex Span();

    [GeneratedRegex("""href="\?page=(?<n>\d+)""")]
    private static partial Regex PageLink();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(?<status>.*?)\s*(?<date>[A-Z][a-z]{2} \d{1,2}, \d{4})?$")]
    private static partial Regex StatusAndDate();

    private static string Text(string html) => Spaces().Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), " ").Trim();

    /// <summary>The agreements on one list page, and the highest page number it links to.</summary>
    public static (IReadOnlyList<Agreement> Agreements, int LastPage) ParsePage(string html, Uri pageUrl)
    {
        var agreements = new List<Agreement>();
        foreach (Match card in Card().Matches(html))
        {
            var heading = Heading().Match(card.Groups["card"].Value);
            if (!heading.Success)
            {
                continue;
            }

            var spans = Metadata().Match(card.Groups["card"].Value) is { Success: true } meta
                ? Span().Matches(meta.Groups["meta"].Value).Select(s => Text(s.Groups["text"].Value)).ToList()
                : [];
            var statusAndDate = spans.Count > 2 ? StatusAndDate().Match(spans[2]) : null;
            DateTime? date = statusAndDate?.Groups["date"].Success == true
                && DateTime.TryParseExact(statusAndDate.Groups["date"].Value, ["MMM dd, yyyy", "MMM d, yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : null;
            agreements.Add(new Agreement(heading.Groups["slug"].Value, Text(heading.Groups["name"].Value), spans.Count > 0 ? spans[0] : null,
                spans.Count > 1 ? spans[1] : null, statusAndDate is { Success: true } ? statusAndDate.Groups["status"].Value.Trim() : null, date,
                new Uri(pageUrl, heading.Groups["href"].Value).AbsoluteUri));
        }

        var last = PageLink().Matches(html).Select(m => int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(1).Max();
        return (agreements, last);
    }

    private static async Task<IReadOnlyList<Agreement>> FetchAllAsync(DatasetContext context, CancellationToken ct)
    {
        var all = new Dictionary<string, Agreement>(StringComparer.Ordinal);
        var baseUrl = new Uri(context.Options.OigCiaUrl);
        var last = 1;
        for (var page = 1; page <= last && page <= MaxPages; page++)
        {
            var url = page == 1 ? baseUrl : new Uri(baseUrl, $"?page={page}");
            var (agreements, pageLast) = ParsePage(await context.Http.GetStringAsync(url, ct), url);
            if (agreements.Count == 0)
            {
                break;
            }

            foreach (var a in agreements)
            {
                all.TryAdd(a.Slug, a);
            }

            last = Math.Max(last, pageLast);
        }

        return all.Count > 0 ? all.Values.ToList() : throw new InvalidDataException("OIG's integrity agreement list had no agreements; the page changed?");
    }

    private static string Version(IReadOnlyList<Agreement> agreements) =>
        $"{agreements.Count} agreements " + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            agreements.OrderBy(a => a.Slug, StringComparer.Ordinal).Select(a => $"{a.Slug}|{a.Name}|{a.Status}|{a.StatusDate:yyyy-MM-dd}")))))[..12];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
        new(Version(await FetchAllAsync(context, ct)), new Uri(context.Options.OigCiaUrl));

    [GeneratedRegex(@"\s*;\s*|\s+and\s+|\s+d\.?/?b\.?/?a\.?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex EntitySeparators();

    /// <summary>
    /// The entities an agreement names, as name keys: the whole name and each part between ";", " and " or " d.b.a. "
    /// ("SNAP Diagnostics, LLC and Gil Raviv" → the whole, SNAP Diagnostics, Gil Raviv). A part matches a company only
    /// when its key equals the company's whole key.
    /// </summary>
    public static IEnumerable<string> EntityKeys(string name) =>
        new[] { name }.Concat(EntitySeparators().Split(name))
            .Select(CompanyNames.Key).OfType<string>().Distinct(StringComparer.Ordinal);

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var agreements = await FetchAllAsync(context, ct);
        await using var connection = await context.Database.OpenAsync(ct);
        var counts = await TableSwap.ReplaceAsync(connection, ["oig_cia", "oig_cia_entity"], context.Options.MinRowRatio, async () =>
        {
            await CompanyRecords.InsertAsync(connection,
                """
                INSERT INTO `oig_cia_staging` (`slug`, `name`, `location`, `agreement_type`, `status`, `status_date`, `url`)
                VALUES (@slug, @name, @location, @type, @status, @date, @url)
                """,
                agreements.Select(a => new
                {
                    slug = a.Slug, name = CompanyRecords.Trim(a.Name, 2000) ?? a.Slug, location = CompanyRecords.Trim(a.Location, 200),
                    type = CompanyRecords.Trim(a.Type, 100), status = CompanyRecords.Trim(a.Status, 40), date = a.StatusDate, url = a.Url,
                }), ct);
            await CompanyRecords.InsertAsync(connection, "INSERT INTO `oig_cia_entity_staging` (`slug`, `name_key`) VALUES (@slug, @key)",
                agreements.SelectMany(a => EntityKeys(a.Name).Select(k => new { slug = a.Slug, key = k })), ct);
        }, ct);
        return counts["oig_cia"];
    }
}

/// <summary>
/// A read-only stream over the array value of one top-level property of a JSON object (openFDA files are
/// {"meta": {…, "results": {…}}, "results": [ … ]}), so <see cref="JsonSerializer.DeserializeAsyncEnumerable{TValue}(Stream, JsonSerializerOptions?, CancellationToken)"/>
/// can read the records one at a time instead of parsing a 400 MB document.
/// </summary>
internal sealed class JsonArrayStream(Stream inner, string property) : Stream
{
    private readonly BufferedStream _in = new(inner, 1 << 16);
    private readonly byte[] _key = Encoding.UTF8.GetBytes(property);
    private bool _started;
    private bool _done;
    private int _depth;
    private bool _inString;
    private bool _escape;
    private byte? _pending;

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (!_started)
        {
            SkipToArray();
        }

        var n = 0;
        if (_pending is { } first && count > 0)
        {
            buffer[offset + n++] = first;
            _pending = null;
        }

        while (n < count && !_done)
        {
            var b = _in.ReadByte();
            if (b < 0)
            {
                throw new InvalidDataException("The JSON ended inside the array.");
            }

            buffer[offset + n++] = (byte)b;
            if (Track((byte)b) && _depth == 0)
            {
                _done = true;
            }
        }

        return n;
    }

    // Updates the string/depth state; true when the byte closed a container.
    private bool Track(byte b)
    {
        if (_inString)
        {
            if (_escape) { _escape = false; }
            else if (b == '\\') { _escape = true; }
            else if (b == '"') { _inString = false; }
            return false;
        }

        switch (b)
        {
            case (byte)'"': _inString = true; return false;
            case (byte)'{' or (byte)'[': _depth++; return false;
            case (byte)'}' or (byte)']': _depth--; return true;
            default: return false;
        }
    }

    // Reads up to the '[' that starts the value of the property at depth 1, keeping it for Read.
    private void SkipToArray()
    {
        var name = new List<byte>();
        var collecting = false;
        var keyAtDepth1 = false;
        int b;
        while ((b = _in.ReadByte()) >= 0)
        {
            var wasInString = _inString;
            if (!wasInString && b == '"' && _depth == 1)
            {
                collecting = true;
                name.Clear();
            }
            else if (wasInString && collecting && b != '"')
            {
                name.Add((byte)b);
            }

            Track((byte)b);
            if (wasInString && !_inString && collecting)
            {
                collecting = false;
                keyAtDepth1 = name.SequenceEqual(_key);
                continue;
            }

            if (!_inString && keyAtDepth1 && b == '[' && _depth == 2)
            {
                _depth = 1; // the array itself; Read stops when it closes
                _started = true;
                _pending = (byte)'[';
                return;
            }

            if (!_inString && keyAtDepth1 && b is not ((byte)':' or (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t'))
            {
                keyAtDepth1 = false; // the property's value isn't an array (e.g. meta.results is an object at depth 2)
            }
        }

        throw new InvalidDataException($"The JSON has no top-level \"{Encoding.UTF8.GetString(_key)}\" array.");
    }

    public override int Read(Span<byte> buffer)
    {
        var array = new byte[buffer.Length];
        var n = Read(array, 0, array.Length);
        array.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _in.Dispose();
        }

        base.Dispose(disposing);
    }
}
