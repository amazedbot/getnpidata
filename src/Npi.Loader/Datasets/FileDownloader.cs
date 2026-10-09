using Serilog;

namespace Npi.Loader.Datasets;

/// <summary>
/// Downloads a dataset file to the work folder: streams to a <c>.part</c> file, checks the length when the
/// server sends one, renames, and retries with backoff. Never reports success for a failed download.
/// </summary>
public sealed class FileDownloader(HttpClient http, ILogger log, int attempts = 4, TimeSpan? firstRetryDelay = null)
{
    private readonly TimeSpan _firstRetryDelay = firstRetryDelay ?? TimeSpan.FromSeconds(30);

    /// <returns>The full path of the downloaded file (always a fresh download).</returns>
    public async Task<string> DownloadAsync(Uri url, string fileName, string workFolder, CancellationToken ct)
    {
        Directory.CreateDirectory(workFolder);
        var target = Path.Combine(workFolder, fileName);
        var part = target + ".part";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var started = DateTime.UtcNow;
                long bytes;
                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    var expected = response.Content.Headers.ContentLength;
                    await using (var source = await response.Content.ReadAsStreamAsync(ct))
                    await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                    {
                        await source.CopyToAsync(file, 1 << 20, ct);
                        bytes = file.Length;
                    }

                    if (expected is not null && bytes != expected)
                    {
                        throw new IOException($"Download of {fileName} was truncated: got {bytes:N0} of {expected:N0} bytes.");
                    }
                }

                if (bytes == 0)
                {
                    throw new InvalidDataException($"{url} returned an empty file.");
                }

                File.Move(part, target, overwrite: true);
                log.Information("Downloaded {File}: {Bytes:N0} bytes in {Seconds:N0}s", fileName, bytes, (DateTime.UtcNow - started).TotalSeconds);
                return target;
            }
            catch (Exception ex) when (attempt < attempts && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
            {
                var delay = _firstRetryDelay * Math.Pow(2, attempt - 1);
                log.Warning(ex, "Download of {Url} failed (attempt {Attempt} of {Attempts}); retrying in {Delay}", url, attempt, attempts, delay);
                await Task.Delay(delay, ct);
            }
            finally
            {
                if (File.Exists(part))
                {
                    File.Delete(part);
                }
            }
        }
    }
}
