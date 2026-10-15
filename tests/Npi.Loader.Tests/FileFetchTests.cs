using System.Net;
using System.Net.Http.Headers;
using Npi.Loader.Datasets;
using Npi.Loader.Nppes;
using Serilog.Core;

namespace Npi.Loader.Tests;

/// <summary>Speed audit item 8: downloads reused when unchanged, and large files fetched in parallel byte ranges.</summary>
public sealed class FileFetchTests : IDisposable
{
    private static readonly Uri Url = new("https://data.example.org/files/big.csv");
    private readonly string _work = Directory.CreateTempSubdirectory("npi-fetch-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private static byte[] Body(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 7 % 251)).ToArray();

    [Fact]
    public async Task An_unchanged_file_left_by_a_failed_load_is_used_again()
    {
        var body = Body(1000);
        var server = new FakeServer(body, etag: "\"v1\"");
        var downloader = new FileDownloader(new HttpClient(server), Logger.None, attempts: 1);

        var path = await downloader.DownloadAsync(Url, "big.csv", _work, TestContext.Current.CancellationToken);
        Assert.Equal(body, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(FileDownloader.MetaPath(path)));

        // The load failed, the file stayed: the next attempt asks "only if changed" and gets 304.
        Assert.Equal(path, await downloader.DownloadAsync(Url, "big.csv", _work, TestContext.Current.CancellationToken));
        Assert.Equal(["\"v1\""], server.IfNoneMatch);
        Assert.Equal(1, server.FullBodies);
        Assert.Equal(body, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        // The publisher changed it: downloaded again.
        server.Change(Body(1200), "\"v2\"");
        await downloader.DownloadAsync(Url, "big.csv", _work, TestContext.Current.CancellationToken);
        Assert.Equal(2, server.FullBodies);
        Assert.Equal(1200, new FileInfo(path).Length);

        FileDownloader.Delete(path);
        Assert.Empty(Directory.EnumerateFiles(_work));
    }

    [Fact]
    public async Task A_copy_that_is_not_complete_is_not_used()
    {
        var server = new FakeServer(Body(1000), etag: "\"v1\"");
        var downloader = new FileDownloader(new HttpClient(server), Logger.None, attempts: 1);
        var path = await downloader.DownloadAsync(Url, "big.csv", _work, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(path, Body(10), TestContext.Current.CancellationToken); // not the length the server gave

        await downloader.DownloadAsync(Url, "big.csv", _work, TestContext.Current.CancellationToken);
        Assert.Empty(server.IfNoneMatch);
        Assert.Equal(1000, new FileInfo(path).Length);
    }

    [Fact]
    public async Task A_large_file_is_fetched_in_parallel_ranges()
    {
        var body = Body(10_003);
        var server = new FakeServer(body, etag: "\"v1\"", ranges: true);
        var downloader = new FileDownloader(new HttpClient(server), Logger.None, attempts: 1, parts: 4, partMinBytes: 1000);

        var path = await downloader.DownloadAsync(Url, "big.csv", _work, TestContext.Current.CancellationToken);

        Assert.Equal(body, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(["0-2500", "2501-5001", "5002-7502", "7503-10002"], server.Ranges.Order(StringComparer.Ordinal));
        Assert.All(server.IfRange, r => Assert.Equal("\"v1\"", r));
    }

    [Fact]
    public async Task A_server_that_ignores_ranges_gets_one_plain_download()
    {
        // data.nber.org under load: ranged requests answered 200 with the whole file.
        var body = Body(10_000);
        var server = new FakeServer(body, etag: "\"v1\"", ranges: true) { IgnoreRanges = true };
        var downloader = new FileDownloader(new HttpClient(server), Logger.None, attempts: 1, parts: 4, partMinBytes: 1000);

        var path = await downloader.DownloadAsync(Url, "big.csv", _work, TestContext.Current.CancellationToken);

        Assert.Equal(body, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path + ".part"));
        Assert.True(server.FullBodies >= 3); // the first request, the parts answered whole, the download in one stream
        Assert.Contains("v1", await File.ReadAllTextAsync(FileDownloader.MetaPath(path), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_part_answered_503_once_is_asked_again_in_the_same_attempt()
    {
        var body = Body(10_003);
        var failed = 0;
        var server = new FakeServer(body, etag: "\"v1\"", ranges: true)
        {
            Fault = (request, _) => request.Headers.Range?.Ranges.Single().From == 2501 && ++failed == 1 ? HttpStatusCode.ServiceUnavailable : null,
        };
        var path = Path.Combine(_work, "big.csv");

        var result = await Fetch(server, path);

        Assert.Equal(body, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(10_003, result.Bytes);
        Assert.Equal(5, server.Ranges.Count); // four parts, one asked twice
        Assert.Equal(1, server.FullBodies); // only the first request: no fall back
    }

    [Fact]
    public async Task A_file_that_changes_while_its_parts_download_is_taken_whole_in_its_new_version()
    {
        // The first two parts come from v1; then the publisher replaces the file, so If-Range "v1" no longer matches and the
        // server sends v2 whole. The result must be v2 entirely, never v1's first parts with v2's last.
        var v1 = Body(10_000);
        var v2 = Enumerable.Range(0, 12_000).Select(i => (byte)(i * 13 % 239)).ToArray();
        var server = new FakeServer(v1, etag: "\"v1\"", ranges: true)
        {
            Fault = (request, s) =>
            {
                if (request.Headers.Range is not null && s.Ranges.Count == 2)
                {
                    s.Change(v2, "\"v2\"");
                }

                return null;
            },
        };
        var downloader = new FileDownloader(new HttpClient(server), Logger.None, attempts: 1, parts: 4, partMinBytes: 1000);

        var path = await downloader.DownloadAsync(Url, "big.csv", _work, TestContext.Current.CancellationToken);

        Assert.Equal(v2, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        var meta = await File.ReadAllTextAsync(FileDownloader.MetaPath(path), TestContext.Current.CancellationToken);
        Assert.Contains("v2", meta); // the validators kept for 304 reuse are the new version's
        Assert.Contains("12000", meta);
        Assert.True(server.FullBodies >= 3); // the first request, the parts answered whole, the download in one stream
    }

    [Fact]
    public async Task Parts_that_keep_failing_fail_the_attempt_with_what_the_server_said()
    {
        var server = new FakeServer(Body(10_000), etag: "\"v1\"", ranges: true)
        {
            // The first request is answered; every part and then the download in one stream get 503.
            Fault = (request, s) => request.Headers.Range is not null || s.FullBodies >= 1 ? HttpStatusCode.ServiceUnavailable : null,
        };

        var error = await Assert.ThrowsAnyAsync<IOException>(() => Fetch(server, Path.Combine(_work, "big.csv")));

        Assert.Contains("failed 3 times", error.Message, StringComparison.Ordinal);
        Assert.Contains("got 503", error.Message, StringComparison.Ordinal);
        Assert.Contains("the download in one stream then got 503", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("changed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_part_refused_outright_fails_the_attempt_without_a_fall_back()
    {
        var server = new FakeServer(Body(10_000), etag: "\"v1\"", ranges: true)
        {
            Fault = (request, _) => request.Headers.Range is not null ? HttpStatusCode.Forbidden : null,
        };

        var error = await Assert.ThrowsAnyAsync<IOException>(() => Fetch(server, Path.Combine(_work, "big.csv")));

        Assert.Contains("got 403", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, server.FullBodies);
    }

    [Fact]
    public async Task Parts_are_used_only_with_a_validator_that_keeps_them_to_one_version()
    {
        // A weak ETag can't be used with If-Range (and there is no Last-Modified), so the file comes in one stream.
        var body = Body(10_000);
        var server = new FakeServer(body, etag: "W/\"v1\"", ranges: true);
        var path = Path.Combine(_work, "big.csv");

        await Fetch(server, path);

        Assert.Empty(server.Ranges);
        Assert.Equal(body, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_nppes_zip_from_a_server_that_stops_sending_ranges_is_downloaded_in_one_attempt()
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var entry = zip.CreateEntry("npidata_pfile_20260101-20260107.csv").Open();
            await entry.WriteAsync(Body(50_000), TestContext.Current.CancellationToken);
        }

        // The first part comes back as asked, the next ones as the whole file (200).
        var server = new FakeServer(buffer.ToArray(), etag: "\"z\"", ranges: true)
        {
            Fault = (_, s) =>
            {
                s.IgnoreRanges = s.Ranges.Count >= 1;
                return null;
            },
        };
        var path = await new Downloader(new HttpClient(server), Logger.None, attempts: 1, parts: 3, partMinBytes: 100, partRetryDelay: TimeSpan.Zero)
            .DownloadAsync(Url, "NPPES_Data_Dissemination_092826_100426_Weekly_V2.zip", _work, TestContext.Current.CancellationToken);

        Assert.True(Downloader.IsValidZip(path));
        Assert.Equal(buffer.ToArray(), await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path + ".part"));
    }

    private static Task<HttpFileFetch.Result> Fetch(FakeServer server, string path) =>
        HttpFileFetch.ToFileAsync(new HttpClient(server), Url, path, earlier: null, parts: 4, partMinBytes: 1000, Logger.None, TimeSpan.Zero,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Small_files_and_servers_without_ranges_use_one_stream()
    {
        var server = new FakeServer(Body(5000), etag: null, ranges: false);
        var downloader = new FileDownloader(new HttpClient(server), Logger.None, attempts: 1, parts: 4, partMinBytes: 1000);
        await downloader.DownloadAsync(Url, "big.csv", _work, TestContext.Current.CancellationToken);
        Assert.Empty(server.Ranges);

        var ranged = new FakeServer(Body(500), etag: "\"v1\"", ranges: true);
        await new FileDownloader(new HttpClient(ranged), Logger.None, attempts: 1, parts: 4, partMinBytes: 1000)
            .DownloadAsync(Url, "small.csv", _work, TestContext.Current.CancellationToken);
        Assert.Empty(ranged.Ranges);
    }

    [Fact]
    public async Task An_nppes_zip_is_fetched_in_parts_too()
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var entry = zip.CreateEntry("npidata_pfile_20260101-20260107.csv").Open();
            await entry.WriteAsync(Body(50_000), TestContext.Current.CancellationToken);
        }

        var server = new FakeServer(buffer.ToArray(), etag: "\"z\"", ranges: true);
        var path = await new Downloader(new HttpClient(server), Logger.None, attempts: 1, parts: 3, partMinBytes: 100)
            .DownloadAsync(Url, "NPPES_Data_Dissemination_092826_100426_Weekly_V2.zip", _work, TestContext.Current.CancellationToken);

        Assert.True(Downloader.IsValidZip(path));
        Assert.Equal(3, server.Ranges.Count);
    }

    /// <summary>
    /// Serves one file with an ETag; answers If-None-Match with 304 and Range requests with 206 when <c>ranges</c>, honouring
    /// If-Range (a validator that no longer matches gets the whole file, 200). <see cref="Fault"/> can answer a request with an
    /// error status instead. Every ranged request is listed in <see cref="Ranges"/>, whatever the answer.
    /// </summary>
    private sealed class FakeServer(byte[] body, string? etag, bool ranges = false) : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private byte[] _body = body;
        private string? _etag = etag;

        public List<string> IfNoneMatch { get; } = [];

        public List<string> Ranges { get; } = [];

        public List<string> IfRange { get; } = [];

        public int FullBodies { get; private set; }

        /// <summary>Answers range requests with the whole file (200), as a busy server or proxy may.</summary>
        public bool IgnoreRanges { get; set; }

        /// <summary>Called first for each request (under the server's lock): a status to answer with, or null to serve it.</summary>
        public Func<HttpRequestMessage, FakeServer, HttpStatusCode?>? Fault { get; init; }

        public void Change(byte[] newBody, string newEtag) => (_body, _etag) = (newBody, newEtag);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                var asked = request.Headers.Range?.Ranges.SingleOrDefault();
                if (Fault?.Invoke(request, this) is { } status)
                {
                    if (asked is not null)
                    {
                        Ranges.Add($"{asked.From}-{asked.To}");
                    }

                    return Task.FromResult(new HttpResponseMessage(status));
                }

                IfNoneMatch.AddRange(request.Headers.IfNoneMatch.Select(t => t.Tag));
                if (_etag is not null && request.Headers.IfNoneMatch.Any(t => t.Tag == _etag))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK);
                var ifRange = request.Headers.IfRange?.EntityTag?.ToString();
                if (asked is not null)
                {
                    Ranges.Add($"{asked.From}-{asked.To}");
                }

                if (asked is not null && !IgnoreRanges && (ifRange is null || ifRange == _etag))
                {
                    IfRange.Add(ifRange ?? "");
                    var (from, to) = ((int)asked.From!.Value, (int)asked.To!.Value);
                    response.StatusCode = HttpStatusCode.PartialContent;
                    response.Content = new ByteArrayContent(_body[from..(to + 1)]);
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, _body.Length);
                }
                else
                {
                    FullBodies++;
                    response.Content = new ByteArrayContent(_body);
                }

                if (_etag is not null)
                {
                    response.Headers.ETag = EntityTagHeaderValue.Parse(_etag);
                }

                if (ranges)
                {
                    response.Headers.AcceptRanges.Add("bytes");
                }

                return Task.FromResult(response);
            }
        }
    }
}
