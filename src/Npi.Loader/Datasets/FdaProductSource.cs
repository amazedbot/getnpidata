using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Npi.Loader.Db;

namespace Npi.Loader.Datasets;

/// <summary>
/// What the products named in Open Payments are (CLAUDE.md §7 Stage 5.5 item 19, part 2), from openFDA's bulk files
/// (listed in <c>api.fda.gov/download.json</c>; the export dates are the version):
/// <list type="bullet">
/// <item>the whole NDC directory (~140k products) and Drugs@FDA (~30k applications: sponsor, first approval);</item>
/// <item>the drug labels (14 files, ~1.8 GB) of only the products named in Open Payments: their indications and boxed warning;</item>
/// <item>the GUDID device records (52 files, ~1.9 GB) of only the device identifiers named in Open Payments, and the
/// 510(k) / PMA decisions they cite.</item>
/// </list>
/// The files are streamed record by record (<see cref="JsonArrayStream"/>) and deleted after use. Run after
/// <c>open_payments</c>, whose products decide which labels and devices are kept.
/// </summary>
public sealed partial class FdaProductSource : DatasetSource
{
    // A package NDC as printed on a label ("NDC 0003-0893-21").
    [GeneratedRegex(@"\b(\d{4,5}-\d{3,4})-\d{1,2}\b")]
    private static partial Regex PrintedNdc();

    public static readonly (string Category, string Endpoint)[] Files =
        [("drug", "ndc"), ("drug", "drugsfda"), ("drug", "label"), ("device", "udi"), ("device", "510k"), ("device", "pma")];

    public override string Name => "fda_products";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    private sealed record Partition(string Endpoint, Uri Url, string ExportDate);

    private static async Task<List<Partition>> FindAsync(DatasetContext context, CancellationToken ct)
    {
        await using var stream = await context.Http.GetStreamAsync(new Uri(context.Options.FdaDownloadIndexUrl), ct);
        using var doc = JsonDocument.Parse(stream);
        var results = doc.RootElement.GetProperty("results");
        var partitions = new List<Partition>();
        foreach (var (category, endpoint) in Files)
        {
            var e = results.GetProperty(category).GetProperty(endpoint);
            var exported = e.GetProperty("export_date").GetString() ?? "";
            partitions.AddRange(e.GetProperty("partitions").EnumerateArray().Select(p => new Partition(endpoint, new Uri(p.GetProperty("file").GetString()!), exported)));
        }

        return partitions;
    }

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var partitions = await FindAsync(context, ct);
        return new DatasetRelease(string.Join(" | ", partitions.GroupBy(p => p.Endpoint).Select(g => $"{g.Key} {g.First().ExportDate}")), partitions[0].Url);
    }

    // The records of one endpoint's files, one at a time (each file downloaded, read and deleted in turn).
    private static async IAsyncEnumerable<JsonElement> ReadAsync(DatasetContext context, IEnumerable<Partition> partitions,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var partition in partitions)
        {
            var zipPath = await context.DownloadAsync(new DatasetRelease(partition.ExportDate, partition.Url), "fda_" + Path.GetFileName(partition.Url.AbsolutePath), ct);
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var entry = zip.Entries.Single(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
                await using var json = entry.Open();
                await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(new JsonArrayStream(json, "results"), cancellationToken: ct))
                {
                    yield return record;
                }
            }
            finally
            {
                File.Delete(zipPath);
            }
        }
    }

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s.Trim() : null,
                JsonValueKind.Array => v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
                _ => null,
            }
            : null;

    public static IEnumerable<string> Strings(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0)
            : [];

    public static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    /// <summary>"20121228" or "2012-12-28" → the date; anything else → null.</summary>
    public static DateTime? Date(string? value) =>
        DateTime.TryParseExact(value, ["yyyyMMdd", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>"0003-0893-21" / "0003-0893" → "000030893" (labeler padded to 5 digits, product to 4); null if not hyphenated so.</summary>
    public static string? NdcKey(string? ndc)
    {
        var parts = (ndc ?? "").Trim().Split('-');
        return parts.Length is 2 or 3 && parts[0].Length is >= 1 and <= 5 && parts[1].Length is >= 1 and <= 4 && parts.Take(2).All(p => p.All(char.IsAsciiDigit))
            ? parts[0].PadLeft(5, '0') + parts[1].PadLeft(4, '0')
            : null;
    }

    private static bool? Flag(string? value) => value is null ? null : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static string? Cap(string? value, int max) => CompanyRecords.Trim(value, max);

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var partitions = await FindAsync(context, ct);
        IEnumerable<Partition> Of(string endpoint) => partitions.Where(p => p.Endpoint == endpoint);
        await using var connection = await context.Database.OpenAsync(ct);
        var counts = await TableSwap.ReplaceAsync(connection,
            ["fda_ndc_product", "fda_application", "fda_drug_label", "fda_drug_label_ndc", "fda_device", "fda_premarket"], context.Options.MinRowRatio, async () =>
            {
                // NDC directory: every product.
                var ndcRows = new List<object>();
                await foreach (var r in ReadAsync(context, Of("ndc"), ct))
                {
                    if (Str(r, "product_ndc") is not { } productNdc)
                    {
                        continue;
                    }

                    var openfda = Obj(r, "openfda");
                    ndcRows.Add(new
                    {
                        ndc = Cap(productNdc, 20), brand = Cap(Str(r, "brand_name"), 500), generic = Cap(Str(r, "generic_name"), 1000),
                        ingredients = Cap(string.Join("; ", Items(r, "active_ingredients").Select(a => $"{Str(a, "name")} {Str(a, "strength")}".Trim())), 2000),
                        form = Cap(Str(r, "dosage_form"), 200), route = Cap(string.Join(", ", Strings(r, "route")), 200), labeler = Cap(Str(r, "labeler_name"), 255),
                        category = Cap(Str(r, "marketing_category"), 100), application = Cap(Str(r, "application_number"), 40),
                        start = Date(Str(r, "marketing_start_date")),
                        classes = Cap(string.Join("; ", Strings(openfda, "pharm_class_epc").Select(c => c.Replace(" [EPC]", "", StringComparison.Ordinal))), 1000),
                        type = Cap(Str(r, "product_type"), 100), setId = Cap(Str(openfda, "spl_set_id"), 64),
                    });
                }

                await CompanyRecords.InsertAsync(connection,
                    """
                    INSERT IGNORE INTO `fda_ndc_product_staging` (`product_ndc`, `brand_name`, `generic_name`, `active_ingredients`, `dosage_form`, `route`, `labeler`,
                      `marketing_category`, `application_number`, `marketing_start`, `pharm_classes`, `product_type`, `spl_set_id`)
                    VALUES (@ndc, @brand, @generic, @ingredients, @form, @route, @labeler, @category, @application, @start, @classes, @type, @setId)
                    """, ndcRows, ct);
                context.Log.Information("FDA NDC directory: {Rows:N0} products", ndcRows.Count);

                // Drugs@FDA: the sponsor and first approval of each application.
                var applications = new List<object>();
                await foreach (var r in ReadAsync(context, Of("drugsfda"), ct))
                {
                    if (Str(r, "application_number") is not { } number)
                    {
                        continue;
                    }

                    var approved = Items(r, "submissions")
                        .Where(s => Str(s, "submission_type") == "ORIG" && Str(s, "submission_status") == "AP")
                        .Select(s => Date(Str(s, "submission_status_date"))).Where(d => d is not null).Min();
                    applications.Add(new { number = Cap(number, 40), sponsor = Cap(Str(r, "sponsor_name"), 255), approved });
                }

                await CompanyRecords.InsertAsync(connection,
                    "INSERT IGNORE INTO `fda_application_staging` (`application_number`, `sponsor`, `approval_date`) VALUES (@number, @sponsor, @approved)",
                    applications, ct);
                context.Log.Information("Drugs@FDA: {Rows:N0} applications", applications.Count);

                // The labels of the products named in Open Payments and of every listing under the same FDA applications (the page
                // borrows a repackager's label when the maker's listing has none), also for products matched by brand name: by the
                // directory's set ID, the label's own product NDCs, or the package NDCs printed on it (FDA's annotations are missing
                // for some products).
                var wanted = (await connection.QueryAsync<(string Key, string? SetId)>(new CommandDefinition(
                    """
                    SELECT DISTINCT p.`ndc_key`, NULL FROM `op_product` p WHERE p.`ndc_key` IS NOT NULL
                    UNION
                    SELECT DISTINCT n2.`ndc_key`, n2.`spl_set_id` FROM `fda_ndc_product_staging` n2
                    WHERE n2.`ndc_key` IS NOT NULL AND n2.`application_number` IN (
                      SELECT n.`application_number` FROM `op_product` p
                      JOIN `fda_ndc_product_staging` n ON n.`ndc_key` = p.`ndc_key` OR (p.`kind` IN ('Drug', 'Biological') AND n.`brand_name` = p.`name`)
                      WHERE n.`application_number` IS NOT NULL)
                    """, cancellationToken: ct))).ToList();
                var wantedKeys = wanted.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
                var keysBySetId = wanted.Where(w => w.SetId is not null).GroupBy(w => w.SetId!, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(w => w.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
                var labels = new List<object>();
                var labelNdcs = new HashSet<(string Key, string SetId)>();
                var labelsSeen = new HashSet<string>(StringComparer.Ordinal);
                await foreach (var r in ReadAsync(context, Of("label"), ct))
                {
                    if (Str(r, "set_id") is not { } setId || labelsSeen.Contains(setId))
                    {
                        continue;
                    }

                    var printed = Strings(r, "package_label_principal_display_panel").Concat(Strings(r, "how_supplied"))
                        .SelectMany(t => PrintedNdc().Matches(t).Select(m => m.Groups[1].Value));
                    var keys = Strings(Obj(r, "openfda"), "product_ndc").Concat(printed).Select(NdcKey).OfType<string>().Where(wantedKeys.Contains)
                        .ToHashSet(StringComparer.Ordinal);
                    if (keysBySetId.TryGetValue(setId, out var byDirectory))
                    {
                        keys.UnionWith(byDirectory);
                    }

                    if (keys.Count == 0)
                    {
                        continue;
                    }

                    labelsSeen.Add(setId);
                    labels.Add(new
                    {
                        setId = Cap(setId, 64), effective = Date(Str(r, "effective_time")), brand = Cap(Str(Obj(r, "openfda"), "brand_name"), 500),
                        indications = Str(r, "indications_and_usage"), boxed = Str(r, "boxed_warning"),
                    });
                    labelNdcs.UnionWith(keys.Select(k => (k, setId)));
                }

                await CompanyRecords.InsertAsync(connection,
                    "INSERT INTO `fda_drug_label_staging` (`set_id`, `effective_date`, `brand_name`, `indications`, `boxed_warning`) VALUES (@setId, @effective, @brand, @indications, @boxed)",
                    labels, ct);
                await CompanyRecords.InsertAsync(connection, "INSERT INTO `fda_drug_label_ndc_staging` (`ndc_key`, `set_id`) VALUES (@Key, @SetId)",
                    labelNdcs.Select(l => new { l.Key, l.SetId }), ct);
                context.Log.Information("FDA drug labels: {Labels:N0} for {Wanted:N0} listings (the products named in Open Payments and the listings under their applications)", labels.Count, wantedKeys.Count);

                // GUDID records of the device identifiers named in Open Payments.
                var wantedDevices = (await connection.QueryAsync<string>(new CommandDefinition(
                    "SELECT DISTINCT TRIM(`device_id`) FROM `op_product` WHERE `device_id` IS NOT NULL", cancellationToken: ct)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var devices = new List<object>();
                var submissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var devicesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await foreach (var r in ReadAsync(context, Of("udi"), ct))
                {
                    var ids = Items(r, "identifiers").Select(i => Str(i, "id")).OfType<string>().Where(wantedDevices.Contains).Where(devicesSeen.Add).ToList();
                    if (ids.Count == 0)
                    {
                        continue;
                    }

                    var gmdn = Items(r, "gmdn_terms").FirstOrDefault();
                    var code = Items(r, "product_codes").FirstOrDefault();
                    var codeFda = Obj(code, "openfda");
                    var numbers = Items(r, "premarket_submissions").Select(s => Str(s, "submission_number")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    submissions.UnionWith(numbers);
                    devices.AddRange(ids.Select(id => (object)new
                    {
                        id = Cap(id, 100), brand = Cap(Str(r, "brand_name"), 500), company = Cap(Str(r, "company_name"), 500), description = Str(r, "device_description"),
                        model = Cap(Str(r, "version_or_model_number"), 500), gmdn = Cap(Str(gmdn, "name"), 500), gmdnDefinition = Str(gmdn, "definition"),
                        code = Cap(Str(code, "code"), 20), codeName = Cap(Str(code, "name"), 500), cls = Cap(Str(codeFda, "device_class"), 10),
                        specialty = Cap(Str(codeFda, "medical_specialty_description"), 200), rx = Flag(Str(r, "is_rx")), otc = Flag(Str(r, "is_otc")),
                        implantable = Flag(Str(gmdn, "implantable")), status = Cap(Str(r, "commercial_distribution_status"), 200),
                        submissions = Cap(string.Join(",", numbers), 2000),
                    }));
                }

                await CompanyRecords.InsertAsync(connection,
                    """
                    INSERT INTO `fda_device_staging` (`device_id`, `brand_name`, `company_name`, `description`, `model`, `gmdn_term`, `gmdn_definition`, `product_code`,
                      `product_code_name`, `device_class`, `medical_specialty`, `is_rx`, `is_otc`, `implantable`, `distribution_status`, `submissions`)
                    VALUES (@id, @brand, @company, @description, @model, @gmdn, @gmdnDefinition, @code, @codeName, @cls, @specialty, @rx, @otc, @implantable, @status, @submissions)
                    """, devices, ct);
                context.Log.Information("GUDID: {Devices:N0} of the {Wanted:N0} device identifiers in Open Payments", devices.Count, wantedDevices.Count);

                // The 510(k) and PMA decisions those devices cite.
                var premarket = new List<object>();
                var premarketSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await foreach (var r in ReadAsync(context, Of("510k"), ct))
                {
                    if (Str(r, "k_number") is { } k && submissions.Contains(k) && premarketSeen.Add(k))
                    {
                        premarket.Add(new
                        {
                            number = Cap(k, 20), kind = "510(k)", applicant = Cap(Str(r, "applicant"), 500), device = Cap(Str(r, "device_name"), 1000),
                            date = Date(Str(r, "decision_date")), decision = Cap(Str(r, "decision_description"), 200),
                        });
                    }
                }

                await foreach (var r in ReadAsync(context, Of("pma"), ct))
                {
                    // PMA records are per supplement; the original (no supplement number) carries the approval.
                    if (Str(r, "pma_number") is { } p && submissions.Contains(p) && string.IsNullOrEmpty(Str(r, "supplement_number")) && premarketSeen.Add(p))
                    {
                        premarket.Add(new
                        {
                            number = Cap(p, 20), kind = "PMA", applicant = Cap(Str(r, "applicant"), 500),
                            device = Cap(Str(r, "trade_name") ?? Str(r, "generic_name"), 1000), date = Date(Str(r, "decision_date")),
                            decision = Cap(Str(r, "decision_code"), 200),
                        });
                    }
                }

                await CompanyRecords.InsertAsync(connection,
                    """
                    INSERT INTO `fda_premarket_staging` (`number`, `kind`, `applicant`, `device_name`, `decision_date`, `decision`)
                    VALUES (@number, @kind, @applicant, @device, @date, @decision)
                    """, premarket, ct);
                context.Log.Information("510(k) / PMA: {Rows:N0} of the {Wanted:N0} submissions the devices cite", premarket.Count, submissions.Count);
            }, ct);
        return counts["fda_ndc_product"];
    }
}
