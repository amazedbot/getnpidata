using System.Net;
using System.Net.Http.Headers;

namespace Npi.Loader;

/// <summary>What a server said about a file: its validators, for asking later "only if it changed".</summary>
public sealed record FileValidators(string? ETag, DateTimeOffset? LastModified, long? Length);

/// <summary>
/// Fetches a URL into a file. Sends <c>If-None-Match</c> / <c>If-Modified-Since</c> when validators of an earlier copy are
/// given (a 304 means that copy is current). A large file from a server that accepts byte ranges is fetched in several
/// parts at once, each written at its offset; <c>If-Range</c> makes a part fail rather than mix two versions of the file.
/// The length is checked against the server's.
/// </summary>
public static class HttpFileFetch
{
    public sealed record Result(bool NotModified, long Bytes, FileValidators Validators);

    /// <param name="parts">How many ranges to fetch at once (1 = one stream).</param>
    /// <param name="partMinBytes">Files smaller than this are fetched in one stream.</param>
    public static async Task<Result> ToFileAsync(HttpClient http, Uri url, string path, FileValidators? earlier, int parts, long partMinBytes,
        CancellationToken ct)
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
            var validators = new FileValidators(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified,
                response.Content.Headers.ContentLength);
            long bytes;
            if (parts > 1 && validators.Length is { } length && length >= partMinBytes && response.Headers.AcceptRanges.Contains("bytes"))
            {
                response.Dispose(); // the parts are separate requests
                bytes = await RangesAsync(http, url, path, length, parts, validators, ct);
            }
            else
            {
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                await source.CopyToAsync(file, 1 << 20, ct);
                bytes = file.Length;
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

    private static async Task<long> RangesAsync(HttpClient http, Uri url, string path, long length, int parts, FileValidators validators,
        CancellationToken ct)
    {
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Write))
        {
            file.SetLength(length);
        }

        var size = (length + parts - 1) / parts;
        var tasks = Enumerable.Range(0, parts)
            .Select(i => (From: i * size, To: Math.Min(length, (i + 1) * size) - 1))
            .Where(r => r.From <= r.To)
            .Select(r => PartAsync(http, url, path, r.From, r.To, validators, ct));
        return (await Task.WhenAll(tasks)).Sum();
    }

    private static async Task<long> PartAsync(HttpClient http, Uri url, string path, long from, long to, FileValidators validators, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, to);
        if (validators.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(tag);
        }
        else if (validators.LastModified is { } modified)
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(modified);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new IOException($"Download of {url} bytes {from}-{to}: expected 206 Partial Content, got {(int)response.StatusCode} (the file changed?).");
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
                throw new IOException($"Download of {url} bytes {from}-{to}: the server sent more than asked for.");
            }

            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
        }

        return written == to - from + 1
            ? written
            : throw new IOException($"Download of {url} bytes {from}-{to} was truncated: got {written:N0} of {to - from + 1:N0} bytes.");
    }
}
