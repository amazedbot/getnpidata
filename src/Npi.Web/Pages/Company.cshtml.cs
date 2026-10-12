using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// /company/{id}: a company that reports to Open Payments, by its CMS ID (CLAUDE.md §7 Stage 5.5 item 17), in the summary-first
/// layout of drawing P09 (docs/PRODUCT_PLAN.md Phase 9, owner 2026-10-12).
/// </summary>
public class CompanyModel(CompanyService companies) : PageModel
{
    public CompanyDetail Company { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        var company = await companies.GetAsync(id, ct);
        if (company is null)
        {
            return NotFound();
        }

        Company = company;
        return Page();
    }

    /// <summary>"2019–2025", or the one year.</summary>
    public string Years => Company.FirstYear is null ? "" : Company.FirstYear == Company.LastYear ? $"{Company.FirstYear}" : $"{Company.FirstYear}–{Company.LastYear}";

    /// <summary>The newest program year with payments, if any.</summary>
    public CompanyYear? Newest => Company.Years.MaxBy(y => y.Year);

    /// <summary>What it makes, from the kinds of products its payments named: "Drug maker", "Device and supply maker", "Drug and device maker" or "Company".</summary>
    public string Kind
    {
        get
        {
            var byKind = Company.TopProducts.GroupBy(p => p.Kind switch { "Drug" or "Biological" => "drug", "Device" or "Medical Supply" => "device", _ => "other" })
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
            var total = byKind.Values.Sum();
            if (total <= 0)
            {
                return "Company";
            }

            var drug = byKind.GetValueOrDefault("drug") / total;
            var device = byKind.GetValueOrDefault("device") / total;
            return drug >= 0.8 ? "Drug maker" : device >= 0.8 ? "Device and supply maker" : drug + device >= 0.5 ? "Drug and device maker" : "Company";
        }
    }

    /// <summary>"Drug maker in NY, United States that paid US providers $5.12B in general and research payments from 2019 to 2025, reaching 332,888 providers."</summary>
    public string Summary
    {
        get
        {
            var c = Company;
            var paid = c.General + c.Research;
            var when = c.FirstYear is null ? "" : c.FirstYear == c.LastYear ? $" in {c.FirstYear}" : $" from {c.FirstYear} to {c.LastYear}";
            var reach = c.Providers is { } n ? $", reaching {Format.Number(n)} providers" : "";
            var where = string.Join(", ", new[] { c.State, c.Country }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var located = where.Length > 0 ? $" in {where}" : "";
            return paid > 0
                ? $"{Kind}{located} that paid US providers {Format.ShortMoney(paid)} in general and research payments{when}{reach}."
                : $"{Kind}{located} reporting to Open Payments{when}, with ownership or investment interests but no general or research payments.";
        }
    }

    public IReadOnlyList<CompanyIntegrityAgreement> ActiveAgreements =>
        [.. Company.IntegrityAgreements.Where(a => string.Equals(a.Status, "Effective", StringComparison.OrdinalIgnoreCase))];

    /// <summary>Short facts for the summary card: (text, style).</summary>
    public IReadOnlyList<(string Text, string? Style)> Pills
    {
        get
        {
            var pills = new List<(string, string?)>();
            if (Company.SecListings is [var s, ..])
            {
                pills.Add(($"{(s.IsParent ? "Parent is a public company" : "Public company")} · {(s.Exchange is null ? "" : s.Exchange + ": ")}{s.Ticker}", null));
            }

            if (Company.Recalls is { } r)
            {
                pills.Add(($"{Format.Number(r.Total)} FDA recall{(r.Total == 1 ? "" : "s")} on record", r.ClassI > 0 ? "warn" : null));
            }

            if (ActiveAgreements.Count > 0)
            {
                pills.Add(("Under an OIG integrity agreement", "alert"));
            }
            else if (Company.IntegrityAgreements.Count > 0)
            {
                pills.Add(("Past OIG integrity agreement", null));
            }

            return pills;
        }
    }

    public YearBars Bars => new("General and research payments by program year",
        [.. Company.Years.OrderBy(y => y.Year).Select(y => (y.Year.ToString(CultureInfo.InvariantCulture), y.General + y.Research))]);
}
