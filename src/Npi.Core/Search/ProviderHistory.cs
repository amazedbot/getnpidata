using System.Globalization;
using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>One change between two versions of an NPI's record, in plain words.</summary>
/// <param name="On">When it was first seen: the date of the monthly file (the archive) or of the load that recorded it.</param>
/// <param name="Kind">One of <see cref="HistoryChangeKinds"/>, or a <c>provider_change</c> kind (<see cref="ChangeTypes"/>) after the archive.</param>
public sealed record HistoryChange(DateOnly On, string Kind, string Text);

/// <summary>The kinds of change the archive tells apart (docs/PRODUCT_PLAN.md step 3.4).</summary>
public static class HistoryChangeKinds
{
    public const string Listed = "listed";
    public const string Deactivated = "deactivated";
    public const string Reactivated = "reactivated";
    public const string Moved = "moved";
    public const string Locations = "locations";
    public const string Specialty = "specialty";
    public const string EntityType = "entity_type";
    public const string Dates = "dates";
    public const string Updated = "updated";

    /// <summary>A short label for a kind (archive kinds and provider_change kinds), for lists of what changed.</summary>
    public static string Brief(string kind) => kind switch
    {
        Listed or "added" => "first listed",
        Deactivated => "deactivated",
        Reactivated => "reactivated",
        Moved or "address" => "moved",
        Locations or "location_added" or "location_removed" => "practice locations",
        Specialty or "taxonomy_added" or "taxonomy_removed" => "specialty",
        EntityType => "entity type",
        Dates => "dates",
        Updated => "record updated",
        _ => ChangeTypes.Label(kind).ToLowerInvariant(),
    };
}

/// <summary>A practice location as the archive keeps it: the state and, for a US address, the ZIP code and its USPS city.</summary>
public sealed record HistoryPlace(string? State, string? Zip5, string? City, string? Country)
{
    public override string ToString()
    {
        if (State is null && Zip5 is null)
        {
            return Country is { Length: > 0 } c && c != "US" ? $"outside the US ({c})" : "no practice address";
        }

        var city = City is { Length: > 0 } ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(City.ToLowerInvariant()) + ", " : "";
        return $"{city}{State}{(Zip5 is null ? "" : " " + Zip5)}";
    }
}

/// <summary>One version of an NPI's record in the monthly NPPES files: what the files listed from <see cref="ListedFrom"/>.</summary>
/// <param name="ListedFrom">The date of the first file that listed this version.</param>
/// <param name="ListedTo">The date of the file that listed the next version (null: still the newest).</param>
/// <param name="Status">"active", "deactivated" (listed with its deactivation date) or "notListed" (no longer in the file: deactivated, before November 2012).</param>
/// <param name="Source">Where the file came from: CMS NPPES, and before June 2015 its historical copy via NBER.</param>
/// <param name="Changes">What changed from the version before, in plain words.</param>
public sealed record ProviderHistoryVersion(
    DateOnly ListedFrom, DateOnly? ListedTo, string Status, int? EntityType, HistoryPlace? Practice, IReadOnlyList<HistoryPlace> SecondaryLocations,
    string? PrimaryTaxonomy, string? PrimarySpecialty, IReadOnlyList<string> TaxonomyCodes, DateOnly? EnumerationDate, DateOnly? LastUpdateDate,
    DateOnly? DeactivationDate, DateOnly? ReactivationDate, string Source, IReadOnlyList<HistoryChange> Changes)
{
    /// <summary>Months without a surviving file just before this version: the change may date from one of them.</summary>
    public IReadOnlyList<DateOnly> MonthsWithoutFileBefore { get; init; } = [];
}

/// <summary>
/// An NPI's record over the years, from the archived monthly NPPES files (docs/PRODUCT_PLAN.md step 3.4: CMS NPPES, historical
/// copies via NBER), newest version first. Changes after the archive are the profile's recorded changes.
/// </summary>
/// <param name="ArchiveFrom">The archive's first file (its month).</param>
/// <param name="ArchiveTo">The archive's newest file (its month).</param>
/// <param name="FirstListed">The date of the first file that listed the NPI.</param>
/// <param name="InFirstFile">The NPI was already in the archive's first file (so it may be older).</param>
/// <param name="SecondaryFrom">The first file that listed secondary practice locations (September 2018); none before.</param>
public sealed record ProviderHistory(DateOnly ArchiveFrom, DateOnly ArchiveTo, DateOnly FirstListed, bool InFirstFile, DateOnly? SecondaryFrom,
    IReadOnlyList<ProviderHistoryVersion> Versions)
{
    public const string Attribution = "CMS NPPES monthly files; historical copies via NBER";

    public const string Doi = "https://doi.org/10.60592/bjwk-8n62";

    /// <summary>The last version listed active (the last known record of a deactivated NPI), or null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ProviderHistoryVersion? LastActive => Versions.FirstOrDefault(v => v.Status == ProviderHistoryReader.Active);
}

/// <summary>
/// Reads the archive's versions of NPIs (npi_history by its primary key) and describes each change in plain words. Shared by
/// the profile, its API record, the deactivated NPI page and Verify NPIs' "changed since".
/// </summary>
public static class ProviderHistoryReader
{
    public const string Active = "active", Deactivated = "deactivated", NotListed = "notListed";

    private sealed record VersionRow(uint Npi, DateTime FromPeriod, DateTime? ToPeriod, sbyte Status, sbyte? EntityType, string? State, string? Zip5,
        string? Country, string? PrimaryTaxonomy, uint? TaxonomySet, uint? LocationSet, DateTime? EnumerationDate, DateTime? LastUpdateDate,
        DateTime? DeactivationDate, DateTime? ReactivationDate);

    private sealed record PeriodRow(DateTime Period, string Status, string? Source, DateTime? FileDate, sbyte Locations);

    private sealed record Periods(IReadOnlyList<PeriodRow> All, IReadOnlyDictionary<DateOnly, PeriodRow> ByFileDate, DateOnly? SecondaryFrom)
    {
        public DateOnly? First => All.Where(p => p.Status == "Completed").Select(p => DateOnly.FromDateTime(p.Period)).Cast<DateOnly?>().FirstOrDefault();

        public DateOnly? Last => All.Where(p => p.Status == "Completed").Select(p => DateOnly.FromDateTime(p.Period)).Cast<DateOnly?>().LastOrDefault();
    }

    private static async Task<Periods> PeriodsAsync(MySqlConnection connection, CancellationToken ct)
    {
        var all = (await connection.QueryAsync<PeriodRow>(new CommandDefinition(
            "SELECT period AS Period, status AS Status, source AS Source, file_date AS FileDate, locations AS Locations FROM history_period ORDER BY period",
            cancellationToken: ct))).ToList();
        var byDate = all.Where(p => p is { Status: "Completed", FileDate: not null })
            .GroupBy(p => DateOnly.FromDateTime(p.FileDate!.Value)).ToDictionary(g => g.Key, g => g.First());
        var secondary = all.FirstOrDefault(p => p is { Status: "Completed", Locations: 1, FileDate: not null })?.FileDate;
        return new Periods(all, byDate, secondary is { } s ? DateOnly.FromDateTime(s) : null);
    }

    /// <summary>One NPI's history; null when the archive isn't loaded or doesn't list the NPI.</summary>
    public static async Task<ProviderHistory?> ReadAsync(MySqlConnection connection, string npi, CancellationToken ct)
    {
        if (!InputFormats.IsNpi(npi))
        {
            return null;
        }

        var rows = (await connection.QueryAsync<VersionRow>(new CommandDefinition(Select + " WHERE npi = @npi ORDER BY from_period",
            new { npi = ulong.Parse(npi, CultureInfo.InvariantCulture) }, cancellationToken: ct))).ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        var periods = await PeriodsAsync(connection, ct);
        if (periods.First is not { } first || periods.Last is not { } last)
        {
            return null;
        }

        var lookups = await LookupsAsync(connection, rows, ct);
        var versions = Build(rows, lookups, periods);
        var firstListed = DateOnly.FromDateTime(rows[0].FromPeriod);
        var inFirstFile = periods.ByFileDate.TryGetValue(firstListed, out var firstPeriod) && DateOnly.FromDateTime(firstPeriod.Period) == first;
        versions.Reverse();
        return new ProviderHistory(first, last, firstListed, inFirstFile, periods.SecondaryFrom, versions);
    }

    /// <summary>
    /// What changed for each NPI since <paramref name="since"/>: the archive's versions first listed after it, then the changes
    /// recorded since (provider_change). One query per table for the batch (≤ 1,000 NPIs). NPIs without a change are left out.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<HistoryChange>>> ChangesSinceAsync(MySqlConnection connection,
        IReadOnlyCollection<string> npis, DateOnly since, CancellationToken ct)
    {
        var result = new Dictionary<string, List<HistoryChange>>(StringComparer.Ordinal);
        var valid = npis.Where(InputFormats.IsNpi).Distinct(StringComparer.Ordinal).ToList();
        if (valid.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<HistoryChange>>();
        }

        var sinceDate = since.ToDateTime(TimeOnly.MinValue);
        // The version in force on the date and every later one, by the primary key's NPI prefix.
        var rows = (await connection.QueryAsync<VersionRow>(new CommandDefinition(
            Select + " WHERE npi IN @npis AND (to_period IS NULL OR to_period > @since) ORDER BY npi, from_period",
            new { npis = valid.Select(n => ulong.Parse(n, CultureInfo.InvariantCulture)).ToList(), since = sinceDate }, cancellationToken: ct))).ToList();
        if (rows.Count > 0)
        {
            var periods = await PeriodsAsync(connection, ct);
            var lookups = await LookupsAsync(connection, rows, ct);
            foreach (var group in rows.GroupBy(r => r.Npi))
            {
                var changes = Build([.. group], lookups, periods).Where(v => v.ListedFrom > since).SelectMany(v => v.Changes).ToList();
                if (changes.Count > 0)
                {
                    result[group.Key.ToString(CultureInfo.InvariantCulture)] = changes;
                }
            }
        }

        var recorded = await connection.QueryAsync<(string Npi, DateTime DetectedAt, string Type, string? OldValue, string? NewValue)>(new CommandDefinition(
            """
            SELECT npi, detected_at, change_type, old_value, new_value FROM provider_change
            WHERE npi IN @valid AND detected_at >= @since AND change_type <> 'removed'
            ORDER BY npi, detected_at, id
            """, new { valid, since = sinceDate }, cancellationToken: ct));
        foreach (var c in recorded)
        {
            if (!result.TryGetValue(c.Npi, out var list))
            {
                result[c.Npi] = list = [];
            }

            list.Add(new HistoryChange(DateOnly.FromDateTime(c.DetectedAt), c.Type, RecordedText(c.Type, c.OldValue, c.NewValue)));
        }

        return result.ToDictionary(r => r.Key, r => (IReadOnlyList<HistoryChange>)r.Value, StringComparer.Ordinal);
    }

    // A provider_change row in words (taxonomy codes by their plain name).
    private static string RecordedText(string type, string? oldValue, string? newValue)
    {
        string? Show(string? value) => value is not null && ChangeTypes.Taxonomy.Contains(type) ? SpecialtyName(value) : value;
        var label = type switch { "deactivated" => "Deactivated", "added" => "First listed", _ => ChangeTypes.Label(type) };
        return (Show(oldValue), Show(newValue)) switch
        {
            (null, null) => label,
            (null, { } now) => $"{label}: {now}",
            ({ } before, null) => $"{label}: {before}",
            ({ } before, { } now) => $"{label}: {before} → {now}",
        };
    }

    private const string Select =
        """
        SELECT npi AS Npi, from_period AS FromPeriod, to_period AS ToPeriod, status AS Status, entity_type AS EntityType, state AS State, zip5 AS Zip5,
               country AS Country, primary_taxonomy AS PrimaryTaxonomy, taxonomy_set AS TaxonomySet, location_set AS LocationSet,
               enumeration_date AS EnumerationDate, last_update_date AS LastUpdateDate, deactivation_date AS DeactivationDate,
               reactivation_date AS ReactivationDate
        FROM npi_history
        """;

    private sealed record Lookups(IReadOnlyDictionary<uint, string[]> Sets, IReadOnlyDictionary<uint, IReadOnlyList<(string State, string Zip)>> Locations,
        IReadOnlyDictionary<string, string> Cities);

    private static async Task<Lookups> LookupsAsync(MySqlConnection connection, IReadOnlyList<VersionRow> rows, CancellationToken ct)
    {
        var setIds = rows.Select(r => r.TaxonomySet).OfType<uint>().Distinct().ToList();
        var locationIds = rows.Select(r => r.LocationSet).OfType<uint>().Distinct().ToList();
        var sets = setIds.Count == 0 ? new Dictionary<uint, string[]>() : (await connection.QueryAsync<(uint Id, string Codes)>(new CommandDefinition(
                "SELECT id, codes FROM npi_history_taxonomy_set WHERE id IN @setIds", new { setIds }, cancellationToken: ct)))
            .ToDictionary(s => s.Id, s => s.Codes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var locations = locationIds.Count == 0 ? new Dictionary<uint, IReadOnlyList<(string, string)>>() : (await connection.QueryAsync<(uint Id, string State, string Zip)>(
                new CommandDefinition("SELECT set_id, state, zip5 FROM npi_history_location_set WHERE set_id IN @locationIds ORDER BY set_id, state, zip5",
                    new { locationIds }, cancellationToken: ct)))
            .GroupBy(l => l.Id).ToDictionary(g => g.Key, g => (IReadOnlyList<(string, string)>)g.Select(l => (l.State, l.Zip)).ToList());
        var zips = rows.Select(r => r.Zip5).Concat(locations.Values.SelectMany(l => l.Select(x => x.Item2)))
            .Where(z => z is { Length: 5 }).Distinct(StringComparer.Ordinal).ToList();
        var cities = zips.Count == 0 ? new Dictionary<string, string>() : (await connection.QueryAsync<(string Zip, string? City)>(new CommandDefinition(
                "SELECT zip5, MAX(usps_city) FROM zip_county WHERE zip5 IN @zips GROUP BY zip5", new { zips }, cancellationToken: ct)))
            .Where(c => c.City is not null).ToDictionary(c => c.Zip, c => c.City!, StringComparer.Ordinal);
        return new Lookups(sets, locations, cities);
    }

    private static string StatusOf(sbyte status) => status switch { 0 => Active, 1 => Deactivated, _ => NotListed };

    private static string? SpecialtyName(string? code)
    {
        if (code is null)
        {
            return null;
        }

        var placement = SpecialtyCategories.Default.IsMapped(code) ? SpecialtyCategories.Default.Place(code) : null;
        return placement is null ? code : $"{placement.Singular} ({code})";
    }

    private static string DateText(DateOnly date) => date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);

    private static DateOnly? D(DateTime? value) => value is { } v ? DateOnly.FromDateTime(v) : null;

    // The versions of one NPI (oldest first), with what changed from each one before.
    private static List<ProviderHistoryVersion> Build(IReadOnlyList<VersionRow> rows, Lookups lookups, Periods periods)
    {
        var versions = new List<ProviderHistoryVersion>(rows.Count);
        ProviderHistoryVersion? before = null;
        DateOnly? beforePeriod = null;
        foreach (var r in rows)
        {
            var from = DateOnly.FromDateTime(r.FromPeriod);
            var period = periods.ByFileDate.GetValueOrDefault(from);
            var place = r.Status == 0 ? Place(r.State, r.Zip5, r.Country, lookups) : null;
            var secondary = r.LocationSet is { } id && lookups.Locations.TryGetValue(id, out var pairs)
                ? pairs.Select(p => Place(p.State, p.Zip.Length == 5 ? p.Zip : null, null, lookups)!).ToList()
                : [];
            var codes = r.TaxonomySet is { } set && lookups.Sets.TryGetValue(set, out var c) ? c : [];
            var source = period?.Source == "nber" ? "CMS NPPES monthly file (historical copy via NBER)" : "CMS NPPES monthly file";
            var version = new ProviderHistoryVersion(from, D(r.ToPeriod), StatusOf(r.Status), r.EntityType, place, secondary, r.PrimaryTaxonomy,
                SpecialtyName(r.PrimaryTaxonomy), codes, D(r.EnumerationDate), D(r.LastUpdateDate), D(r.DeactivationDate), D(r.ReactivationDate), source, []);
            var month = period is null ? (DateOnly?)null : DateOnly.FromDateTime(period.Period);
            var missing = beforePeriod is { } b && month is { } m
                ? periods.All.Where(p => p.Status == "Missing" && DateOnly.FromDateTime(p.Period) > b && DateOnly.FromDateTime(p.Period) < m)
                    .Select(p => DateOnly.FromDateTime(p.Period)).ToList()
                : [];
            version = version with
            {
                Changes = Describe(before, version, periods.SecondaryFrom, before is null && month == periods.First),
                MonthsWithoutFileBefore = missing,
            };
            versions.Add(version);
            before = version;
            beforePeriod = month ?? beforePeriod;
        }

        return versions;
    }

    private static HistoryPlace? Place(string? state, string? zip, string? country, Lookups lookups) =>
        new(state, zip, zip is not null && lookups.Cities.TryGetValue(zip, out var city) ? city : null, country);

    /// <summary>What changed from <paramref name="before"/> (null: the first version) to <paramref name="now"/>, in plain words.</summary>
    internal static IReadOnlyList<HistoryChange> Describe(ProviderHistoryVersion? before, ProviderHistoryVersion now, DateOnly? secondaryFrom, bool inFirstFile)
    {
        var on = now.ListedFrom;
        var changes = new List<HistoryChange>();
        void Add(string kind, string text) => changes.Add(new HistoryChange(on, kind, text));

        if (before is null)
        {
            var issued = now.EnumerationDate is { } e ? $" (NPI issued {DateText(e)})" : "";
            Add(HistoryChangeKinds.Listed, now.Status switch
            {
                Active when inFirstFile => $"In the archive's first file{issued}",
                Active => $"First listed{issued}",
                _ when inFirstFile => "In the archive's first file, already deactivated" + (now.DeactivationDate is { } d0 ? $" (on {DateText(d0)})" : ""),
                _ => "First listed as deactivated" + (now.DeactivationDate is { } d1 ? $" (on {DateText(d1)})" : ""),
            });
            return changes;
        }

        if (before.Status == Active && now.Status != Active)
        {
            Add(HistoryChangeKinds.Deactivated, now.Status == NotListed
                ? "No longer listed: deactivated (until November 2012 the files left deactivated NPIs out)"
                : "Deactivated" + (now.DeactivationDate is { } d ? $" on {DateText(d)}" : ""));
            return changes;
        }

        if (before.Status != Active && now.Status == Active)
        {
            // A deactivated version lists no details to compare with.
            Add(HistoryChangeKinds.Reactivated, "Reactivated" + (now.ReactivationDate is { } r ? $" on {DateText(r)}" : "")
                + (now.Practice is { } place ? $", practising in {place}" : ""));
            return changes;
        }
        else if (before.Status != Active)
        {
            if (before.Status == NotListed && now.Status == Deactivated)
            {
                Add(HistoryChangeKinds.Dates, "Listed again as deactivated" + (now.DeactivationDate is { } d ? $" on {DateText(d)}" : ""));
            }
            else if (before.DeactivationDate != now.DeactivationDate)
            {
                Add(HistoryChangeKinds.Dates, $"Deactivation date: {Text(before.DeactivationDate)} → {Text(now.DeactivationDate)}");
            }

            return changes;
        }

        if (before.Status == Active && !Equals(before.Practice, now.Practice))
        {
            Add(HistoryChangeKinds.Moved, $"Primary practice location: {before.Practice} → {now.Practice}");
        }

        var oldSecondary = before.SecondaryLocations.Select(p => p.ToString()).ToHashSet(StringComparer.Ordinal);
        var newSecondary = now.SecondaryLocations.Select(p => p.ToString()).ToList();
        if (secondaryFrom == now.ListedFrom && before.ListedFrom < now.ListedFrom && oldSecondary.Count == 0)
        {
            if (newSecondary.Count > 0)
            {
                Add(HistoryChangeKinds.Locations, $"Secondary practice locations listed for the first time (the files list them from {AreaHistoryMath.MonthName(now.ListedFrom)}): {string.Join("; ", newSecondary)}");
            }
        }
        else
        {
            var added = newSecondary.Where(p => !oldSecondary.Contains(p)).ToList();
            var dropped = oldSecondary.Where(p => !newSecondary.Contains(p)).Order(StringComparer.Ordinal).ToList();
            if (added.Count > 0)
            {
                Add(HistoryChangeKinds.Locations, $"Practice location added: {string.Join("; ", added)}");
            }

            if (dropped.Count > 0)
            {
                Add(HistoryChangeKinds.Locations, $"Practice location dropped: {string.Join("; ", dropped)}");
            }
        }

        if (!string.Equals(before.PrimaryTaxonomy, now.PrimaryTaxonomy, StringComparison.OrdinalIgnoreCase))
        {
            Add(HistoryChangeKinds.Specialty, $"Primary specialty: {before.PrimarySpecialty ?? "none"} → {now.PrimarySpecialty ?? "none"}");
        }

        var addedCodes = now.TaxonomyCodes.Except(before.TaxonomyCodes, StringComparer.OrdinalIgnoreCase).Where(c => !string.Equals(c, now.PrimaryTaxonomy, StringComparison.OrdinalIgnoreCase)).ToList();
        var droppedCodes = before.TaxonomyCodes.Except(now.TaxonomyCodes, StringComparer.OrdinalIgnoreCase).Where(c => !string.Equals(c, before.PrimaryTaxonomy, StringComparison.OrdinalIgnoreCase)).ToList();
        if (addedCodes.Count > 0)
        {
            Add(HistoryChangeKinds.Specialty, $"Specialty added: {string.Join("; ", addedCodes.Select(SpecialtyName))}");
        }

        if (droppedCodes.Count > 0)
        {
            Add(HistoryChangeKinds.Specialty, $"Specialty dropped: {string.Join("; ", droppedCodes.Select(SpecialtyName))}");
        }

        if (before.EntityType != now.EntityType && before.EntityType is not null && now.EntityType is not null)
        {
            static string Entity(int? e) => e == 2 ? "organization" : "individual";
            Add(HistoryChangeKinds.EntityType, $"Entity type: {Entity(before.EntityType)} → {Entity(now.EntityType)}");
        }

        if (before.EnumerationDate != now.EnumerationDate)
        {
            Add(HistoryChangeKinds.Dates, $"Enumeration date: {Text(before.EnumerationDate)} → {Text(now.EnumerationDate)}");
        }

        if (before.ReactivationDate != now.ReactivationDate && changes.All(c => c.Kind != HistoryChangeKinds.Reactivated))
        {
            Add(HistoryChangeKinds.Dates, $"Reactivation date: {Text(before.ReactivationDate)} → {Text(now.ReactivationDate)}");
        }

        if (now.LastUpdateDate != before.LastUpdateDate && now.LastUpdateDate is { } updated)
        {
            Add(HistoryChangeKinds.Updated, changes.Count == 0 || changes.All(c => c.Kind == HistoryChangeKinds.Reactivated)
                ? $"Record updated in NPPES on {DateText(updated)} (no change in the location, specialty or status)"
                : $"Updated in NPPES on {DateText(updated)}");
        }

        if (changes.Count == 0)
        {
            Add(HistoryChangeKinds.Updated, "Other details changed (such as the country or a date)");
        }

        return changes;
    }

    private static string Text(DateOnly? date) => date is { } d ? DateText(d) : "none";
}
