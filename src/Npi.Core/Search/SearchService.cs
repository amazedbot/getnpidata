using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>
/// Provider search over the projection tables (CLAUDE.md §7 Stage 3). Shared by the web pages, the
/// CSV export and /api/v1, so all three return the same results.
/// </summary>
public sealed class SearchService(string connectionString, TaxonomyCatalog taxonomy)
{
    // Types must match what MySqlConnector returns exactly (Dapper maps records by constructor): TINYINT → sbyte.
    private sealed record ProviderRow(
        string Npi, sbyte EntityType, string? LastName, string? FirstName, string? MiddleName, string? NameSuffix, string? Credential,
        string? OrgName, string? Gender, string? PrimaryTaxonomyCode, string? Phone, DateTime? EnumerationDate, DateTime? LastUpdateDate);

    private sealed record LocationRow(
        string Npi, string? Address1, string? Address2, string? City, string? State, string? Zip5, string? Zip4,
        string? PostalCode, string? CountryCode, string? Phone);

    private sealed record ZipCountyRow(string Zip5, string CountyName);

    // EXISTS yields a BIGINT; CAST keeps the type fixed for Dapper.
    private sealed record FlagRow(string Npi, long Excluded, long OptedOut, long OrderRefer, long AcceptsAssignment, long Telehealth, long BilledMedicare);

    private sealed record PageRow(string Npi, long Total);

    private sealed record AreaRow(string Npi, string AddrKey, double Lat, double Lon, sbyte Approximate);

    private sealed record AreaLocationRow(
        string Npi, string AddrKey, string? Address1, string? Address2, string? City, string? State, string? Zip5, string? Zip4, string? PostalCode, string? Phone);

    /// <summary>The most providers <see cref="SearchAreaAsync"/> returns; a busier area asks the visitor to zoom in.</summary>
    public const int MaxAreaResults = 1000;

    // Sides (as a share of the map area's) of the boxes around the centre probed for a busy area.
    private static readonly double[] AreaProbeSides = [1.0 / 8, 1.0 / 4, 1.0 / 2];

    /// <summary>
    /// Map search (CLAUDE.md §7 Stage 5.5 item 10): providers with a street address inside the map area that match
    /// the filter, one item per provider and address, nearest the centre first, at most <see cref="MaxAreaResults"/>.
    /// The area replaces the location filters (state, county, city, ZIP, radius), which are ignored.
    /// </summary>
    /// <exception cref="SearchValidationException">Invalid filter or area, or an area too busy to answer in time.</exception>
    public async Task<AreaResult> SearchAreaAsync(SearchFilter filter, MapBounds bounds, CancellationToken ct)
    {
        if (bounds.Problem is { } problem)
        {
            throw new SearchValidationException(new Dictionary<string, string[]> { ["bbox"] = [problem] });
        }

        var f = SearchValidation.Normalize(
            filter with { State = null, CountyFips = null, City = null, Zip5 = null, RadiusMiles = null, Sort = null, Page = 1, PageSize = SearchFilter.DefaultPageSize },
            requireFilter: false);
        var query = new SearchQuery(f, await taxonomy.ResolveAsync(f, ct), radiusCenter: null, areaSearch: true);
        var p = new DynamicParameters(query.Parameters);
        p.Add("cells", bounds.Cells);
        p.Add("box", bounds.Polygon);
        p.Add("clat", bounds.CenterLat);
        p.Add("clon", bounds.CenterLon);
        p.Add("cosLat", Math.Cos(bounds.CenterLat * Math.PI / 180));
        p.Add("areaTake", MaxAreaResults + 1);
        var areaSql = query.AreaSql("@box");

        await using var connection = await OpenAsync(ct);
        List<AreaRow> rows;
        var truncated = false;
        try
        {
            if (!query.HasSelectiveDriver)
            {
                // A busy area (Manhattan with no specialty) holds hundreds of thousands of points, and sorting them all by
                // distance is slow. Find the smallest box around the centre that already holds more than the limit: the
                // nearest points are in it, so only that box is sorted. Each probe stops at limit + 1 matches.
                foreach (var side in AreaProbeSides)
                {
                    var probe = bounds.Around(side);
                    p.Add("box", probe.Polygon);
                    var found = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                        $"SELECT COUNT(*) FROM ({areaSql} LIMIT @areaTake) x", p, commandTimeout: SearchFilter.SearchTimeoutSeconds, cancellationToken: ct));
                    if (found > MaxAreaResults)
                    {
                        truncated = true;
                        break;
                    }

                    p.Add("box", bounds.Polygon);
                }
            }

            rows = (await connection.QueryAsync<AreaRow>(new CommandDefinition(
                areaSql + " ORDER BY POW(m.lat - @clat, 2) + POW((m.lon - @clon) * @cosLat, 2), m.npi, m.addr_key LIMIT @areaTake",
                p, commandTimeout: SearchFilter.SearchTimeoutSeconds, cancellationToken: ct))).ToList();
        }
        catch (MySqlException ex) when (ex.ErrorCode is MySqlErrorCode.CommandTimeoutExpired or MySqlErrorCode.QueryInterrupted && !ct.IsCancellationRequested)
        {
            throw new SearchValidationException(new Dictionary<string, string[]>
            {
                ["bbox"] = ["This area has too many providers to search quickly. Zoom in or add a filter."],
            });
        }

        if (rows.Count > MaxAreaResults)
        {
            truncated = true;
            rows.RemoveAt(rows.Count - 1);
        }

        var npis = rows.Select(r => r.Npi).Distinct(StringComparer.Ordinal).ToList();
        var summaries = (await SummarizeAsync(connection, new SearchQuery(new SearchFilter(), null, null), npis, ct)).ToDictionary(s => s.Npi, StringComparer.Ordinal);
        var locations = new Dictionary<(string, string), AreaLocationRow>();
        if (rows.Count > 0)
        {
            var keys = rows.Select(r => r.AddrKey).Distinct(StringComparer.Ordinal).ToList();
            foreach (var l in await connection.QueryAsync<AreaLocationRow>(new CommandDefinition(
                         """
                         SELECT npi AS Npi, addr_key AS AddrKey, address1 AS Address1, address2 AS Address2, city AS City, state AS State,
                                zip5 AS Zip5, zip4 AS Zip4, postal_code AS PostalCode, phone AS Phone
                         FROM provider_location WHERE npi IN @npis AND addr_key IN @keys ORDER BY npi, is_primary DESC, id
                         """, new { npis, keys }, cancellationToken: ct)))
            {
                locations.TryAdd((l.Npi, l.AddrKey), l);
            }
        }

        var items = new List<AreaProvider>(rows.Count);
        foreach (var r in rows)
        {
            if (!summaries.TryGetValue(r.Npi, out var s))
            {
                continue; // replaced by a concurrent projection swap
            }

            if (locations.TryGetValue((r.Npi, r.AddrKey), out var l))
            {
                var sameZip = l.Zip5 is not null && s.Zip?.StartsWith(l.Zip5, StringComparison.Ordinal) == true;
                s = s with
                {
                    Address1 = l.Address1, Address2 = l.Address2, City = l.City, State = l.State, Zip = ProviderNames.Zip(l.Zip5, l.Zip4, l.PostalCode),
                    Phone = l.Phone ?? s.Phone, County = sameZip ? s.County : null,
                };
            }

            items.Add(new AreaProvider(s, r.Lat, r.Lon, r.Approximate != 0, MilesBetween(bounds.CenterLat, bounds.CenterLon, r.Lat, r.Lon)));
        }

        var asOf = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition("SELECT as_of_date FROM data_version WHERE id = 1", cancellationToken: ct));
        return new AreaResult(items, truncated, MaxAreaResults, asOf is null ? null : DateOnly.FromDateTime(asOf.Value));
    }

    private static double MilesBetween(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double d) => d * Math.PI / 180;
        var a = Math.Pow(Math.Sin(Rad(lat2 - lat1) / 2), 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(Rad(lon2 - lon1) / 2), 2);
        return 3958.8 * 2 * Math.Asin(Math.Sqrt(a));
    }

    /// <summary>Validates the filter and returns one page of results with the total count.</summary>
    /// <exception cref="SearchValidationException">Invalid filter, or a search too broad to answer within <see cref="SearchFilter.SearchTimeoutSeconds"/>.</exception>
    public async Task<SearchResult> SearchAsync(SearchFilter filter, CancellationToken ct)
    {
        try
        {
            return await SearchPageAsync(filter, ct);
        }
        catch (MySqlException ex) when (ex.ErrorCode is MySqlErrorCode.CommandTimeoutExpired or MySqlErrorCode.QueryInterrupted && !ct.IsCancellationRequested)
        {
            throw new SearchValidationException(new Dictionary<string, string[]>
            {
                ["filter"] = ["This search matches too many providers to page through quickly. Add a specialty, city or name, or download the CSV."],
            });
        }
    }

    private async Task<SearchResult> SearchPageAsync(SearchFilter filter, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var query = await PrepareAsync(connection, filter, ct);
        long total;
        List<string> npis;
        if (query.MayBeBroad && query.IndexOrderPageSql is { } indexOrderSql
            && (total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(query.CountSql, query.Parameters, commandTimeout: SearchFilter.SearchTimeoutSeconds, cancellationToken: ct)))
               >= SearchFilter.BroadSearchThreshold)
        {
            // Huge result (e.g. a whole state): matches are dense, so walking the sort index is fast,
            // while sorting a million rows for one page is not (CLAUDE.md §11 item 6).
            npis = (await connection.QueryAsync<string>(new CommandDefinition(indexOrderSql, query.Parameters, commandTimeout: SearchFilter.SearchTimeoutSeconds, cancellationToken: ct))).ToList();
        }
        else
        {
            var page = (await connection.QueryAsync<PageRow>(new CommandDefinition(query.PageSql, query.Parameters, commandTimeout: SearchFilter.SearchTimeoutSeconds, cancellationToken: ct))).ToList();
            // The total comes with the page (COUNT(*) OVER ()); only a page past the end needs a separate count.
            total = page.Count > 0 ? page[0].Total
                : query.Filter.Page == 1 ? 0
                : await connection.ExecuteScalarAsync<long>(new CommandDefinition(query.CountSql, query.Parameters, commandTimeout: SearchFilter.SearchTimeoutSeconds, cancellationToken: ct));
            npis = page.Select(r => r.Npi).ToList();
        }

        var items = await SummarizeAsync(connection, query, npis, ct);
        var asOf = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT as_of_date FROM data_version WHERE id = 1", cancellationToken: ct));
        return new SearchResult(items, query.Filter.Page, query.Filter.PageSize, total, asOf is null ? null : DateOnly.FromDateTime(asOf.Value));
    }

    /// <summary>Every matching provider in sort order, fetched in batches (CSV export: streamed, no row cap).</summary>
    public async IAsyncEnumerable<ProviderSummary> SearchAllAsync(SearchFilter filter, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        const int batchSize = 1000;
        await using var connection = await OpenAsync(ct);
        var query = await PrepareAsync(connection, filter with { Page = 1, PageSize = SearchFilter.DefaultPageSize }, ct);

        // The NPI list streams from an unbuffered reader on a second connection, so memory stays flat.
        await using var listConnection = await OpenAsync(ct);
        var batch = new List<string>(batchSize);
        await using var reader = await listConnection.ExecuteReaderAsync(new CommandDefinition(query.AllSql, query.Parameters, commandTimeout: 3600, cancellationToken: ct));
        while (await reader.ReadAsync(ct))
        {
            batch.Add(reader.GetString(0));
            if (batch.Count == batchSize)
            {
                foreach (var item in await SummarizeAsync(connection, query, batch, ct))
                {
                    yield return item;
                }

                batch.Clear();
            }
        }

        foreach (var item in await SummarizeAsync(connection, query, batch, ct))
        {
            yield return item;
        }
    }

    /// <summary>The most NPIs <see cref="LookupAsync"/> accepts at once (the CSV upload goes through in batches).</summary>
    public const int MaxLookupBatch = 1000;

    /// <summary>
    /// Bulk lookup (Stage 5.5 item 8): every requested NPI in the given order (duplicates removed), with its
    /// summary when it is an active provider. Invalid NPIs (format or check digit) are reported, not looked up.
    /// </summary>
    public async Task<IReadOnlyList<LookupRow>> LookupAsync(IReadOnlyList<string> npis, CancellationToken ct)
    {
        if (npis.Count > MaxLookupBatch)
        {
            throw new ArgumentException($"At most {MaxLookupBatch} NPIs per lookup.", nameof(npis));
        }

        var requested = npis.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var valid = requested.Where(InputFormats.HasValidCheckDigit).ToList();
        var found = new Dictionary<string, ProviderSummary>(StringComparer.Ordinal);
        if (valid.Count > 0)
        {
            await using var connection = await OpenAsync(ct);
            var query = new SearchQuery(new SearchFilter(), taxonomyCodes: null, radiusCenter: null); // no location filter: primary location first
            foreach (var summary in await SummarizeAsync(connection, query, valid, ct))
            {
                found[summary.Npi] = summary;
            }
        }

        return requested.Select(n => !InputFormats.HasValidCheckDigit(n)
                ? new LookupRow(n, LookupStatus.Invalid, null)
                : found.TryGetValue(n, out var p) ? new LookupRow(n, LookupStatus.Found, p) : new LookupRow(n, LookupStatus.NotFound, null))
            .ToList();
    }

    private async Task<SearchQuery> PrepareAsync(MySqlConnection connection, SearchFilter filter, CancellationToken ct)
    {
        var f = SearchValidation.Normalize(filter);
        var codes = await taxonomy.ResolveAsync(f, ct);
        GeoPoint? center = null;
        if (f.RadiusMiles is not null)
        {
            var point = await connection.QuerySingleOrDefaultAsync<(double Lat, double Lon)?>(new CommandDefinition(
                "SELECT CAST(lat AS DOUBLE), CAST(lon AS DOUBLE) FROM zip_centroid WHERE zip5 = @zip", new { zip = f.Zip5 }, cancellationToken: ct));
            center = point is null
                ? throw new SearchValidationException(new Dictionary<string, string[]>
                {
                    [nameof(SearchFilter.Zip5)] = [$"ZIP {f.Zip5} has no Census location (e.g. a PO-box-only ZIP), so it can't be used for a radius search."],
                })
                : new GeoPoint(point.Value.Lat, point.Value.Lon);
        }

        return new SearchQuery(f, codes, center);
    }

    private async Task<IReadOnlyList<ProviderSummary>> SummarizeAsync(MySqlConnection connection, SearchQuery query, List<string> npis, CancellationToken ct)
    {
        if (npis.Count == 0)
        {
            return [];
        }

        var providers = (await connection.QueryAsync<ProviderRow>(new CommandDefinition(
                """
                SELECT npi AS Npi, entity_type AS EntityType, last_name AS LastName, first_name AS FirstName, middle_name AS MiddleName,
                       name_suffix AS NameSuffix, credential AS Credential, org_name AS OrgName, gender AS Gender,
                       primary_taxonomy_code AS PrimaryTaxonomyCode, phone AS Phone,
                       enumeration_date AS EnumerationDate, last_update_date AS LastUpdateDate
                FROM provider WHERE npi IN @npis
                """, new { npis }, cancellationToken: ct)))
            .ToDictionary(p => p.Npi);

        var locationParams = new DynamicParameters(query.Parameters);
        locationParams.Add("npis", npis);
        var locations = (await connection.QueryAsync<LocationRow>(new CommandDefinition(query.LocationsSql, locationParams, cancellationToken: ct)))
            .GroupBy(l => l.Npi)
            .ToDictionary(g => g.Key, g => g.First());

        var zips = locations.Values.Select(l => l.Zip5).OfType<string>().Distinct().ToList();
        var counties = new Dictionary<string, string>();
        if (zips.Count > 0)
        {
            // With a county filter, show that county; otherwise the county holding most of the ZIP's addresses.
            var sql = query.Filter.CountyFips is not null
                ? "SELECT z.zip5 AS Zip5, c.county_name AS CountyName FROM zip_county z JOIN county c ON c.county_fips = z.county_fips WHERE z.zip5 IN @zips AND z.county_fips = @fips"
                : "SELECT z.zip5 AS Zip5, c.county_name AS CountyName FROM zip_county z JOIN county c ON c.county_fips = z.county_fips WHERE z.zip5 IN @zips ORDER BY z.zip5, z.tot_ratio DESC";
            foreach (var row in await connection.QueryAsync<ZipCountyRow>(new CommandDefinition(sql, new { zips, fips = query.Filter.CountyFips }, cancellationToken: ct)))
            {
                counties.TryAdd(row.Zip5, row.CountyName);
            }
        }

        var flags = (await connection.QueryAsync<FlagRow>(new CommandDefinition(EnrichmentSql.FlagsSql, new { npis }, cancellationToken: ct)))
            .ToDictionary(f => f.Npi, f => new ProviderFlags(f.Excluded != 0, f.OptedOut != 0, f.OrderRefer != 0, f.AcceptsAssignment != 0, f.Telehealth != 0,
                f.BilledMedicare != 0));

        var result = new List<ProviderSummary>(npis.Count);
        foreach (var npi in npis)
        {
            if (!providers.TryGetValue(npi, out var p))
            {
                continue; // replaced by a concurrent projection swap; skip rather than fail the page
            }

            locations.TryGetValue(npi, out var l);
            var specialty = await taxonomy.FindAsync(p.PrimaryTaxonomyCode, ct);
            result.Add(new ProviderSummary(
                p.Npi,
                p.EntityType,
                ProviderNames.Display(p.EntityType, p.LastName, p.FirstName, p.MiddleName, p.NameSuffix, p.OrgName),
                p.Credential,
                ProviderNames.Specialty(specialty?.Classification, specialty?.Specialization) ?? p.PrimaryTaxonomyCode,
                l?.Address1,
                l?.Address2,
                l?.City,
                l?.State,
                ProviderNames.Zip(l?.Zip5, l?.Zip4, l?.PostalCode),
                l?.Zip5 is { } z && counties.TryGetValue(z, out var county) ? county : null,
                l?.Phone ?? p.Phone,
                p.Gender,
                p.EnumerationDate is null ? null : DateOnly.FromDateTime(p.EnumerationDate.Value),
                p.LastUpdateDate is null ? null : DateOnly.FromDateTime(p.LastUpdateDate.Value))
            {
                Flags = flags.GetValueOrDefault(npi, ProviderFlags.None),
            });
        }

        return result;
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}

/// <summary>Display formatting shared by the grid, CSV and API.</summary>
public static class ProviderNames
{
    /// <summary>"LAST, FIRST MIDDLE SUFFIX" for individuals, the legal business name for organizations.</summary>
    public static string Display(int entityType, string? last, string? first, string? middle, string? suffix, string? org)
    {
        if (entityType == 2)
        {
            return org ?? "";
        }

        var given = string.Join(' ', new[] { first, middle, suffix }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return given.Length == 0 ? last ?? "" : $"{last}, {given}";
    }

    /// <summary>"Classification – Specialization", or the classification alone.</summary>
    public static string? Specialty(string? classification, string? specialization) =>
        classification is null ? null : specialization is null ? classification : $"{classification} – {specialization}";

    /// <summary>"11701-1234", "11701", or the raw postal code for foreign addresses.</summary>
    public static string? Zip(string? zip5, string? zip4, string? postalCode) =>
        zip5 is null ? postalCode : zip4 is null ? zip5 : $"{zip5}-{zip4}";

    /// <summary>A raw NPPES postal code for display: US "117011234" → "11701-1234"; anything else unchanged.</summary>
    public static string? PostalCode(string? raw, string? countryCode) =>
        raw is { Length: 9 } && raw.All(char.IsAsciiDigit) && (countryCode is null or "US") ? $"{raw[..5]}-{raw[5..]}" : raw;
}
