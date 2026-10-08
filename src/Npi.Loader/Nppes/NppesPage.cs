using System.Text.RegularExpressions;

namespace Npi.Loader.Nppes;

/// <summary>A zip link found on NPI_Files.html.</summary>
public sealed record NppesLink(string FileName, Uri Url);

/// <summary>Extracts zip links from the NPI_Files.html page.</summary>
public static partial class NppesPage
{
    // href='./x.zip' or href="x.zip" (the page currently uses single quotes and a ./ prefix).
    [GeneratedRegex(@"href\s*=\s*(?:""(?<u>[^""]+)""|'(?<u>[^']+)')", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    /// <summary>Returns each distinct .zip link, resolved against <paramref name="pageUrl"/>.</summary>
    public static IReadOnlyList<NppesLink> ParseZipLinks(string html, Uri pageUrl)
    {
        var links = new List<NppesLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Href().Matches(html))
        {
            if (!Uri.TryCreate(pageUrl, m.Groups["u"].Value.Trim(), out var url) || url.Scheme is not ("https" or "http"))
            {
                continue;
            }

            var fileName = Uri.UnescapeDataString(url.Segments[^1]);
            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && seen.Add(fileName))
            {
                links.Add(new NppesLink(fileName, url));
            }
        }

        return links;
    }
}
