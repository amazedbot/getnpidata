using System.Text.Json;
using Serilog;

namespace Npi.Loader.Datasets;

/// <summary>
/// Downloads a dataset file to the work folder: streams to a <c>.part</c> file (in parallel parts when large and the server
/// allows it, <see cref="HttpFileFetch"/>), checks the length when the server sends one, renames, and retries with backoff.
/// Never reports success for a failed download. A copy left by an earlier download of the same URL (a load that failed keeps
/// its file) is reused when the server says it hasn't changed since (ETag / Last-Modified, kept in <c>{file}.download.json</c>).
/// </summary>
public sealed class FileDownloader(HttpClient http, ILogger log, int attempts = 4, TimeSpan? firstRetryDelay = null, int parts = 1,
    long partMinBytes = FileDownloader.DefaultPartMinBytes)
{
    /// <summary>Smaller files are fetched in one stream.</summary>
    public const long DefaultPartMinBytes = 64L << 20;

    private readonly TimeSpan _firstRetryDelay = firstRetryDelay ?? TimeSpan.FromSeconds(30);

    private sealed record Stored(string Url, string? ETag, DateTimeOffset? LastModified, long? Length);

    /// <summary>The sidecar file with the validators of a downloaded file.</summary>
    public static string MetaPath(string path) => path + ".download.json";

    /// <summary>Deletes a downloaded file and its sidecar (after a successful load).</summary>
    public static void Delete(string path)
    {
        File.Delete(path);
        File.Delete(MetaPath(path));
    }

    /// <returns>The full path of the downloaded (or still current) file.</returns>
    public async Task<string> DownloadAsync(Uri url, string fileName, string workFolder, CancellationToken ct)
    {
        Directory.CreateDirectory(workFolder);
        var target = Path.Combine(workFolder, fileName);
        var part = target + ".part";
        var earlier = await EarlierAsync(target, url, ct);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var started = DateTime.UtcNow;
                var result = await HttpFileFetch.ToFileAsync(http, url, part, earlier, parts, partMinBytes, ct);
                if (result.NotModified)
                {
                    log.Information("{File} is unchanged since it was downloaded; using it again", fileName);
                    return target;
                }

                if (result.Bytes == 0)
                {
                    throw new InvalidDataException($"{url} returned an empty file.");
                }

                File.Move(part, target, overwrite: true);
                var v = result.Validators;
                await File.WriteAllTextAsync(MetaPath(target), JsonSerializer.Serialize(new Stored(url.ToString(), v.ETag, v.LastModified, v.Length)), ct);
                log.Information("Downloaded {File}: {Bytes:N0} bytes in {Seconds:N0}s", fileName, result.Bytes, (DateTime.UtcNow - started).TotalSeconds);
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

    // The validators of a complete earlier download of this URL, if its file is still there with the length the server gave.
    private static async Task<FileValidators?> EarlierAsync(string target, Uri url, CancellationToken ct)
    {
        var meta = MetaPath(target);
        if (!File.Exists(target) || !File.Exists(meta))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(await File.ReadAllTextAsync(meta, ct));
            return stored is not null && stored.Url == url.ToString() && (stored.ETag is not null || stored.LastModified is not null)
                   && (stored.Length is null || stored.Length == new FileInfo(target).Length)
                ? new FileValidators(stored.ETag, stored.LastModified, stored.Length)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
