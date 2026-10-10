# Npi.Client

Typed .NET client for the getnpidata REST API (`/api/v1`): search every active US healthcare provider in the CMS NPPES registry.
Targets netstandard2.0, so it works on .NET Framework 4.6.2+ and every modern .NET.

```csharp
using Npi.Client;

using var npi = new NpiClient(new Uri("https://<site>/"));            // add apiKey: "…" if the server requires keys

var suffolk = (await npi.GetCountiesAsync("NY")).First(c => c.Name == "Suffolk County");
var page = await npi.SearchProvidersAsync(new ProviderSearch { Classification = "Chiropractor", County = suffolk.Fips });
Console.WriteLine($"{page.TotalCount} chiropractors");                // 961 (data as of 2026-10-04)

var provider = await npi.GetProviderAsync(page.Items[0].Npi);         // null if unknown or deactivated

using var file = File.Create("chiropractors.csv");                    // every match, no row cap
await npi.DownloadProvidersCsvAsync(new ProviderSearch { Classification = "Chiropractor", County = suffolk.Fips }, file);
```

| Method | API |
|---|---|
| `SearchProvidersAsync(search)` | `GET /api/v1/providers` (one page, with `TotalCount`) |
| `DownloadProvidersCsvAsync(search, stream)` | `GET /api/v1/providers.csv` |
| `GetProviderAsync(npi)` | `GET /api/v1/providers/{npi}` |
| `SearchCompaniesAsync(name, page, pageSize)` | `GET /api/v1/companies` |
| `GetCompanyAsync(id)` | `GET /api/v1/companies/{id}` (null if unknown) |
| `GetClassificationsAsync()` / `GetSpecializationsAsync(c)` | `GET /api/v1/taxonomy/classifications[/{c}/specializations]` |
| `GetStatesAsync()` / `GetCountiesAsync(st)` | `GET /api/v1/states[/{st}/counties]` |
| `GetMetaAsync()` | `GET /api/v1/meta` |

**Errors.** Every API error becomes an `NpiApiException`, which carries:
- `StatusCode`;
- `Title` and `Detail`;
- `Errors`: validation messages keyed by parameter name, such as `radius`, or `filter` for the search as a whole;
- `RetryAfter`, for 429 (rate limit) answers.

**HttpClient.** Pass your own `HttpClient`, for example from `IHttpClientFactory`, with `new NpiClient(httpClient, baseAddress)`. The client doesn't dispose an `HttpClient` you pass in.

**Package.** Build it with `dotnet pack src/Npi.Client -o <folder>`. The package isn't published to nuget.org; reference the `.nupkg` from a local feed, or reference the project directly.
