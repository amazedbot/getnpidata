using System.Globalization;
using System.IO.Compression;
using System.Text;
using Npi.Core;

namespace Npi.Loader.Datasets;

/// <summary>A current enrollment in Florida Medicaid from the Provider Master List.</summary>
public sealed record FloridaMedicaidEnrollment(string Npi, DateOnly? Since, bool Limited);

/// <summary>
/// Florida AHCA's Provider Master List (docs/PRODUCT_PLAN.md step 3.7): every Florida Medicaid provider, published for the
/// Medicaid health plans to check that their network providers are enrolled (portal.flmmis.com, Managed Care › Registration ›
/// Provider Information Reports; <c>prw19002.zip</c>, a pipe-delimited text file of ~470,000 lines, ~17 MB zipped, no login,
/// updated weekly or so). One line per Medicaid provider ID and service location; a provider's other service locations follow on
/// lines with only the ID and the address. Kept: the lines of an enrollment of type "ENROLLMENT" or "LIMITED" (limited enrollment)
/// whose current Medicaid enrollment status is A (active) and whose claims eligibility hasn't ended, with a valid NPI.
/// "REGISTERED" (no Medicaid ID) and "ROPA" (registered only to order, refer, prescribe or attend) don't count (owner,
/// 2026-10-15). Read from the zip as it unzips; the columns are found by their names.
/// </summary>
public static class FloridaMedicaidList
{
    private static readonly string[] Enrolled = ["ENROLLMENT", "LIMITED", "LIMITED ENROLLMENT"];

    /// <summary>The current enrollments in the zip, one per line (an NPI with several Medicaid IDs comes several times).</summary>
    public static IEnumerable<FloridaMedicaidEnrollment> Read(Stream zipFile, DateOnly today)
    {
        using var zip = new ZipArchive(zipFile, ZipArchiveMode.Read, leaveOpen: true);
        using var reader = new StreamReader(HhsOpenData.OpenLargestCsv(zip, "The Florida Provider Master List"), Encoding.Latin1);
        var header = (reader.ReadLine() ?? throw new InvalidDataException("The Florida Provider Master List is empty.")).Split('|');
        int Find(string start) =>
            Array.FindIndex(header, h => h.Trim().StartsWith(start, StringComparison.OrdinalIgnoreCase)) is var i and >= 0
                ? i
                : throw new InvalidDataException($"The Florida Provider Master List has no \"{start}\" column (header: {string.Join(" | ", header.Take(14))}).");
        int name = Find("Provider Name"), type = Find("Enrollment Type"), npi = Array.FindIndex(header, h => h.Trim() == "NPI"),
            status = Find("Current Medicaid Enrollment Status"), from = Find("Medicaid Claims Eligibility Effective Date"),
            to = Find("Medicaid Claims Eligibility End Date");
        if (npi < 0)
        {
            throw new InvalidDataException("The Florida Provider Master List has no NPI column.");
        }

        var needed = new[] { name, type, npi, status, from, to }.Max() + 1;
        while (reader.ReadLine() is { } line)
        {
            var f = line.Split('|');
            if (f.Length < needed || f[name].Trim().Length == 0)
            {
                continue; // another service location of the provider above
            }

            var kind = f[type].Trim().ToUpperInvariant();
            var number = f[npi].Trim();
            if (!Enrolled.Contains(kind) || f[status].Trim() != "A" || !InputFormats.HasValidCheckDigit(number)
                || (Date(f[to]) is { } end && end < today))
            {
                continue;
            }

            yield return new FloridaMedicaidEnrollment(number, Date(f[from]), kind != "ENROLLMENT");
        }
    }

    private static DateOnly? Date(string value) =>
        DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year > 1900 ? d : null;
}
