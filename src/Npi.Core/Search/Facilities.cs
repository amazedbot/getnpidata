using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>
/// A kind of Medicare-certified facility on the Facilities &amp; hospitals page (docs/PRODUCT_PLAN.md step 3.6). <see cref="RatingName"/>
/// is the CMS star rating the list shows for it (null: CMS rates none of them); <see cref="Category"/> and
/// <see cref="Subcategory"/> are the plain specialty its NPIs are listed under, for the map and the search list.
/// </summary>
public sealed record FacilityType(string Code, string Plural, string Singular, string? RatingName, string? Category, string? Subcategory);

/// <summary>The facility types and how a CCN tells them apart.</summary>
public static class FacilityTypes
{
    public const string Hospital = "hospital";
    public const string NursingHome = "nursing_home";
    public const string HomeHealth = "home_health";
    public const string Hospice = "hospice";
    public const string Fqhc = "fqhc";
    public const string Rhc = "rhc";
    public const string Dialysis = "dialysis";
    public const string SurgeryCenter = "asc";
    public const string Other = "other";

    /// <summary>In the order the page lists them.</summary>
    public static readonly IReadOnlyList<FacilityType> All =
    [
        new(Hospital, "Hospitals", "Hospital", "CMS overall hospital rating", "Hospitals", null),
        new(NursingHome, "Nursing homes", "Nursing home", "CMS overall nursing home rating", "Nursing homes & senior living", "Nursing homes"),
        new(HomeHealth, "Home health agencies", "Home health agency", "Quality of patient care", "Home care & hospice", "Home health agencies"),
        new(Hospice, "Hospices", "Hospice", "Family caregiver survey", "Home care & hospice", "Hospice"),
        new(Fqhc, "Federally qualified health centers", "Federally qualified health center", null, "Clinics & centers", "Primary care & community health centers"),
        new(Rhc, "Rural health clinics", "Rural health clinic", null, "Clinics & centers", "Primary care & community health centers"),
        new(Dialysis, "Dialysis facilities", "Dialysis facility", null, "Clinics & centers", "Dialysis centers"),
        new(SurgeryCenter, "Ambulatory surgery centers", "Ambulatory surgery center", null, "Clinics & centers", "Surgery centers"),
        new(Other, "Other certified facilities", "Certified facility", null, null, null),
    ];

    public static FacilityType? Find(string? code) =>
        All.FirstOrDefault(t => string.Equals(t.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static FacilityType Get(string code) => Find(code) ?? All[^1];

    /// <summary>
    /// The type and kind a CCN's number range stands for (CMS State Operations Manual §2779A): the state's two digits, then four
    /// digits for the facility (0001–0879 short-term hospitals, 1300–1399 critical access hospitals, 5000–6499 nursing facilities …),
    /// or a letter in the third place (E/F Medicaid nursing facilities; units and swing beds of a hospital). Ten-character numbers
    /// with a C are ambulatory surgery centers.
    /// </summary>
    public static (string Type, string? Kind) FromCcn(string? ccn)
    {
        var id = ccn?.Trim().ToUpperInvariant() ?? "";
        if (id.Length == 10 && id[2] == 'C')
        {
            return (SurgeryCenter, null);
        }

        if (id.Length != 6)
        {
            return (Other, null);
        }

        if (!char.IsAsciiDigit(id[2]))
        {
            return id[2] switch
            {
                'E' or 'F' => (NursingHome, "Medicaid nursing facility"),
                'G' => (Other, "Intermediate care facility (intellectual disabilities)"),
                'M' or 'R' or 'S' or 'T' => (Other, "Hospital unit"),
                'U' or 'W' or 'Y' or 'Z' => (Other, "Hospital swing beds"),
                _ => (Other, null),
            };
        }

        if (!int.TryParse(id.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            return (Other, null);
        }

        return n switch
        {
            >= 1 and <= 899 => (Hospital, "Short-term hospital"),
            >= 1000 and <= 1199 or >= 1800 and <= 1989 => (Fqhc, null),
            >= 1300 and <= 1399 => (Hospital, "Critical access hospital"),
            >= 1400 and <= 1499 or >= 4600 and <= 4799 or >= 4900 and <= 4999 => (Other, "Community mental health center"),
            >= 1500 and <= 1799 => (Hospice, null),
            >= 1990 and <= 1999 => (Other, "Religious nonmedical health care institution"),
            >= 2000 and <= 2299 => (Hospital, "Long-term care hospital"),
            >= 2300 and <= 2999 or >= 3500 and <= 3799 => (Dialysis, null),
            >= 3025 and <= 3099 => (Hospital, "Rehabilitation hospital"),
            >= 3100 and <= 3199 or >= 7000 and <= 8499 or >= 9000 and <= 9799 => (HomeHealth, null),
            >= 3200 and <= 3299 or >= 4500 and <= 4599 or >= 4800 and <= 4899 => (Other, "Comprehensive outpatient rehabilitation facility"),
            >= 3300 and <= 3399 => (Hospital, "Children's hospital"),
            >= 3400 and <= 3499 or >= 3800 and <= 3999 or >= 8500 and <= 8999 => (Rhc, null),
            >= 4000 and <= 4499 => (Hospital, "Psychiatric hospital"),
            >= 5000 and <= 6499 => (NursingHome, null),
            >= 6500 and <= 6989 => (Other, "Outpatient physical therapy or speech pathology"),
            >= 9800 and <= 9899 => (Other, "Transplant center"),
            _ => (Other, null),
        };
    }

    /// <summary>Why a facility's certification ended (the Provider of Services file's termination code), in plain words.</summary>
    public static string? TerminationReason(string? code) => code switch
    {
        "01" => "Closed or merged",
        "02" => "Withdrew (payment)",
        "03" => "Withdrew before an involuntary termination",
        "04" => "Withdrew",
        "05" => "Terminated by CMS (health and safety requirements)",
        "06" => "Terminated by CMS (provider agreement)",
        "07" => "Status change, such as a new owner",
        _ => null,
    };

    /// <summary>"Acute Care Hospitals" → "Acute care hospital", "Critical Access Hospitals" → "Critical access hospital".</summary>
    public static string? HospitalKind(string? hospitalType)
    {
        var text = hospitalType?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.EndsWith("Hospitals", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^1];
        }
        else if (!text.Contains("hospital", StringComparison.OrdinalIgnoreCase))
        {
            text += " hospital";
        }

        return char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();
    }
}

/// <summary>
/// A Medicare-certified facility in the Facilities &amp; hospitals list: a hospital, nursing home, home health agency or hospice
/// listed in Care Compare, or any facility in CMS's Provider of Services file (active or closed). <see cref="Rating"/> is the
/// type's CMS star rating (1–5; home health in halves). <see cref="Active"/> false: its certification ended on
/// <see cref="TerminationDate"/>. <see cref="Latitude"/>/<see cref="Longitude"/> are the centre of its ZIP code (approximate).
/// <see cref="BillingNpis"/> counts the active NPIs CMS's enrollment files link to it.
/// </summary>
public sealed record FacilitySummary(string Ccn, string Type, string TypeName, string? Kind, string Name, string? Address, string? City, string? State,
    string? Zip, string? Phone, string? Ownership, int? Beds, double? Rating, bool Active, DateOnly? TerminationDate, string? TerminationReason,
    int BillingNpis, double? Latitude, double? Longitude);

/// <summary>A page of facilities.</summary>
public sealed record FacilityPage(IReadOnlyList<FacilitySummary> Items, int Page, int PageSize, int TotalCount);

/// <summary>How many facilities of a type are certified now and how many have closed.</summary>
public sealed record FacilityTypeCount(string Type, string Name, int Active, int Closed);

/// <summary>How the active facilities of a rated type are rated: <see cref="ByStars"/>[0] = 1 star … [4] = 5 stars (halves round down).</summary>
public sealed record FacilityRatingCounts(string Type, string Name, string RatingName, IReadOnlyList<int> ByStars, int NotRated);

/// <summary>The totals on the Facilities &amp; hospitals page.</summary>
public sealed record FacilityOverview(int Active, int Closed, int States, IReadOnlyList<FacilityTypeCount> Types, IReadOnlyList<FacilityRatingCounts> Ratings);

/// <summary>
/// The facility list's filters, as the page and /api/v1/facilities take them. <see cref="Q"/>: a CCN, a ZIP, or words of the name
/// or city (every word must appear). <see cref="MinRating"/>/<see cref="MaxRating"/> (1–5) keep rated facilities only, a star
/// level counting halves down (maxRating=1 keeps 1 and 1.5 stars). <see cref="Status"/>: active (default), closed or all.
/// </summary>
public sealed record FacilityFilter
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxQueryLength = 100;
    public const string StatusActive = "active";
    public const string StatusClosed = "closed";
    public const string StatusAll = "all";

    public static readonly IReadOnlyList<string> Statuses = [StatusActive, StatusClosed, StatusAll];

    /// <summary>Sort keys; "-" first is descending. "rating" sorts the unrated last either way; "closed" is the termination date.</summary>
    public static readonly IReadOnlyList<string> Sorts = ["name", "city", "state", "type", "rating", "closed"];

    public string? Q { get; init; }

    public string? Type { get; init; }

    public string? State { get; init; }

    public int? MinRating { get; init; }

    public int? MaxRating { get; init; }

    public string? Status { get; init; }

    public string? Sort { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>Errors by parameter name; empty when the filter is valid.</summary>
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (Q?.Trim().Length > MaxQueryLength)
        {
            errors["q"] = [$"At most {MaxQueryLength} characters."];
        }

        if (!string.IsNullOrWhiteSpace(Type) && FacilityTypes.Find(Type) is null)
        {
            errors["type"] = [$"One of {string.Join(", ", FacilityTypes.All.Select(t => t.Code))}."];
        }

        if (!string.IsNullOrWhiteSpace(State) && !(State.Trim() is { Length: 2 } state && state.All(char.IsAsciiLetter)))
        {
            errors["state"] = ["A two-letter state code."];
        }

        if (MinRating is < 1 or > 5)
        {
            errors["minRating"] = ["1–5 stars."];
        }

        if (MaxRating is < 1 or > 5)
        {
            errors["maxRating"] = ["1–5 stars."];
        }
        else if (MinRating is { } min && MaxRating is { } max && max < min)
        {
            errors["maxRating"] = ["At least minRating."];
        }

        if (!string.IsNullOrWhiteSpace(Status) && !Statuses.Contains(Status.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            errors["status"] = [$"One of {string.Join(", ", Statuses)}."];
        }

        if (!string.IsNullOrWhiteSpace(Sort) && !Sorts.Contains(Sort.Trim().TrimStart('-'), StringComparer.OrdinalIgnoreCase))
        {
            errors["sort"] = [$"One of {string.Join(", ", Sorts)}, with \"-\" first for descending."];
        }

        if (Page < 1)
        {
            errors["page"] = ["Must be 1 or more."];
        }

        if (PageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"Must be 1–{MaxPageSize}."];
        }

        return errors;
    }
}

/// <summary>
/// Every Medicare-certified facility (docs/PRODUCT_PLAN.md step 3.6): the Provider of Services file (every CCN, active or closed)
/// joined with the Care Compare listings of hospitals, nursing homes, home health agencies and hospices (names, ratings,
/// ownership, beds), kept in memory (~130,000 rows) until the data changes, so filters, sorts and pages need no query.
/// </summary>
public sealed class FacilityDirectory : IDisposable
{
    private readonly SnapshotCache<Snapshot> _cache;

    public FacilityDirectory(string connectionString)
    {
        _cache = new SnapshotCache<Snapshot>(ct => DataStamp.ReadAsync(connectionString, ct), ct => LoadAsync(connectionString, ct));
    }

    /// <summary>For tests: a directory over rows already built.</summary>
    internal FacilityDirectory(IReadOnlyList<FacilitySummary> rows)
    {
        var snapshot = new Snapshot(rows);
        _cache = new SnapshotCache<Snapshot>(_ => Task.FromResult(""), _ => Task.FromResult(snapshot));
    }

    public void Dispose() => _cache.Dispose();

    /// <summary>Fills the cache (the site calls it once after starting, so the first visitor doesn't wait for it).</summary>
    public Task WarmAsync(CancellationToken ct) => _cache.GetAsync(ct);

    public async Task<FacilityOverview> GetOverviewAsync(CancellationToken ct) => (await _cache.GetAsync(ct)).Overview;

    /// <summary>One page of the facilities matching a valid filter (<see cref="FacilityFilter.Validate"/>).</summary>
    public async Task<FacilityPage> SearchAsync(FacilityFilter filter, CancellationToken ct)
    {
        Check(filter);
        var matches = (await _cache.GetAsync(ct)).Match(filter).ToList();
        var items = Sorted(matches.Select(e => e.Item), filter.Sort).Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToList();
        return new FacilityPage(items, filter.Page, filter.PageSize, matches.Count);
    }

    /// <summary>How many facilities of each type match the filter's other conditions (the type chips above the list).</summary>
    public async Task<IReadOnlyList<FacilityTypeCount>> CountByTypeAsync(FacilityFilter filter, CancellationToken ct)
    {
        var matches = (await _cache.GetAsync(ct)).Match(filter with { Type = null }).Select(e => e.Item).ToList();
        return FacilityTypes.All.Select(t => new FacilityTypeCount(t.Code, t.Plural, matches.Count(r => r.Type == t.Code && r.Active),
                matches.Count(r => r.Type == t.Code && !r.Active)))
            .ToList();
    }

    /// <summary>Every facility matching the filter in its sort order (paging ignored), for the CSV.</summary>
    public async IAsyncEnumerable<FacilitySummary> AllAsync(FacilityFilter filter, [EnumeratorCancellation] CancellationToken ct)
    {
        Check(filter with { Page = 1, PageSize = FacilityFilter.DefaultPageSize });
        var snapshot = await _cache.GetAsync(ct);
        foreach (var item in Sorted(snapshot.Match(filter).Select(e => e.Item), filter.Sort))
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private static void Check(FacilityFilter filter)
    {
        if (filter.Validate() is { Count: > 0 } errors)
        {
            throw new ArgumentException(string.Join(" ", errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}")), nameof(filter));
        }
    }

    private static IEnumerable<FacilitySummary> Sorted(IEnumerable<FacilitySummary> rows, string? sort)
    {
        var key = sort?.Trim() ?? "";
        var descending = key.StartsWith('-');
        key = key.TrimStart('-').ToLowerInvariant();
        var byName = StringComparer.OrdinalIgnoreCase;
        IOrderedEnumerable<FacilitySummary> ordered = key switch
        {
            "city" => descending ? rows.OrderByDescending(r => r.City ?? "", byName) : rows.OrderBy(r => r.City ?? "", byName),
            "state" => descending ? rows.OrderByDescending(r => r.State ?? "", byName) : rows.OrderBy(r => r.State ?? "", byName),
            "type" => descending
                ? rows.OrderByDescending(r => TypeOrder(r.Type))
                : rows.OrderBy(r => TypeOrder(r.Type)),
            // Unrated last both ways; "-rating" is best first.
            "rating" => descending
                ? rows.OrderBy(r => r.Rating is null).ThenByDescending(r => r.Rating)
                : rows.OrderBy(r => r.Rating is null).ThenBy(r => r.Rating),
            "closed" => descending
                ? rows.OrderBy(r => r.TerminationDate is null).ThenByDescending(r => r.TerminationDate)
                : rows.OrderBy(r => r.TerminationDate is null).ThenBy(r => r.TerminationDate),
            _ => descending ? rows.OrderByDescending(r => r.Name, byName) : rows.OrderBy(r => r.Name, byName),
        };
        return key is "name" or "" ? ordered.ThenBy(r => r.Ccn, StringComparer.Ordinal) : ordered.ThenBy(r => r.Name, byName).ThenBy(r => r.Ccn, StringComparer.Ordinal);
    }

    private static int TypeOrder(string type)
    {
        for (var i = 0; i < FacilityTypes.All.Count; i++)
        {
            if (FacilityTypes.All[i].Code == type)
            {
                return i;
            }
        }

        return FacilityTypes.All.Count;
    }

    /// <summary>Upper-case letters and digits, words separated by one space; apostrophes dropped ("CHILDREN'S" = "childrens").</summary>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
            else if (c is '\'' or '’' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                // dropped
            }
            else if (sb.Length > 0 && sb[^1] != ' ')
            {
                sb.Append(' ');
            }
        }

        return sb.ToString().TrimEnd();
    }

    private sealed record Entry(FacilitySummary Item, string Key);

    private sealed class Snapshot
    {
        private readonly IReadOnlyList<Entry> _entries;
        private readonly Dictionary<string, Entry> _byCcn;

        public Snapshot(IReadOnlyList<FacilitySummary> rows)
        {
            _entries = rows.Select(r => new Entry(r, " " + Normalize(r.Name) + " | " + Normalize(r.City) + " ")).ToList();
            _byCcn = _entries.GroupBy(e => e.Item.Ccn, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            Overview = BuildOverview(rows);
        }

        public FacilityOverview Overview { get; }

        public IEnumerable<Entry> Match(FacilityFilter filter)
        {
            var q = filter.Q?.Trim() ?? "";
            IEnumerable<Entry> rows = _entries;
            if (q.Length > 0)
            {
                if (_byCcn.TryGetValue(q, out var exact))
                {
                    rows = [exact];
                }
                else if (InputFormats.IsZip5(q))
                {
                    rows = rows.Where(e => e.Item.Zip == q);
                }
                else
                {
                    var words = Normalize(q).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    rows = rows.Where(e => words.All(w => e.Key.Contains(w, StringComparison.Ordinal)));
                }
            }

            var type = FacilityTypes.Find(filter.Type)?.Code;
            var state = string.IsNullOrWhiteSpace(filter.State) ? null : filter.State.Trim().ToUpperInvariant();
            var status = (filter.Status?.Trim() ?? FacilityFilter.StatusActive).ToLowerInvariant();
            return rows.Where(e =>
                (type is null || e.Item.Type == type)
                && (state is null || e.Item.State == state)
                && (status switch { FacilityFilter.StatusClosed => !e.Item.Active, FacilityFilter.StatusAll => true, _ => e.Item.Active })
                && (filter.MinRating is not { } min || e.Item.Rating >= min)
                && (filter.MaxRating is not { } max || e.Item.Rating < max + 1));
        }

        private static FacilityOverview BuildOverview(IReadOnlyList<FacilitySummary> rows)
        {
            var types = FacilityTypes.All.Select(t => new FacilityTypeCount(t.Code, t.Plural, rows.Count(r => r.Type == t.Code && r.Active),
                    rows.Count(r => r.Type == t.Code && !r.Active)))
                .ToList();
            var ratings = FacilityTypes.All.Where(t => t.RatingName is not null)
                .Select(t =>
                {
                    var active = rows.Where(r => r.Type == t.Code && r.Active).ToList();
                    var stars = new int[5];
                    foreach (var r in active)
                    {
                        if (r.Rating is { } rating)
                        {
                            stars[Math.Clamp((int)Math.Floor(rating), 1, 5) - 1]++;
                        }
                    }

                    return new FacilityRatingCounts(t.Code, t.Plural, t.RatingName!, stars, active.Count(r => r.Rating is null));
                })
                .ToList();
            return new FacilityOverview(rows.Count(r => r.Active), rows.Count(r => !r.Active),
                rows.Where(r => r.Active && r.State is not null).Select(r => r.State).Distinct().Count(), types, ratings);
        }
    }

    // Types as MySqlConnector returns them (Dapper maps records by constructor).
    private sealed record PosRow(string Ccn, string? Name, string? Address, string? City, string? State, string? Zip5, long Active,
        string? TerminationCode, DateTime? TerminationDate);

    private sealed record CareCompareRow(string Ccn, string Name, string? Address, string? City, string? State, string? Zip, string? Phone,
        string? Kind, string? Ownership, long? Beds, double? Rating);

    private sealed record NpiKindRow(string Ccn, string Kind, long Npis);

    private sealed record CentroidRow(string Zip5, double Lat, double Lon);

    private static async Task<Snapshot> LoadAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        const int timeout = 300;
        var pos = await connection.QueryAsync<PosRow>(new CommandDefinition(
            """
            SELECT ccn AS Ccn, name AS Name, address AS Address, city AS City, state AS State, zip5 AS Zip5, CAST(active AS SIGNED) AS Active,
                   termination_code AS TerminationCode, termination_date AS TerminationDate
            FROM cms_pos_facility
            """, commandTimeout: timeout, cancellationToken: ct));

        // The Care Compare listings, each with the type it stands for: their names, ratings, ownership and beds win.
        var listed = new List<(string Type, CareCompareRow Row)>();
        foreach (var (type, sql) in new[]
                 {
                     (FacilityTypes.Hospital, "SELECT ccn AS Ccn, name AS Name, address AS Address, city AS City, state AS State, zip AS Zip, phone AS Phone, hospital_type AS Kind, ownership AS Ownership, CAST(NULL AS SIGNED) AS Beds, CAST(overall_rating AS DOUBLE) AS Rating FROM cms_hospital"),
                     (FacilityTypes.NursingHome, "SELECT ccn AS Ccn, name AS Name, address AS Address, city AS City, state AS State, zip AS Zip, phone AS Phone, CAST(NULL AS CHAR) AS Kind, ownership AS Ownership, CAST(certified_beds AS SIGNED) AS Beds, CAST(overall_rating AS DOUBLE) AS Rating FROM cms_nursing_home"),
                     (FacilityTypes.HomeHealth, "SELECT ccn AS Ccn, name AS Name, address AS Address, city AS City, state AS State, zip AS Zip, phone AS Phone, CAST(NULL AS CHAR) AS Kind, ownership AS Ownership, CAST(NULL AS SIGNED) AS Beds, quality_rating AS Rating FROM cms_home_health"),
                     (FacilityTypes.Hospice, "SELECT ccn AS Ccn, name AS Name, address AS Address, city AS City, state AS State, zip AS Zip, phone AS Phone, CAST(NULL AS CHAR) AS Kind, ownership AS Ownership, CAST(NULL AS SIGNED) AS Beds, CAST(family_rating AS DOUBLE) AS Rating FROM cms_hospice"),
                 })
        {
            listed.AddRange((await connection.QueryAsync<CareCompareRow>(new CommandDefinition(sql, commandTimeout: timeout, cancellationToken: ct)))
                .Select(r => (type, r)));
        }

        // The NPIs CMS's enrollment files link to each CCN (active ones), and the kind of enrollment (fqhc, rhc …).
        var npis = await connection.QueryAsync<NpiKindRow>(new CommandDefinition(
            "SELECT f.ccn AS Ccn, f.kind AS Kind, COUNT(*) AS Npis FROM cms_facility_npi f JOIN provider p ON p.npi = f.npi GROUP BY f.ccn, f.kind",
            commandTimeout: timeout, cancellationToken: ct));
        var centroids = await connection.QueryAsync<CentroidRow>(new CommandDefinition(
            "SELECT zip5 AS Zip5, CAST(lat AS DOUBLE) AS Lat, CAST(lon AS DOUBLE) AS Lon FROM zip_centroid", commandTimeout: timeout, cancellationToken: ct));
        return new Snapshot(Merge(pos.Select(p => new PosFacility(p.Ccn, p.Name, p.Address, p.City, p.State, p.Zip5, p.Active != 0, p.TerminationCode,
                p.TerminationDate is { } d ? DateOnly.FromDateTime(d) : null)),
            listed.Select(l => new ListedFacility(l.Type, l.Row.Ccn, l.Row.Name, l.Row.Address, l.Row.City, l.Row.State, l.Row.Zip, l.Row.Phone,
                l.Row.Kind, l.Row.Ownership, (int?)l.Row.Beds, l.Row.Rating)),
            npis.Select(n => (n.Ccn, n.Kind, (int)n.Npis)),
            centroids.ToDictionary(c => c.Zip5, c => (c.Lat, c.Lon))));
    }

    /// <summary>A row of the Provider of Services file.</summary>
    internal sealed record PosFacility(string Ccn, string? Name, string? Address, string? City, string? State, string? Zip5, bool Active,
        string? TerminationCode, DateOnly? TerminationDate);

    /// <summary>A facility listed in Care Compare (hospitals, nursing homes, home health agencies, hospices).</summary>
    internal sealed record ListedFacility(string Type, string Ccn, string Name, string? Address, string? City, string? State, string? Zip, string? Phone,
        string? Kind, string? Ownership, int? Beds, double? Rating);

    /// <summary>
    /// One row per CCN. The type: the Care Compare listing it is in, else the kind of enrollment linking it to an NPI (FQHC, rural
    /// health clinic …), else its number range (<see cref="FacilityTypes.FromCcn"/>). A Care Compare listing is a facility certified
    /// now; otherwise the Provider of Services file says whether its certification ended. Facilities without a name are left out.
    /// </summary>
    internal static IReadOnlyList<FacilitySummary> Merge(IEnumerable<PosFacility> pos, IEnumerable<ListedFacility> listed,
        IEnumerable<(string Ccn, string Kind, int Npis)> npis, IReadOnlyDictionary<string, (double Lat, double Lon)> centroids)
    {
        var byCcn = new Dictionary<string, PosFacility>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pos)
        {
            byCcn.TryAdd(p.Ccn.Trim(), p);
        }

        var listings = new Dictionary<string, ListedFacility>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in listed)
        {
            listings.TryAdd(l.Ccn.Trim(), l);
        }

        var npiCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var npiKinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ccn, kind, count) in npis)
        {
            npiCounts[ccn] = npiCounts.GetValueOrDefault(ccn) + count;
            if (FacilityTypes.Find(kind) is not null)
            {
                npiKinds.TryAdd(ccn, kind);
            }
        }

        var result = new List<FacilitySummary>(byCcn.Count + listings.Count);
        foreach (var ccn in byCcn.Keys.Union(listings.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var p = byCcn.GetValueOrDefault(ccn);
            var l = listings.GetValueOrDefault(ccn);
            var name = l?.Name ?? p?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var (rangeType, rangeKind) = FacilityTypes.FromCcn(ccn);
            var type = l?.Type ?? (npiKinds.TryGetValue(ccn, out var k) ? k : rangeType);
            var kind = l is { Type: FacilityTypes.Hospital } ? FacilityTypes.HospitalKind(l.Kind) ?? rangeKind : type == rangeType ? rangeKind : null;
            var zip = FirstFive(l?.Zip) ?? FirstFive(p?.Zip5);
            var active = l is not null || p?.Active == true;
            (double Lat, double Lon)? point = zip is not null && centroids.TryGetValue(zip, out var c) ? c : null;
            result.Add(new FacilitySummary(ccn.ToUpperInvariant(), type, FacilityTypes.Get(type).Singular, kind, name.Trim(), l?.Address ?? p?.Address,
                l?.City ?? p?.City, (l?.State ?? p?.State)?.ToUpperInvariant(), zip, l?.Phone, l?.Ownership, l?.Beds,
                l?.Rating is { } r and >= 1 and <= 5 ? r : null, active, active ? null : p?.TerminationDate,
                active ? null : FacilityTypes.TerminationReason(p?.TerminationCode), npiCounts.GetValueOrDefault(ccn), point?.Lat, point?.Lon));
        }

        return result;
    }

    private static string? FirstFive(string? zip) => zip is { Length: >= 5 } && zip[..5].All(char.IsAsciiDigit) ? zip[..5] : null;
}

/// <summary>The facility list as CSV (UTF-8 with BOM, like the other exports), a link to each facility's page.</summary>
public static class FacilityCsv
{
    public static readonly string[] Header =
    [
        "CCN", "Facility", "Type", "Kind", "Address", "City", "State", "ZIP", "Phone", "Ownership", "Certified Beds", "CMS Star Rating", "Status",
        "Certification Ended", "Reason", "Billing NPIs", "Page",
    ];

    public static Task<long> WriteAsync(IAsyncEnumerable<FacilitySummary> rows, Stream output, string siteBase, CancellationToken ct) =>
        IndustryCsv.WriteRowsAsync(rows, output, Header, f =>
        [
            f.Ccn, f.Name, f.TypeName, f.Kind, f.Address, f.City, f.State, f.Zip, f.Phone, f.Ownership, f.Beds?.ToString(CultureInfo.InvariantCulture),
            f.Rating?.ToString("0.#", CultureInfo.InvariantCulture), f.Active ? "Active" : "Closed",
            f.TerminationDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), f.TerminationReason,
            f.BillingNpis.ToString(CultureInfo.InvariantCulture), $"{siteBase}/facility/{Uri.EscapeDataString(f.Ccn)}",
        ], ct);
}
