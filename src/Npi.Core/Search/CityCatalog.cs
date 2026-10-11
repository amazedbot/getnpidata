using System.Collections.Concurrent;
using System.Globalization;
using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>
/// The cities of a state (or of one county) for the search form's City dropdown, cached per state and county: the names
/// USPS uses for the ZIPs there that have practice locations (HUD crosswalk), so the many spellings providers write
/// ("BROOKYLN", "BKLYN") don't appear. The City filter matches the name as written or a ZIP USPS gives that name, so
/// those providers are still found. Names are title-cased ("Saint James"); matching is case-insensitive.
/// </summary>
public sealed class CityCatalog(string connectionString, TimeSpan? maxAge = null)
{
    private readonly TimeSpan _maxAge = maxAge ?? TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, (DateTime LoadedAt, Task<IReadOnlyList<string>> Cities)> _cache = new(StringComparer.Ordinal);

    /// <returns>The cities in alphabetical order; empty for an unknown state.</returns>
    public Task<IReadOnlyList<string>> GetCitiesAsync(string state, string? countyFips, CancellationToken ct)
    {
        var key = state.ToUpperInvariant() + "|" + countyFips;
        var entry = _cache.AddOrUpdate(key,
            _ => (DateTime.UtcNow, LoadAsync(state.ToUpperInvariant(), countyFips)),
            (_, old) => DateTime.UtcNow - old.LoadedAt < _maxAge && !old.Cities.IsFaulted ? old : (DateTime.UtcNow, LoadAsync(state.ToUpperInvariant(), countyFips)));
        return entry.Cities.WaitAsync(ct);
    }

    private async Task<IReadOnlyList<string>> LoadAsync(string state, string? countyFips)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var inCounty = countyFips is null ? "" : " AND z.county_fips = @countyFips";
        var names = await connection.QueryAsync<string>(new CommandDefinition(
            $"""
            SELECT DISTINCT z.usps_city FROM zip_county z
            WHERE z.usps_state = @state AND z.usps_city IS NOT NULL{inCounty}
              AND EXISTS (SELECT 1 FROM provider_location l WHERE l.zip5 = z.zip5)
            """, new { state, countyFips }, commandTimeout: 60));
        return names
            .Where(n => n.Trim().Length > 0)
            .Select(TitleCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>"SAINT JAMES" → "Saint James", "o'fallon" → "O'Fallon", "MC LEAN" → "Mc Lean".</summary>
    public static string TitleCase(string city)
    {
        var text = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(city.Trim().ToLowerInvariant());
        // ToTitleCase leaves the letter after an apostrophe lower-case: O'fallon → O'Fallon.
        var chars = text.ToCharArray();
        for (var i = 1; i < chars.Length - 1; i++)
        {
            if (chars[i] == '\'' && char.IsLetter(chars[i - 1]) && (i == 1 || !char.IsLetter(chars[i - 2])))
            {
                chars[i + 1] = char.ToUpperInvariant(chars[i + 1]);
            }
        }

        return new string(chars);
    }
}
