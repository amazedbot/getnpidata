using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>
/// The standardized credentials (<c>credential_list</c>, CLAUDE.md §7 Stage 5.5 item 12), cached in memory: the search
/// form's dropdown, the API's list, and resolving a typed credential filter ("M.D." → MD).
/// </summary>
public sealed class CredentialCatalog(string connectionString, TimeSpan? maxAge = null, int minProviders = CredentialCatalog.MinProviders) : IDisposable
{
    /// <summary>Credentials held by fewer providers are left out of the list (they are typos and one-offs).</summary>
    public const int MinProviders = 25;

    private readonly TimeSpan _maxAge = maxAge ?? TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (DateTime LoadedAt, IReadOnlyList<CredentialInfo> List, IReadOnlyDictionary<string, IReadOnlyList<string>> ByKey)? _cache;

    /// <summary>Every standardized credential held by at least <see cref="MinProviders"/> providers, most common first.</summary>
    public async Task<IReadOnlyList<CredentialInfo>> GetAllAsync(CancellationToken ct) => (await LoadAsync(ct)).List;

    /// <summary>
    /// The standardized credential a typed filter means ("m.d." → MD, "Pharm D" → PharmD), or null when it is none of
    /// them (the search then falls back to a prefix match on the raw credential).
    /// </summary>
    public async Task<string?> ResolveAsync(string typed, CancellationToken ct)
    {
        var byKey = (await LoadAsync(ct)).ByKey;
        return Credentials.Standardize(typed, k => byKey.GetValueOrDefault(k)) is [var single] && byKey.TryGetValue(Credentials.Key(single), out var listed)
            ? listed[0]
            : null;
    }

    private async Task<(DateTime LoadedAt, IReadOnlyList<CredentialInfo> List, IReadOnlyDictionary<string, IReadOnlyList<string>> ByKey)> LoadAsync(CancellationToken ct)
    {
        if (_cache is { } fresh && DateTime.UtcNow - fresh.LoadedAt < _maxAge)
        {
            return fresh;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } again && DateTime.UtcNow - again.LoadedAt < _maxAge)
            {
                return again;
            }

            await using var connection = new MySqlConnection(connectionString);
            var list = (await connection.QueryAsync<CredentialInfo>(new CommandDefinition(
                "SELECT credential AS Credential, providers AS Providers FROM credential_list WHERE providers >= @min ORDER BY providers DESC, credential",
                new { min = minProviders }, cancellationToken: ct))).ToList();
            var byKey = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var c in list)
            {
                byKey.TryAdd(Credentials.Key(c.Credential), [c.Credential]);
            }

            var loaded = (DateTime.UtcNow, (IReadOnlyList<CredentialInfo>)list, (IReadOnlyDictionary<string, IReadOnlyList<string>>)byKey);
            _cache = loaded;
            return loaded;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}
