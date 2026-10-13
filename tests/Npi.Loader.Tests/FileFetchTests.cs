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
    public async Task A_file_that_changes_while_its_parts_download_fails_rather_than_mixing_versions()
    {
        var server = new FakeServer(Body(10_000), etag: "\"v1\"", ranges: true) { IgnoreRanges = true };
        var downloader = new FileDownloader(new HttpClient(server), Logger.None, attempts: 1, parts: 4, partMinBytes: 1000);

        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(Url, "big.csv", _work, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(_work));
    }

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

    /// <summary>Serves one file with an ETag; answers If-None-Match with 304 and Range requests with 206 when <c>ranges</c>.</summary>
    private sealed class FakeServer(byte[] body, string? etag, bool ranges = false) : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private byte[] _body = body;
        private string? _etag = etag;

        public List<string> IfNoneMatch { get; } = [];

        public List<string> Ranges { get; } = [];

        public List<string> IfRange { get; } = [];

        public int FullBodies { get; private set; }

        /// <summary>Answers range requests with the whole file (200), as a server whose file changed does under If-Range.</summary>
        public bool IgnoreRanges { get; init; }

        public void Change(byte[] newBody, string newEtag) => (_body, _etag) = (newBody, newEtag);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                IfNoneMatch.AddRange(request.Headers.IfNoneMatch.Select(t => t.Tag));
                if (_etag is not null && request.Headers.IfNoneMatch.Any(t => t.Tag == _etag))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK);
                if (request.Headers.Range?.Ranges.SingleOrDefault() is { } range && !IgnoreRanges)
                {
                    Ranges.Add($"{range.From}-{range.To}");
                    IfRange.Add(request.Headers.IfRange?.EntityTag?.Tag ?? "");
                    var (from, to) = ((int)range.From!.Value, (int)range.To!.Value);
                    response.StatusCode = HttpStatusCode.PartialContent;
                    response.Content = new ByteArrayContent(_body[from..(to + 1)]);
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
