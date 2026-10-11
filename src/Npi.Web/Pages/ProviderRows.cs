using System.Globalization;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>The rows of a result list (_ProviderRows); <see cref="Page"/> makes the headers sortable.</summary>
public sealed record ProviderRows(IReadOnlyList<ProviderSummary> Items, string Caption, IndexModel? Page = null)
{
    public sealed record Badge(string Text, string Kind, string Title);

    /// <summary>A provider's badges, most important first (a row shows at most three).</summary>
    public static IEnumerable<Badge> Badges(ProviderSummary p)
    {
        if (p.Flags.Excluded)
        {
            yield return new Badge("OIG excluded", "alert", "On the HHS-OIG List of Excluded Individuals/Entities");
        }

        if (p.Flags.OptedOutOfMedicare)
        {
            yield return new Badge("Medicare opt-out", "warn", "Has an active Medicare opt-out affidavit");
        }

        if (p.EnumerationDate is { } enumerated && enumerated >= DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-90))
        {
            yield return new Badge("New", "", "NPI issued " + enumerated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (p.Flags.AcceptsMedicareAssignment)
        {
            yield return new Badge("Accepts Medicare", "good", "Accepts Medicare's approved amount as full payment (Care Compare)");
        }

        if (p.Flags.OffersTelehealth)
        {
            yield return new Badge("Telehealth", "good", "Offers telehealth (Care Compare)");
        }
    }
}
