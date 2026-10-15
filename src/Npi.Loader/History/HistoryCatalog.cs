using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Serilog;

namespace Npi.Loader.History;

/// <summary>Where one archived monthly NPPES file can be downloaded (docs/PRODUCT_PLAN.md step 3.4).</summary>
/// <param name="Period">The first day of the file's month.</param>
/// <param name="Source">"cms" (download.cms.gov) or "nber" (data.nber.org).</param>
public sealed record HistoryFile(DateOnly Period, string FileName, Uri Url, string Source);

/// <summary>
/// Finds the archived monthly NPPES files (step 3.4). CMS still serves every monthly from June 2015 at its usual URL, though
/// NPI_Files.html links only the newest; its names vary ("June_2015", "Sept_2015", "Jan_2016", "August_2016", "_V2" from 2025), so
/// each month's candidate names are tried with HEAD. Before that, the NBER copies: their directory listings are read
/// (<c>npi/&lt;year&gt;/</c>, <c>npi/backfiles/</c>, <c>nppes/zip-orig/</c>), and from 2017 NBER's renamed copies
/// (<c>npi/zip/&lt;year&gt;/npi_raw_YYYYMM.zip</c>, byte-identical to CMS's) stand in when CMS doesn't answer.
/// data.nber.org answers 503/504 about half the time, so every request is retried.
/// </summary>
public sealed partial class HistoryCatalog(HttpClient http, ILogger log, HistoryCatalogOptions? options = null)
{
    private readonly HistoryCatalogOptions _options = options ?? new HistoryCatalogOptions();

    /// <summary>The first month NPPES published a file (November 2007, from NBER).</summary>
    public static readonly DateOnly FirstPeriod = new(2007, 11, 1);

    /// <summary>The first month CMS still serves.</summary>
    public static readonly DateOnly FirstCmsPeriod = new(2015, 6, 1);

    /// <summary>The first month NBER's renamed copies exist (npi/zip/&lt;year&gt;/npi_raw_YYYYMM.zip).</summary>
    public static readonly DateOnly FirstNberRawPeriod = new(2017, 1, 1);

    // The month names CMS and NBER have used: full, three letters, "Sept", and June/July written out.
    private static readonly string[][] MonthNames =
    [
        ["January", "Jan"], ["February", "Feb"], ["March", "Mar"], ["April", "Apr"], ["May"], ["June", "Jun"],
        ["July", "Jul"], ["August", "Aug"], ["September", "Sept", "Sep"], ["October", "Oct"], ["November", "Nov"], ["December", "Dec"],
    ];

    [GeneratedRegex(@"^NPPES_Data_Dissemina(?:tion|t)_(?:(?<month>[A-Za-z]+)_(?<year>\d{4})|(?<year>\d{4})_(?<month>[A-Za-z]+))(?:_V2)?\.zip$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MonthlyName();

    [GeneratedRegex(@"^npi_raw_(?<year>\d{4})(?<month>\d{2})\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex NberRawName();

    [GeneratedRegex(@"href\s*=\s*[""'](?<href>[^""'?#]+\.zip)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ZipHref();

    /// <summary>
    /// The month of a monthly NPPES zip name, any era: "NPPES_Data_Dissemination_Nov_2007.zip", "…_2011_Dec.zip",
    /// "…_Sept_2015.zip", "…_January_2025_V2.zip", NBER's "NPPES_Data_Disseminat_April_2021.zip" and "npi_raw_201701.zip".
    /// Null for anything else (weekly files, deactivation reports, other names).
    /// </summary>
    public static DateOnly? PeriodOf(string fileName)
    {
        var raw = NberRawName().Match(fileName);
        if (raw.Success)
        {
            var y = int.Parse(raw.Groups["year"].Value, CultureInfo.InvariantCulture);
            var m = int.Parse(raw.Groups["month"].Value, CultureInfo.InvariantCulture);
            return m is >= 1 and <= 12 && y is >= 2005 and <= 2100 ? new DateOnly(y, m, 1) : null;
        }

        var match = MonthlyName().Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        var month = MonthNumber(match.Groups["month"].Value);
        var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        return month is { } number && year is >= 2005 and <= 2100 ? new DateOnly(year, number, 1) : null;
    }

    /// <summary>1–12 for a month name in any of the spellings used (case-insensitive), else null.</summary>
    public static int? MonthNumber(string name)
    {
        for (var i = 0; i < MonthNames.Length; i++)
        {
            if (MonthNames[i].Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>The names CMS may have used for a month's file, the most likely first (V2 first from 2025, when both exist).</summary>
    public static IReadOnlyList<string> CmsCandidates(DateOnly period)
    {
        var suffixes = period.Year >= 2025 ? new[] { "_V2", "" } : ["", "_V2"];
        return MonthNames[period.Month - 1]
            .SelectMany(name => suffixes.Select(suffix => string.Create(CultureInfo.InvariantCulture, $"NPPES_Data_Dissemination_{name}_{period.Year}{suffix}.zip")))
            .ToList();
    }

    /// <summary>The .zip links of an Apache directory listing (or any page), with the months they are for.</summary>
    public static IReadOnlyList<(string FileName, DateOnly Period)> ParseListing(string html) =>
        ZipHref().Matches(html)
            .Select(m => Path.GetFileName(WebUtility.UrlDecode(m.Groups["href"].Value)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => (Name: name, Period: PeriodOf(name)))
            .Where(x => x.Period is not null)
            .Select(x => (x.Name, x.Period!.Value))
            .ToList();

    /// <summary>
    /// Every month from <see cref="FirstPeriod"/> to <paramref name="lastPeriod"/> that isn't in <paramref name="skip"/>, with
    /// where its file is; a month with no file (none was ever published or kept) has no entry. A month CMS should have but
    /// doesn't answer for falls back to NBER.
    /// </summary>
    public async Task<IReadOnlyDictionary<DateOnly, HistoryFile>> FindAsync(DateOnly lastPeriod, IReadOnlySet<DateOnly> skip, CancellationToken ct)
    {
        var found = new Dictionary<DateOnly, HistoryFile>();
        var wanted = Months(FirstPeriod, lastPeriod).Where(p => !skip.Contains(p)).ToList();

        // NBER's listings, for the months before CMS's own files (and as a fallback).
        var nberMonths = wanted.Where(p => p < FirstCmsPeriod).ToList();
        if (nberMonths.Count > 0)
        {
            foreach (var listing in NberListings(nberMonths))
            {
                var html = await GetStringAsync(listing, ct);
                if (html is null)
                {
                    log.Warning("History: the NBER listing {Url} did not answer; its files are looked for elsewhere", listing);
                    continue;
                }

                foreach (var (name, period) in ParseListing(html))
                {
                    if (period >= FirstPeriod && period < FirstCmsPeriod && period <= lastPeriod && !skip.Contains(period) && !found.ContainsKey(period))
                    {
                        found[period] = new HistoryFile(period, name, new Uri(listing, Uri.EscapeDataString(name)), "nber");
                    }
                }
            }
        }

        foreach (var period in wanted.Where(p => p >= FirstCmsPeriod))
        {
            var file = await FindCmsAsync(period, ct);
            if (file is null && period >= FirstNberRawPeriod)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"npi_raw_{period:yyyyMM}.zip");
                var url = new Uri(_options.NberBase, $"npi/zip/{period.Year}/{name}");
                if (await ExistsAsync(url, ct))
                {
                    file = new HistoryFile(period, name, url, "nber");
                }
            }

            if (file is not null)
            {
                found[period] = file;
            }
        }

        return found;
    }

    private IEnumerable<Uri> NberListings(IReadOnlyList<DateOnly> months)
    {
        foreach (var year in months.Select(m => m.Year).Distinct().Order())
        {
            yield return new Uri(_options.NberBase, $"npi/{year}/");
        }

        yield return new Uri(_options.NberBase, "npi/backfiles/");
        yield return new Uri(_options.NberBase, "nppes/zip-orig/");
    }

    private async Task<HistoryFile?> FindCmsAsync(DateOnly period, CancellationToken ct)
    {
        foreach (var name in CmsCandidates(period))
        {
            var url = new Uri(_options.CmsBase, name);
            if (await ExistsAsync(url, ct))
            {
                return new HistoryFile(period, name, url, "cms");
            }
        }

        return null;
    }

    /// <summary>HEAD: true for 200, false for 403/404/410; other answers are retried, then count as missing.</summary>
    private async Task<bool> ExistsAsync(Uri url, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _options.Attempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                {
                    return false;
                }
            }
            catch (HttpRequestException ex) when (attempt < _options.Attempts)
            {
                log.Debug(ex, "History: HEAD {Url} failed (attempt {Attempt})", url, attempt);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < _options.Attempts)
            {
                // A timeout: try again.
            }

            await Task.Delay(_options.RetryDelay * attempt, ct);
        }

        log.Warning("History: {Url} gave no clear answer after {Attempts} attempts; counted as not there", url, _options.Attempts);
        return false;
    }

    private async Task<string?> GetStringAsync(Uri url, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _options.Attempts; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(ct);
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                {
                    return null;
                }
            }
            catch (HttpRequestException) when (attempt < _options.Attempts)
            {
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < _options.Attempts)
            {
            }

            await Task.Delay(_options.RetryDelay * attempt, ct);
        }

        return null;
    }

    /// <summary>Every first-of-month from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static IEnumerable<DateOnly> Months(DateOnly from, DateOnly to)
    {
        for (var m = new DateOnly(from.Year, from.Month, 1); m <= to; m = m.AddMonths(1))
        {
            yield return m;
        }
    }
}

/// <summary>Where the catalog looks, and how hard it tries.</summary>
public sealed record HistoryCatalogOptions
{
    public Uri CmsBase { get; init; } = new("https://download.cms.gov/nppes/");

    public Uri NberBase { get; init; } = new("https://data.nber.org/");

    public int Attempts { get; init; } = 6;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);
}
