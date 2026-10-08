using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

public sealed record TaxonomyEntry(string Code, string? Classification, string? Specialization, string? DisplayName);

/// <summary>
/// The NUCC taxonomy codes, cached in memory (about 900 rows, refreshed after <c>maxAge</c>). Resolves a
/// Classification/Specialization to its codes and feeds the dropdowns.
/// </summary>
public sealed class TaxonomyCatalog(string connectionString, TimeSpan? maxAge = null, TimeProvider? clock = null) : IDisposable
{
    private readonly TimeSpan _maxAge = maxAge ?? TimeSpan.FromHours(1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (DateTimeOffset LoadedAt, IReadOnlyList<TaxonomyEntry> Entries)? _cache;

    public async Task<IReadOnlyList<TaxonomyEntry>> GetAllAsync(CancellationToken ct)
    {
        if (_cache is { } fresh && _clock.GetUtcNow() - fresh.LoadedAt < _maxAge)
        {
            return fresh.Entries;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } again && _clock.GetUtcNow() - again.LoadedAt < _maxAge)
            {
                return again.Entries;
            }

            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(ct);
            var entries = (await connection.QueryAsync<TaxonomyEntry>(new CommandDefinition(
                "SELECT Taxonomy_Code AS Code, Classification, Specialization, Display_Name AS DisplayName FROM taxonomy_codes",
                cancellationToken: ct))).ToList();
            _cache = (_clock.GetUtcNow(), entries);
            return entries;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetClassificationsAsync(CancellationToken ct) =>
        (await GetAllAsync(ct)).Select(e => e.Classification).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<IReadOnlyList<string>> GetSpecializationsAsync(string classification, CancellationToken ct) =>
        (await GetAllAsync(ct)).Where(e => string.Equals(e.Classification, classification, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Specialization).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The taxonomy codes a filter means, or null when it has no specialty filter. Classification alone
    /// includes every specialization under it. Throws when the classification/specialization is unknown.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ResolveAsync(SearchFilter filter, CancellationToken ct)
    {
        if (filter.Classification is null && filter.TaxonomyCode is null)
        {
            return null;
        }

        var entries = await GetAllAsync(ct);
        IEnumerable<TaxonomyEntry> matches = entries;
        if (filter.Classification is not null)
        {
            matches = matches.Where(e => string.Equals(e.Classification, filter.Classification, StringComparison.OrdinalIgnoreCase));
            if (!matches.Any())
            {
                throw Invalid(nameof(SearchFilter.Classification), $"Unknown classification '{filter.Classification}'.");
            }
        }

        if (filter.Specialization is not null)
        {
            matches = matches.Where(e => string.Equals(e.Specialization, filter.Specialization, StringComparison.OrdinalIgnoreCase));
            if (!matches.Any())
            {
                throw Invalid(nameof(SearchFilter.Specialization), $"Unknown specialization '{filter.Specialization}' for '{filter.Classification}'.");
            }
        }

        if (filter.TaxonomyCode is not null)
        {
            matches = matches.Where(e => string.Equals(e.Code, filter.TaxonomyCode, StringComparison.OrdinalIgnoreCase));
        }

        return matches.Select(e => e.Code).ToList();
    }

    public async Task<TaxonomyEntry?> FindAsync(string? code, CancellationToken ct) =>
        code is null ? null : (await GetAllAsync(ct)).FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase));

    public void Dispose() => _lock.Dispose();

    private static SearchValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
