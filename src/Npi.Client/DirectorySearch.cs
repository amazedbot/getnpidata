using System.Globalization;
using System.Text;

namespace Npi.Client;

/// <summary>
/// Filters for <see cref="NpiClient.SearchFacilitiesAsync"/>: one property per /api/v1/facilities parameter. Everything is
/// optional; with nothing set, every facility certified now is listed by name.
/// </summary>
public sealed class FacilitySearch
{
    /// <summary>A CCN, a ZIP, or words of the name or city (every word must appear).</summary>
    public string? Q { get; set; }

    /// <summary>hospital, nursing_home, home_health, hospice, fqhc, rhc, dialysis, asc or other.</summary>
    public string? Type { get; set; }

    /// <summary>Two-letter state code.</summary>
    public string? State { get; set; }

    /// <summary>1–5: rated at least this many stars (the type's CMS rating; halves count down).</summary>
    public int? MinRating { get; set; }

    /// <summary>1–5: rated at most this many stars.</summary>
    public int? MaxRating { get; set; }

    /// <summary>active (default), closed or all.</summary>
    public string? Status { get; set; }

    /// <summary>1–10: facilities whose ownership changed in that many years (CMS's hospital and nursing home change-of-ownership files).</summary>
    public int? OwnershipChangedWithinYears { get; set; }

    /// <summary>True: nursing homes fined or denied payment in the last three years.</summary>
    public bool? HasPenalties { get; set; }

    /// <summary>
    /// name, city, state, type, rating, closed, ownershipChange or fines; "-" first for descending ("-rating" = best first,
    /// "-fines" = the largest fines first).
    /// </summary>
    public string? Sort { get; set; }

    /// <summary>An AHRQ health system ID (e.g. HSI00000010): only its facilities.</summary>
    public string? System { get; set; }

    /// <summary>true: only participating 340B covered entities (HRSA OPAIS); false: only facilities that aren't one.</summary>
    public bool? In340B { get; set; }

    public int? Page { get; set; }

    /// <summary>1–200 (default 50).</summary>
    public int? PageSize { get; set; }

    /// <summary>"?type=hospital&amp;state=NY…" with only the properties that are set (empty when none is).</summary>
    public string ToQueryString()
    {
        var q = new QueryBuilder();
        q.Add("q", Q);
        q.Add("type", Type);
        q.Add("state", State);
        q.Add("minRating", MinRating);
        q.Add("maxRating", MaxRating);
        q.Add("status", Status);
        q.Add("ownershipChangedWithinYears", OwnershipChangedWithinYears);
        q.Add("hasPenalties", HasPenalties);
        q.Add("sort", Sort);
        q.Add("system", System);
        q.Add("in340b", In340B is { } in340b ? (in340b ? "true" : "false") : null);
        q.Add("page", Page);
        q.Add("pageSize", PageSize);
        return q.ToString();
    }
}

/// <summary>
/// Filters for <see cref="NpiClient.SearchGroupsAsync"/>: one property per /api/v1/groups parameter. With nothing set, every Medicare
/// group with an active clinician is listed, largest first.
/// </summary>
public sealed class GroupSearch
{
    /// <summary>A PAC ID, an NPI (the groups it is enrolled as or bills through) or words of the name.</summary>
    public string? Q { get; set; }

    /// <summary>Two-letter state code.</summary>
    public string? State { get; set; }

    public int? MinClinicians { get; set; }

    public int? MaxClinicians { get; set; }

    /// <summary>clinicians, name, state, mips or organizations; "-" first for descending (default "-clinicians").</summary>
    public string? Sort { get; set; }

    public int? Page { get; set; }

    /// <summary>1–200 (default 50).</summary>
    public int? PageSize { get; set; }

    /// <summary>"?state=NY&amp;minClinicians=50…" with only the properties that are set (empty when none is).</summary>
    public string ToQueryString()
    {
        var q = new QueryBuilder();
        q.Add("q", Q);
        q.Add("state", State);
        q.Add("minClinicians", MinClinicians);
        q.Add("maxClinicians", MaxClinicians);
        q.Add("sort", Sort);
        q.Add("page", Page);
        q.Add("pageSize", PageSize);
        return q.ToString();
    }
}

/// <summary>
/// Filters for <see cref="NpiClient.SearchSystemsAsync"/>: one property per /api/v1/systems parameter. With nothing set, every health
/// system is listed, most hospitals first.
/// </summary>
public sealed class SystemSearch
{
    /// <summary>An AHRQ system ID or words of the name or headquarters city.</summary>
    public string? Q { get; set; }

    /// <summary>Two-letter state code of the headquarters.</summary>
    public string? State { get; set; }

    /// <summary>name, hospitals, physicians or beds; "-" first for descending (default "-hospitals").</summary>
    public string? Sort { get; set; }

    public int? Page { get; set; }

    /// <summary>1–200 (default 50).</summary>
    public int? PageSize { get; set; }

    public string ToQueryString()
    {
        var q = new QueryBuilder();
        q.Add("q", Q);
        q.Add("state", State);
        q.Add("sort", Sort);
        q.Add("page", Page);
        q.Add("pageSize", PageSize);
        return q.ToString();
    }
}

/// <summary>Filters for <see cref="NpiClient.SearchAcosAsync"/>: one property per /api/v1/acos parameter. With nothing set, every ACO by name.</summary>
public sealed class AcoSearch
{
    /// <summary>An ACO ID, words of its name, or part of a participant's name.</summary>
    public string? Q { get; set; }

    /// <summary>Two-letter state code in the ACO's service area.</summary>
    public string? State { get; set; }

    public int? Page { get; set; }

    /// <summary>1–200 (default 50).</summary>
    public int? PageSize { get; set; }

    public string ToQueryString()
    {
        var q = new QueryBuilder();
        q.Add("q", Q);
        q.Add("state", State);
        q.Add("page", Page);
        q.Add("pageSize", PageSize);
        return q.ToString();
    }
}

internal sealed class QueryBuilder
{
    private readonly StringBuilder _sb = new();

    public void Add(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _sb.Append(_sb.Length == 0 ? '?' : '&').Append(name).Append('=').Append(Uri.EscapeDataString(value!.Trim()));
    }

    public void Add(string name, int? value) => Add(name, value?.ToString(CultureInfo.InvariantCulture));

    public void Add(string name, bool? value) => Add(name, value is { } b ? (b ? "true" : "false") : null);

    public override string ToString() => _sb.ToString();
}
