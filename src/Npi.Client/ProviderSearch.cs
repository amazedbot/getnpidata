using System.Globalization;
using System.Text;

namespace Npi.Client;

/// <summary>
/// Search filters for <see cref="NpiClient.SearchProvidersAsync"/> and <see cref="NpiClient.DownloadProvidersCsvAsync"/>.
/// Set at least one filter. Properties map one-to-one to the API's query parameters.
/// </summary>
public sealed class ProviderSearch
{
    /// <summary>NUCC classification, e.g. "Chiropractor" (<see cref="NpiClient.GetClassificationsAsync"/>).</summary>
    public string? Classification { get; set; }

    /// <summary>Specialization within <see cref="Classification"/> (<see cref="NpiClient.GetSpecializationsAsync"/>).</summary>
    public string? Specialization { get; set; }

    /// <summary>NUCC taxonomy code, e.g. 111N00000X.</summary>
    public string? Taxonomy { get; set; }

    /// <summary>Two-letter state code of any practice location.</summary>
    public string? State { get; set; }

    /// <summary>Five-digit county FIPS code (<see cref="NpiClient.GetCountiesAsync"/>).</summary>
    public string? County { get; set; }

    public string? City { get; set; }

    /// <summary>Five-digit ZIP.</summary>
    public string? Zip { get; set; }

    /// <summary>Miles around <see cref="Zip"/>, 1–100.</summary>
    public int? Radius { get; set; }

    /// <summary>Last name prefix (individuals).</summary>
    public string? LastName { get; set; }

    /// <summary>First name prefix (individuals).</summary>
    public string? FirstName { get; set; }

    /// <summary>Organization name prefix.</summary>
    public string? OrgName { get; set; }

    /// <summary>Ten-digit NPI.</summary>
    public string? Npi { get; set; }

    /// <summary>1 = individual, 2 = organization.</summary>
    public int? EntityType { get; set; }

    /// <summary>F or M.</summary>
    public string? Gender { get; set; }

    /// <summary>Credential, punctuation ignored ("M.D." = "MD").</summary>
    public string? Credential { get; set; }

    /// <summary>true: only providers on the HHS-OIG exclusion list (matched by NPI); false: leave them out.</summary>
    public bool? Excluded { get; set; }

    /// <summary>true: only practitioners with an active Medicare opt-out; false: leave them out.</summary>
    public bool? OptedOut { get; set; }

    /// <summary>true: only providers eligible to order or refer in Medicare; false: only those who aren't.</summary>
    public bool? OrderRefer { get; set; }

    /// <summary>true: only clinicians who accept Medicare assignment (Care Compare); false: everyone else.</summary>
    public bool? AcceptsAssignment { get; set; }

    /// <summary>true: only clinicians who offer telehealth (Care Compare); false: everyone else.</summary>
    public bool? Telehealth { get; set; }

    /// <summary>primaryCare, dental or mentalHealth: a practice location in a county with an HRSA shortage area of that kind.</summary>
    public string? Shortage { get; set; }

    /// <summary>true: only providers who billed Medicare Part B or Part D in the latest data year; false: those who didn't.</summary>
    public bool? MedicareActive { get; set; }

    /// <summary>At least this many years since graduation (Care Compare), 1–70.</summary>
    public int? MinYears { get; set; }

    /// <summary>New providers: enumerated within the last this many days, 1–3650.</summary>
    public int? NewWithinDays { get; set; }

    /// <summary>NPPES record updated within the last this many days, 1–3650.</summary>
    public int? UpdatedWithinDays { get; set; }

    /// <summary>name (default), npi, credential, city, state, zip, lastUpdate or enumeration; prefix "-" for descending.</summary>
    public string? Sort { get; set; }

    /// <summary>Page number from 1 (ignored by the CSV download).</summary>
    public int? Page { get; set; }

    /// <summary>1–200, default 50 (ignored by the CSV download).</summary>
    public int? PageSize { get; set; }

    /// <summary>"?classification=…&amp;state=…" with only the properties that are set.</summary>
    public string ToQueryString()
    {
        var sb = new StringBuilder();
        void Add(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            sb.Append(sb.Length == 0 ? '?' : '&').Append(name).Append('=').Append(Uri.EscapeDataString(value!.Trim()));
        }

        void AddInt(string name, int? value) => Add(name, value?.ToString(CultureInfo.InvariantCulture));
        void AddBool(string name, bool? value) => Add(name, value is null ? null : value.Value ? "true" : "false");

        Add("classification", Classification);
        Add("specialization", Specialization);
        Add("taxonomy", Taxonomy);
        Add("state", State);
        Add("county", County);
        Add("city", City);
        Add("zip", Zip);
        AddInt("radius", Radius);
        Add("lastName", LastName);
        Add("firstName", FirstName);
        Add("orgName", OrgName);
        Add("npi", Npi);
        AddInt("entityType", EntityType);
        Add("gender", Gender);
        Add("credential", Credential);
        AddBool("excluded", Excluded);
        AddBool("optedOut", OptedOut);
        AddBool("orderRefer", OrderRefer);
        AddBool("acceptsAssignment", AcceptsAssignment);
        AddBool("telehealth", Telehealth);
        AddInt("minYears", MinYears);
        AddBool("medicareActive", MedicareActive);
        Add("shortage", Shortage);
        AddInt("newWithinDays", NewWithinDays);
        AddInt("updatedWithinDays", UpdatedWithinDays);
        Add("sort", Sort);
        AddInt("page", Page);
        AddInt("pageSize", PageSize);
        return sb.ToString();
    }
}
