using System.Globalization;
using System.Text;
using Dapper;
using MySqlConnector;
using Npi.Core.Sql;
using Npi.Loader.Db;
using Serilog;

namespace Npi.Loader.Projection;

/// <summary>
/// Builds the search projection (CLAUDE.md §6.2, §7 Stage 3.1 and Stage 5.5 item 1): provider,
/// provider_taxonomy, provider_location, provider_other_name, provider_profile, provider_identifier
/// and provider_endpoint, from npidata / practice_locations / other_names / endpoints.
/// Deactivated NPIs are left out. Everything is built in *_staging tables and swapped in with one
/// atomic RENAME, so searches never see a partial projection.
/// </summary>
public sealed class ProjectionBuilder(Database database, ILogger log, double minRowRatio, TimeProvider? clock = null)
{
    public static readonly string[] Tables =
    [
        "provider", "provider_taxonomy", "provider_location", "provider_other_name", "provider_search",
        "provider_profile", "provider_identifier", "provider_endpoint",
    ];

    private const int TaxonomySlots = 15;
    private const int IdentifierSlots = 50;
    private const string Active = "n.`Is_Deactivated` = 0 AND n.`Entity_Type_Code` IN ('1', '2')";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>True when the projection is missing or older than the last completed NPPES or reference load.</summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT NOT EXISTS (SELECT 1 FROM `data_version` WHERE `id` = 1)
                OR (SELECT `projected_at` FROM `data_version` WHERE `id` = 1) <
                   GREATEST(COALESCE((SELECT MAX(`completed_at`) FROM `downlog` WHERE `status` = 'Completed'), '1000-01-01'),
                            COALESCE((SELECT MAX(`loaded_at`) FROM `reference_data`), '1000-01-01'))
            """, cancellationToken: ct));
    }

    /// <returns>The number of providers in the new projection.</returns>
    public async Task<long> BuildAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await Database.ExecuteAsync(connection, "SET SESSION group_concat_max_len = 16777216", ct);
        var started = DateTime.UtcNow;
        try
        {
            foreach (var table in Tables)
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {Staging(table)}", ct);
                await Database.ExecuteAsync(connection, $"CREATE TABLE {Staging(table)} LIKE {SqlIdentifier.Quote(table)}", ct);
            }

            await StepAsync(connection, "provider", ProviderSql, ct);
            await StepAsync(connection, "provider_taxonomy", TaxonomySql(), ct);
            await StepAsync(connection, "provider_location (primary)", PrimaryLocationSql, ct);
            await StepAsync(connection, "provider_location (secondary)", SecondaryLocationSql, ct);
            await StepAsync(connection, "provider_other_name", OtherNameSql, ct);
            await StepAsync(connection, "provider_search", SearchSql, ct);
            await StepAsync(connection, "provider_profile", ProfileSql, ct);
            await StepAsync(connection, "provider_identifier", IdentifierSql(), ct);
            await StepAsync(connection, "provider_endpoint", EndpointSql, ct);
            await StepAsync(connection, "row_hash", RowHashSql, ct);

            var providers = await Database.CountAsync(connection, "provider_staging", ct);
            var current = await Database.CountAsync(connection, "provider", ct);
            if (current > 0 && providers < current * minRowRatio)
            {
                throw new InvalidDataException(
                    $"New projection has {providers:N0} providers, fewer than {minRowRatio:P0} of the current {current:N0}; not replacing it.");
            }

            foreach (var table in Tables)
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {SqlIdentifier.Quote(table + "_old")}", ct);
            }

            var renames = Tables.SelectMany(t => new[]
            {
                $"{SqlIdentifier.Quote(t)} TO {SqlIdentifier.Quote(t + "_old")}",
                $"{Staging(t)} TO {SqlIdentifier.Quote(t)}",
            });
            await Database.ExecuteAsync(connection, $"RENAME TABLE {string.Join(", ", renames)}", ct);
            foreach (var table in Tables)
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE {SqlIdentifier.Quote(table + "_old")}", ct);
            }

            await Database.ExecuteAsync(connection, DataVersionSql, ct, param: new { now = _clock.GetUtcNow().UtcDateTime, providers });
            log.Information("Projection built: {Providers:N0} providers in {Duration}", providers,
                (DateTime.UtcNow - started).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
            return providers;
        }
        finally
        {
            foreach (var table in Tables)
            {
                await Database.ExecuteAsync(connection, $"DROP TABLE IF EXISTS {Staging(table)}", CancellationToken.None);
            }
        }
    }

    private async Task StepAsync(MySqlConnection connection, string what, string sql, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
        log.Information("Projection {Step}: {Rows:N0} rows in {Seconds:N0}s", what, rows, (DateTime.UtcNow - started).TotalSeconds);
    }

    private static string Staging(string table) => SqlIdentifier.Quote(table + "_staging");

    /// <summary>NULL for empty or whitespace-only text.</summary>
    private static string Text(string column) => $"NULLIF(TRIM({column}), '')";

    /// <summary>MM/DD/YYYY text → DATE.</summary>
    private static string Date(string column) => $"STR_TO_DATE({Text(column)}, '%m/%d/%Y')";

    // ZIP+4 from a raw US postal code ("117011234", "11701", "11701-1234"); NULL for foreign addresses.
    private static string IsUsZip(string postal, string country) =>
        $"(COALESCE({Text(country)}, 'US') = 'US' AND REGEXP_REPLACE(COALESCE({postal}, ''), '[^0-9]', '') REGEXP '^[0-9]{{5}}([0-9]{{4}})?$')";

    private static string Zip5(string postal, string country) =>
        $"IF({IsUsZip(postal, country)}, LEFT(REGEXP_REPLACE({postal}, '[^0-9]', ''), 5), NULL)";

    private static string Zip4(string postal, string country) =>
        $"IF({IsUsZip(postal, country)} AND CHAR_LENGTH(REGEXP_REPLACE({postal}, '[^0-9]', '')) = 9, RIGHT(REGEXP_REPLACE({postal}, '[^0-9]', ''), 4), NULL)";

    private static readonly string ProviderSql = BuildProviderSql();

    private static string BuildProviderSql()
    {
        var last = Text("n.`Provider_Last_Name_Legal_Name`");
        var first = Text("n.`Provider_First_Name`");
        var middle = Text("n.`Provider_Middle_Name`");
        var org = Text("n.`Provider_Organization_Name_Legal_Business_Name`");
        var primary = new StringBuilder("COALESCE(CASE");
        for (var slot = 1; slot <= TaxonomySlots; slot++)
        {
            primary.Append(CultureInfo.InvariantCulture,
                $" WHEN n.`Healthcare_Provider_Primary_Taxonomy_Switch_{slot}` = 'Y' AND {Text($"n.`Healthcare_Provider_Taxonomy_Code_{slot}`")} IS NOT NULL THEN {Text($"n.`Healthcare_Provider_Taxonomy_Code_{slot}`")}");
        }

        primary.Append(CultureInfo.InvariantCulture, $" END, {Text("n.`Healthcare_Provider_Taxonomy_Code_1`")})");

        return $"""
            INSERT INTO `provider_staging` (`npi`, `entity_type`, `last_name`, `first_name`, `middle_name`, `name_prefix`, `name_suffix`,
              `credential`, `credential_key`, `org_name`, `sort_name`, `gender`, `primary_taxonomy_code`, `phone`, `enumeration_date`, `last_update_date`)
            SELECT n.`NPI`,
              CAST(n.`Entity_Type_Code` AS UNSIGNED),
              {last}, {first}, {middle},
              {Text("n.`Provider_Name_Prefix_Text`")}, {Text("n.`Provider_Name_Suffix_Text`")},
              {Text("n.`Provider_Credential_Text`")},
              NULLIF(REGEXP_REPLACE(UPPER(COALESCE(n.`Provider_Credential_Text`, '')), '[^A-Z0-9]', ''), ''),
              {org},
              CASE WHEN n.`Entity_Type_Code` = '2' THEN COALESCE({org}, '')
                   ELSE CONCAT(COALESCE({last}, ''), IF(COALESCE({first}, {middle}) IS NULL, '', CONCAT(', ', CONCAT_WS(' ', {first}, {middle})))) END,
              LEFT({Text("n.`Provider_Gender_Code`")}, 1),
              {primary},
              {Text("n.`Provider_Business_Practice_Location_Address_Telephone_Number`")},
              {Date("n.`Provider_Enumeration_Date`")},
              {Date("n.`Last_Update_Date`")}
            FROM `npidata` n
            WHERE {Active}
            """;
    }

    // One pass over npidata: each row is crossed with the slot numbers 1-15 and the slot's columns
    // picked with CASE, instead of 15 separate scans. CMS writes the literal text "NULL" in ~16,500
    // license state codes (Sep 2026); that, and only that column, is treated as empty.
    private static string TaxonomySql()
    {
        string Pick(string prefix) => "CASE s.slot " + string.Join(" ", Enumerable.Range(1, TaxonomySlots)
            .Select(i => $"WHEN {i} THEN n.`{prefix}_{i}`")) + " END";
        var slots = string.Join(" UNION ALL ", Enumerable.Range(1, TaxonomySlots).Select(i => $"SELECT {i} AS slot"));
        return $"""
            INSERT INTO `provider_taxonomy_staging` (`npi`, `slot`, `taxonomy_code`, `is_primary`, `license_no`, `license_state`)
            SELECT x.npi, x.slot, x.code, x.is_primary, x.license_no, x.license_state
            FROM (
              SELECT n.`NPI` AS npi, s.slot,
                {Text(Pick("Healthcare_Provider_Taxonomy_Code"))} AS code,
                COALESCE({Pick("Healthcare_Provider_Primary_Taxonomy_Switch")} = 'Y', 0) AS is_primary,
                {Text(Pick("Provider_License_Number"))} AS license_no,
                NULLIF({Text(Pick("Provider_License_Number_State_Code"))}, 'NULL') AS license_state
              FROM `npidata` n
              CROSS JOIN ({slots}) s
              WHERE {Active}
            ) x
            WHERE x.code IS NOT NULL
            """;
    }

    private static readonly string PrimaryLocationSql = $"""
        INSERT INTO `provider_location_staging` (`npi`, `is_primary`, `address1`, `address2`, `city`, `state`, `zip5`, `zip4`, `postal_code`, `country_code`, `phone`)
        SELECT n.`NPI`, 1,
          {Text("n.`Provider_First_Line_Business_Practice_Location_Address`")},
          {Text("n.`Provider_Second_Line_Business_Practice_Location_Address`")},
          {Text("n.`Provider_Business_Practice_Location_Address_City_Name`")},
          {Text("n.`Provider_Business_Practice_Location_Address_State_Name`")},
          {Zip5("n.`Provider_Business_Practice_Location_Address_Postal_Code`", "n.`Provider_Business_Practice_Location_Address_Country_Code`")},
          {Zip4("n.`Provider_Business_Practice_Location_Address_Postal_Code`", "n.`Provider_Business_Practice_Location_Address_Country_Code`")},
          {Text("n.`Provider_Business_Practice_Location_Address_Postal_Code`")},
          {Text("n.`Provider_Business_Practice_Location_Address_Country_Code`")},
          {Text("n.`Provider_Business_Practice_Location_Address_Telephone_Number`")}
        FROM `npidata` n
        WHERE {Active}
        """;

    private static readonly string SecondaryLocationSql = $"""
        INSERT INTO `provider_location_staging` (`npi`, `is_primary`, `address1`, `address2`, `city`, `state`, `zip5`, `zip4`, `postal_code`, `country_code`, `phone`)
        SELECT l.`NPI`, 0,
          {Text("l.`Provider_Secondary_Practice_Location_Address_Line_1`")},
          {Text("l.`Provider_Secondary_Practice_Location_Address_Line_2`")},
          {Text("l.`Provider_Secondary_Practice_Location_Address_City_Name`")},
          {Text("l.`Provider_Secondary_Practice_Location_Address_State_Name`")},
          {Zip5("l.`Provider_Secondary_Practice_Location_Address_Postal_Code`", "l.`Provider_Secondary_Practice_Location_Address_Country_Code`")},
          {Zip4("l.`Provider_Secondary_Practice_Location_Address_Postal_Code`", "l.`Provider_Secondary_Practice_Location_Address_Country_Code`")},
          {Text("l.`Provider_Secondary_Practice_Location_Address_Postal_Code`")},
          {Text("l.`Provider_Secondary_Practice_Location_Address_Country_Code`")},
          {Text("l.`Provider_Secondary_Practice_Location_Address_Telephone_Number`")}
        FROM `practice_locations` l
        JOIN `provider_staging` p ON p.`npi` = l.`NPI`
        ORDER BY l.`NPI`, l.`ID`
        """;

    private static readonly string OtherNameSql = $"""
        INSERT INTO `provider_other_name_staging` (`npi`, `name`, `type_code`)
        SELECT o.`NPI`, {Text("o.`Provider_Other_Organization_Name`")}, {Text("o.`Provider_Other_Organization_Name_Type_Code`")}
        FROM `other_names` o
        JOIN `provider_staging` p ON p.`npi` = o.`NPI`
        WHERE {Text("o.`Provider_Other_Organization_Name`")} IS NOT NULL
        ORDER BY o.`NPI`, o.`ID`
        """;

    private static readonly string ProfileSql = $"""
        INSERT INTO `provider_profile_staging` (`npi`, `is_sole_proprietor`, `is_subpart`, `parent_org_name`,
          `official_prefix`, `official_first_name`, `official_middle_name`, `official_last_name`, `official_suffix`, `official_credential`,
          `official_title`, `official_phone`, `mailing_address1`, `mailing_address2`, `mailing_city`, `mailing_state`, `mailing_postal_code`,
          `mailing_country_code`, `mailing_phone`, `mailing_fax`, `practice_fax`)
        SELECT n.`NPI`,
          CASE {Text("n.`Is_Sole_Proprietor`")} WHEN 'Y' THEN 1 WHEN 'N' THEN 0 END,
          CASE {Text("n.`Is_Organization_Subpart`")} WHEN 'Y' THEN 1 WHEN 'N' THEN 0 END,
          {Text("n.`Parent_Organization_LBN`")},
          {Text("n.`Authorized_Official_Name_Prefix_Text`")}, {Text("n.`Authorized_Official_First_Name`")},
          {Text("n.`Authorized_Official_Middle_Name`")}, {Text("n.`Authorized_Official_Last_Name`")},
          {Text("n.`Authorized_Official_Name_Suffix_Text`")}, {Text("n.`Authorized_Official_Credential_Text`")},
          {Text("n.`Authorized_Official_Title_or_Position`")}, {Text("n.`Authorized_Official_Telephone_Number`")},
          {Text("n.`Provider_First_Line_Business_Mailing_Address`")}, {Text("n.`Provider_Second_Line_Business_Mailing_Address`")},
          {Text("n.`Provider_Business_Mailing_Address_City_Name`")}, {Text("n.`Provider_Business_Mailing_Address_State_Name`")},
          {Text("n.`Provider_Business_Mailing_Address_Postal_Code`")}, {Text("n.`Provider_Business_Mailing_Address_Country_Code`")},
          {Text("n.`Provider_Business_Mailing_Address_Telephone_Number`")}, {Text("n.`Provider_Business_Mailing_Address_Fax_Number`")},
          {Text("n.`Provider_Business_Practice_Location_Address_Fax_Number`")}
        FROM `npidata` n
        JOIN `provider_staging` p ON p.`npi` = n.`NPI`
        """;

    // Same one-pass slot unpivot as the taxonomies, over the 50 other-identifier slots.
    private static string IdentifierSql()
    {
        string Pick(string prefix) => "CASE s.slot " + string.Join(" ", Enumerable.Range(1, IdentifierSlots)
            .Select(i => $"WHEN {i} THEN n.`{prefix}_{i}`")) + " END";
        var slots = string.Join(" UNION ALL ", Enumerable.Range(1, IdentifierSlots).Select(i => $"SELECT {i} AS slot"));
        return $"""
            INSERT INTO `provider_identifier_staging` (`npi`, `slot`, `identifier`, `type_code`, `state`, `issuer`)
            SELECT x.npi, x.slot, x.identifier, x.type_code, x.state, x.issuer
            FROM (
              SELECT n.`NPI` AS npi, s.slot,
                {Text(Pick("Other_Provider_Identifier"))} AS identifier,
                {Text(Pick("Other_Provider_Identifier_Type_Code"))} AS type_code,
                {Text(Pick("Other_Provider_Identifier_State"))} AS state,
                {Text(Pick("Other_Provider_Identifier_Issuer"))} AS issuer
              FROM `npidata` n
              JOIN `provider_staging` p ON p.`npi` = n.`NPI`
              CROSS JOIN ({slots}) s
              WHERE n.`Other_Provider_Identifier_1` IS NOT NULL
            ) x
            WHERE x.identifier IS NOT NULL
            """;
    }

    private static readonly string EndpointSql = $"""
        INSERT INTO `provider_endpoint_staging` (`npi`, `endpoint_type`, `endpoint_type_description`, `endpoint`, `endpoint_description`,
          `use_description`, `content_description`, `affiliation_name`, `affiliation_city`, `affiliation_state`)
        SELECT e.`NPI`, {Text("e.`Endpoint_Type`")}, {Text("e.`Endpoint_Type_Description`")}, {Text("e.`Endpoint`")},
          {Text("e.`Endpoint_Description`")},
          COALESCE({Text("e.`Use_Description`")}, {Text("e.`Other_Use_Description`")}),
          COALESCE({Text("e.`Content_Description`")}, {Text("e.`Other_Content_Description`")}),
          {Text("e.`Affiliation_Legal_Business_Name`")}, {Text("e.`Affiliation_Address_City`")}, {Text("e.`Affiliation_Address_State`")}
        FROM `endpoints` e
        JOIN `provider_staging` p ON p.`npi` = e.`NPI`
        WHERE {Text("e.`Endpoint`")} IS NOT NULL
        ORDER BY e.`NPI`, e.`ID`
        """;

    // Every (taxonomy code, location area) pair of each provider; DISTINCT because two locations can
    // share state/city/ZIP. Derived entirely from the two tables above, so not part of row_hash.
    private const string SearchSql = """
        INSERT INTO `provider_search_staging` (`taxonomy_code`, `state`, `city`, `zip5`, `npi`)
        SELECT DISTINCT t.taxonomy_code, IFNULL(l.state, ''), IFNULL(l.city, ''), IFNULL(l.zip5, ''), t.npi
        FROM `provider_taxonomy_staging` t
        JOIN `provider_location_staging` l ON l.npi = t.npi
        """;

    // One MD5 per provider over its own columns and all its child rows (ordered by content, not by
    // id), so the publisher (Stage 6) can find changed NPIs by comparing hashes.
    private const string RowHashSql = """
        UPDATE `provider_staging` p
        LEFT JOIN (
          SELECT npi, MD5(GROUP_CONCAT(CONCAT_WS('|', slot, taxonomy_code, is_primary, IFNULL(license_no, ''), IFNULL(license_state, ''))
                                       ORDER BY slot SEPARATOR '\n')) AS h
          FROM `provider_taxonomy_staging` GROUP BY npi) t ON t.npi = p.npi
        LEFT JOIN (
          SELECT npi, MD5(GROUP_CONCAT(CONCAT_WS('|', is_primary, IFNULL(address1, ''), IFNULL(address2, ''), IFNULL(city, ''), IFNULL(state, ''),
                                                 IFNULL(zip5, ''), IFNULL(zip4, ''), IFNULL(postal_code, ''), IFNULL(country_code, ''), IFNULL(phone, ''))
                                       ORDER BY is_primary DESC, address1, address2, city, state, postal_code, phone SEPARATOR '\n')) AS h
          FROM `provider_location_staging` GROUP BY npi) l ON l.npi = p.npi
        LEFT JOIN (
          SELECT npi, MD5(GROUP_CONCAT(CONCAT_WS('|', name, IFNULL(type_code, '')) ORDER BY name, type_code SEPARATOR '\n')) AS h
          FROM `provider_other_name_staging` GROUP BY npi) o ON o.npi = p.npi
        LEFT JOIN `provider_profile_staging` pp ON pp.npi = p.npi
        LEFT JOIN (
          SELECT npi, MD5(GROUP_CONCAT(CONCAT_WS('|', slot, identifier, IFNULL(type_code, ''), IFNULL(state, ''), IFNULL(issuer, ''))
                                       ORDER BY slot SEPARATOR '\n')) AS h
          FROM `provider_identifier_staging` GROUP BY npi) i ON i.npi = p.npi
        LEFT JOIN (
          SELECT npi, MD5(GROUP_CONCAT(CONCAT_WS('|', IFNULL(endpoint_type, ''), endpoint, IFNULL(endpoint_description, ''), IFNULL(use_description, ''),
                                                 IFNULL(content_description, ''), IFNULL(affiliation_name, ''), IFNULL(affiliation_city, ''), IFNULL(affiliation_state, ''))
                                       ORDER BY endpoint_type, endpoint, affiliation_name SEPARATOR '\n')) AS h
          FROM `provider_endpoint_staging` GROUP BY npi) e ON e.npi = p.npi
        SET p.row_hash = UNHEX(MD5(CONCAT_WS('#', p.entity_type, IFNULL(p.last_name, ''), IFNULL(p.first_name, ''), IFNULL(p.middle_name, ''),
            IFNULL(p.name_prefix, ''), IFNULL(p.name_suffix, ''), IFNULL(p.credential, ''), IFNULL(p.org_name, ''), IFNULL(p.gender, ''),
            IFNULL(p.primary_taxonomy_code, ''), IFNULL(p.phone, ''), IFNULL(p.enumeration_date, ''), IFNULL(p.last_update_date, ''),
            IFNULL(t.h, ''), IFNULL(l.h, ''), IFNULL(o.h, ''),
            MD5(CONCAT_WS('|', IFNULL(pp.is_sole_proprietor, ''), IFNULL(pp.is_subpart, ''), IFNULL(pp.parent_org_name, ''),
              IFNULL(pp.official_prefix, ''), IFNULL(pp.official_first_name, ''), IFNULL(pp.official_middle_name, ''), IFNULL(pp.official_last_name, ''),
              IFNULL(pp.official_suffix, ''), IFNULL(pp.official_credential, ''), IFNULL(pp.official_title, ''), IFNULL(pp.official_phone, ''),
              IFNULL(pp.mailing_address1, ''), IFNULL(pp.mailing_address2, ''), IFNULL(pp.mailing_city, ''), IFNULL(pp.mailing_state, ''),
              IFNULL(pp.mailing_postal_code, ''), IFNULL(pp.mailing_country_code, ''), IFNULL(pp.mailing_phone, ''), IFNULL(pp.mailing_fax, ''),
              IFNULL(pp.practice_fax, ''))),
            IFNULL(i.h, ''), IFNULL(e.h, ''))))
        """;

    private const string DataVersionSql = """
        REPLACE INTO `data_version` (`id`, `as_of_date`, `monthly_file`, `weekly_file`, `deactivation_file`,
          `nucc_version`, `hud_version`, `census_version`, `provider_count`, `projected_at`, `published_at`)
        SELECT 1,
          (SELECT MAX(`last_update_date`) FROM `provider`),
          (SELECT `filename` FROM `downlog` WHERE `status` = 'Completed' AND `kind` = 'Monthly' ORDER BY `file_date` DESC LIMIT 1),
          (SELECT `filename` FROM `downlog` WHERE `status` = 'Completed' AND `kind` = 'Weekly' ORDER BY `file_date` DESC LIMIT 1),
          (SELECT `filename` FROM `downlog` WHERE `status` = 'Completed' AND `kind` = 'Deactivation' ORDER BY `file_date` DESC LIMIT 1),
          (SELECT `version` FROM `reference_data` WHERE `source` = 'nucc'),
          (SELECT `version` FROM `reference_data` WHERE `source` = 'hud_zip_county'),
          (SELECT `version` FROM `reference_data` WHERE `source` = 'census_gazetteer'),
          @providers, @now, NULL
        """;
}
