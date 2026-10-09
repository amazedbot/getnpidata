using System.Globalization;

namespace Npi.Loader.Datasets;

/// <summary>
/// HHS-OIG LEIE, the full list of excluded individuals and entities (CLAUDE.md §7 Stage 5.5 item 2a). The URL
/// never changes, so the version is the server's Last-Modified/ETag/length (or today's date without them).
/// </summary>
public sealed class LeieSource : DatasetSource
{
    public override string Name => "oig_leie";

    private static readonly CsvColumn[] Columns =
    [
        new("LASTNAME", "last_name"), new("FIRSTNAME", "first_name"), new("MIDNAME", "middle_name"), new("BUSNAME", "business_name"),
        new("GENERAL", "general_category"), new("SPECIALTY", "specialty"), new("NPI", "npi", CsvValue.Npi),
        new("ADDRESS", "address"), new("CITY", "city"), new("STATE", "state"), new("ZIP", "zip"),
        new("EXCLTYPE", "exclusion_type"), new("EXCLDATE", "exclusion_date", CsvValue.DateYmd),
        new("REINDATE", "reinstatement_date", CsvValue.DateYmd), new("WAIVERDATE", "waiver_date", CsvValue.DateYmd),
        new("WVRSTATE", "waiver_state"),
    ];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var url = new Uri(context.Options.LeieUrl);
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await context.Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var parts = new List<string>();
        if (response.Content.Headers.LastModified is { } modified)
        {
            parts.Add(modified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        }

        if (response.Headers.ETag is { } etag)
        {
            parts.Add(etag.Tag.Trim('"'));
        }

        if (response.Content.Headers.ContentLength is { } length)
        {
            parts.Add(length.ToString(CultureInfo.InvariantCulture));
        }

        var version = parts.Count > 0 ? string.Join(" ", parts) : DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new DatasetRelease(version.Length > 200 ? version[..200] : version, url);
    }

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "oig_leie.csv", async (connection, path) =>
        {
            var counts = await TableSwap.ReplaceAsync(connection, ["oig_exclusion"], context.Options.MinRowRatio,
                () => CsvTableLoader.LoadAsync(connection, path, "oig_exclusion_staging", Columns, ct), ct);
            return counts["oig_exclusion"];
        }, ct);
}

/// <summary>CMS Opt Out Affidavits: practitioners who opted out of Medicare (Stage 5.5 item 2b).</summary>
public sealed class OptOutSource : DatasetSource
{
    public const string CatalogTitle = "Opt Out Affidavits";

    public override string Name => "cms_opt_out";

    private static readonly CsvColumn[] Columns =
    [
        new("npi", "npi", CsvValue.Npi), new("Specialty", "specialty"),
        new("Optout Effective Date", "effective_date", CsvValue.DateMdy), new("Optout End Date", "end_date", CsvValue.DateMdy),
        new("Eligible to Order and Refer", "can_order_refer", CsvValue.YesNo), new("Last updated", "last_updated", CsvValue.DateMdy),
    ];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
        (await context.CmsCatalogAsync(ct)).FindLatest(CatalogTitle)
        ?? throw new InvalidDataException($"data.cms.gov lists no \"{CatalogTitle}\" CSV.");

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "cms_opt_out.csv", async (connection, path) =>
        {
            var counts = await TableSwap.ReplaceAsync(connection, ["medicare_opt_out"], context.Options.MinRowRatio,
                () => CsvTableLoader.LoadAsync(connection, path, "medicare_opt_out_staging", Columns, ct), ct);
            return counts["medicare_opt_out"];
        }, ct);
}

/// <summary>CMS Order and Referring: who may order or refer in Medicare, per program (Stage 5.5 item 2c).</summary>
public sealed class OrderReferringSource : DatasetSource
{
    public const string CatalogTitle = "Order and Referring";

    public override string Name => "cms_order_referring";

    private static readonly CsvColumn[] Columns =
    [
        new("NPI", "npi", CsvValue.Npi), new("PARTB", "part_b", CsvValue.YesNo), new("DME", "dme", CsvValue.YesNo),
        new("HHA", "hha", CsvValue.YesNo), new("PMD", "pmd", CsvValue.YesNo), new("HOSPICE", "hospice", CsvValue.YesNo),
    ];

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct) =>
        (await context.CmsCatalogAsync(ct)).FindLatest(CatalogTitle)
        ?? throw new InvalidDataException($"data.cms.gov lists no \"{CatalogTitle}\" CSV.");

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "cms_order_referring.csv", async (connection, path) =>
        {
            var counts = await TableSwap.ReplaceAsync(connection, ["medicare_order_referring"], context.Options.MinRowRatio,
                () => CsvTableLoader.LoadAsync(connection, path, "medicare_order_referring_staging", Columns, ct), ct);
            return counts["medicare_order_referring"];
        }, ct);
}
