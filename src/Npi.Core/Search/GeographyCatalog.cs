using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

public sealed record StateInfo(string Code, string Name);

public sealed record CountyInfo(string Fips, string Name);

public sealed record DataVersion(DateTime? AsOfDate, string? MonthlyFile, string? WeeklyFile, string? DeactivationFile,
    string? NuccVersion, string? HudVersion, string? CensusVersion, int ProviderCount, DateTime ProjectedAt, DateTime? PublishedAt);

/// <summary>States, counties and the data version for dropdowns and the footer, cached in memory.</summary>
public sealed class GeographyCatalog(string connectionString, TimeSpan? maxAge = null) : IDisposable
{
    private static readonly Dictionary<string, string> StateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AL"] = "Alabama", ["AK"] = "Alaska", ["AZ"] = "Arizona", ["AR"] = "Arkansas", ["CA"] = "California", ["CO"] = "Colorado",
        ["CT"] = "Connecticut", ["DE"] = "Delaware", ["DC"] = "District of Columbia", ["FL"] = "Florida", ["GA"] = "Georgia",
        ["HI"] = "Hawaii", ["ID"] = "Idaho", ["IL"] = "Illinois", ["IN"] = "Indiana", ["IA"] = "Iowa", ["KS"] = "Kansas",
        ["KY"] = "Kentucky", ["LA"] = "Louisiana", ["ME"] = "Maine", ["MD"] = "Maryland", ["MA"] = "Massachusetts", ["MI"] = "Michigan",
        ["MN"] = "Minnesota", ["MS"] = "Mississippi", ["MO"] = "Missouri", ["MT"] = "Montana", ["NE"] = "Nebraska", ["NV"] = "Nevada",
        ["NH"] = "New Hampshire", ["NJ"] = "New Jersey", ["NM"] = "New Mexico", ["NY"] = "New York", ["NC"] = "North Carolina",
        ["ND"] = "North Dakota", ["OH"] = "Ohio", ["OK"] = "Oklahoma", ["OR"] = "Oregon", ["PA"] = "Pennsylvania", ["RI"] = "Rhode Island",
        ["SC"] = "South Carolina", ["SD"] = "South Dakota", ["TN"] = "Tennessee", ["TX"] = "Texas", ["UT"] = "Utah", ["VT"] = "Vermont",
        ["VA"] = "Virginia", ["WA"] = "Washington", ["WV"] = "West Virginia", ["WI"] = "Wisconsin", ["WY"] = "Wyoming",
        ["AS"] = "American Samoa", ["GU"] = "Guam", ["MP"] = "Northern Mariana Islands", ["PR"] = "Puerto Rico",
        ["VI"] = "U.S. Virgin Islands", ["UM"] = "U.S. Minor Outlying Islands",
    };

    private readonly TimeSpan _maxAge = maxAge ?? TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (DateTime LoadedAt, IReadOnlyList<StateInfo> States, ILookup<string, CountyInfo> Counties, DataVersion? Version)? _cache;

    public async Task<IReadOnlyList<StateInfo>> GetStatesAsync(CancellationToken ct) => (await LoadAsync(ct)).States;

    public async Task<IReadOnlyList<CountyInfo>> GetCountiesAsync(string state, CancellationToken ct) =>
        (await LoadAsync(ct)).Counties[state.ToUpperInvariant()].ToList();

    public async Task<DataVersion?> GetDataVersionAsync(CancellationToken ct) => (await LoadAsync(ct)).Version;

    public static string StateName(string code) => StateNames.TryGetValue(code, out var name) ? name : code;

    private async Task<(DateTime LoadedAt, IReadOnlyList<StateInfo> States, ILookup<string, CountyInfo> Counties, DataVersion? Version)> LoadAsync(CancellationToken ct)
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
            await connection.OpenAsync(ct);
            var counties = (await connection.QueryAsync<(string State, string Fips, string Name)>(new CommandDefinition(
                "SELECT state, county_fips, county_name FROM county ORDER BY state, county_name", cancellationToken: ct))).ToList();
            var states = counties.Select(c => c.State).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(code => new StateInfo(code, StateName(code))).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var version = await connection.QuerySingleOrDefaultAsync<DataVersion>(new CommandDefinition(
                """
                SELECT as_of_date AS AsOfDate, monthly_file AS MonthlyFile, weekly_file AS WeeklyFile, deactivation_file AS DeactivationFile,
                       nucc_version AS NuccVersion, hud_version AS HudVersion, census_version AS CensusVersion, provider_count AS ProviderCount,
                       projected_at AS ProjectedAt, published_at AS PublishedAt
                FROM data_version WHERE id = 1
                """, cancellationToken: ct));
            var loaded = (DateTime.UtcNow, (IReadOnlyList<StateInfo>)states,
                counties.ToLookup(c => c.State.ToUpperInvariant(), c => new CountyInfo(c.Fips, c.Name)), version);
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
