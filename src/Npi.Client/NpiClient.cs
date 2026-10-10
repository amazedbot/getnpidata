using System.Net;
using System.Text.Json;

namespace Npi.Client;

/// <summary>
/// Typed client for the getnpidata REST API (/api/v1). Thread-safe; create one per base address and reuse it.
/// </summary>
/// <example>
/// <code>
/// using var npi = new NpiClient(new Uri("https://example.azurewebsites.net/"));
/// var page = await npi.SearchProvidersAsync(new ProviderSearch { Classification = "Chiropractor", County = "36103" });
/// </code>
/// </example>
public sealed class NpiClient : IDisposable
{
    /// <summary>Header carrying the API key, when the server requires one.</summary>
    public const string ApiKeyHeader = "X-Api-Key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _baseAddress;
    private readonly string? _apiKey;

    /// <summary>Creates a client with its own <see cref="HttpClient"/>.</summary>
    /// <param name="baseAddress">The site's address, e.g. https://example.azurewebsites.net/ (the API lives under api/v1/).</param>
    /// <param name="apiKey">Sent as <see cref="ApiKeyHeader"/> when set. Only needed if the server requires keys.</param>
    public NpiClient(Uri baseAddress, string? apiKey = null)
        : this(new HttpClient(), baseAddress, apiKey, ownsHttp: true)
    {
    }

    /// <summary>Creates a client on an existing <see cref="HttpClient"/> (e.g. from IHttpClientFactory). It is not disposed by this client.</summary>
    /// <param name="httpClient">The HTTP client to send requests with.</param>
    /// <param name="baseAddress">The site's address; defaults to <paramref name="httpClient"/>'s BaseAddress.</param>
    /// <param name="apiKey">Sent as <see cref="ApiKeyHeader"/> when set.</param>
    public NpiClient(HttpClient httpClient, Uri? baseAddress = null, string? apiKey = null)
        : this(httpClient, baseAddress ?? httpClient?.BaseAddress ?? throw new ArgumentException("Pass a base address or set HttpClient.BaseAddress.", nameof(baseAddress)), apiKey, ownsHttp: false)
    {
    }

    private NpiClient(HttpClient httpClient, Uri baseAddress, string? apiKey, bool ownsHttp)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("The base address must be absolute.", nameof(baseAddress));
        }

        // A trailing slash keeps any path prefix (https://host/npi/ + api/v1/… = https://host/npi/api/v1/…).
        _baseAddress = baseAddress.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? baseAddress : new Uri(baseAddress.AbsoluteUri + "/");
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _ownsHttp = ownsHttp;
    }

    /// <summary>One page of providers matching <paramref name="search"/>.</summary>
    /// <exception cref="NpiApiException">Invalid filters (see <see cref="NpiApiException.Errors"/>), rate limit, or another API error.</exception>
    public Task<ProviderPage> SearchProvidersAsync(ProviderSearch search, CancellationToken cancellationToken = default) =>
        GetJsonAsync<ProviderPage>("api/v1/providers" + Query(search), cancellationToken);

    /// <summary>Streams every provider matching <paramref name="search"/> (no row cap) as CSV into <paramref name="destination"/>.</summary>
    /// <remarks>UTF-8 with BOM; the summary columns. Paging properties are ignored.</remarks>
    public async Task DownloadProvidersCsvAsync(ProviderSearch search, Stream destination, CancellationToken cancellationToken = default)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        using var response = await SendAsync("api/v1/providers.csv" + Query(search), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await body.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Looks up many NPIs at once (up to 1,000 per call; larger lists are sent in batches). One row per requested NPI, in order.</summary>
    public async Task<IReadOnlyList<LookupRow>> LookupProvidersAsync(IEnumerable<string> npis, CancellationToken cancellationToken = default)
    {
        if (npis is null)
        {
            throw new ArgumentNullException(nameof(npis));
        }

        var all = new List<LookupRow>();
        foreach (var batch in npis.Select((npi, i) => (npi, i)).GroupBy(x => x.i / 1000, x => x.npi))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseAddress, "api/v1/providers/lookup"))
            {
                Content = new StringContent(JsonSerializer.Serialize(new { npis = batch.ToList() }, Json), System.Text.Encoding.UTF8, "application/json"),
            };
            if (_apiKey is not null)
            {
                request.Headers.Add(ApiKeyHeader, _apiKey);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            all.AddRange((await ReadJsonAsync<LookupResponse>(response, cancellationToken).ConfigureAwait(false)).Items);
        }

        return all;
    }

    /// <summary>Everything published for one NPI, or null when it is unknown or deactivated.</summary>
    public async Task<ProviderDetail?> GetProviderAsync(string npi, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(npi))
        {
            throw new ArgumentException("An NPI is required.", nameof(npi));
        }

        using var response = await SendAsync("api/v1/providers/" + Uri.EscapeDataString(npi.Trim()), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadJsonAsync<ProviderDetail>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>All NUCC classifications, e.g. "Chiropractor".</summary>
    public Task<IReadOnlyList<string>> GetClassificationsAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync<IReadOnlyList<string>>("api/v1/taxonomy/classifications", cancellationToken);

    /// <summary>The specializations of one classification.</summary>
    /// <exception cref="NpiApiException">404 when the classification is unknown.</exception>
    public Task<IReadOnlyList<string>> GetSpecializationsAsync(string classification, CancellationToken cancellationToken = default) =>
        GetJsonAsync<IReadOnlyList<string>>($"api/v1/taxonomy/classifications/{Segment(classification, nameof(classification))}/specializations", cancellationToken);

    /// <summary>Standardized credentials with provider counts, most common first; pass one as <see cref="ProviderSearch.Credential"/>.</summary>
    public Task<IReadOnlyList<CredentialInfo>> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync<IReadOnlyList<CredentialInfo>>("api/v1/credentials", cancellationToken);

    /// <summary>
    /// Companies that report to Open Payments, largest payments first; <paramref name="name"/> matches any part of a name
    /// or an Open Payments ID (null or empty lists every company).
    /// </summary>
    public Task<CompanyPage> SearchCompaniesAsync(string? name = null, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(name))
        {
            query.Add("name=" + Uri.EscapeDataString(name!.Trim()));
        }

        query.Add("page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture));
        query.Add("pageSize=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return GetJsonAsync<CompanyPage>("api/v1/companies?" + string.Join("&", query), cancellationToken);
    }

    /// <summary>One company by its Open Payments ID, or null when it is unknown.</summary>
    public async Task<CompanyDetail?> GetCompanyAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync("api/v1/companies/" + Segment(id, nameof(id)), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadJsonAsync<CompanyDetail>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Products named in Open Payments, largest payments first; <paramref name="name"/> matches any part of a name,
    /// <paramref name="kind"/> is Drug, Biological, Device or Medical Supply (null for any).
    /// </summary>
    public Task<ProductPage> SearchProductsAsync(string? name = null, string? kind = null, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(name))
        {
            query.Add("name=" + Uri.EscapeDataString(name!.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            query.Add("kind=" + Uri.EscapeDataString(kind!.Trim()));
        }

        query.Add("page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture));
        query.Add("pageSize=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return GetJsonAsync<ProductPage>("api/v1/products?" + string.Join("&", query), cancellationToken);
    }

    /// <summary>One product by its slug (from a list, a company's products or a provider's payments), or null when it is unknown.</summary>
    public async Task<ProductDetail?> GetProductAsync(string slug, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync("api/v1/products/" + Segment(slug, nameof(slug)), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadJsonAsync<ProductDetail>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>States and territories.</summary>
    public Task<IReadOnlyList<StateInfo>> GetStatesAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync<IReadOnlyList<StateInfo>>("api/v1/states", cancellationToken);

    /// <summary>The counties of one state, with the FIPS codes <see cref="ProviderSearch.County"/> takes.</summary>
    /// <exception cref="NpiApiException">404 when the state is unknown.</exception>
    public Task<IReadOnlyList<CountyInfo>> GetCountiesAsync(string state, CancellationToken cancellationToken = default) =>
        GetJsonAsync<IReadOnlyList<CountyInfo>>($"api/v1/states/{Segment(state, nameof(state))}/counties", cancellationToken);

    /// <summary>County population and HRSA shortage areas, or null when the FIPS code is unknown.</summary>
    public async Task<CountyFacts?> GetCountyAsync(string fips, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync("api/v1/counties/" + Segment(fips, nameof(fips)), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadJsonAsync<CountyFacts>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Data as-of date, source files and reference versions.</summary>
    public Task<ApiMeta> GetMetaAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync<ApiMeta>("api/v1/meta", cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private static string Query(ProviderSearch search) =>
        (search ?? throw new ArgumentNullException(nameof(search))).ToQueryString();

    private static string Segment(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : Uri.EscapeDataString(value.Trim());

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseAddress, path));
        if (_apiKey is not null)
        {
            request.Headers.Add(ApiKeyHeader, _apiKey);
        }

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(body, Json, cancellationToken).ConfigureAwait(false)
            ?? throw new NpiApiException(response.StatusCode, "Empty response", "The API returned no content.", null, null);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? title = null, detail = null;
        Dictionary<string, string[]>? errors = null;
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
                {
                    errors = e.EnumerateObject().ToDictionary(
                        p => p.Name,
                        p => p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().Select(m => m.ToString()).ToArray() : [p.Value.ToString()],
                        StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (JsonException)
        {
            detail = text.Length > 500 ? text.Substring(0, 500) : text; // not a problem document (e.g. a proxy error page)
        }

        throw new NpiApiException(response.StatusCode, title ?? response.ReasonPhrase ?? "API error", detail, errors, response.Headers.RetryAfter?.Delta);
    }
}
