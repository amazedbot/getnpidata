# getnpidata

Search every active US healthcare provider (doctors, dentists, nurses, therapists, clinics, hospitals and more) in the
[CMS NPPES](https://download.cms.gov/nppes/NPI_Files.html) National Provider Identifier registry.

Ask questions like *"all chiropractors in Suffolk County, NY"* or *"dentists within 10 miles of ZIP 10001"*, then page
through the results, open any provider, or download every match as a CSV. The same searches are available as a
public REST API and a typed .NET client.

> **Status:** the website, API and data loader are complete and running on the loader PC. Public hosting on Azure
> (App Service + Azure Database for MySQL) is the next step.

---

## Contents

- [What you can do](#what-you-can-do)
- [Search filters](#search-filters)
- [Website](#website)
- [REST API](#rest-api)
- [.NET client library](#net-client-library)
- [The data](#the-data)
- [Limits](#limits)
- [Running it yourself](#running-it-yourself)
- [Project layout](#project-layout)
- [Development](#development)

---

## What you can do

| Capability | Where |
|---|---|
| Search by specialty, location (state, county, city, ZIP, radius), name, NPI, credential, gender and entity type | Website `/`, API `/api/v1/providers` |
| Paged, sortable results with the total match count | Website grid, API JSON |
| Download **every** match as CSV, streamed, with no row cap | "Download CSV" button, `/export.csv`, `/api/v1/providers.csv` |
| See a provider's full record: all specialties with license numbers, all practice locations, other names | `/provider/{npi}`, `/api/v1/providers/{npi}` |
| Look up the lists behind the filters: classifications, specializations, states, counties (FIPS) | `/api/v1/taxonomy/…`, `/api/v1/states/…` |
| Check how fresh the data is and which source files it came from | Site footer, `/api/v1/meta` |
| Interactive API docs (try every call in the browser) | `/swagger` (OpenAPI document at `/openapi/v1.json`) |
| Call the API from .NET with typed models and errors | [`Npi.Client`](src/Npi.Client/README.md) |

Every search URL is shareable: the website, its CSV download and the API all use the same query parameters and the
same search code, so they always return the same providers.

## Search filters

At least one filter is required. Filters combine with AND.

| Parameter | Meaning | Example |
|---|---|---|
| `classification` | NUCC classification. Matches **any** of a provider's up to 15 taxonomies, not just the primary one | `Chiropractor` |
| `specialization` | Specialization within the classification | `Sports Physician` |
| `taxonomy` | A NUCC taxonomy code | `111N00000X` |
| `state` | Two-letter state or territory code of any practice location | `NY` |
| `county` | Five-digit county FIPS code. A ZIP that spans several counties matches **all** of them | `36103` (Suffolk, NY) |
| `city` | City of any practice location (whole name, case-insensitive) | `Babylon` |
| `zip` | Five-digit ZIP of any practice location | `11702` |
| `radius` | Miles around `zip`, 1–100 | `10` |
| `lastName`, `firstName` | Name prefix (individuals) | `smi` |
| `orgName` | Organization name prefix | `north shore` |
| `npi` | Exact ten-digit NPI | `1003000126` |
| `entityType` | `1` = individual, `2` = organization | `2` |
| `gender` | `F` or `M` (individuals) | `F` |
| `credential` | Credential; punctuation and case are ignored (`M.D.` = `MD`) | `DC` |
| `sort` | `name` (default), `npi`, `credential`, `city`, `state`, `zip`, `lastUpdate`, `enumeration`; prefix `-` for descending | `-lastUpdate` |
| `page`, `pageSize` | Page from 1; 1–200 results per page (default 50) | `2`, `100` |

**Locations searched.** Each provider's primary practice address **and** all secondary practice locations. Mailing
addresses are not searched. Each result row shows the location that matched.

## Website

- **`/`**: the search form. Specialty and location use dependent dropdowns (Classification → Specialization,
  State → County). Results appear in a sortable grid, 50 per page, with the total and a **Download CSV** button
  for the same search. The page also works without JavaScript and on phones.
- **`/provider/{npi}`**: one provider's full record, with a link to the official
  [NPPES NPI Registry](https://npiregistry.cms.hhs.gov/) entry.
- **`/export.csv?…`**: the CSV for any search (UTF-8 with BOM, so Excel shows accents correctly). It's named
  `npi_search_<yyyyMMdd>.csv`.
- **Footer**: the date the data is current through and the number of active providers.

Grid and CSV columns: NPI, Entity Type, Name, Credential, Primary Specialty, Address 1, Address 2, City, State, ZIP,
County, Phone, Gender, Enumeration Date, Last Update Date.

## REST API

Base path `/api/v1`. Responses are JSON (camelCase). Errors are [RFC 7807](https://www.rfc-editor.org/rfc/rfc7807)
problem documents. CORS is open for GET, so browser apps on any site can call it.

| Method and path | Returns |
|---|---|
| `GET /api/v1/providers?…filters…` | `{ items, page, pageSize, totalCount, dataAsOf }` |
| `GET /api/v1/providers.csv?…filters…` | Every match as CSV (paging ignored), streamed |
| `GET /api/v1/providers/{npi}` | Full record: taxonomies with licenses, locations, other names. `404` if unknown or deactivated |
| `GET /api/v1/taxonomy/classifications` | All NUCC classifications |
| `GET /api/v1/taxonomy/classifications/{classification}/specializations` | Specializations of one classification |
| `GET /api/v1/states` | States and territories (`code`, `name`) |
| `GET /api/v1/states/{state}/counties` | Counties with their FIPS codes (`fips`, `name`) |
| `GET /api/v1/meta` | Data as-of date, source files, reference-data versions, provider count |

```http
GET /api/v1/providers?classification=Chiropractor&county=36103&pageSize=1
```

```json
{
  "items": [
    {
      "npi": "1164907002", "entityType": 2, "entityTypeName": "Organization",
      "name": "132 NORTH CARLL CHIROPRACTIC PC", "credential": null, "primarySpecialty": "Chiropractor",
      "address1": "132 N CARLL AVE", "address2": null, "city": "BABYLON", "state": "NY", "zip": "11702-2218",
      "county": "Suffolk County", "phone": "6314828829", "gender": null,
      "enumerationDate": "2018-09-26", "lastUpdateDate": "2018-11-26"
    }
  ],
  "page": 1, "pageSize": 1, "totalCount": 961, "dataAsOf": "2026-10-04"
}
```

**Errors.** Validation errors are keyed by the parameter that caused them:

```json
{ "title": "One or more validation errors occurred.", "status": 400,
  "errors": { "radius": ["Radius must be between 1 and 100 miles."] } }
```

| Status | When |
|---|---|
| `400` | Invalid or missing filters (`errors` says which), or a search too broad to page through in time |
| `401` | API keys are switched on and the `X-Api-Key` header is missing or wrong |
| `404` | Unknown or deactivated NPI, unknown classification or state, unknown route |
| `429` | Rate limit reached; wait for the `Retry-After` seconds |

**Rate limit.** 60 requests per minute per client IP across all of `/api/v1` (configurable). The website itself is
not rate-limited.

**API keys.** Off by default. The operator can require an `X-Api-Key` header with one setting; no code change is
needed.

## .NET client library

[`src/Npi.Client`](src/Npi.Client/README.md) is a typed client for the API. It targets netstandard2.0, so it works
on .NET Framework 4.6.2+ and every modern .NET.

```csharp
using Npi.Client;

using var npi = new NpiClient(new Uri("https://<site>/"));
var page = await npi.SearchProvidersAsync(new ProviderSearch { Classification = "Chiropractor", County = "36103" });
Console.WriteLine(page.TotalCount);                       // 961

var provider = await npi.GetProviderAsync("1003000126");  // null if unknown or deactivated
```

It has one method per endpoint. API errors become `NpiApiException`, which carries the status, the message, errors
by parameter and `RetryAfter`. Build the NuGet package with `dotnet pack src/Npi.Client`.

## The data

| Source | Used for | Refreshed |
|---|---|---|
| [CMS NPPES Data Dissemination](https://download.cms.gov/nppes/NPI_Files.html) | Providers, practice locations, other names | Full monthly file + weekly updates, checked daily |
| NPPES Deactivated NPI Report | Deactivated NPIs, which are **never shown** | Monthly |
| [NUCC Health Care Provider Taxonomy](https://www.nucc.org/) | Specialty names (classification / specialization) | When NUCC publishes a new version (twice a year) |
| [HUD USPS ZIP–County crosswalk](https://www.huduser.gov/portal/datasets/usps_crosswalk.html) | ZIP → county | Quarterly |
| [Census Gazetteer files](https://www.census.gov/geographies/reference-files/time-series/geo/gazetteer-files.html) | County names, ZIP centroids for radius search | Yearly |

As of October 2026 the data holds **9,482,099 active providers**, current through 2026-10-04.

All of this is public information published by CMS, HUD, the Census Bureau and NUCC. NPPES data is self-reported by
providers. Verify anything important in the official [NPPES NPI Registry](https://npiregistry.cms.hhs.gov/).

## Limits

- **Paging** stops at the first 10,000 matches. Download the CSV to get them all.
- **Broad searches** (for example a whole state with no other filter) can take a few seconds. A search that would
  take more than 30 seconds to page through asks you to add a filter or download the CSV.
- **Radius search** needs a ZIP with a Census centroid. PO-box-only ZIPs have none, and the search says so.
- **Text filters** are limited to 60 characters. Name filters are prefix matches, never "contains".

## Running it yourself

### How it works

```
CMS NPPES files ──► Npi.Loader (Windows, scheduled) ──► local MySQL (full raw data + search tables)
                                                            │
                                                  Npi.Loader publish (diff sync)   [planned]
                                                            ▼
                         Npi.Web (website + /api/v1) ◄── Azure Database for MySQL (search tables only)
```

The loader downloads the NPPES files, loads them into MySQL with all-or-nothing table swaps (the site never sees a
half-loaded table), refreshes the reference data, and rebuilds compact search tables. The website reads only the
search tables.

### Requirements

- .NET 10 SDK
- MySQL 8.4 with `local_infile=ON`. `deploy/setup-local-mysql.ps1` configures a Windows install.
- About 25 GB of disk for the database, plus room for the ~1.2 GB monthly download
- A free [HUD USPS API token](https://www.huduser.gov/portal/dataset/uspszip-api.html) for the ZIP–county crosswalk

### Configure

Secrets go in user-secrets, environment variables or a git-ignored `appsettings.Local.json`, never in tracked files.

```powershell
dotnet user-secrets --project src/Npi.Loader set "ConnectionStrings:LocalMySql" "Server=localhost;Database=npi;User ID=…;Password=…;AllowLoadLocalInfile=true"
dotnet user-secrets --project src/Npi.Loader set "HudApiToken" "<token>"
dotnet user-secrets --project src/Npi.Web    set "ConnectionStrings:RemoteMySql" "Server=localhost;Database=npi;User ID=…;Password=…"
```

The loader's database user needs all privileges on its database plus `SESSION_VARIABLES_ADMIN`. The website only
needs `SELECT`.

| Setting | Project | Default | Meaning |
|---|---|---|---|
| `ConnectionStrings:LocalMySql` | Loader | — | Database the loader writes (secret) |
| `HudApiToken` | Loader | — | HUD API token (secret) |
| `WorkFolder`, `LogFolder` | Loader | `%ProgramData%\getnpidata\work`, `…\logs` | Downloads and daily log files (kept 60 days) |
| `KeepLastZip` | Loader | `true` | Keep the newest downloaded zip |
| `MinRowRatio` | Loader | `0.95` | A full reload must have at least 95% of the current row count, or it is rejected |
| `HudRefreshDays` | Loader | `14` | How often to re-check the HUD crosswalk |
| `ConnectionStrings:RemoteMySql` | Web | — | Database the site reads (secret) |
| `Api:PermitLimit`, `Api:WindowSeconds` | Web | `60`, `60` | API rate limit per client IP |
| `Api:RequireKey`, `Api:Keys` | Web | `false`, — | Require `X-Api-Key` (keys are secrets) |

Environment variables work too: prefix `NPI_` for the loader (for example `NPI_HudApiToken`). For the site, use the
standard ASP.NET Core form, for example `Api__RequireKey=true`.

### Load the data

```powershell
dotnet run --project src/Npi.Loader -- migrate     # create or upgrade the schema
dotnet run --project src/Npi.Loader -- discover    # dry run: list the NPPES files run would load
dotnet run --project src/Npi.Loader -- run         # load everything new, refresh reference data, rebuild search tables
```

| Command | Does |
|---|---|
| `run` (default) | Load new NPPES files (newest monthly, then weeklies in order, then the deactivation report), refresh stale reference data, and rebuild the search tables if anything changed |
| `migrate` | Apply the numbered migrations in `db/migrations` |
| `discover` | List what `run` would load |
| `load-file <zip>` | Load one NPPES zip manually |
| `reference` | Force-refresh NUCC, HUD and Census reference data |
| `project` | Rebuild the search tables now |
| `publish` | Sync the search tables to Azure *(planned)* |

`run` skips files already loaded, so running it daily is safe. Exit code `0` means everything completed, `1` means
something failed (see the log), and `2` means a bad command line. A first full load takes about an hour.

### Run the site

```powershell
dotnet run --project src/Npi.Web   # http://<this-PC>:5000 and https://<this-PC>:5001, listening on all interfaces
```

To reach it from other devices on your network, allow the port through Windows Firewall. LAN clients use http,
because the development HTTPS certificate is trusted only for localhost.

## Project layout

| Path | What |
|---|---|
| `src/Npi.Core` | Search filters, validation, SQL builder, search, detail and lookup services, CSV writer |
| `src/Npi.Loader` | Console app: discover, download, load, reference data, search tables |
| `src/Npi.Web` | ASP.NET Core site (Razor Pages) and the `/api/v1` REST API, in one app |
| `src/Npi.Client` | Typed .NET client for the API (netstandard2.0) |
| `tests/` | xUnit v3 tests for each project, plus hand-made fixtures with tricky values (commas, quotes, braces, backslashes, Unicode) |
| `db/migrations` | Numbered, idempotent SQL migrations, applied by `Npi.Loader migrate` ([details](db/migrations/README.md)) |
| `deploy/` | [Azure setup and deployment](deploy/azure.md), local MySQL setup script |
| `CLAUDE.md` | Design decisions, as-built notes and progress log |

## Development

```powershell
dotnet build getnpidata.sln
dotnet test
```

CI (GitHub Actions) builds and tests every pull request. The MySQL integration tests run when `NPI_TEST_MYSQL` holds
a connection string to a MySQL server where the user may create `npi_test_it_*` databases; otherwise they are
skipped.

```powershell
$env:NPI_TEST_MYSQL = "Server=localhost;User ID=…;Password=…"
dotnet test
```

Tests run on Microsoft.Testing.Platform (selected in `global.json`). Use `dotnet test`, not
`dotnet test getnpidata.sln`.
