using System.Text.Json;

namespace Npi.Loader.Datasets;

/// <summary>
/// A published release of a dataset: its version label, the file to download and (CMS catalog) the end of its data period.
/// A source that needs more of what it found for its load derives from it (<see cref="StateLicenseSource"/>): the source
/// objects are shared (<see cref="DatasetLoader.AllSources"/>), so nothing found for one refresh may be kept on them.
/// </summary>
public record DatasetRelease(string Version, Uri Url, string? PeriodEnd = null)
{
    /// <summary>The data year (e.g. 2024 for a period ending 2024-12-31), or null when the catalog gives no period.</summary>
    public int? DataYear => PeriodEnd is { Length: >= 4 } p && int.TryParse(p.AsSpan(0, 4), System.Globalization.CultureInfo.InvariantCulture, out var y) ? y : null;
}

/// <summary>
/// The data.cms.gov catalog (<c>https://data.cms.gov/data.json</c>, DCAT-US). Every release is its own
/// entry titled "&lt;dataset&gt; : &lt;date&gt;". The newest release is the one whose <c>temporal</c>
/// period ends last; <c>modified</c> is not used, because old releases get re-modified.
/// </summary>
public sealed class CmsCatalog
{
    private readonly List<(string BaseTitle, string PeriodEnd, string Modified, string? CsvUrl, string? ApiUrl)> _entries = [];

    private CmsCatalog()
    {
    }

    public static async Task<CmsCatalog> LoadAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        await using var stream = await http.GetStreamAsync(url, ct);
        return Parse(stream);
    }

    public static CmsCatalog Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var catalog = new CmsCatalog();
        foreach (var dataset in doc.RootElement.GetProperty("dataset").EnumerateArray())
        {
            var title = dataset.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var baseTitle = title.Split(" : ", 2)[0].Trim();
            var periodEnd = "";
            if (dataset.TryGetProperty("temporal", out var temporal) && temporal.ValueKind == JsonValueKind.Array && temporal.GetArrayLength() > 0
                && temporal[0].TryGetProperty("endDate", out var end))
            {
                periodEnd = end.GetString() ?? "";
            }

            var modified = dataset.TryGetProperty("modified", out var m) ? m.GetString() ?? "" : "";
            string? csv = null, csvResources = null, api = null;
            if (dataset.TryGetProperty("distribution", out var distributions) && distributions.ValueKind == JsonValueKind.Array)
            {
                foreach (var distribution in distributions.EnumerateArray())
                {
                    if (distribution.TryGetProperty("downloadURL", out var download) && download.GetString() is { } u
                        && u.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    {
                        csv = u;
                        csvResources = distribution.TryGetProperty("resourcesAPI", out var r) ? r.GetString() : null;
                        break;
                    }
                }

                // The data API of that file: the "API" distribution describing the same resources as the CSV.
                foreach (var distribution in distributions.EnumerateArray())
                {
                    if (csvResources is not null && distribution.TryGetProperty("accessURL", out var access) && access.GetString() is { } a
                        && distribution.TryGetProperty("resourcesAPI", out var r) && r.GetString() == csvResources)
                    {
                        api = a;
                        break;
                    }
                }
            }

            catalog._entries.Add((baseTitle, periodEnd, modified, csv, api));
        }

        return catalog;
    }

    /// <summary>The newest release of the dataset titled <paramref name="title"/> (e.g. "Order and Referring") that has a CSV file.</summary>
    public DatasetRelease? FindLatest(string title)
    {
        var best = _entries
            .Where(e => e.CsvUrl is not null && string.Equals(e.BaseTitle, title, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.PeriodEnd, StringComparer.Ordinal)
            .ThenByDescending(e => e.Modified, StringComparer.Ordinal)
            .FirstOrDefault();
        if (best.CsvUrl is null)
        {
            return null;
        }

        var url = new Uri(best.CsvUrl);
        return new DatasetRelease($"{best.PeriodEnd} {Path.GetFileName(url.AbsolutePath)}".Trim(), url, best.PeriodEnd.Length > 0 ? best.PeriodEnd : null);
    }

    /// <summary>
    /// The newest release of <paramref name="title"/> (as <see cref="FindLatest"/>), with <see cref="DatasetRelease.Url"/> its data API
    /// (<c>https://data.cms.gov/data-api/v1/dataset/&lt;id&gt;/data</c>, which filters and selects columns) instead of the CSV file; the
    /// version is the CSV's, so it changes when CMS publishes another file. Null when the newest release has no data API.
    /// </summary>
    public DatasetRelease? FindLatestApi(string title)
    {
        var best = _entries
            .Where(e => e.CsvUrl is not null && string.Equals(e.BaseTitle, title, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.PeriodEnd, StringComparer.Ordinal)
            .ThenByDescending(e => e.Modified, StringComparer.Ordinal)
            .FirstOrDefault();
        if (best.CsvUrl is null || best.ApiUrl is null)
        {
            return null;
        }

        var csv = new Uri(best.CsvUrl);
        return new DatasetRelease($"{best.PeriodEnd} {Path.GetFileName(csv.AbsolutePath)}".Trim(), new Uri(best.ApiUrl), best.PeriodEnd.Length > 0 ? best.PeriodEnd : null);
    }

    /// <summary>
    /// Every data year of a yearly dataset (e.g. "Medicare Part D Prescribers - by Provider", one release per year), oldest first:
    /// per year the release whose period ends last (then the newest <c>modified</c>), with a CSV file. The version is the file's
    /// path, folder included, because CMS republishes old years under new folders with the same file name.
    /// </summary>
    public IReadOnlyList<DatasetRelease> FindYears(string title) =>
        _entries
            .Where(e => e.CsvUrl is not null && string.Equals(e.BaseTitle, title, StringComparison.OrdinalIgnoreCase))
            .Select(e => (Entry: e, Release: new DatasetRelease(new Uri(e.CsvUrl!).AbsolutePath, new Uri(e.CsvUrl!), e.PeriodEnd.Length > 0 ? e.PeriodEnd : null)))
            .Where(x => x.Release.DataYear is not null)
            .GroupBy(x => x.Release.DataYear!.Value)
            .OrderBy(g => g.Key)
            .Select(g => g.OrderByDescending(x => x.Entry.PeriodEnd, StringComparer.Ordinal).ThenByDescending(x => x.Entry.Modified, StringComparer.Ordinal)
                .First().Release)
            .ToList();
}

/// <summary>
/// The Provider Data Catalog behind Care Compare (<c>https://data.cms.gov/provider-data/</c>). Each dataset
/// has a fixed id (e.g. <c>mj5m-pzi6</c>); its metastore entry names the current file and when it changed.
/// </summary>
public static class ProviderDataCatalog
{
    public static async Task<DatasetRelease> FindLatestAsync(HttpClient http, Uri metastoreBase, string datasetId, CancellationToken ct)
    {
        await using var stream = await http.GetStreamAsync(new Uri(metastoreBase, datasetId), ct);
        return Parse(stream, datasetId);
    }

    public static DatasetRelease Parse(Stream json, string datasetId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var modified = root.TryGetProperty("modified", out var m) ? m.GetString() ?? "" : "";
        if (root.TryGetProperty("distribution", out var distributions) && distributions.ValueKind == JsonValueKind.Array)
        {
            foreach (var distribution in distributions.EnumerateArray())
            {
                if (distribution.TryGetProperty("downloadURL", out var download) && download.GetString() is { } u)
                {
                    var url = new Uri(u);
                    return new DatasetRelease($"{modified} {Path.GetFileName(url.AbsolutePath)}".Trim(), url);
                }
            }
        }

        throw new InvalidDataException($"Provider Data Catalog entry {datasetId} has no downloadURL.");
    }
}
