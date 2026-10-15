# getnpidata

Search every active US healthcare provider (doctors, dentists, nurses, therapists, clinics, hospitals and more) in the
[CMS NPPES](https://download.cms.gov/nppes/NPI_Files.html) National Provider Identifier registry.

Ask questions like *"all chiropractors in Suffolk County, NY"* or *"dentists within 10 miles of ZIP 10001"*, then page
through the results, open any provider, or download every match as a CSV. The same searches are available as a
public REST API and a typed .NET client.

Each provider is enriched with other public data keyed by NPI: HHS-OIG exclusions, Medicare opt-out and
order/refer eligibility, Medicare Care Compare (accepts assignment, telehealth, years in practice, group practices,
hospital affiliations), Medicare services and prescribing, industry payments (Open Payments) and the health
facilities an organization runs. Counties come with population and shortage-area facts.

> **Status:** the website, API and data loader run on the project's own PC; public hosting on Azure comes later.
> To run your own copy, see [Running it yourself](#running-it-yourself).

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
| Type what you want in the search box at the top of every page: an NPI (opens the provider's page), a name (people and organizations), or a specialty and a place in plain words ("chiropractors in Suffolk County, NY", "dentists near 11701", "cardiologists in Houston"), with suggestions as you type | Every page, `/search?q=` |
| Build a list with filters and export it with the columns you need (summary, contact or compliance), as CSV or Excel | `/search`, `/export?columns=&format=csv|xlsx` |
| Search by specialty, location (state, county, city, ZIP, radius), name, NPI, credential, gender and entity type | Website `/`, API `/api/v1/providers` |
| Find names despite typos or a different spelling, and organizations by any words of their legal or DBA name | "Name match: Similar" on the search and map pages, `nameMatch=similar` |
| See Medicare billing and Part D prescribing for every year since 2013 on profiles; the record-quality label notes when a provider stopped billing Medicare ("Last billed Medicare in 2016") | Provider pages ("Medicare by year"), API `medicareHistory` |
| Filter by OIG exclusion, Medicare opt-out, order/refer eligibility, Medicare assignment, telehealth, years in practice, Medicare activity and shortage areas | Same (see [Search filters](#search-filters)) |
| Filter by the **kind of building** at a practice address: commercial or institutional, residential (often a home office), or unknown; each provider page shows the building and what the label rests on | "Practice building" on the search and map pages, `addressType`, CSV column "Practice Building" |
| See how **current and trustworthy** each record looks: **Current**, **Check** (something conflicts or is missing: Medicare's Care Compare lists another address or profession, a lapsed state license, an address that can't be mapped or is a PO box, no phone, a business at the address listed as permanently closed or a certified facility there that closed, a 10+ year old record whose only sign of practice is a map listing, or an old record with no sign of practice) or **Stale** (not updated in NPPES for 10+ years and no sign of current practice anywhere in the public data), with the reasons; hide stale records from any list | Provider pages ("Record quality"), result badges, `/verify` tabs, CSV/Excel columns "Record Quality" and "Quality Reasons", `quality` filter, API `quality` |
| Find **new providers** (NPI issued in the last 30/90/365 days) and **recently updated** records in any area; sort newest first for a feed | Same (`newWithinDays`, `updatedWithinDays`) |
| See compliance and Medicare badges on every result | Website grid, CSV and API flags |
| Paged, sortable results with the total match count | Website grid, API JSON |
| Search **on a map**: pins at practice street addresses, "Search in this area", near me, and a sortable table of the pins in view | Website `/map` ("View on map" from any search) |
| Download **every** match as CSV, streamed, with no row cap | "Download CSV" button, `/export.csv`, `/api/v1/providers.csv` |
| See a provider's profile: who they are in one sentence ("Cardiologist in West Islip, NY since 2005, part of a 96-clinician group"), status at a glance (active, exclusions, Medicare opt-out, board actions), a map of the practice locations, what the specialty means in plain words, a "Download raw data" button (JSON or CSV), and **over the years**: each earlier version of the record in the monthly NPPES files since 2007 in plain words (moves, practice locations added or dropped, specialty changes, deactivation and reactivation, updates). A deactivated NPI shows "Deactivated on [date]" and its last known record (place, specialty, dates; no name) | `/provider/{npi}`, `/provider/{npi}/raw.json`, `/provider/{npi}/raw.csv` |
| Understand an area: how many providers a state or county has (all, or one specialty), per 100,000 people compared with the state and the US, new providers in the last 90 days, shortage areas, the cities with the most providers, hospitals and recorded changes; every number links to its list | `/area`, `/area/{state}`, `/area/{state}/{county FIPS}?category=&subcategory=` |
| See an area **over time**: a map of the states or a state's counties coloured by providers per 100,000, new NPIs, net change, moves, deactivations or stale records, with shortage areas hatched; month by month figures (providers, new and deactivated NPIs, moves, specialty, license and exclusion changes, record-quality mix), each count listing the providers behind it; back to November 2007 from the archived monthly NPPES files (CMS NPPES; historical copies via NBER, [doi.org/10.60592/bjwk-8n62](https://doi.org/10.60592/bjwk-8n62)), in today's county boundaries, counted by every practice location from September 2018 (the first file that lists secondary ones, as today) and by the primary practice location before | Area pages; `/area/{state}/{county}/changes?kind=&month=` (with CSV), `/api/v1/areas/{us\|state\|state/county}/history`, `/api/v1/areas/map` |
| See an organization's, group practice's or facility's profile: its clinicians by specialty (with a full list to export), locations, who's responsible, and for hospitals, nursing homes, home health agencies and hospices how CMS rates them and which NPIs they bill under | `/provider/{npi}`, `/group/{pacId}`, `/facility/{ccn}`, `…/clinicians.csv` |
| See a provider's full record: all specialties with license numbers, all practice locations, other names, registration details (mailing address, authorized official, parent organization), other identifiers, electronic endpoints (Direct addresses, FHIR), compliance, state license status and board actions (AL, CA, CO, CT, DE, FL, IL, MD, MI, NY, RI, TX, VT, WA, WV), Care Compare, MIPS quality scores, facilities (hospitals with HCAHPS patient-survey stars and deaths / safety / readmission measures vs the national rate, nursing homes, home health agencies, hospices, dialysis facilities, surgery centers, rehabilitation and long-term care hospitals), Medicare services and prescribing, industry payments (newest year by kind; every year since 2019 with research and ownership interests; top companies over all years), and the changes recorded between NPPES updates (name, credential, primary specialty, primary address) | `/provider/{npi}`, `/api/v1/providers/{npi}` |
| See **who owns a facility**: the owners CMS lists for a hospital's, nursing home's, home health agency's, hospice's, health center's or rural clinic's Medicare enrollment, with their role, stake and since when, marked as private equity, REIT, holding company …; its changes of ownership since 2016 (hospitals and nursing homes) with the owners before; and a page per owning organization with every facility it owns or runs, linked to its own NPIs, facility and group by its CMS ID only (people who own or run a facility are named on the facility's page only, with no page or search of their own) | `/facility/{ccn}` ("Who owns it", "Ownership changes"), `/owners`, `/owner/{id}`; API `GET /api/v1/facilities/{ccn}/ownership`, `GET /api/v1/owners?q=&kind=&page=&pageSize=`, `GET /api/v1/owners/{id}` |
| See a **nursing home's inspections and penalties**: health and fire safety deficiencies of the last three inspection cycles by scope and severity (harm and immediate jeopardy marked), the most serious and newest citations in CMS's words, every inspection, and the fines and payment denials of the last three years | `/facility/{ccn}` ("Inspections and penalties"); API `GET /api/v1/facilities/{ccn}/inspections` |
| Start from **Facilities & hospitals**: every Medicare-certified facility (hospitals, nursing homes, home health agencies, hospices, FQHCs, rural health clinics, dialysis facilities, surgery centers, inpatient rehabilitation and long-term care hospitals), active or closed, with CMS ratings, recent changes of ownership, nursing home penalties, a map and CSV; each facility's page shows **how it compares** with the nation on CMS's main measures (deaths, complications, infections, readmissions, emergency department times, spending; dialysis outcomes against what is expected; surgery center hospital visits and patient survey: professional care and a clean facility, communication about the procedure, the facility's rating, would recommend; a dialysis facility's Medicare quality incentive score and payment reduction) and a hospital's **Medicare volume** (discharges, outpatient services, payments, top DRGs and APCs, with the data year); or from **Groups & organizations**: Medicare groups by size with their MIPS score, organization NPIs by kind and record quality, CSV | Website `/facilities` (`/hospitals`), `/organizations` (`/groups`); API `GET /api/v1/facilities[.csv]?q=&type=&state=&minRating=&maxRating=&status=&ownershipChangedWithinYears=&hasPenalties=&sort=&page=&pageSize=`, `GET /api/v1/facilities/{ccn}?page=`, `GET /api/v1/groups[.csv]?q=&state=&minClinicians=&maxClinicians=&sort=&page=&pageSize=` |
| See which **health system** a hospital, nursing home or group belongs to (AHRQ Compendium), each system's hospitals, facilities and groups; browse **Medicare ACOs** (Shared Savings Program) with their track and participant organizations, and see "In ACO …" on organization and group pages (CMS lists participants by name only, so these are marked as **name matches**); find **HRSA health center sites** (FQHCs and look-alikes, school clinics, mobile vans) in the facility list, matched to certified FQHCs by Medicare number or NPI; see which facilities are **340B covered entities** and list only those | Website `/systems`, `/system/{id}`, `/acos`, `/aco/{id}`, `/health-center/{siteId}`, `/facilities?system=&type=health_center&in340b=true`; API `GET /api/v1/systems?q=&state=&sort=&page=&pageSize=`, `/api/v1/systems/{id}`, `/api/v1/acos?q=&state=&page=&pageSize=`, `/api/v1/acos/{id}`, `/api/v1/providers/{npi}/acos`, `/api/v1/groups/{pacId}/acos`, `/api/v1/health-center-sites/{id}` |
| On **Find care**, see the community health centers nearest you for a family doctor, children's doctor, dentist or mental health, which charge on a sliding fee scale based on income | `/find-care` |
| Start from **Companies & products**: totals of what drug and device makers paid providers each year, the companies that paid the most and the products named most, a search across both, and every company or product as CSV | `/companies-and-products`, `/companies.csv`, `/products.csv`, `/api/v1/companies.csv`, `/api/v1/products.csv` |
| See **companies that pay providers** (each page has its full record as raw data, `/company/{id}/raw.json`): what each drug or device maker paid per year, what for, which products the payments named, which specialties and which providers it paid the most, plus its FDA recalls, SEC stock listing and HHS-OIG integrity agreements; linked from every provider's payments | `/companies`, `/company/{id}`, `/api/v1/companies[/{id}]` |
| See **products in industry payments**: for each drug, device or supply named in payments, which companies pay for it, what for, which specialties and providers they pay most, and the research studies naming it, plus what it is from FDA (drugs: generic name, ingredients, drug class, approval, other makers, approved uses and any boxed warning from the label; devices: description, FDA class, 510(k)/PMA decisions), and public data: Medicare and Medicaid spending by year, what pharmacies pay (NADAC), FDA shortages and recalls, adverse event report counts, ClinicalTrials.gov studies, and which providers paid for a drug also prescribe it to Medicare patients (counts, a sortable list and a CSV); linked from company and provider pages; each page has its full record as raw data | `/products`, `/product/{slug}`, `/product/{slug}/raw.json`, `/api/v1/products[/{slug}]` |
| Find care for yourself (patients): pick a kind of care (family doctor, dentist, mental health …), a ZIP or your location, and optional preferences (takes Medicare, video visits, a woman or a man); see the nearest providers with phone, address, directions and a hospital they work with | `/find-care` |
| Verify a roster: paste NPIs or upload a CSV or Excel file, see how many are active, deactivated (with the date), excluded, opted out of Medicare, not found or invalid, filter the rows, and download every row with its details; optionally, what changed for each NPI since a date (location, specialty, status, from the monthly files since 2007 and the changes recorded since October 2026) | `/verify` (also `/lookup`), `POST /api/v1/providers/lookup[.csv]` |
| County insights: population and HRSA Health Professional Shortage Areas | Search page (when a county is chosen), `/api/v1/counties/{fips}` |
| Look up the lists behind the filters: specialty categories and subcategories, NUCC classifications and specializations, states, counties (FIPS) | `/api/v1/taxonomy/…`, `/api/v1/states/…` |
| Check how fresh the data is and which source files it came from | Site footer, `/api/v1/meta` |
| A guide to the API: quick start (curl and .NET), limits, freshness, endpoints by task | `/developers` |
| Interactive API docs (try every call in the browser) | `/swagger` (OpenAPI document at `/openapi/v1.json`) |
| What the site measures about visits (Azure Application Insights: paths and timings, no search terms, IP addresses or cookies) | `/privacy` |
| Call the API from .NET with typed models and errors | [`Npi.Client`](src/Npi.Client/README.md) |

Every search URL is shareable: the website, its CSV download and the API all use the same query parameters and the
same search code, so they always return the same providers.

## Search filters

Every filter is optional: none lists every active provider. Filters combine with AND.

| Parameter | Meaning | Example |
|---|---|---|
| `category` | Plain specialty category. Matches **any** of a provider's up to 15 taxonomies | `Doctors (MD, DO)` |
| `subcategory` | Subcategory within the category | `Heart (cardiology)` |
| `classification` | NUCC classification. Matches **any** of a provider's up to 15 taxonomies, not just the primary one | `Chiropractor` |
| `specialization` | Specialization within the classification | `Sports Physician` |
| `taxonomy` | A NUCC taxonomy code | `111N00000X` |
| `state` | Two-letter state or territory code of any practice location | `NY` |
| `county` | Five-digit county FIPS code. A ZIP that spans several counties matches **all** of them | `36103` (Suffolk, NY) |
| `city` | City of any practice location: the name as written (whole name, case-insensitive), or a ZIP that USPS gives that name, so misspellings in the record ("BROOKYLN") are found too. The search form's City dropdown lists the USPS names of the chosen state or county (`GET /api/v1/states/{state}/cities`) | `Babylon` |
| `zip` | Five-digit ZIP of any practice location | `11702` |
| `radius` | Miles around `zip`, 1–100 | `10` |
| `lastName`, `firstName` | Name prefix (individuals) | `smi` |
| `orgName` | Organization name prefix | `north shore` |
| `nameMatch` | `prefix` (default) or `similar`: last/first names also match by sound, so typos and spelling variants are found ("Smiht" → Smith, "Nunes" → Nuñez, "Jonhson" → Johnson); organization names match when every word appears anywhere in the legal name or another (DBA) name, in any order. Closest matches come first unless you choose a sort | `similar` |
| `npi` | Exact ten-digit NPI | `1003000126` |
| `entityType` | `1` = individual, `2` = organization | `2` |
| `gender` | `F` or `M` (individuals) | `F` |
| `credential` | A standardized credential (`GET /api/v1/credentials`; the search form's dropdown). It matches however the provider wrote it: `MD` finds "M.D.", "MD", "MD, PhD". Other text is a prefix of the credential as written, punctuation ignored | `PharmD` |
| `excluded` | `true`: only providers on the HHS-OIG exclusion list (matched by NPI); `false`: leave them out | `false` |
| `optedOut` | `true`: only practitioners with an active Medicare opt-out; `false`: leave them out | `false` |
| `orderRefer` | `true`: only providers eligible to order or refer in Medicare | `true` |
| `acceptsAssignment` | `true`: only clinicians who accept Medicare assignment (Care Compare) | `true` |
| `telehealth` | `true`: only clinicians who offer telehealth (Care Compare) | `true` |
| `minYears` | At least this many years since graduation (Care Compare), 1–70 | `20` |
| `medicareActive` | `true`: only providers who billed Medicare Part B or Part D in the latest data year | `true` |
| `shortage` | `primaryCare`, `dental` or `mentalHealth`: a practice location in a county with that kind of shortage area | `dental` |
| `addressType` | `commercial`, `residential` or `unknown`: a practice location in that kind of building (FEMA USA Structures, and signs of a business at the address) | `residential` |
| `quality` | Record quality: `notStale` (hide stale records), `current`, `check` or `stale` | `notStale` |
| `newWithinDays` | New providers: NPI issued (enumerated) within the last this many days, 1–3650. With `sort=-enumeration`, a newest-first feed | `30` |
| `updatedWithinDays` | NPPES record updated within the last this many days, 1–3650 | `7` |
| `sort` | `name` (default), `npi`, `credential`, `city`, `state`, `zip`, `lastUpdate`, `enumeration`; prefix `-` for descending | `-lastUpdate` |
| `page`, `pageSize` | Page from 1; 1–200 results per page (default 50) | `2`, `100` |

**Locations searched.** Each provider's primary practice address **and** all secondary practice locations. Mailing
addresses are not searched. Each result row shows the location that matched.

## Website

- **`/`**: the search form. Specialty and location use dependent dropdowns (Category → Subcategory in plain words,
  e.g. "Doctors (MD, DO)" → "Heart (cardiology)", with NUCC's exact Classification → Specialization folded underneath;
  State → County → City). Results appear in a sortable grid, 50 per page, with the total and a **Download CSV** button
  for the same search, and **View on map** opens the map search with the same filters. The page also works without
  JavaScript and on phones.
- **`/map`**: map search on [OpenStreetMap](https://www.openstreetmap.org/).
  - Move the map and choose **Search in this area**: every provider practicing inside the visible area that matches
    the filters. The filters sit beside the map as on the search page (specialty first, the rest under "More
    filters"); the map area replaces state/county/city/ZIP. The map's button is the only search button.
  - One pin per street address. Hover a pin to see who practices there (everyone at that address); click it for the
    same list with links. Solid pins are at the building (Overture Maps address points and places), white pins are
    estimated along the street (Census geocoder), and gray dashed pins are approximate: the address couldn't be
    located, so it sits at its ZIP code's center.
  - The table below the map (beside it on very wide screens) lists every provider at the pins in view, and
    updates as you pan. Sort it by distance from the map center, name, specialty, city or last update, either way.
  - At most 1,000 providers per search, the closest to the center. **Open as list** and **Download CSV** give every
    match in the searched area, with the same filters (the list's "Map area" filter, `bbox` in the API). A view too large to
    search (more than about 170 miles across) says how large it is and how large an area can be searched, and the
    button becomes **Zoom in and search**, which zooms in on the middle of the view and searches there.
  - **Near me** centers the map on your location (the browser asks first; it stays in your browser) and searches
    there. Browsers offer location only over https (or on localhost).
  - The search URL (filters + area) is shareable.
- **`/provider/{npi}`**: one provider's full record, with a link to the official
  [NPPES NPI Registry](https://npiregistry.cms.hhs.gov/) entry. **Recorded changes** lists what changed between
  NPPES updates (name, credential, primary specialty, primary practice address), from October 2026 on: NPPES itself
  publishes only the current record. **Over the years** shows the earlier versions from the archived monthly files
  (November 2007 to September 2026; CMS NPPES, historical copies via NBER before June 2015), the newest four first and
  "Show all"; the same history is `history` in the API record.
- Results enumerated in the last 90 days carry a **New** badge.
- **`/verify`** (also `/lookup`): verify NPIs. Paste NPIs or upload a CSV, text or Excel file (up to 50,000 NPIs,
  10 MB); every 10-digit number counts, or, when the first row names a column with "NPI", only that column. The page
  shows a summary (active, deactivated, OIG excluded, opted out of Medicare, not found or invalid), tabs to filter the
  rows ("Needs attention" first), each NPI's result ("Deactivated on [date]", "On the OIG exclusion list since …",
  "Opted out of Medicare until …") and the sources' dates. The CSV and Excel downloads have one row per NPI, in your
  order, with a Lookup Status (Active, Deactivated, Not found, Invalid NPI — a wrong check digit, usually a typo), the
  deactivation, exclusion and opt-out end dates, and the summary columns. With **Changed since** (a date), each row
  also says what changed after it ("Changed since Jan 1, 2020: moved, specialty", the details under "What changed"),
  a "Changed since" tab lists those that did, and the downloads add Changed Since and Changes columns; the API takes
  `"changedSince": "YYYY-MM-DD"` in the same request.
- **`/export.csv?…`**: the CSV for any search (UTF-8 with BOM, so Excel shows accents correctly). It's named
  `npi_search_<yyyyMMdd>.csv`. With no filter it is every active provider, sorted by NPI, from a ready-made file the
  loader rebuilds after each load (`npi_all_providers_<as-of date>.csv`, ~2.4 GB, sent gzipped, ~600 MB);
  `/export/all.csv.gz` downloads that file still gzipped.
- **Footer**: the date the data is current through and the number of active providers.

Credentials are shown standardized ("M.D." and "MD" both show as MD; "M.S., CCC-SLP" as "MS, CCC-SLP"), and the
search form's Credential box lists every credential held by at least 100 providers nationwide, with counts, and
**Other** for everyone holding a rarer credential; typing narrows the list to the credentials starting with what you
typed ("m" → MD, MS, MA …; punctuation and case don't matter).

Grid and CSV columns: NPI, Entity Type, Name, Credential, Primary Specialty, Address 1, Address 2, City, State, ZIP,
County, Phone, Gender, Enumeration Date, Last Update Date, then the flags OIG Excluded, Medicare Opt-Out, Medicare
Order/Refer, Accepts Medicare Assignment, Telehealth and Billed Medicare.

## REST API

Base path `/api/v1`. Responses are JSON (camelCase). Errors are [RFC 7807](https://www.rfc-editor.org/rfc/rfc7807)
problem documents. CORS is open for GET, so browser apps on any site can call it.

| Method and path | Returns |
|---|---|
| `GET /api/v1/providers?…filters…` | `{ items, page, pageSize, totalCount, dataAsOf }` |
| `GET /api/v1/providers.csv?…filters…` | Every match as CSV (paging ignored), streamed |
| `GET /api/v1/providers/{npi}` | Full record, including the enrichment sections. `404` if unknown or deactivated |
| `POST /api/v1/providers/lookup` | Body `{ "npis": [...] }`, up to 1,000. `{ items: [{ npi, status, provider, deactivationDate, exclusionDate, optOutEndDate }] }`, one per requested NPI in order; `status` is `found`, `deactivated`, `notFound` or `invalid` |
| `POST /api/v1/providers/lookup.csv` | The same, as CSV |
| `GET /api/v1/taxonomy/categories` | The plain specialty categories |
| `GET /api/v1/taxonomy/categories/{category}/subcategories` | Subcategories of one category |
| `GET /api/v1/taxonomy/classifications` | All NUCC classifications |
| `GET /api/v1/taxonomy/classifications/{classification}/specializations` | Specializations of one classification |
| `GET /api/v1/credentials` | Standardized credentials with provider counts (`credential`, `providers`), most common first |
| `GET /api/v1/states` | States and territories (`code`, `name`) |
| `GET /api/v1/states/{state}/counties` | Counties with their FIPS codes (`fips`, `name`) |
| `GET /api/v1/states/{state}/cities?county=` | City names for the `city` parameter: the names USPS uses for the state's (or the county's) ZIPs with practice locations |
| `GET /api/v1/counties/{fips}` | County population and the shortage areas in force |
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
var rows = await npi.LookupProvidersAsync(myNpis);         // any number; sent 1,000 per request
```

It has one method per endpoint. API errors become `NpiApiException`, which carries the status, the message, errors
by parameter and `RetryAfter`. Build the NuGet package with `dotnet pack src/Npi.Client`.

## The data

| Source | Used for | Refreshed |
|---|---|---|
| [CMS NPPES Data Dissemination](https://download.cms.gov/nppes/NPI_Files.html) | Providers, practice locations, other names | Full monthly file + weekly updates, checked daily |
| NPPES Deactivated NPI Report | Deactivated NPIs, which are **never shown** | Monthly |
| Archived monthly NPPES files: CMS's own from June 2015 (still served at their usual URLs), before that the copies kept by NBER ([doi.org/10.60592/bjwk-8n62](https://doi.org/10.60592/bjwk-8n62)) | The area figures back to November 2007, each NPI's history (`npi_history`: profiles' "Over the years", Verify NPIs' "changed since") and, from September 2018, the secondary practice locations; 30 months have no surviving file | `Npi.Loader history` once (~140 GB downloaded, each file deleted after use); then every monthly file `run` loads |
| [NUCC Health Care Provider Taxonomy](https://www.nucc.org/) | Specialty names (classification / specialization) | When NUCC publishes a new version (twice a year) |
| [HUD USPS ZIP–County crosswalk](https://www.huduser.gov/portal/datasets/usps_crosswalk.html) | ZIP → county | Quarterly |
| [Census Gazetteer files](https://www.census.gov/geographies/reference-files/time-series/geo/gazetteer-files.html) | County names, ZIP centroids for radius search | Yearly |
| [Overture Maps](https://overturemaps.org/) addresses (US DOT National Address Database) and places | Map pins at the building: address points, the addresses of places (hospitals, campuses), building names | `Npi.Loader overture` (every state), when a new release is out |
| [Census Geocoder](https://geocoding.geo.census.gov/) (batch) | Map pins where Overture has no match: estimated along the street | New addresses with each load; each address once |
| FEMA [USA Structures](https://gis-fema.hub.arcgis.com/pages/usa-structures) (Oak Ridge National Laboratory; the [GeoParquet copy on Source Cooperative](https://source.coop/wherobots/usa-structures)) | The kind of building at each practice address (commercial or institutional, residential) | When the files change, or after 50,000 newly mapped addresses (checked weekly) |
| [HHS-OIG LEIE](https://oig.hhs.gov/exclusions/) | Exclusions from federal health programs | Monthly |
| [CMS Opt Out Affidavits, Order and Referring](https://data.cms.gov/) | Medicare opt-out, order/refer eligibility | Monthly / weekly |
| [CMS Medicare Fee-For-Service Public Provider Enrollment and group reassignments](https://data.cms.gov/) | Which NPIs make up a Medicare group practice, and which clinicians bill through it | Quarterly / monthly |
| [Medicare Care Compare (Provider Data Catalog)](https://data.cms.gov/provider-data/) | Clinicians, group practices, hospital affiliations, hospitals and nursing homes | Monthly |
| CMS Hospital, SNF, home health, hospice, FQHC and rural health clinic Enrollments | Linking facilities (CCN) to NPIs and to their Medicare enrollment | Quarterly |
| CMS [All Owners](https://data.cms.gov/) files (hospitals, skilled nursing facilities, home health agencies, hospices, FQHCs, rural health clinics) and the hospital and SNF [Change of Ownership](https://data.cms.gov/) files with their owner information | Who owns and runs each facility (role, stake, kind of owner), pages for owning organizations, ownership changes since 2016 and the owners before, the "Changed ownership" filter. Joined to facilities by Medicare enrollment ID; owners to NPIs, facilities and groups by CMS associate ID only | Monthly / quarterly |
| Care Compare nursing home [Health Deficiencies, Fire Safety Deficiencies and Penalties](https://data.cms.gov/provider-data/topics/nursing-homes) | Nursing home inspections (last three cycles) and fines and payment denials (last three years) | Monthly |
| Care Compare facility files: [dialysis facilities](https://data.cms.gov/provider-data/dataset/23ew-n7w9) (listing, patient survey), [ambulatory surgical centers](https://data.cms.gov/provider-data/dataset/4jcv-atw7), [inpatient rehabilitation facilities](https://data.cms.gov/provider-data/dataset/7t8x-u3ir), [long-term care hospitals](https://data.cms.gov/provider-data/dataset/azum-44iv); the [OAS CAHPS survey for surgical centers](https://data.cms.gov/provider-data/dataset/48nr-hqxx); the [ESRD QIP total performance scores](https://data.cms.gov/provider-data/dataset/tps); hospital Complications and Deaths, Healthcare Associated Infections, Unplanned Hospital Visits, Timely and Effective Care, Medicare Spending Per Beneficiary | Facility types, ratings and "How it compares" on facility pages (each facility's value, the national value and CMS's comparison) | Monthly or quarterly |
| CMS [Medicare Inpatient Hospitals by Provider](https://data.cms.gov/provider-summary-by-type-of-service/medicare-inpatient-hospitals/medicare-inpatient-hospitals-by-provider) and [by Provider and Service](https://data.cms.gov/provider-summary-by-type-of-service/medicare-inpatient-hospitals/medicare-inpatient-hospitals-by-provider-and-service), [Outpatient Hospitals by Provider and Service](https://data.cms.gov/provider-summary-by-type-of-service/medicare-outpatient-hospitals/medicare-outpatient-hospitals-by-provider-and-service); the [Medicare Dialysis Facilities](https://data.cms.gov/quality-of-care/medicare-dialysis-facilities) report (NPIs only) | A hospital's Medicare volume and top DRGs and APCs; the NPIs a dialysis facility bills under | Yearly |
| [Medicare Physician & Other Practitioners, Part D Prescribers](https://data.cms.gov/) | Medicare services (totals + top 5), prescribing | Yearly |
| State open data: [NY Board for Professional Medical Conduct actions](https://health.data.ny.gov/d/ebmi-8ctw), [Texas Medical Board licenses](https://data.texas.gov/d/tm3v-pfq9), [WA Department of Health credentials](https://data.wa.gov/d/qxh8-f4bd), [IL IDFPR licenses](https://illinois-edp.data.socrata.com/d/pzzh-kp68) (Open Database License), [CO DORA licenses](https://data.colorado.gov/d/7s5z-vewr), [DE professional licenses](https://data.delaware.gov/d/pjnv-eaih) and [actions](https://data.delaware.gov/d/dz6p-akeq), [CT eLicense credentials](https://data.ct.gov/d/ngch-56tr), [MD Board of Physicians lists](https://www.mbp.state.md.us/), [FL DOH MQA licenses and complaints](https://mqa-internet.doh.state.fl.us/downloadnet/Licensure.aspx) (downloaded by the operator) | State license status, expiration and discipline; board actions. Matched to NPIs by license state, number and last name | Weekly |
| CMS [Medicare Part D Prescribers by Provider and Drug](https://data.cms.gov/provider-summary-by-type-of-service/medicare-part-d-prescribers/medicare-part-d-prescribers-by-provider-and-drug) | Product pages: who prescribed each brand to Medicare patients, joined with the providers paid for it; provider pages: Part D claims of the drugs a provider was paid for | Yearly |
| CMS [Medicare Part D](https://data.cms.gov/summary-statistics-on-use-and-payments/medicare-medicaid-spending-by-drug), Part B and Medicaid Spending by Drug; Medicaid [NADAC](https://data.medicaid.gov/); openFDA drug shortages, [FAERS and MAUDE](https://open.fda.gov/apis/) counts; [ClinicalTrials.gov](https://clinicaltrials.gov/data-api/api) | Product pages: spending per year, pharmacy prices, shortages, adverse event report counts, number of studies. Spending by brand name, prices and shortages by NDC; the API counts are refreshed a slice a day | Yearly / weekly / daily |
| [openFDA](https://open.fda.gov/data/downloads/) NDC directory, Drugs@FDA, drug labels, GUDID device records, 510(k) and PMA decisions | Product pages: what each drug or device named in payments is. Matched by the NDC or device identifier reported in the payments | Weekly |
| [openFDA](https://open.fda.gov/data/downloads/) drug and device enforcement reports, [SEC EDGAR](https://www.sec.gov/edgar/searchedgar/companysearch) company tickers, [HHS-OIG Corporate Integrity Agreements](https://oig.hhs.gov/compliance/corporate-integrity-agreements/) | Company pages: FDA recalls (Class I–III, since 2004), the public company's ticker and filings, integrity agreements after fraud settlements. Matched to Open Payments companies by name | Weekly |
| [CMS Open Payments](https://openpaymentsdata.cms.gov/) | Industry payments: totals, by kind, top 3 payers (newest program year); per program year since 2019: general, research, research as principal investigator, ownership/investment interests; top 5 companies over all years (CMS's own summaries); per company: payments per year, kinds of payment and products named (newest year), specialties and providers paid most; per product (every product a payment names): companies, kinds of payment, specialties, providers paid most, research studies with ClinicalTrials.gov IDs | Yearly |
| State Medicaid enrolled providers: [New York](https://health.data.ny.gov/d/keti-qx5t), [California (Medi-Cal fee-for-service)](https://data.chhs.ca.gov/dataset/profile-of-enrolled-medi-cal-fee-for-service-ffs-providers) | A sign of current practice for the record-quality label; shown under Standing on the profile | Weekly |
| [HRSA HPSA](https://data.hrsa.gov/) | Health Professional Shortage Areas by county | Weekly |
| [HRSA Health Center Service Delivery and Look-Alike Sites](https://data.hrsa.gov/data/download) | Health center sites in the facility list, and the health center behind each FQHC (by Medicare number or NPI) | Weekly |
| Agency for Healthcare Research and Quality (AHRQ), [Compendium of U.S. Health Systems](https://www.ahrq.gov/chsp/data-resources/compendium.html), 2023 (downloaded by the operator; AHRQ built it from IQVIA OneKey and the American Hospital Association (AHA) Annual Survey) | Health systems and the hospitals, nursing homes and home health agencies in them (by CCN) | When AHRQ publishes a new year |
| CMS [Medicare Shared Savings Program](https://data.cms.gov/) ACOs, participants and SNF affiliates | ACO pages; participants linked to organization NPIs whose NPPES legal business name is the same (case, spacing and punctuation aside; up to 5, or up to 50 when all are hospitals), shown as name matches | Yearly |
| HRSA [340B OPAIS](https://340bopais.hrsa.gov/Reports) Covered Entity Daily Export (the JSON zip, or the Excel report; downloaded by the operator) | 340B covered entities on facility and health center pages and the `in340b` filter, matched by Medicare provider number, HRSA site ID or NPI | When the operator saves a new export |
| [Census population estimates](https://www.census.gov/programs-surveys/popest.html) | County population | Yearly |

As of October 2026 the data holds **9,482,099 active providers**, current through 2026-10-04.

All of this is public information published by CMS, HHS-OIG, HRSA, HUD, the Census Bureau and NUCC. Datasets are
matched to providers by NPI or another identifier (CCN, PAC ID, license number) and never by name alone; an OIG exclusion without an NPI is not shown.
The one exception is ACO participants, which CMS publishes by legal business name only: they are linked to organizations with exactly
that legal name and always marked as a name match. NPPES data is self-reported by
providers. Verify anything important in the official [NPPES NPI Registry](https://npiregistry.cms.hhs.gov/).

## Limits

- **Paging** stops at the first 10,000 matches. Download the CSV to get them all.
- **Broad searches** (for example a whole state with no other filter) can take a few seconds. A search that would
  take more than 30 seconds to page through asks you to add a filter or download the CSV.
- **Radius search** needs a ZIP with a Census centroid. PO-box-only ZIPs have none, and the search says so.
- **Text filters** are limited to 60 characters. Name filters are prefix matches, never "contains".

## Running it yourself

Everything here is free and public; a full copy of what the project's own PC runs fits on one computer.

### How it works

```
CMS NPPES files + other public datasets ──► Npi.Loader (run daily) ──► MySQL 8.4: raw data + search tables
                                                                              │
                                                    Npi.Web (website + /api/v1) reads the search tables
```

The loader downloads the NPPES files and the other public datasets, loads them into MySQL with all-or-nothing table
swaps (the site never sees a half-loaded table), and rebuilds the search tables. After the first load, a daily `run`
loads only what changed (about 15 minutes after a weekly file).

### Requirements

- **The .NET 10 SDK** ([download](https://dotnet.microsoft.com/download/dotnet/10.0)). Opening `getnpidata.sln` in an
  editor and building fails without it. In VS Code, also install the C# Dev Kit extension (VS Code suggests it); Visual
  Studio 2026 and JetBrains Rider work too. Check with `dotnet --list-sdks`: a 10.0 line must be listed.
- **MySQL 8.4** with `local_infile=ON`: Docker on any OS, or a local install (the Windows script below sets it up).
- **PowerShell** for the `scripts/`: Windows PowerShell 5.1 comes with Windows; on Linux or macOS install PowerShell 7.
  What they do can also be typed by hand (shown below).
- **Disk:** about 100 GB free for a full load (the database grows to ~35 GB; downloads of up to ~9 GB at a time are
  deleted after loading). A sample load needs about 1 GB.
- **Optional:** a free [HUD USPS API token](https://www.huduser.gov/portal/dataset/uspszip-api.html) for county search
  and the area pages.

### 1. Get a MySQL database

Pick one:

- **Docker (any OS):** copy `.env.example` to `.env` and replace the passwords, then run `docker compose up -d`. It
  starts MySQL 8.4 on `127.0.0.1:3306` with the settings the loader needs and creates the database `npi` and three
  logins: `npi_loader` (the loader), `npi_web` (the site, read-only) and `npi_test` (the tests). Then store their
  connection strings with `scripts/setup-secrets.ps1 -Docker`.
- **MySQL on Windows without Docker:** `winget install Oracle.MySQL --version 8.4.9`, then, from an elevated
  PowerShell, `deploy/setup-local-mysql.ps1` (it writes `my.ini` with `local_infile=ON` and registers and starts the
  `MySQL84` service; set a root password right away, as it says). Then `scripts/setup-secrets.ps1 -CreateLogins` asks
  for the root password once, creates the database and the three logins with random passwords, and stores them.
- **A MySQL 8.4 you already have:** create a database, a loader login with all privileges on it plus
  `SESSION_VARIABLES_ADMIN`, and a site login with `SELECT` on it, and turn on `local_infile`. Then
  `scripts/setup-secrets.ps1` asks for each connection.

`setup-secrets.ps1` stores everything as .NET user-secrets (outside the repository) and also asks for the optional
settings. By hand, the same is:

```powershell
dotnet user-secrets --project src/Npi.Loader set "ConnectionStrings:LocalMySql" "Server=localhost;Database=npi;User ID=npi_loader;Password=…"
dotnet user-secrets --project src/Npi.Web    set "ConnectionStrings:RemoteMySql" "Server=localhost;Database=npi;User ID=npi_web;Password=…"
dotnet user-secrets --project src/Npi.Loader set "HudApiToken" "<token>"     # optional
```

### 2. Check the setup

```powershell
scripts/loader.ps1 doctor
```

It checks the .NET runtime, the folders and disk space, MySQL's version, `local_infile`, the logins and their
privileges, the migrations, the network and the optional settings, and prints what to fix for each problem. It changes
nothing.

### 3. Load the data

```powershell
scripts/loader.ps1 migrate     # create the tables (db/migrations; db/schema.sql shows the result)
scripts/loader.ps1 sample      # optional quick try: the newest weekly file only, a few minutes
scripts/loader.ps1 run         # the full load (below); then run it daily
```

**The full load, as the project's own PC runs it.** The first `run` takes many hours (about a working day, mostly
downloads and table builds). It loads:

- the NPPES monthly file (~1.1 GB zipped, ~9.8M NPIs), then the newer weekly files and the deactivation report;
- the reference data (NUCC taxonomy, HUD ZIP–county crosswalk, Census counties and ZIP centres);
- every enrichment dataset: OIG exclusions, Medicare opt-out and order-and-refer, Care Compare, facilities, Medicare
  billing and prescribing, Open Payments (several GB), FDA product data, state licenses, FEMA buildings (21 GB, one file
  at a time) and Overture Maps building points (read in place);
- the search tables, standardized credentials, address types, area counts and record-quality labels.

Practice addresses are geocoded with the Census batch geocoder, 200,000 per `run`. To place all ~3.3 million at once
(about 3 hours), run `scripts/loader.ps1 geocode` after the first `run`, then `run` again. From then on, a daily `run`
(Windows Task Scheduler, cron, …) keeps everything current; it skips files already loaded, so running it often is safe.
Exit code `0` means everything completed, `1` that something failed (the log says what), `2` a bad command line.

**Optional inputs** the load works without:

- `HudApiToken`: without it there is no ZIP–county crosswalk, so county search and the area pages stay empty.
- `SecUserAgent` (`getnpidata <your e-mail>`, SEC's fair-access rule): SEC listings on the company pages.
- `OpenFdaApiKey` (free): product adverse-event counts fill in one run instead of 900 a day.
- **State license files a person has to download** (the other 11 states load by themselves). Put them in the state
  license files folder (`doctor` shows where), one subfolder per state:
  - `FL`: `PROF_ALL.zip` and `dxe004dd.txt` from the Florida Department of Health MQA data download (free account);
  - `AL`: the newest `roster-*.csv` from the Alabama Board of Medical Examiners roster;
  - `MI`: the license lists (`*.xlsx`) from Michigan LARA;
  - `CA`: the Medical Board of California physician and surgeon database (Microsoft Access files, sent on request;
    read on Windows only).

| Setting | Project | Default | Meaning |
|---|---|---|---|
| `ConnectionStrings:LocalMySql` | Loader | — | Database the loader writes (secret) |
| `HudApiToken` | Loader | — | HUD API token (secret; optional: without it county search and the area pages stay empty) |
| `SecUserAgent`, `OpenFdaApiKey` | Loader | — | Optional: SEC listings on company pages ("getnpidata <your e-mail>"); a larger openFDA quota (secret) |
| `WorkFolder`, `LogFolder`, `StateLicenseFilesFolder` | Loader | `%ProgramData%\getnpidata\work`, `…\logs`, `…\state-files` on Windows; `~/.local/share/getnpidata/…` elsewhere | Downloads, daily log files (kept 60 days), hand-downloaded state license files |
| `AhrqCompendiumFolder`, `Hrsa340bFolder` | Loader | `…\ahrq-compendium`, `…\hrsa-340b` beside the work folder | Hand-downloaded files: the AHRQ Compendium CSVs (the systems file and the hospital, nursing home and home care linkage files), and the newest 340B OPAIS covered entity export (the JSON export's .zip as downloaded, a .json, or the Excel report). Empty: the source is skipped |
| `KeepLastZip` | Loader | `true` | Keep the newest downloaded zip |
| `MinRowRatio` | Loader | `0.95` | A full reload must have at least 95% of the current row count, or it is rejected |
| `HudRefreshDays` | Loader | `14` | How often to re-check the HUD crosswalk |
| `ConnectionStrings:RemoteMySql` | Web | — | Database the site reads (secret) |
| `Api:PermitLimit`, `Api:WindowSeconds` | Web | `60`, `60` | API rate limit per client IP |
| `Api:RequireKey`, `Api:Keys` | Web | `false`, — | Require `X-Api-Key` (keys are secrets) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` (or `ApplicationInsights:ConnectionString`) | Web | not set (telemetry off) | Azure Application Insights for the site and API, server-side only; query strings and client details are removed (see `/privacy`) |
| `Map:TileUrl`, `Map:Attribution`, `Map:MaxZoom` | Web | OpenStreetMap standard tiles, its credit line, `19` | Map tiles for the map search page; empty `TileUrl` turns the map off. OpenStreetMap's free server allows only light use: a busy public site should switch to a hosted tile provider (a key in the URL is a secret) |

Environment variables work too: prefix `NPI_` for the loader (for example `NPI_HudApiToken`). For the site, use the
standard ASP.NET Core form, for example `Api__RequireKey=true`.

| Command (`scripts/loader.ps1 <command>`, or `dotnet run --project src/Npi.Loader -- <command>`) | Does |
|---|---|
| `doctor` | Check this computer's setup (MySQL version, `local_infile`, logins and privileges, migrations, folders, disk, network, optional settings) and say what to fix |
| `sample` | Quick try: only the newest weekly file (~50,000 providers) plus reference data, search tables, map and labels, in minutes. The enrichment datasets are skipped; a later `run` does the full load |
| `run` (default) | Load new NPPES files (newest monthly, then weeklies in order, then the deactivation report), refresh stale reference data, and rebuild the search tables if anything changed |
| `migrate` | Apply the numbered migrations in `db/migrations` |
| `discover` | List what `run` would load |
| `load-file <zip>` | Load one NPPES zip manually |
| `reference` | Force-refresh NUCC, HUD and Census reference data |
| `datasets [name]` | Force-reload the enrichment datasets (OIG, CMS, HRSA, Census, Open Payments), or just one. `run` reloads each when its publisher releases a new version |
| `project` | Rebuild the search tables now (and the standardized credentials) |
| `credentials` | Rebuild the standardized credential tables (from the 173k ways providers wrote 1,753 credentials) |
| `address-types` | Relabel the practice addresses (commercial, residential, unknown) from the loaded FEMA buildings; `run` does it after each projection, `datasets fema_structures` after each FEMA load |
| `history [yyyy-MM]` | Build the NPPES history from the archived monthly files, oldest first (CMS from June 2015, NBER before), up to that month or the newest: each NPI's versions and the area figures back to November 2007. Each zip is deleted after use; a stopped run goes on where it stopped. `history discover` lists the files and the months without one; `history measure <zip> …` compares local zips in memory only |
| `areas` | Recount the providers per area (US, states, counties) and plain specialty for the area pages; `run` and `project` do it after each projection |
| `overture [area]` | Place practice addresses at their building with Overture Maps data (read in place from its public files, one state's area at a time), then rebuild the map tables: every state, one state (`NY`) or one county (`36103`) |
| `geocode [county]` | Geocode every practice address not geocoded yet (Census batch geocoder, ~24,000 a minute) and rebuild the map tables. With a 5-digit county FIPS (e.g. `36103`), only that county's addresses. `run` geocodes up to 200,000 new addresses per run |

### 4. Run the site

```powershell
scripts/site.ps1               # http://localhost:5080, and this computer's address on the network
scripts/site.ps1 -Port 5000
```

It builds the site into `.run/site` first and serves it from there, so building the solution meanwhile doesn't fail on
files the running site holds. Other devices on your network need a firewall rule for the port, and use http. Without
the script: `dotnet run --project src/Npi.Web` (http on 5000, https on 5001).

## Project layout

| Path | What |
|---|---|
| `src/Npi.Core` | Search filters, validation, SQL builder, search, detail and lookup services, CSV writer |
| `src/Npi.Loader` | Console app: discover, download, load, reference data, enrichment datasets, search tables |
| `src/Npi.Web` | ASP.NET Core site (Razor Pages) and the `/api/v1` REST API, in one app |
| `src/Npi.Client` | Typed .NET client for the API (netstandard2.0) |
| `tests/` | xUnit v3 tests for each project, plus hand-made fixtures with tricky values (commas, quotes, braces, backslashes, Unicode) |
| `db/migrations` | Numbered SQL migrations in topic folders, applied by `Npi.Loader migrate` ([details](db/migrations/README.md)) |
| `db/schema.sql` | Every table as the migrations leave it, in one file, for reading |
| `scripts/` | PowerShell scripts: `setup-secrets.ps1`, `loader.ps1`, `site.ps1`, `test.ps1` |
| `docker-compose.yml`, `.env.example` | MySQL 8.4 set up for the loader, in Docker |
| `deploy/` | [Azure setup and deployment](deploy/azure.md), the Windows MySQL setup script, the Docker login script |
| `CLAUDE.md` | Design decisions, as-built notes and progress log |

## Development

```powershell
dotnet build getnpidata.sln
scripts/test.ps1                 # every test; the MySQL tests use the NpiTestMySql login (setup-secrets.ps1)
scripts/test.ps1 -UpdateSchema   # after adding a migration: rewrites db/schema.sql
```

CI (GitHub Actions) builds and tests every pull request, with MySQL in Docker. The MySQL integration tests run when
`NPI_TEST_MYSQL` holds a connection string to a MySQL server where the user may create `npi_test_it_*` databases
(`scripts/test.ps1` sets it from the `NpiTestMySql` user-secret); otherwise they are skipped. A second job checks a fresh
clone on Linux (MySQL in Docker, `setup-secrets.ps1 -Docker`, `doctor`, every test, `sample`, then the site answering)
when a pull request changes the setup, once a week, and on demand.

Tests run on Microsoft.Testing.Platform (selected in `global.json`). Use `dotnet test` or `scripts/test.ps1`, not
`dotnet test getnpidata.sln`.
