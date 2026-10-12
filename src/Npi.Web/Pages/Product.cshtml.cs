using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /product/{slug}: a product named in Open Payments (CLAUDE.md §7 Stage 5.5 item 19), in the summary-first layout of drawing P10
/// (docs/PRODUCT_PLAN.md Phase 9, owner 2026-10-12).
/// </summary>
public class ProductModel(ProductService products) : PageModel
{
    public ProductDetail Product { get; private set; } = null!;

    /// <summary>The ten paid prescribers paid the most (part 4); null without Part D prescribing.</summary>
    public ProductPrescriberPage? TopPrescribers { get; private set; }

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        var product = await products.GetAsync(slug, ct);
        if (product is null)
        {
            return NotFound();
        }

        Product = product;
        TopPrescribers = product.Prescribing is null ? null : await products.PrescribersAsync(slug, "paid", 1, 10, ct);
        return Page();
    }

    public bool IsDrug => Product.Drug is not null || Product.Kind is "Drug" or "Biological";

    /// <summary>"Prescription drug", "Over-the-counter drug", "Biological", "Medical device", "Medical supply".</summary>
    public string KindLabel => Product.Drug?.ProductType?.ToUpperInvariant() switch
    {
        "HUMAN PRESCRIPTION DRUG" => Product.Kind == "Biological" ? "Prescription biological" : "Prescription drug",
        "HUMAN OTC DRUG" => "Over-the-counter drug",
        _ => Product.Kind switch { "Device" => "Medical device", "Medical Supply" => "Medical supply", null => "Product", var k => k },
    };

    /// <summary>The drug's established pharmacologic class ("Factor Xa Inhibitor"), or the device's GMDN term.</summary>
    public string? ClassName
    {
        get
        {
            if (Product.Drug?.PharmClasses is { } classes)
            {
                var parts = classes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var epc = parts.FirstOrDefault(p => p.EndsWith("[EPC]", StringComparison.Ordinal)) ?? parts.FirstOrDefault();
                return epc is null ? null : epc[..(epc.IndexOf(" [", StringComparison.Ordinal) is > 0 and var i ? i : epc.Length)];
            }

            return Product.Device?.GmdnTerm;
        }
    }

    /// <summary>The newest year of Medicare Part D (else Part B) spending: (program, year, spending, patients).</summary>
    public (string Program, int Year, double Spending, double Patients)? MedicareSpending
    {
        get
        {
            foreach (var program in new[] { "Part D", "Part B" })
            {
                var rows = Product.Spending.Where(s => s.Program == program).ToList();
                if (rows.Count > 0)
                {
                    var year = rows.Max(s => s.Year);
                    var newest = rows.Where(s => s.Year == year).ToList();
                    return ($"Medicare {program}", year, newest.Sum(s => s.Spending), newest.Sum(s => s.Beneficiaries ?? 0));
                }
            }

            return null;
        }
    }

    /// <summary>"Prescription drug, factor Xa inhibitor. Medicare Part D spent $20.8B on it in 2024, for 4.42M patients."</summary>
    public string Summary
    {
        get
        {
            var first = ClassName is { } cls ? $"{KindLabel}: {cls}." : $"{KindLabel}.";
            if (MedicareSpending is { } m)
            {
                return $"{first} {m.Program} spent {Format.ShortMoney(m.Spending)} on it in {m.Year}" +
                       (m.Patients > 0 ? $", for {Format.ShortNumber((long)m.Patients)} patients." : ".");
            }

            var p = Product;
            return $"{first} {Format.Number(p.CompanyCount)} {(p.CompanyCount == 1 ? "company" : "companies")} paid {Format.Number(p.Providers)} providers " +
                   $"{Format.ShortMoney(p.Amount)} in {p.Year} payments naming it.";
        }
    }

    public IReadOnlyList<ProductShortage> CurrentShortages => [.. Product.Shortages.Where(s => s.Status is "Current")];

    /// <summary>Short facts for the summary card: (text, style).</summary>
    public IReadOnlyList<(string Text, string? Style)> Pills
    {
        get
        {
            var pills = new List<(string, string?)> { (KindLabel, null) };
            if (Product.Companies.Count > 0)
            {
                var names = Product.Companies.Take(2).Select(c => CityCatalog.TitleCase(c.Name));
                var more = Product.Companies.Count > 2 ? $" +{Product.Companies.Count - 2}" : "";
                pills.Add(($"Paid for by {string.Join(", ", names)}{more}", null));
            }

            if (Product.Device?.DeviceClass is { } dc)
            {
                pills.Add(($"FDA Class {dc}", null));
            }

            if (CurrentShortages.Count > 0)
            {
                pills.Add(("Current FDA shortage", "alert"));
            }

            if (Product.Drug?.BoxedWarning is not null)
            {
                pills.Add(("Boxed warning", "warn"));
            }

            return pills;
        }
    }

    /// <summary>The icon: "drug" (a capsule) or "device".</summary>
    public string Icon => IsDrug ? "drug" : "device";

    /// <summary>At most about max characters, cut at a space.</summary>
    public static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..(text.LastIndexOf(' ', max) is > 0 and var i ? i : max)] + " …";

    /// <summary>"73%", "23.0%" (one decimal with <paramref name="decimals"/> 1).</summary>
    public static string Percent(double? share, int decimals = 0) =>
        share is { } s ? (s * 100).ToString(decimals == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%" : "—";
}
