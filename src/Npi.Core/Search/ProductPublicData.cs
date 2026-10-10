using System.Text;
using Dapper;
using MySqlConnector;

namespace Npi.Core.Search;

/// <summary>CMS spending on a drug in one program ("Part D", "Part B", "Medicaid") and year, matched by brand name.</summary>
public sealed record ProductSpending(string Program, string BrandName, string GenericName, int Year, double Spending, double? Units, double? Claims,
    double? Beneficiaries)
{
    /// <summary>Average spending per dosage unit (tablet, mL …), when units are reported.</summary>
    public double? PerUnit => Units is > 0 ? Spending / Units : null;
}

/// <summary>A NADAC price (what pharmacies pay on average, per unit) of one package NDC of the product.</summary>
public sealed record ProductPrice(string Ndc, string? Description, double PerUnit, string? PricingUnit, DateOnly? EffectiveDate, string? Classification)
{
    /// <summary>NADAC's rate-setting classification in words: B brand, G generic, B-ANDA a brand under a generic approval, B-BIO a biological.</summary>
    public string? Kind => Classification switch { "B" => "Brand", "G" => "Generic", "B-ANDA" => "Branded generic", "B-BIO" => "Biological", _ => Classification };
}

/// <summary>An FDA drug shortage listing for the product's NDC or its generic name.</summary>
public sealed record ProductShortage(string? GenericName, string? Company, string? Presentation, string? Status, string? Availability, string? Reason,
    string? RelatedInfo, DateOnly? InitialDate, DateOnly? UpdateDate);

/// <summary>FDA recalls (enforcement reports) naming the product: counts by class and the newest.</summary>
public sealed record ProductRecalls(int Total, int ClassI, int ClassII, int ClassIII, IReadOnlyList<CompanyRecall> Latest);

/// <summary>
/// Adverse event report counts from openFDA: for a drug, FAERS reports naming it (and the serious ones); for a device,
/// MAUDE reports by brand name (deaths, injuries, malfunctions). A report does not prove the product caused the event.
/// </summary>
public sealed record ProductAdverseEvents(string Kind, string QueryName, int? Reports, int? Serious, int? Deaths, int? Injuries, int? Malfunctions,
    DateTime FetchedAt)
{
    /// <summary>The openFDA query the counts came from.</summary>
    public string QueryUrl => Kind == "drug"
        ? $"https://api.fda.gov/drug/event.json?search=patient.drug.medicinalproduct:%22{Uri.EscapeDataString(QueryName)}%22&count=serious"
        : $"https://api.fda.gov/device/event.json?search=device.brand_name:%22{Uri.EscapeDataString(QueryName)}%22&count=event_type.exact";
}

/// <summary>ClinicalTrials.gov studies with the product (by its generic name) as an intervention: all, and recruiting now.</summary>
public sealed record ProductTrials(string QueryName, int? Studies, int? Recruiting, DateTime FetchedAt)
{
    public string SearchUrl => $"https://clinicaltrials.gov/search?intr={Uri.EscapeDataString(QueryName)}";
}

/// <summary>Part 3 of the product pages (CLAUDE.md §7 Stage 5.5 item 19): public data about a product, from the tables the loader fills.</summary>
internal static class ProductPublicData
{
    public const int MaxRecalls = 20;

    public const int MaxPrices = 8;

    private sealed record SpendingRow(string Program, string BrandName, string GenericName, short Year, double Spending, double? Units, double? Claims,
        double? Beneficiaries);

    private sealed record RecallCountRow(long Total, long ClassI, long ClassII, long ClassIII);

    private sealed record RecallRow(string RecallNumber, string ProductType, string? Firm, string? Classification, string? Status, DateTime? Initiated,
        string? Product, string? Reason);

    private sealed record PriceRow(string Ndc, string? Description, double PerUnit, string? PricingUnit, DateTime? EffectiveDate, string? Classification);

    private sealed record ShortageRow(string? GenericName, string? Company, string? Presentation, string? Status, string? Availability, string? Reason,
        string? RelatedInfo, DateTime? InitialDate, DateTime? UpdateDate);

    private static DateOnly? ToDate(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;

    /// <summary>"0003-0893-21" / "0003-0893" → "000030893" (as the loader's ndc_key); null if not hyphenated so.</summary>
    public static string? NdcKey(string? ndc)
    {
        var parts = (ndc ?? "").Trim().Split('-');
        return parts.Length is 2 or 3 && parts[0].Length is >= 1 and <= 5 && parts[1].Length is >= 1 and <= 4 && parts.Take(2).All(p => p.All(char.IsAsciiDigit))
            ? parts[0].PadLeft(5, '0') + parts[1].PadLeft(4, '0')
            : null;
    }

    /// <summary>A FULLTEXT phrase of a name's words of 3+ letters or digits ("Eliquis 5 mg" → "eliquis"), or null.</summary>
    public static string? Phrase(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var words = new StringBuilder();
        foreach (var word in new string(name.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3))
        {
            words.Append(words.Length == 0 ? "" : " ").Append(word.ToLowerInvariant());
        }

        return words.Length == 0 ? null : $"\"{words}\"";
    }

    /// <summary>
    /// CMS spending rows of the brand names, and of brand names that are one of them plus more words (CMS lists some drugs by
    /// form: "Dupixent Pen", "Dupixent Syringe").
    /// </summary>
    public static async Task<IReadOnlyList<ProductSpending>> SpendingAsync(MySqlConnection connection, IReadOnlyCollection<string> brands, CancellationToken ct)
    {
        if (brands.Count == 0)
        {
            return [];
        }

        var parameters = new DynamicParameters();
        var conditions = brands.Select((b, i) =>
        {
            parameters.Add($"b{i}", b);
            parameters.Add($"p{i}", SearchQuery.Prefix(b + " "));
            return $"(brand_name = @b{i} OR brand_name LIKE @p{i})";
        });
        return (await connection.QueryAsync<SpendingRow>(new CommandDefinition(
            $"""
            SELECT program AS Program, brand_name AS BrandName, generic_name AS GenericName, year AS Year, spending AS Spending, units AS Units,
                   claims AS Claims, beneficiaries AS Beneficiaries
            FROM drug_spending WHERE {string.Join(" OR ", conditions)}
            ORDER BY FIELD(program, 'Part D', 'Part B', 'Medicaid'), year DESC, spending DESC
            LIMIT 60
            """, parameters, cancellationToken: ct)))
            .Select(r => new ProductSpending(r.Program, r.BrandName, r.GenericName, r.Year, r.Spending, r.Units, r.Claims, r.Beneficiaries)).ToList();
    }

    public static async Task<IReadOnlyList<ProductPrice>> PricesAsync(MySqlConnection connection, IReadOnlyCollection<string> ndcKeys, CancellationToken ct) =>
        ndcKeys.Count == 0 ? [] : (await connection.QueryAsync<PriceRow>(new CommandDefinition(
            $"""
            SELECT ndc AS Ndc, description AS Description, per_unit AS PerUnit, pricing_unit AS PricingUnit, effective_date AS EffectiveDate,
                   classification AS Classification
            FROM nadac WHERE ndc_key IN @keys
            ORDER BY description, ndc
            LIMIT {MaxPrices}
            """, new { keys = ndcKeys.ToArray() }, cancellationToken: ct)))
            .Select(r => new ProductPrice(r.Ndc, r.Description, r.PerUnit, r.PricingUnit, ToDate(r.EffectiveDate), r.Classification)).ToList();

    public static async Task<IReadOnlyList<ProductShortage>> ShortagesAsync(MySqlConnection connection, IReadOnlyCollection<string> ndcKeys, string? generic,
        CancellationToken ct) =>
        ndcKeys.Count == 0 && generic is null ? [] : (await connection.QueryAsync<ShortageRow>(new CommandDefinition(
            """
            SELECT s.generic_name AS GenericName, s.company AS Company, s.presentation AS Presentation, s.status AS Status, s.availability AS Availability,
                   s.reason AS Reason, s.related_info AS RelatedInfo, s.initial_date AS InitialDate, s.update_date AS UpdateDate
            FROM drug_shortage s
            WHERE s.id IN (SELECT shortage_id FROM drug_shortage_ndc WHERE ndc_key IN @keys) OR (@generic IS NOT NULL AND s.substance = @generic)
            ORDER BY s.status = 'Resolved', s.update_date DESC, s.id
            LIMIT 20
            """, new { keys = ndcKeys.Count == 0 ? [""] : ndcKeys.ToArray(), generic }, cancellationToken: ct)))
            .Select(r => new ProductShortage(r.GenericName, r.Company, r.Presentation, r.Status, r.Availability, r.Reason, r.RelatedInfo, ToDate(r.InitialDate),
                ToDate(r.UpdateDate))).ToList();

    /// <summary>
    /// FDA recalls naming the product: for a drug or biological, its name, FDA brand name or listed NDC (labeler-product) in the
    /// recalled product's description; for a device, its name, only in recalls by a company paying for the product (product
    /// names like "Attune" or "General Therapies" also occur in unrelated recalls).
    /// </summary>
    public static async Task<ProductRecalls?> RecallsAsync(MySqlConnection connection, string slug, bool isDrug, IEnumerable<string?> names, CancellationToken ct)
    {
        var phrases = names.Select(Phrase).OfType<string>().Distinct().ToList();
        if (phrases.Count == 0)
        {
            return null;
        }

        var match = string.Join(" ", phrases);
        var where = isDrug
            ? "MATCH(f.product_description) AGAINST(@match IN BOOLEAN MODE)"
            : """
              MATCH(f.product_description) AGAINST(@match IN BOOLEAN MODE)
              AND f.firm_key IN (SELECT k.name_key FROM op_product_company pc JOIN op_company_key k ON k.company_id = pc.company_id WHERE pc.slug = @slug)
              """;
        var counts = await connection.QuerySingleAsync<RecallCountRow>(new CommandDefinition(
            $"""
            SELECT COUNT(*) AS Total, CAST(COALESCE(SUM(f.classification = 'Class I'), 0) AS SIGNED) AS ClassI,
                   CAST(COALESCE(SUM(f.classification = 'Class II'), 0) AS SIGNED) AS ClassII, CAST(COALESCE(SUM(f.classification = 'Class III'), 0) AS SIGNED) AS ClassIII
            FROM fda_enforcement f WHERE {where}
            """, new { match, slug }, cancellationToken: ct));
        if (counts.Total == 0)
        {
            return null;
        }

        var latest = await connection.QueryAsync<RecallRow>(new CommandDefinition(
            $"""
            SELECT f.recall_number AS RecallNumber, f.product_type AS ProductType, f.firm AS Firm, f.classification AS Classification, f.status AS Status,
                   f.initiation_date AS Initiated, f.product_description AS Product, f.reason AS Reason
            FROM fda_enforcement f WHERE {where}
            ORDER BY f.initiation_date DESC, f.recall_number DESC
            LIMIT {MaxRecalls}
            """, new { match, slug }, cancellationToken: ct));
        return new ProductRecalls((int)counts.Total, (int)counts.ClassI, (int)counts.ClassII, (int)counts.ClassIII,
            latest.Select(r => new CompanyRecall(r.RecallNumber, r.ProductType, r.Firm, r.Classification, r.Status, ToDate(r.Initiated), r.Product, r.Reason)).ToList());
    }

    public static Task<ProductAdverseEvents?> AdverseEventsAsync(MySqlConnection connection, string slug, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<ProductAdverseEvents?>(new CommandDefinition(
            """
            SELECT kind AS Kind, query_name AS QueryName, reports AS Reports, serious AS Serious, deaths AS Deaths, injuries AS Injuries,
                   malfunctions AS Malfunctions, fetched_at AS FetchedAt
            FROM product_adverse_events WHERE slug = @slug
            """, new { slug }, cancellationToken: ct));

    public static Task<ProductTrials?> TrialsAsync(MySqlConnection connection, string slug, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<ProductTrials?>(new CommandDefinition(
            "SELECT query_name AS QueryName, studies AS Studies, recruiting AS Recruiting, fetched_at AS FetchedAt FROM product_trials WHERE slug = @slug",
            new { slug }, cancellationToken: ct));
}
