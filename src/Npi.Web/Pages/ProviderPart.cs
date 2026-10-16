using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>
/// One NPI's record for the partials the provider page and an organization's page share (docs/PRODUCT_PLAN.md step 3.9):
/// _SpecialtiesLicenses, _MedicareActivity and _RegistrationIdentifiers.
/// </summary>
public sealed record ProviderPart(ProviderDetail Provider, bool IsPerson);
