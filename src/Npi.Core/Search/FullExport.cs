using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace Npi.Core.Search;

/// <summary>
/// The ready-made file of every active provider (owner, 2026-10-14): the summary CSV (<see cref="ProviderCsv"/>, the columns
/// of /export.csv), sorted by NPI and gzipped, with a small JSON file describing it. The loader rebuilds it after a load
/// changed its data (<c>run</c>, or the <c>export</c> command); the site serves it for an export with no filter, instead of
/// streaming 9.5M providers through the search (about 35 minutes). Built from several NPI ranges at once, each its own gzip
/// member; gzip members joined end to end are one valid file.
/// </summary>
public static class FullExport
{
    public const string FileName = "providers_all.csv.gz";
    public const string InfoFileName = "providers_all.json";

    /// <summary>The folder unless configured: the data folder's <c>exports</c>.</summary>
    public static string DefaultFolder => Path.Combine(DataFolders.DefaultRoot, "exports");

    /// <summary>What the file holds: the data's as-of date, the providers, when it was built (UTC), its size.</summary>
    public sealed record Info(DateOnly? AsOf, long Rows, DateTime BuiltAt, long Bytes);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The file's description, or null when there is no (complete) file in <paramref name="folder"/>.</summary>
    public static Info? Read(string folder)
    {
        try
        {
            var info = JsonSerializer.Deserialize<Info>(File.ReadAllText(Path.Combine(folder, InfoFileName)), Json);
            return info is not null && File.Exists(Path.Combine(folder, FileName)) ? info : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Opens the file for reading; it may be replaced meanwhile (the open copy stays readable).</summary>
    public static FileStream Open(string folder) =>
        new(Path.Combine(folder, FileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous);

    /// <summary>
    /// Writes every active provider into <paramref name="folder"/>, reading <paramref name="parts"/> NPI ranges at once, then
    /// replaces the previous file. Returns its description.
    /// </summary>
    public static async Task<Info> WriteAsync(SearchService search, string folder, DateOnly? asOf, int parts, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        parts = Math.Clamp(parts, 1, 64);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var partPaths = Enumerable.Range(0, parts + 1).Select(i => Path.Combine(folder, $".building-{stamp}-{i:D2}.gz")).ToArray();
        var target = Path.Combine(folder, FileName);
        var building = Path.Combine(folder, $".building-{stamp}.csv.gz");
        try
        {
            // Part 0: the BOM and the header row. Parts 1…n: the NPIs 1000000000-1999999999 in equal ranges.
            await using (var header = Gzip(partPaths[0]))
            {
                await ProviderCsv.WriteAsync(AsyncEnumerable.Empty<ProviderSummary>(), header, ct);
            }

            var counts = await Task.WhenAll(Enumerable.Range(0, parts).Select(async i =>
            {
                var (from, to) = Range(i, parts);
                await using var output = Gzip(partPaths[i + 1]);
                return await ProviderCsv.WriteAsync(search.AllInNpiRangeAsync(from, to, ct), output, ct, header: false);
            }));

            await using (var joined = new FileStream(building, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                // A range without providers is left out: its writer was still flushed, which leaves a gzip member without its end
                // (15 bytes) that makes the joined file unreadable.
                foreach (var part in partPaths.Where((_, i) => i == 0 || counts[i - 1] > 0))
                {
                    await using var read = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
                    await read.CopyToAsync(joined, ct);
                }
            }

            File.Move(building, target, overwrite: true);
            var info = new Info(asOf, counts.Sum(), DateTime.UtcNow, new FileInfo(target).Length);
            var infoPath = Path.Combine(folder, InfoFileName);
            await File.WriteAllTextAsync(infoPath + ".tmp", JsonSerializer.Serialize(info, Json), ct);
            File.Move(infoPath + ".tmp", infoPath, overwrite: true);
            return info;
        }
        finally
        {
            foreach (var path in partPaths.Append(building))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // a leftover temporary file; the next build uses new names
                }
            }
        }
    }

    /// <summary>The <paramref name="i"/>th of <paramref name="parts"/> equal ranges of the NPIs (10 digits, first digit 1).</summary>
    public static (string From, string To) Range(int i, int parts)
    {
        const long first = 1_000_000_000, end = 2_000_000_000;
        var size = (end - first) / parts;
        var from = first + i * size;
        var to = i == parts - 1 ? end : from + size;
        return (from.ToString(CultureInfo.InvariantCulture), to.ToString(CultureInfo.InvariantCulture));
    }

    private static GZipStream Gzip(string path) =>
        new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous), CompressionLevel.Optimal);
}
