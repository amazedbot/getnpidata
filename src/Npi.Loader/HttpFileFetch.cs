using System.Net;
using System.Net.Http.Headers;
using Serilog;

namespace Npi.Loader;

/// <summary>What a server said about a file: its validators, for asking later "only if it changed".</summary>
public sealed record FileValidators(string? ETag, DateTimeOffset? LastModified, long? Length);

/// <summary>
/// Fetches a URL into a file. Sends <c>If-None-Match</c> / <c>If-Modified-Since</c> when validators of an earlier copy are
/// given (a 304 means that copy is current). A large file from a server that accepts byte ranges is fetched in several
/// parts at once, each written at its offset; <c>If-Range</c> makes the server send the whole file rather than a part of
/// another version. A part answered with a server error, 429 or a dropped connection is asked again a couple of times; a
/// part answered with the whole file (the server ignored the range, or the file changed), or a part that keeps failing, makes
/// the fetch start again as one plain download of the whole file, so a file is never put together from two versions.
/// The length is checked against the server's.
/// </summary>
public static class HttpFileFetch
{
    public sealed record Result(bool NotModified, long Bytes, FileValidators Validators);

    /// <summary>Tries per part before the fetch falls back to one stream.</summary>
    internal const int PartAttempts = 3;

    /// <summary>The wait before a part is asked again (doubled each time).</summary>
    public static readonly TimeSpan DefaultPartRetryDelay = TimeSpan.FromSeconds(2);

    /// <param name="parts">How many ranges to fetch at once (1 = one stream).</param>
    /// <param name="partMinBytes">Files smaller than this are fetched in one stream.</param>
    /// <param name="log">Where part retries and a fall back to one stream are logged (optional).</param>
    /// <param name="partRetryDelay">The first wait before a failed part is asked again (<see cref="DefaultPartRetryDelay"/>).</param>
    public static async Task<Result> ToFileAsync(HttpClient http, Uri url, string path, FileValidators? earlier, int parts, long partMinBytes,
        ILogger? log, TimeSpan? partRetryDelay, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (earlier?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }
        else if (earlier?.LastModified is { } modified)
        {
            request.Headers.IfModifiedSince = modified;
        }

        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            if (response.StatusCode == HttpStatusCode.NotModified && earlier is not null)
            {
                return new Result(true, 0, earlier);
            }

            response.EnsureSuccessStatusCode();
            var validators = ValidatorsOf(response);
            long bytes;
            if (parts > 1 && validators.Length is { } length && length >= partMinBytes && response.Headers.AcceptRanges.Contains("bytes")
                && IfRange(validators) is not null)
            {
                response.Dispose(); // the parts are separate requests
                try
                {
                    bytes = await RangesAsync(http, url, path, length, parts, validators, log, partRetryDelay ?? DefaultPartRetryDelay, ct);
                }
                catch (PartFailedException ex) when (ex.FallBack)
                {
                    log?.Warning("{Problem}; downloading the whole file again in one stream", ex.Message);
                    (bytes, validators) = await OneStreamAgainAsync(http, url, path, ex.Message, ct);
                }
            }
            else
            {
                bytes = await WriteAsync(response, path, ct);
            }

            if (validators.Length is { } expected && bytes != expected)
            {
                throw new IOException($"Download of {url} was truncated: got {bytes:N0} of {expected:N0} bytes.");
            }

            return new Result(false, bytes, validators);
        }
        finally
        {
            response.Dispose();
        }
    }

    private static FileValidators ValidatorsOf(HttpResponseMessage response) =>
        new(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified, response.Content.Headers.ContentLength);

    // The If-Range condition that keeps the parts to one version: a strong ETag, else the Last-Modified date. A weak ETag can't be
    // used (RFC 9110 §13.1.5), and without either the parts couldn't tell a changed file, so such a file is fetched in one stream.
    private static RangeConditionHeaderValue? IfRange(FileValidators validators)
    {
        if (validators.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag) && !tag.IsWeak)
        {
            return new RangeConditionHeaderValue(tag);
        }

        return validators.LastModified is { } modified ? new RangeConditionHeaderValue(modified) : null;
    }

    private static async Task<long> WriteAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        await source.CopyToAsync(file, 1 << 20, ct);
        return file.Length;
    }

    // The whole file from scratch, without conditions: whatever version the server has now, with its own validators. The parts
    // already written are overwritten (FileMode.Create truncates), so nothing of them remains.
    private static async Task<(long Bytes, FileValidators Validators)> OneStreamAgainAsync(HttpClient http, Uri url, string path, string problem,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new IOException($"{problem}; the download in one stream then got {Status(response)}.");
        }

        return (await WriteAsync(response, path, ct), ValidatorsOf(response));
    }

    private static async Task<long> RangesAsync(HttpClient http, Uri url, string path, long length, int parts, FileValidators validators, ILogger? log,
        TimeSpan retryDelay, CancellationToken ct)
    {
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Write))
        {
            file.SetLength(length);
        }

        var size = (length + parts - 1) / parts;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? first = null;
        var tasks = Enumerable.Range(0, parts)
            .Select(i => (From: i * size, To: Math.Min(length, (i + 1) * size) - 1))
            .Where(r => r.From <= r.To)
            .Select(async r =>
            {
                try
                {
                    return await PartWithRetriesAsync(http, url, path, r.From, r.To, length, validators, log, retryDelay, stop.Token);
                }
                catch (Exception ex) when (!stop.IsCancellationRequested)
                {
                    // The first part to fail decides; the others stop (and end with OperationCanceledException).
                    Interlocked.CompareExchange(ref first, ex, null);
                    await stop.CancelAsync();
                    throw;
                }
            })
            .ToList();
        try
        {
            return (await Task.WhenAll(tasks)).Sum();
        }
        catch when (!ct.IsCancellationRequested && first is not null)
        {
            // Task.WhenAll returns only when every part has ended, so no part still writes to the file.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
            throw;
        }
    }

    private static async Task<long> PartWithRetriesAsync(HttpClient http, Uri url, string path, long from, long to, long length, FileValidators validators,
        ILogger? log, TimeSpan retryDelay, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await PartAsync(http, url, path, from, to, length, validators, ct);
            }
            catch (PartFailedException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
            {
                if (attempt >= PartAttempts)
                {
                    throw new PartFailedException($"Download of {url} bytes {from}-{to} failed {PartAttempts} times (last: {ex.Message})", fallBack: true, ex);
                }

                var delay = retryDelay * Math.Pow(2, attempt - 1);
                log?.Information("Download of {Url} bytes {From}-{To}: {Problem}; asking for the part again in {Delay}", url, from, to, ex.Message, delay);
                await Task.Delay(delay, ct);
            }
        }
    }

    // A server error, 429 or 408 (TransientPartException), a dropped connection or a part cut short (IOException,
    // HttpRequestException), or a request timeout (cancelled while the caller's token was not).
    private static bool IsTransient(Exception ex) => ex is IOException or HttpRequestException or OperationCanceledException;

    private static async Task<long> PartAsync(HttpClient http, Uri url, string path, long from, long to, long length, FileValidators validators,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, to);
        request.Headers.IfRange = IfRange(validators);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = response.StatusCode;
        if (status == HttpStatusCode.OK)
        {
            throw new PartFailedException(
                $"Download of {url} bytes {from}-{to}: the server sent the whole file instead of the range (it ignored the range, or the file changed)",
                fallBack: true);
        }

        if (status != HttpStatusCode.PartialContent)
        {
            throw (int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
                ? new TransientPartException($"got {Status(response)}")
                : new PartFailedException($"Download of {url} bytes {from}-{to}: got {Status(response)}.", fallBack: false);
        }

        // If-Range should make this impossible; checked so that a server or proxy that doesn't honour it can't mix versions.
        if (response.Content.Headers.ContentRange is { } range && (range.From != from || range.To != to || (range.Length is { } total && total != length))
            || response.Headers.ETag is { } sent && validators.ETag is { } expected && sent.ToString() != expected)
        {
            throw new PartFailedException(
                $"Download of {url} bytes {from}-{to}: the server sent another range or version ({response.Content.Headers.ContentRange}, ETag {response.Headers.ETag})",
                fallBack: true);
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Write, 1 << 20, useAsync: true);
        file.Position = from;
        var buffer = new byte[1 << 20];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (written + read > to - from + 1)
            {
                throw new PartFailedException($"Download of {url} bytes {from}-{to}: the server sent more than asked for.", fallBack: false);
            }

            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
        }

        return written == to - from + 1
            ? written
            : throw new IOException($"cut short: got {written:N0} of {to - from + 1:N0} bytes");
    }

    private static string Status(HttpResponseMessage response) =>
        $"{(int)response.StatusCode} {(string.IsNullOrEmpty(response.ReasonPhrase) ? response.StatusCode.ToString() : response.ReasonPhrase)}";

    /// <summary>A part that may succeed when asked again (a server error, 429, 408).</summary>
    private sealed class TransientPartException(string message) : IOException(message);

    /// <summary>A part that won't be asked again: <see cref="FallBack"/> = fetch the whole file in one stream instead, else the fetch fails.</summary>
    private sealed class PartFailedException(string message, bool fallBack, Exception? inner = null) : IOException(message, inner)
    {
        public bool FallBack { get; } = fallBack;
    }
}
