using System.IO.Compression;
using System.Net;
using Npi.Loader.Nppes;
using Serilog;
using Serilog.Core;

namespace Npi.Loader.Tests;

public sealed class DownloaderTests : IDisposable
{
    private static readonly Uri Url = new("https://download.cms.gov/nppes/NPPES_Data_Dissemination_092826_100426_Weekly_V2.zip");
    private const string FileName = "NPPES_Data_Dissemination_092826_100426_Weekly_V2.zip";
    private readonly string _work = Directory.CreateTempSubdirectory("npi-dl-").FullName;
    private readonly ILogger _log = Logger.None;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    [Fact]
    public async Task A_good_zip_is_saved_under_its_name()
    {
        var handler = new StubHandler(_ => Ok(ZipBytes()));
        var downloader = new Downloader(new HttpClient(handler), _log, attempts: 1);

        var path = await downloader.DownloadAsync(Url, FileName, _work, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_work, FileName), path);
        Assert.True(Downloader.IsValidZip(path));
        Assert.False(File.Exists(path + ".part"));
    }

    // Legacy defect #4: download errors were swallowed and reported as success.
    [Fact]
    public async Task Http_errors_are_retried_then_thrown_and_leave_no_file()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var downloader = new Downloader(new HttpClient(handler), _log, attempts: 3, firstRetryDelay: TimeSpan.Zero);

        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(Url, FileName, _work, TestContext.Current.CancellationToken));

        Assert.Equal(3, handler.Calls);
        Assert.Empty(Directory.EnumerateFiles(_work));
    }

    [Fact]
    public async Task A_corrupt_zip_is_not_accepted()
    {
        var handler = new StubHandler(_ => Ok("this is not a zip"u8.ToArray()));
        var downloader = new Downloader(new HttpClient(handler), _log, attempts: 2, firstRetryDelay: TimeSpan.Zero);

        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(Url, FileName, _work, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFiles(_work));
    }

    [Fact]
    public async Task A_transient_failure_is_retried()
    {
        var handler = new StubHandler(n => n == 1 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Ok(ZipBytes()));
        var downloader = new Downloader(new HttpClient(handler), _log, attempts: 3, firstRetryDelay: TimeSpan.Zero);

        var path = await downloader.DownloadAsync(Url, FileName, _work, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Calls);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task An_already_downloaded_valid_zip_is_reused()
    {
        await File.WriteAllBytesAsync(Path.Combine(_work, FileName), ZipBytes(), TestContext.Current.CancellationToken);
        var handler = new StubHandler(_ => throw new InvalidOperationException("should not download"));
        var downloader = new Downloader(new HttpClient(handler), _log, attempts: 1);

        await downloader.DownloadAsync(Url, FileName, _work, TestContext.Current.CancellationToken);

        Assert.Equal(0, handler.Calls);
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static byte[] ZipBytes()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("npidata_pfile_x.csv").Open());
            writer.Write("\"NPI\"\n\"1234567893\"\n");
        }

        return ms.ToArray();
    }

    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(++Calls));
    }
}
