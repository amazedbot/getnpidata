using System.IO.Compression;
using Serilog;

namespace Npi.Loader.Nppes;

/// <summary>
/// Downloads NPPES zips to the work folder (CLAUDE.md §7 Stage 1.3): streams to a temporary
/// <c>.part</c> file, checks the length and that the zip opens, then renames. Retries with backoff.
/// Never reports success for a failed download (legacy defect #4).
/// </summary>
public sealed class Downloader(HttpClient http, ILogger log, int attempts = 4, TimeSpan? firstRetryDelay = null)
{
    private readonly TimeSpan _firstRetryDelay = firstRetryDelay ?? TimeSpan.FromSeconds(30);

    /// <returns>The full path of the verified zip.</returns>
    public async Task<string> DownloadAsync(Uri url, string fileName, string workFolder, CancellationToken ct)
    {
        Directory.CreateDirectory(workFolder);
        var target = Path.Combine(workFolder, fileName);
        if (File.Exists(target) && IsValidZip(target))
        {
            log.Information("Using already-downloaded {File}", target);
            return target;
        }

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

                if (!IsValidZip(part))
                {
                    throw new InvalidDataException($"{fileName} is not a readable zip file.");
                }

                File.Move(part, target, overwrite: true);
                log.Information("Downloaded {File}: {Bytes:N0} bytes in {Seconds:N0}s", fileName, bytes, (DateTime.UtcNow - started).TotalSeconds);
                return target;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                TryDelete(part);
                if (attempt >= attempts)
                {
                    throw new IOException($"Downloading {url} failed after {attempts} attempts: {ex.Message}", ex);
                }

                var delay = _firstRetryDelay * Math.Pow(4, attempt - 1);
                log.Warning(ex, "Download attempt {Attempt}/{Attempts} of {File} failed; retrying in {Delay}", attempt, attempts, fileName, delay);
                await Task.Delay(delay, ct);
            }
            catch
            {
                TryDelete(part);
                throw;
            }
        }
    }

    internal static bool IsValidZip(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Count > 0;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort; the next attempt overwrites it.
        }
    }
}
