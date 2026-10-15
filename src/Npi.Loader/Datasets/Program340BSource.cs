using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExcelDataReader;
using Npi.Core;
using Npi.Loader.Db;
using Npi.Loader.Reference;

namespace Npi.Loader.Datasets;

/// <summary>A 340B covered entity in HRSA's OPAIS export, with the NPIs it lists.</summary>
public sealed record Entity340BRow(string Id340B, int? CeId, string EntityType, bool Participating, DateTime? StartDate, DateTime? TerminationDate,
    string? TerminationReason, string Name, string? SubName, string? GrantNumber, string? SiteId, string? Ccn, string? Address, string? City, string? State,
    string? Zip5, bool? Rural, IReadOnlyList<string> Npis)
{
    /// <summary>The registration a child site (a hospital's outpatient site, a grantee's clinic) is registered under.</summary>
    public string? ParentId340B { get; init; }
}

/// <summary>
/// HRSA's 340B OPAIS covered entity export (docs/PRODUCT_PLAN.md step 3.6 part 2), as the owner downloads it from OPAIS's Reports
/// page (https://340bopais.hrsa.gov/Reports): the <b>Covered Entity Daily Export (JSON)</b>, which downloads as a zip
/// (340B_CoveredEntityJson_Daily_&lt;yyyyMMdd_HHmmss&gt;.zip holding OPA_CE_DAILY_PUBLIC.JSON, 275 MB in October 2026; streamed from
/// the zip entry, nothing is extracted; see <see cref="ReadJsonAsync"/> for its keys), the JSON itself, or the <b>Covered Entity
/// Daily Report</b> (.xlsx; its "Covered Entities" worksheet, columns as HRSA's help page "Daily Reports" documents them: 340B ID,
/// CE ID, Entity Type, Participating, Participating Start Date, Termination Date, Termination Reason, Entity Name, Entity
/// Sub-Division Name, Grant Number, Site ID, Medicare Provide Number, Street Address 1–3, Street City, Street State, Street Zip,
/// NPI, Rural …; the Excel reader is built on that documentation, not a real file). A file it can't read fails the load, and the
/// tables stay as they were. Contacts, billing and shipping addresses, Medicaid numbers and contract pharmacies are not kept.
/// </summary>
public static partial class Opais340B
{
    // Normalized column names (letters and digits, lower case), each with the spellings tried in turn.
    private static readonly string[] Id = ["340bid", "id340b"];
    private static readonly string[] CeIdKeys = ["ceid"];
    private static readonly string[] ParentKeys = ["parentid340b", "parent340bid"];
    private static readonly string[] TypeKeys = ["entitytype", "entitytypecode"];
    private static readonly string[] ParticipatingKeys = ["participating", "isparticipating"];
    private static readonly string[] StartKeys = ["participatingstartdate", "participationstartdate", "startdate"];
    private static readonly string[] EndKeys = ["terminationdate"];
    private static readonly string[] ReasonKeys = ["terminationreason"];
    private static readonly string[] NameKeys = ["entityname", "name"];
    private static readonly string[] SubKeys = ["entitysubdivisionname", "subdivisionname", "entitysubname"];
    private static readonly string[] GrantKeys = ["grantnumber"];
    private static readonly string[] SiteKeys = ["siteid"];
    private static readonly string[] MedicareKeys = ["medicareprovidenumber", "medicareprovidernumber", "cmscertificationnumber", "medicarenumber"];
    private static readonly string[] Address1Keys = ["streetaddress1", "streetaddressaddressline1", "streetaddressaddress1", "streetaddressline1"];
    private static readonly string[] CityKeys = ["streetcity", "streetaddresscity"];
    private static readonly string[] StateKeys = ["streetstate", "streetaddressstate"];
    private static readonly string[] ZipKeys = ["streetzip", "streetaddresszip", "streetaddresszipcode", "streetzipcode"];
    private static readonly string[] RuralKeys = ["rural", "isrural"];

    /// <summary>"Medicare Provide Number" → "medicareprovidenumber".</summary>
    public static string Key(string name)
    {
        var key = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                key.Append(char.ToLowerInvariant(ch));
            }
        }

        return key.ToString();
    }

    /// <summary>
    /// The covered entities of a .zip (the JSON export as OPAIS downloads it: its largest .json entry), .json or .xlsx/.xls export,
    /// one per 340B ID (a repeated ID adds its NPIs to the first).
    /// </summary>
    public static async Task<IReadOnlyList<Entity340BRow>> ReadAsync(string path, CancellationToken ct)
    {
        var file = Path.GetFileName(path);
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".zip":
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                var entry = zip.Entries.Where(e => e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).MaxBy(e => e.Length)
                    ?? throw new InvalidDataException($"340B export {file}: the zip holds no .json file.");
                await using var json = entry.Open();
                return OnePerId(await ReadJsonAsync(json, $"{file} ({entry.FullName})", ct));
            }

            case ".json":
            {
                await using var json = File.OpenRead(path);
                return OnePerId(await ReadJsonAsync(json, file, ct));
            }

            case ".xlsx" or ".xls":
                return OnePerId(ReadExcel(path));
            default:
                throw new InvalidDataException($"340B export {file}: {Path.GetExtension(path)} files aren't read; save the JSON export (.zip) or the Excel report.");
        }
    }

    static Opais340B() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// The "Covered Entities" worksheet: the sheet whose header row (the first with a "340B ID" cell) also has "Entity Type" and an
    /// NPI or Medicare number column; the shipping address and contract pharmacy sheets are skipped.
    /// </summary>
    public static IEnumerable<Entity340BRow> ReadExcel(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var found = false;
        do
        {
            Dictionary<string, int>? columns = null;
            while (reader.Read())
            {
                if (columns is null)
                {
                    var names = Enumerable.Range(0, reader.FieldCount).Select(i => Key(Cell(reader.GetValue(i)) ?? "")).ToList();
                    if (names.Contains("340bid") && names.Contains("entitytype") && (names.Contains("npi") || names.Any(MedicareKeys.Contains)))
                    {
                        columns = new Dictionary<string, int>(StringComparer.Ordinal);
                        for (var i = 0; i < names.Count; i++)
                        {
                            if (names[i].Length > 0)
                            {
                                columns.TryAdd(names[i], i);
                            }
                        }
                    }

                    continue;
                }

                found = true;
                var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var (name, i) in columns)
                {
                    values[name] = i < reader.FieldCount ? Cell(reader.GetValue(i)) : null;
                }

                if (ToRow(values, [.. Npis(values.GetValueOrDefault("npi"))]) is { } row)
                {
                    yield return row;
                }
            }

            if (found)
            {
                yield break;
            }
        }
        while (reader.NextResult());

        throw new InvalidDataException($"340B export {Path.GetFileName(path)}: no worksheet with the columns 340B ID, Entity Type and NPI (the Covered Entities sheet of the Covered Entity Daily Report).");
    }

    // The JSON export's keys (as in OPA_CE_DAILY_PUBLIC.JSON, October 2026), compared letters and digits only, case aside, each
    // mapped to the worksheet column it stands for; a second spelling is accepted where HRSA's names and the worksheet's differ.
    private static readonly (string Column, string[] Keys)[] JsonFields =
    [
        ("340bid", ["id340b"]), ("ceid", ["ceid"]), ("parentid340b", ["parentid340b"]), ("entitytype", ["entitytype"]),
        ("participating", ["participating"]), ("participatingstartdate", ["participatingstartdate"]), ("terminationdate", ["terminationdate"]),
        ("terminationreason", ["terminationreason"]), ("entityname", ["name", "entityname"]), ("entitysubdivisionname", ["subname", "entitysubdivisionname"]),
        ("grantnumber", ["grantnumber"]), ("siteid", ["siteid"]),
        ("medicareprovidenumber", ["cmscertificationnumber", "medicareprovidernumber", "medicareprovidenumber"]), ("rural", ["rural"]),
    ];

    private static readonly (string Column, string Key)[] StreetFields =
        [("streetaddress1", "addressline1"), ("streetcity", "city"), ("streetstate", "state"), ("streetzip", "zip")];

    /// <summary>
    /// The JSON export, streamed one entity at a time (the October 2026 file is 275 MB): <c>{"coveredEntities":[{…}, …]}</c>, each
    /// entity with id340B, ceId, parentId340B (a child site's registration), entityType, name, subName, participating ("TRUE" /
    /// "FALSE"), participatingStartDate and terminationDate ("2019-01-01T00:00:00"), terminationReason, grantNumber, siteId
    /// (BPS-… for health center sites, "Admin" for a grantee's office), cmsCertificationNumber, streetAddress {addressLine1, city,
    /// state, zip}, npiNumbers [{npiNumber, state}], rural, and the billing and shipping addresses, Medicaid numbers, contacts and
    /// contract pharmacies, which are not read. A key that is absent is empty.
    /// </summary>
    public static async Task<List<Entity340BRow>> ReadJsonAsync(Stream json, string file, CancellationToken ct)
    {
        var rows = new List<Entity340BRow>();
        try
        {
            await foreach (var entity in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(new JsonArrayStream(json, "coveredEntities"), cancellationToken: ct))
            {
                if (entity.ValueKind == JsonValueKind.Object && FromJson(entity) is { } row)
                {
                    rows.Add(row);
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"340B export {file}: not the Covered Entity Daily Export JSON ({ex.Message}).", ex);
        }

        return rows;
    }

    /// <summary>One entity of the JSON export as the worksheet's columns.</summary>
    public static Entity340BRow? FromJson(JsonElement entity)
    {
        var properties = Properties(entity);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (column, keys) in JsonFields)
        {
            values[column] = keys.Select(k => properties.TryGetValue(k, out var v) ? Text(v) : null).FirstOrDefault(v => v is not null);
        }

        if (properties.TryGetValue("streetaddress", out var street) && street.ValueKind == JsonValueKind.Object)
        {
            var address = Properties(street);
            foreach (var (column, key) in StreetFields)
            {
                values[column] = address.TryGetValue(key, out var v) ? Text(v) : null;
            }
        }

        var npis = new List<string>();
        if (properties.TryGetValue("npinumbers", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var value = item.ValueKind == JsonValueKind.Object ? Properties(item).GetValueOrDefault("npinumber") : item;
                npis.AddRange(Npis(Text(value)));
            }
        }

        return ToRow(values, [.. npis.Distinct(StringComparer.Ordinal)]);
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement element)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            properties.TryAdd(Key(property.Name), property.Value);
        }

        return properties;
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static Entity340BRow? ToRow(Dictionary<string, string?> values, IReadOnlyList<string> npis)
    {
        string? First(string[] keys) => keys.Select(k => values.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

        var id = First(Id)?.ToUpperInvariant();
        var type = First(TypeKeys)?.ToUpperInvariant();
        var name = First(NameKeys);
        if (id is null || type is null || name is null)
        {
            return null;
        }

        var zip = First(ZipKeys);
        if (zip is { Length: 4 } && zip.All(char.IsAsciiDigit))
        {
            zip = "0" + zip; // 55 entities in October 2026 (New England ZIPs) lost the leading zero
        }

        var state = First(StateKeys)?.ToUpperInvariant();
        // "Admin" marks a grantee's office, not an HRSA site.
        var site = First(SiteKeys)?.ToUpperInvariant() is { } s && s != "ADMIN" ? s : null;
        return new Entity340BRow(Clip(id, 20)!, int.TryParse(First(CeIdKeys), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ceId) ? ceId : null,
            Clip(type, 10)!, Truthy(First(ParticipatingKeys)) == true, Date(First(StartKeys)), Date(First(EndKeys)), Clip(First(ReasonKeys), 200),
            Clip(name, 200)!, Clip(First(SubKeys), 255), Clip(First(GrantKeys), 30), Clip(site, 20), Medicare(First(MedicareKeys)),
            Clip(First(Address1Keys), 255), Clip(First(CityKeys), 100), state is { Length: 2 } ? state : null,
            zip is { Length: >= 5 } && zip[..5].All(char.IsAsciiDigit) ? zip[..5] : null, Truthy(First(RuralKeys)), npis)
        {
            ParentId340B = Clip(First(ParentKeys)?.ToUpperInvariant(), 20),
        };
    }

    /// <summary>The Medicare provider number as a CCN: six characters (five digits padded); anything else is no CCN.</summary>
    public static string? Medicare(string? value)
    {
        var text = value?.Trim().ToUpperInvariant();
        return text is { Length: 5 } && text.All(char.IsAsciiDigit) ? "0" + text
            : text is { Length: 6 } && text.All(char.IsAsciiLetterOrDigit) ? text
            : null;
    }

    /// <summary>Every NPI in a value: 10 digits on their own with a valid check digit (so a phone number is not one).</summary>
    public static IEnumerable<string> Npis(string? value) =>
        value is null ? [] : NpiPattern().Matches(value).Select(m => m.Value).Where(InputFormats.HasValidCheckDigit);

    public static bool? Truthy(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "TRUE" or "YES" or "Y" or "1" => true,
        "FALSE" or "NO" or "N" or "0" => false,
        _ => null,
    };

    public static DateTime? Date(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // An ISO date with a time ("2012-01-01T00:00:00Z") is the day as written: no time zone is applied.
        if (text.Length > 10 && text[4] == '-' && text[7] == '-' && text[10] is 'T' or ' ')
        {
            text = text[..10];
        }

        string[] formats = ["M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "M/d/yyyy h:mm:ss tt", "M/d/yyyy H:mm:ss"];
        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date) && date.Year is > 1900 and < 2200
            ? date.Date
            : null;
    }

    // An Excel cell as text: dates as yyyy-MM-dd, whole numbers without decimals (CE ID 12345, an NPI as a number).
    private static string? Cell(object? value) => value switch
    {
        null => null,
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        double n when n == Math.Floor(n) && Math.Abs(n) < 1e15 => ((long)n).ToString(CultureInfo.InvariantCulture),
        double n => n.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => value.ToString() is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null,
    };

    private static List<Entity340BRow> OnePerId(IEnumerable<Entity340BRow> rows)
    {
        var byId = new Dictionary<string, Entity340BRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            byId[row.Id340B] = byId.TryGetValue(row.Id340B, out var first)
                ? first with { Npis = [.. first.Npis.Union(row.Npis, StringComparer.Ordinal)] }
                : row;
        }

        return [.. byId.Values];
    }

    private static string? Clip(string? value, int length) => value is null || value.Length <= length ? value : value[..length];

    [GeneratedRegex("(?<![0-9])[0-9]{10}(?![0-9])")]
    private static partial Regex NpiPattern();
}

/// <summary>
/// HRSA 340B covered entities (source hrsa_340b) from the export the owner saves in <see cref="LoaderOptions.Hrsa340bFolder"/>: the
/// newest .zip (the JSON export as OPAIS downloads it), .json, .xlsx or .xls file there, whatever its kind (version = its name, size and time); none → version "no files", skipped with a warning.
/// Replaces <c>hrsa_340b_entity</c> and <c>hrsa_340b_npi</c> together (staging + RENAME, the usual row check).
/// </summary>
public sealed class Program340BSource : DatasetSource
{
    public const string NoFiles = "no files";

    public override string Name => "hrsa_340b";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(1);

    /// <summary>The newest export in the folder (by its time; any accepted kind), or null.</summary>
    public static FileInfo? Choose(string folder) =>
        !Directory.Exists(folder) ? null
        : new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => f.Extension.ToLowerInvariant() is ".zip" or ".json" or ".xlsx" or ".xls" && !f.Name.StartsWith("~$", StringComparison.Ordinal))
            .OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    public override Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var page = new Uri(context.Options.Hrsa340bPageUrl);
        if (Choose(context.Options.ResolvedHrsa340bFolder) is not { } file)
        {
            return Task.FromResult(new DatasetRelease(NoFiles, page));
        }

        var stamp = $"{file.Name}|{file.Length}|{file.LastWriteTimeUtc:O}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))[..12];
        return Task.FromResult(new DatasetRelease($"{file.Name} {file.LastWriteTimeUtc:yyyy-MM-dd} {hash}", page, file.LastWriteTimeUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var folder = context.Options.ResolvedHrsa340bFolder;
        if (Choose(folder) is not { } file)
        {
            context.Log.Warning("340B covered entities skipped: no export (.zip, .json or .xlsx) in {Folder}; download the Covered Entity Daily Export (JSON) from {Page}",
                folder, context.Options.Hrsa340bPageUrl);
            return 0;
        }

        var entities = await Opais340B.ReadAsync(file.FullName, ct);
        if (entities.Count == 0)
        {
            throw new InvalidDataException($"340B export {file.Name}: no covered entity with a 340B ID, entity type and name was read.");
        }

        context.Log.Information("340B {File}: {Entities:N0} covered entities ({Participating:N0} participating), {Ccn:N0} with a Medicare number, {Npi:N0} with an NPI",
            file.Name, entities.Count, entities.Count(e => e.Participating), entities.Count(e => e.Ccn is not null), entities.Count(e => e.Npis.Count > 0));
        await using var connection = await context.Database.OpenAsync(ct);
        var counts = await TableSwap.ReplaceAsync(connection, ["hrsa_340b_entity", "hrsa_340b_npi"], context.Options.MinRowRatio, async () =>
        {
            await ReferenceTables.CopyToStagingAsync(connection, "hrsa_340b_entity", ReferenceTables.ToDataTable(entities,
                ("id_340b", typeof(string), e => e.Id340B), ("ce_id", typeof(int), e => e.CeId), ("entity_type", typeof(string), e => e.EntityType),
                ("participating", typeof(sbyte), e => (sbyte)(e.Participating ? 1 : 0)), ("start_date", typeof(DateTime), e => e.StartDate),
                ("termination_date", typeof(DateTime), e => e.TerminationDate), ("termination_reason", typeof(string), e => e.TerminationReason),
                ("name", typeof(string), e => e.Name), ("sub_name", typeof(string), e => e.SubName), ("grant_number", typeof(string), e => e.GrantNumber),
                ("site_id", typeof(string), e => e.SiteId), ("ccn", typeof(string), e => e.Ccn), ("address", typeof(string), e => e.Address),
                ("city", typeof(string), e => e.City), ("state", typeof(string), e => e.State), ("zip5", typeof(string), e => e.Zip5),
                ("rural", typeof(sbyte), e => e.Rural is { } r ? (sbyte)(r ? 1 : 0) : null), ("parent_id_340b", typeof(string), e => e.ParentId340B)), ct);
            await ReferenceTables.CopyToStagingAsync(connection, "hrsa_340b_npi", ReferenceTables.ToDataTable(
                entities.SelectMany(e => e.Npis.Select(n => (e.Id340B, Npi: n))).ToList(),
                ("id_340b", typeof(string), n => n.Id340B), ("npi", typeof(string), n => n.Npi)), ct);
        }, ct);
        context.Log.Information("340B: {Entities:N0} covered entities, {Npis:N0} NPIs", counts["hrsa_340b_entity"], counts["hrsa_340b_npi"]);
        return counts["hrsa_340b_entity"] + counts["hrsa_340b_npi"];
    }
}
