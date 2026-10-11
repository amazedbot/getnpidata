using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>Builds the <see cref="SearchVocabulary"/> from the taxonomy, counties and USPS cities, cached in memory (an hour by default).</summary>
public sealed class SearchVocabularyCatalog(string connectionString, TaxonomyCatalog taxonomy, TimeSpan? maxAge = null) : IDisposable
{
    private readonly TimeSpan _maxAge = maxAge ?? TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (DateTime LoadedAt, SearchVocabulary Vocabulary)? _cache;

    public async Task<SearchVocabulary> GetAsync(CancellationToken ct)
    {
        if (_cache is { } fresh && DateTime.UtcNow - fresh.LoadedAt < _maxAge)
        {
            return fresh.Vocabulary;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } again && DateTime.UtcNow - again.LoadedAt < _maxAge)
            {
                return again.Vocabulary;
            }

            var entries = await taxonomy.GetAllAsync(ct);
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(ct);
            var counties = await connection.QueryAsync<CountyTerm>(new CommandDefinition(
                """
                SELECT c.county_fips AS Fips, c.state AS State, c.county_name AS Name, CAST(COALESCE(p.population, 0) AS SIGNED) AS Population
                FROM county c LEFT JOIN county_population p ON p.county_fips = c.county_fips
                """, cancellationToken: ct));
            var cities = await connection.QueryAsync<CityTerm>(new CommandDefinition(
                """
                SELECT usps_city AS City, usps_state AS State, CAST(COUNT(DISTINCT zip5) AS SIGNED) AS Zips
                FROM zip_county WHERE usps_city IS NOT NULL AND usps_state IS NOT NULL GROUP BY usps_city, usps_state
                """, cancellationToken: ct));
            var vocabulary = new SearchVocabulary(entries, counties, cities, GeographyCatalog.AllStateNames);
            _cache = (DateTime.UtcNow, vocabulary);
            return vocabulary;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}
