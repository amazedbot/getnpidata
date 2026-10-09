using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>HRSA shortage areas still in force in a county, for one discipline (Stage 5.5 item 7a).</summary>
public sealed record CountyShortage(string Discipline, string DisciplineName, bool WholeCounty, int ShortageAreas, int? MaxScore);

/// <summary>Population and shortage facts about a county (Stage 5.5 item 7).</summary>
public sealed record CountyFacts(string Fips, string Name, string State, int? Population, int? PopulationYear, IReadOnlyList<CountyShortage> Shortages);

/// <summary>Area insights: county population (Census) and Health Professional Shortage Areas (HRSA).</summary>
public sealed class AreaService(string connectionString)
{
    /// <summary>The <c>shortage</c> filter values and their HRSA discipline codes.</summary>
    public static readonly IReadOnlyDictionary<string, string> Disciplines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["primaryCare"] = "PC",
        ["dental"] = "DH",
        ["mentalHealth"] = "MH",
    };

    public static string DisciplineName(string code) => code switch
    {
        "PC" => "Primary care",
        "DH" => "Dental",
        "MH" => "Mental health",
        _ => code,
    };

    private sealed record CountyRow(string Fips, string Name, string State, int? Population, short? Year);

    private sealed record ShortageRow(string Discipline, sbyte WholeCounty, int HpsaCount, int? MaxScore);

    /// <returns>The county, or null when the FIPS code is unknown.</returns>
    public async Task<CountyFacts?> GetCountyAsync(string fips, CancellationToken ct)
    {
        if (fips is not { Length: 5 } || !fips.All(char.IsAsciiDigit))
        {
            return null;
        }

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var county = await connection.QuerySingleOrDefaultAsync<CountyRow>(new CommandDefinition(
            """
            SELECT c.county_fips AS Fips, c.county_name AS Name, c.state AS State, p.population AS Population, p.year AS Year
            FROM county c LEFT JOIN county_population p ON p.county_fips = c.county_fips
            WHERE c.county_fips = @fips
            """, new { fips }, cancellationToken: ct));
        if (county is null)
        {
            return null;
        }

        var shortages = await connection.QueryAsync<ShortageRow>(new CommandDefinition(
            """
            SELECT discipline AS Discipline, whole_county AS WholeCounty, hpsa_count AS HpsaCount, max_score AS MaxScore
            FROM county_shortage WHERE county_fips = @fips ORDER BY FIELD(discipline, 'PC', 'DH', 'MH')
            """, new { fips }, cancellationToken: ct));
        return new CountyFacts(county.Fips, county.Name, county.State, county.Population, county.Year,
            shortages.Select(s => new CountyShortage(s.Discipline, DisciplineName(s.Discipline), s.WholeCounty != 0, s.HpsaCount, s.MaxScore)).ToList());
    }
}
