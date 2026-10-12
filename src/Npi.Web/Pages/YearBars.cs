namespace Npi.Web.Pages;

/// <summary>
/// A simple column chart of amounts per year (companies and products, docs/PRODUCT_PLAN.md Phase 9), drawn with CSS: each column
/// is a list item with its amount as text, so it reads as a list without the picture.
/// </summary>
public sealed record YearBars(string Label, IReadOnlyList<(string Year, double Value)> Columns)
{
    /// <summary>A column's height as a percentage of the tallest (at least 2 so a small year still shows).</summary>
    public string Height(double value)
    {
        var max = Columns.Count == 0 ? 0 : Columns.Max(c => c.Value);
        var pct = max <= 0 ? 0 : Math.Max(2, value / max * 100);
        return pct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }
}
