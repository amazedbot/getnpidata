using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

public sealed record ProviderTaxonomy(int Slot, string Code, string? Classification, string? Specialization, bool IsPrimary, string? LicenseNumber, string? LicenseState);

public sealed record ProviderLocation(bool IsPrimary, string? Address1, string? Address2, string? City, string? State, string? Zip, string? CountryCode, string? Phone);

/// <summary>Everything the detail page and GET /api/v1/providers/{npi} show for one NPI.</summary>
public sealed record ProviderDetail(
    string Npi, int EntityType, string Name, string? NamePrefix, string? Credential, string? Gender,
    DateOnly? EnumerationDate, DateOnly? LastUpdateDate,
    IReadOnlyList<ProviderTaxonomy> Taxonomies, IReadOnlyList<ProviderLocation> Locations, IReadOnlyList<string> OtherNames)
{
    public string EntityTypeName => EntityType == 2 ? "Organization" : "Individual";
}

/// <summary>Reads one provider from the projection. Deactivated NPIs are not in the projection, so they come back as null.</summary>
public sealed class ProviderDetailService(string connectionString)
{
    private sealed record Row(string Npi, sbyte EntityType, string? LastName, string? FirstName, string? MiddleName, string? NamePrefix,
        string? NameSuffix, string? Credential, string? OrgName, string? Gender, DateTime? EnumerationDate, DateTime? LastUpdateDate);

    private sealed record TaxRow(sbyte Slot, string Code, string? Classification, string? Specialization, sbyte IsPrimary, string? LicenseNo, string? LicenseState);

    private sealed record LocRow(sbyte IsPrimary, string? Address1, string? Address2, string? City, string? State, string? Zip5, string? Zip4,
        string? PostalCode, string? CountryCode, string? Phone);

    public async Task<ProviderDetail?> GetAsync(string npi, CancellationToken ct)
    {
        if (!InputFormats.IsNpi(npi))
        {
            return null;
        }

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var p = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT npi AS Npi, entity_type AS EntityType, last_name AS LastName, first_name AS FirstName, middle_name AS MiddleName,
                   name_prefix AS NamePrefix, name_suffix AS NameSuffix, credential AS Credential, org_name AS OrgName, gender AS Gender,
                   enumeration_date AS EnumerationDate, last_update_date AS LastUpdateDate
            FROM provider WHERE npi = @npi
            """, new { npi }, cancellationToken: ct));
        if (p is null)
        {
            return null;
        }

        var taxonomies = await connection.QueryAsync<TaxRow>(new CommandDefinition(
            """
            SELECT t.slot AS Slot, t.taxonomy_code AS Code, c.Classification, c.Specialization, t.is_primary AS IsPrimary,
                   t.license_no AS LicenseNo, t.license_state AS LicenseState
            FROM provider_taxonomy t LEFT JOIN taxonomy_codes c ON c.Taxonomy_Code = t.taxonomy_code
            WHERE t.npi = @npi ORDER BY t.is_primary DESC, t.slot
            """, new { npi }, cancellationToken: ct));
        var locations = await connection.QueryAsync<LocRow>(new CommandDefinition(
            """
            SELECT is_primary AS IsPrimary, address1 AS Address1, address2 AS Address2, city AS City, state AS State, zip5 AS Zip5, zip4 AS Zip4,
                   postal_code AS PostalCode, country_code AS CountryCode, phone AS Phone
            FROM provider_location WHERE npi = @npi ORDER BY is_primary DESC, id
            """, new { npi }, cancellationToken: ct));
        var otherNames = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM provider_other_name WHERE npi = @npi ORDER BY id", new { npi }, cancellationToken: ct));

        return new ProviderDetail(
            p.Npi, p.EntityType,
            ProviderNames.Display(p.EntityType, p.LastName, p.FirstName, p.MiddleName, p.NameSuffix, p.OrgName),
            p.NamePrefix, p.Credential, p.Gender,
            p.EnumerationDate is null ? null : DateOnly.FromDateTime(p.EnumerationDate.Value),
            p.LastUpdateDate is null ? null : DateOnly.FromDateTime(p.LastUpdateDate.Value),
            taxonomies.Select(t => new ProviderTaxonomy(t.Slot, t.Code, t.Classification, t.Specialization, t.IsPrimary != 0, t.LicenseNo, t.LicenseState)).ToList(),
            locations.Select(l => new ProviderLocation(l.IsPrimary != 0, l.Address1, l.Address2, l.City, l.State,
                ProviderNames.Zip(l.Zip5, l.Zip4, l.PostalCode), l.CountryCode, l.Phone)).ToList(),
            otherNames.ToList());
    }
}
