using System.Net;

namespace Npi.Client;

/// <summary>An error answer from the API (an RFC 7807 problem document), e.g. invalid filters (400), rate limit (429).</summary>
public sealed class NpiApiException : Exception
{
    /// <summary>Creates the exception from a problem document's fields.</summary>
    public NpiApiException(HttpStatusCode statusCode, string title, string? detail, IReadOnlyDictionary<string, string[]>? errors, TimeSpan? retryAfter)
        : base(Describe(statusCode, title, detail, errors))
    {
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
        Errors = errors ?? new Dictionary<string, string[]>();
        RetryAfter = retryAfter;
    }

    /// <summary>HTTP status, e.g. 400, 401, 404, 429.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Short summary from the problem document.</summary>
    public string Title { get; }

    /// <summary>Explanation from the problem document, when given.</summary>
    public string? Detail { get; }

    /// <summary>Validation messages keyed by query parameter name ("radius", "zip"), or "filter" for the search as a whole.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    /// <summary>For 429: how long to wait before retrying.</summary>
    public TimeSpan? RetryAfter { get; }

    private static string Describe(HttpStatusCode status, string title, string? detail, IReadOnlyDictionary<string, string[]>? errors)
    {
        var message = $"{(int)status} {title}";
        if (!string.IsNullOrEmpty(detail))
        {
            message += ": " + detail;
        }

        if (errors is { Count: > 0 })
        {
            message += " " + string.Join(" ", errors.SelectMany(e => e.Value.Select(m => $"[{e.Key}] {m}")));
        }

        return message;
    }
}
