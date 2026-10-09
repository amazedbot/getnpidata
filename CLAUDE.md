# CLAUDE.md — getnpidata

This file is the brief for any Claude Code session working in this repo. Read it fully before changing anything. It
records decisions the owner has already made; do not re-ask them. Open items are in §11; ask the owner about those,
and about anything new and ambiguous. The user-facing description of the service is in [README.md](README.md).

**The repository is public.** Never put private details in tracked files or commit messages: no passwords, tokens or
keys, no personal names, e-mail addresses, home paths (`C:\Users\<name>`), machine names, LAN or home IP addresses,
or private hosts.

**Section numbers are referenced from code comments and from the (immutable) migration files** ("CLAUDE.md §6.2",
"§7 Stage 1.4", "§11 item 6"). Keep the numbering stable; add new material inside existing sections or at the end.

## Status (Oct 2026)

| Stage | State |
|---|---|
| 0 Repo hygiene, schema baseline | Done |
| 1 C# loader (replaced the VB loader) | Done |
| 2 Reference data (NUCC, HUD, Census) | Done |
| 3 Search projection + `Npi.Core` search service | Done |
| 4 Website | Done |
| 5 REST API + `Npi.Client` | Done |
| 5.5 More data and features (owner's addition, before Azure) | **In progress.** Items 1–8, 10 and 12 merged; items 9 and 11 left, one at a time (§7 Stage 5.5) |
| 6 Publish to Azure, deploy, Task Scheduler | **Not started. Wait for the owner to say "start Stage 6"** (§11 item 2) |

On the loader PC, `workplace` holds the full data (current through 2026-10-04, 9,482,099 active providers). The site
runs locally against it for LAN use while Azure is pending.

---

## 1. What this project is

**Goal:** a public website and REST API for searching every US healthcare provider (medical, dental, etc.) in the
CMS NPPES registry. Example query: "all Chiropractors in Suffolk County, NY", shown in a paged grid and downloadable
as CSV.

```
CMS NPPES files ──► Npi.Loader (Windows PC, scheduled) ──► local MySQL `workplace` (full raw data, system of record)
                                                              │
                                                build search projection (slim tables)
                                                              │
                                    Npi.Loader publish (diff sync) ──► Azure Database for MySQL (search tables only)
                                                                               │
                                                    Npi.Web: site + /api/v1 (Azure App Service, same region)
```

- **Data source:** https://download.cms.gov/nppes/NPI_Files.html; files at `https://download.cms.gov/nppes/<file>.zip`.
- **Repo:** https://github.com/amazedbot/getnpidata (`origin`). It is the only remote; never push anywhere else.

---

## 2. Decisions already made (do not re-ask)

| Topic | Decision |
|---|---|
| Website | ASP.NET Core on **.NET 10 (LTS)**, Razor Pages. Not .NET 8: its support ends Nov 2026 |
| Language | **C#** for all code. The legacy VB loader was ported and then deleted (§3) |
| Loader runtime | C# .NET 10 console app on the owner's Windows PC next to the local MySQL, scheduled with **Windows Task Scheduler** |
| Local database | **MySQL 8.4 LTS** (8.0 reached end of life in April 2026) |
| Hosting | **Azure App Service** (Linux, .NET 10) + **Azure Database for MySQL – Flexible Server** (Burstable B1ms to start, MySQL 8.4), both in **East US** |
| Azure provisioning | The **owner creates the resources in the Azure portal** following `deploy/azure.md`. Claude Code does not provision. No custom domain for now (default `*.azurewebsites.net`) |
| Access | Public site. API public too, protected by rate limiting; an API-key requirement can be switched on via config (built, off) |
| County search | ZIP→county via the **HUD USPS ZIP-COUNTY crosswalk**. A ZIP matches **every** county it overlaps ("match any") |
| Specialty search | Match **all 15** taxonomy slots. UI dropdown by NUCC **Classification**, optional dependent Specialization |
| Addresses searched | Primary practice location + secondary practice locations (`pl_pfile`). Not the mailing address |
| Filters | Specialty, state, county, city, ZIP, **ZIP + radius (miles)**, name (last/first or org), NPI, entity type, gender, credential |
| Deactivated NPIs | **Flagged** in the DB (not deleted) and **never** returned by the site or API |
| Grid/CSV columns | Summary columns (§7.3). CSV is **streamed**, with **no row cap** |
| NUCC taxonomy | Loader refreshes `taxonomy_codes` automatically |
| Load cycle | Full monthly replace + weekly incremental updates. The site must stay up during reloads |
| Schema changes | Allowed: widen columns for V2, utf8mb4, new tables and indexes |
| Logging | Log file only (no email/alerts). Non-zero exit code on failure |
| Stage 5.5 | Added by the owner (2026-10-08) between Stages 5 and 6: **all** the datasets and features in §7 Stage 5.5. Named 5.5 so that "Stage 6" references stay valid. Planned as one PR; the owner then had items 1–8 merged first. Work **one item at a time** and wait for the owner's "next" |
| Git | Claude Code may create branches, commit, open PRs and merge when CI is green (§10) |

---

## 3. History: the legacy VB loader

### 3.1 What it was

The repo started as a VB.NET (.NET Framework 4.8.1) console app. In the owner's environment it loaded NPPES files
into MySQL; its load branches were half commented out. Stage 0 removed unrelated sibling projects, a nested old copy,
a ClickOnce key and stale files. Stage 1 ported the loader to C#, checked parity against the VB-loaded data, and
deleted the VB project. It remains in git history before the `stage-1-loader` merge, under `legacy/getnpidata-vb/`.

### 3.2 Known defects of the VB loader (each has a regression test; code comments cite them as "legacy defect #N")

1. **Silent row loss:** each CSV line was used as a *format string*; lines with `{` or `}` threw and were dropped.
2. **Column shifting:** values were split with quote awareness, then re-joined with commas *without re-quoting*, so a comma inside a value shifted the columns.
3. **No retry after failure:** a file was logged in `downlog` *before* processing, so a crash skipped it forever.
4. **False success:** download exceptions were swallowed and reported as "Successfully Downloaded".
5. **NULL never equals NULL:** the `other_names` / `practice_locations` dedupe compared nullable dates with `=`, so re-runs inserted duplicates.
6. **Deactivation report:** the zip *name* was passed to the Excel reader, and exactly 2 header rows were skipped by assumption.
7. **Case-sensitive file matching:** `"_FileHeader"` vs `"_fileheader"`.
8. **Old file naming / widths:** V1 names and widths; V2 widened first-name and legal-business-name fields.
9. **Weekly before monthly:** older weeklies could overwrite newer data (no `Last_Update_Date` guard).
10. **Unsafe SQL:** file and table names concatenated into SQL.
11. **Site outage during monthly load:** `TRUNCATE npidata` before the reload left the table empty.
12. **Minor:** shadowed connection string, meaningless counters, unused helpers, a DEBUG `Console.ReadLine()`.

---

## 4. Source data facts (verified against the CMS page and real files, Oct 2026)

- **V1 retired 03/03/2026.** Only V2 files are published. Accept only `_V2` names.
- Files on `NPI_Files.html` (match by regex, case-insensitive, on the href file name):
  - Monthly full: `NPPES_Data_Dissemination_<MonthName>_<YYYY>_V2.zip`, ~1.1 GB zipped, roughly 10 GB of CSV unzipped
  - Weekly incremental: `NPPES_Data_Dissemination_<MMDDYY>_<MMDDYY>_Weekly_V2.zip` (~6–8 MB; the page keeps about 4)
  - Monthly deactivations: `NPPES_Deactivated_NPI_Report_<MMDDYY>_V2.zip` (contains an .xlsx)
- Links are single-quoted relative hrefs with a `./` prefix (`href='./NPPES_…_V2.zip'`).
- Each data zip contains `npidata_pfile_*.csv`, `othername_pfile_*.csv`, `pl_pfile_*.csv`, `endpoint_pfile_*.csv`, matching `*_fileheader.csv` files and a Readme PDF. Match inner names case-insensitively. **Endpoints are out of scope** (skipped and logged).
- **CSV format:** `\n` line endings, every field double-quoted, dates `MM/DD/YYYY`. Per the Readme, double quotes inside values are replaced by single quotes, so no escaping occurs. The data file's header equals its `_fileheader.csv`. The header row is the source of truth for column order.
- **Header → DB column rule:** replace each run of non-alphanumeric characters with `_` and trim `_` (`Provider Organization Name (Legal Business Name)` → `Provider_Organization_Name_Legal_Business_Name`). All 330 `npidata` columns match in order, given these aliases (`HeaderMapper`):
  - the suffix `_If_outside_U_S` is dropped (the full name would also exceed MySQL's 64-character limit);
  - V2's `Provider Sex Code` → `Provider_Gender_Code`;
  - `pl_pfile`'s `Provider Secondary Practice Location Address- Address Line 1/2` → `Provider_Secondary_Practice_Location_Address_Line_1/2`.

  An unmapped header fails the load; new CMS columns need a migration.
- **Field lengths (V2 Readme):** LBN and other organization name 100, first/last names 35, middle 20, credential 20, addresses 55, city/state 40, postal 20.
- **Deactivation report:** one `.xlsx` with a title row, a header row, then NPI (text) + date (text `MM/DD/YYYY`). It is the **full current list** (355,330 NPIs on 2026-09-14, back to 2005), not a delta.
- **NPPES has no county field.** County comes from the ZIP crosswalk (§6.3).

---

## 5. Solution layout and configuration

```
getnpidata/
  README.md                ← user-facing description of the service (keep it current with capabilities)
  CLAUDE.md
  getnpidata.sln
  global.json              ← pins the .NET 10 SDK band; selects Microsoft.Testing.Platform for `dotnet test`
  Directory.Build.props    ← net10.0, nullable, implicit usings, central package management
  Directory.Packages.props ← central package versions (no Version= in project files)
  src/
    Directory.Build.props  ← warnings as errors, AnalysisLevel latest-recommended
    Npi.Core/     (net10.0 classlib)  search filter, validation, SQL builder, search/detail/lookup services, CSV writer
    Npi.Loader/   (net10.0 console)   discover → download → load → reference data → projection (→ publish, Stage 6)
    Npi.Web/      (net10.0 ASP.NET)   Razor Pages site + /api/v1 in ONE deployable app
    Npi.Client/   (netstandard2.0)    typed .NET client for /api/v1 (packable as Npi.Client.nupkg)
  tests/
    Npi.Core.Tests/    query builder, input formats, SQL identifiers
    Npi.Loader.Tests/  file classification, header mapping, downloads, deactivation report, migrations; MySQL integration tests
    Npi.Web.Tests/     query-string binding, API behaviour (WebApplicationFactory, no database)
    Npi.Client.Tests/  the client against the in-memory API + client/server contract checks
    fixtures/          real V2 headers, NPI_Files.html, reference-data excerpts (byte-exact; see .gitattributes)
  db/migrations/           numbered .sql files (001–053) + README, embedded in and applied by `Npi.Loader migrate`.
                           Applied migrations are checksummed: never edit one, add a new one instead.
  deploy/
    azure.md               Azure setup + deploy notes
    setup-local-mysql.ps1  configures a local MySQL 8.4 for the loader
    register-task.ps1      (Stage 6) Task Scheduler registration
  .github/workflows/ci.yml build + test on every PR and push to master
```

**Tests** use **xUnit v3** on **Microsoft.Testing.Platform** (MTP). The .NET 10 SDK no longer runs xunit.v3 4.x
through VSTest, so `global.json` opts `dotnet test` into MTP. Consequences:
- run tests with `dotnet test` or `dotnet test --solution getnpidata.sln`, not `dotnet test getnpidata.sln`;
- a test project with zero tests fails (exit code 8);
- no `Microsoft.NET.Test.Sdk` or `xunit.runner.visualstudio` packages are needed.

**Libraries:**

| Area | Libraries |
|---|---|
| Database | `MySqlConnector` (async, `MySqlBulkLoader.SourceStream`), `Dapper` |
| Files | `ExcelDataReader` (+ `System.Text.Encoding.CodePages`), `CsvHelper` |
| Logging and config | `Serilog` (+ File/Console sinks), Microsoft.Extensions.Configuration |
| Web | `Microsoft.AspNetCore.OpenApi`, `Swashbuckle.AspNetCore.SwaggerUI`, the built-in rate limiting and output caching |
| Client | `System.Text.Json` |
| Tests | `Microsoft.AspNetCore.Mvc.Testing` |

**Config & secrets:**
- **Where things go.** `appsettings.json` holds non-secret defaults. Secrets go in user-secrets (loader and dev), environment variables or the git-ignored `appsettings.Local.json`; in production, in App Service → Configuration. **Never commit a real password, token or API key.**
- **Names.** Connection strings are `LocalMySql` (loader) and `RemoteMySql` (site; publisher in Stage 6). Settings: `WorkFolder` (default `%ProgramData%\getnpidata\work`), `LogFolder` (default `%ProgramData%\getnpidata\logs`), `HudApiToken`, and `Api:*` for the site (§7 Stage 5).
- **The HUD token** has been issued to the owner. If it's missing, ask the owner to run `dotnet user-secrets --project src/Npi.Loader set "HudApiToken" "<token>"`, and never paste it into a tracked file.

---

## 6. Database design

**Local MySQL** (database `workplace` on the loader PC) holds the full raw data and the search projection.
**Azure Database for MySQL** will hold only the search projection plus reference tables. It requires TLS
(`SslMode=Required`) and firewall rules (see `deploy/azure.md`). utf8mb4 everywhere, InnoDB.

### 6.1 Raw tables (local only)

| Table | Notes |
|---|---|
| `npidata` | 1 row per NPI, all 330 CSV columns, **all TEXT** utf8mb4 except the `NPI CHAR(10)` PK (migration 002). Raw dates are kept as text; empty → NULL. Adds `Is_Deactivated TINYINT` and `Loaded_From`. No secondary indexes (search uses the projection). InnoDB's worst-case row-size check rejects 330 columns even as TEXT, so DDL creating or altering it runs with `innodb_strict_mode=OFF`, and the loader's DB user needs `SESSION_VARIABLES_ADMIN`. Real rows always fit (~7.3 KB at most) |
| `other_names` | from `othername_pfile`. No unique key: the loader **replaces** rows (whole table monthly, per NPI weekly), which removes defect #5 by construction |
| `practice_locations` | from `pl_pfile` (secondary locations), surrogate `ID` |
| `nppes_deactivated_npi_report` | NPI, deactivation date. **Replaced in full** by each report (staging + RENAME), so reactivated NPIs drop out |
| `downlog` | (003) `filename` UNIQUE, `kind` (Monthly/Weekly/Deactivation), `file_date`, `status` (Downloading/Loading/Completed/Failed; VB-era rows = `Legacy`, which does not count as done), `started_at`, `completed_at`, `rows_loaded`, `error` |
| `extractlog` | zip, extracted file, rows |
| `schema_migrations` | version, name, sha256, applied_at; written by `Npi.Loader migrate` |
| `*_staging` | per-load staging tables, created `LIKE` the target, dropped after the swap |

**Deactivation flag rule** (`Deactivations.ApplyAsync`):
- **When it's set:** `Is_Deactivated = 1` when the NPI has a deactivation date (the report date, else `NPI_Deactivation_Date`) and no `NPI_Reactivation_Date` on or after it. An empty `NPI_Deactivation_Date` is filled from the report.
- **When it's recomputed:** for all rows after every monthly and deactivation load, and for that week's NPIs after every weekly.
- **Date comparisons** use `YYYYMMDD` strings built with SUBSTRING, because `STR_TO_DATE` errors on malformed values in strict SQL mode.

**Baseline schema:** the owner's backup (2026-10-07) is captured in `db/migrations/001_baseline.sql`, with caveats in
`db/migrations/README.md`. It used V1 widths and latin1 for several tables; migrations 002–006 fixed that. The
NPPES code tables (`entity_types`, `gender_codes`, `state_codes`, `country_codes`, …) came with seed rows.
Unrelated or obsolete tables (`animals`, `dicomhosts`, `taxonomy_codes_old`) are left alone; dropping them needs the
owner's OK.

### 6.2 Search projection (local, then published to Azure)

Built by `Npi.Loader project` (migrations 011–016). Deactivated NPIs are **excluded**; their flag stays in local `npidata`.

| Table | Columns | Key indexes |
|---|---|---|
| `provider` | npi PK, entity_type (1 = individual, 2 = org), last/first/middle name, prefix/suffix, credential, `credential_key` (upper-cased letters/digits, "M.D." = "MD"), org_name, `sort_name` ("LAST, FIRST MIDDLE" or org name; default sort), gender, primary_taxonomy_code, phone, enumeration_date, last_update_date, row_hash | (sort_name), (last_name, first_name), (org_name), (credential_key) |
| `provider_taxonomy` | npi, slot 1–15, taxonomy_code, is_primary, license_no, license_state | (taxonomy_code, npi) |
| `provider_location` | id, npi, is_primary, address1/2, city, state, zip5, zip4, postal_code (raw), country_code, phone. zip5/zip4 only for US addresses | (state, city), (zip5), (npi) |
| `provider_other_name` | npi, name, type_code (for the detail page) | (npi) |
| `provider_search` | taxonomy_code, state, city, zip5, npi: provider_taxonomy × provider_location reduced to the searched columns (~13M rows) | PK (taxonomy_code, state, city, zip5, npi), (taxonomy_code, zip5, npi) |
| `zip_county` | zip5, county_fips, res/bus/oth/tot ratios, year, quarter | PK (zip5, county_fips), (county_fips) |
| `county` | county_fips PK, state, county_name, source | (state, county_name) |
| `zip_centroid` | zip5 PK, lat, lon (Census ZCTA) | — |
| `taxonomy_codes` | NUCC codes + `Display_Name`, `Nucc_Version` | (Classification) |
| `reference_data` | source, version, source_url, rows_loaded, loaded_at, checked_at | — |
| `data_version` | one row (id = 1): as_of_date (newest last_update_date), latest monthly/weekly/deactivation file, NUCC/HUD/Census versions, provider_count, projected_at (UTC), published_at (UTC) | — |

- **Change detection:** `row_hash` = MD5 over the provider's columns **and** all its taxonomy/location/other-name rows, ordered by content, so a change in a child row alone still changes the hash.
- **Rebuilding on Azure:** `provider_search` derives from the published child tables, so Stage 6 can rebuild it on Azure instead of transferring it.
- **Build process:** `project` builds all five tables in `*_staging`, checks ≥ 95% of the current provider count, and swaps them in with one RENAME. `run` rebuilds when `data_version.projected_at` is older than the newest completed downlog or reference load.

### 6.3 Reference data sources

- **NUCC taxonomy CSV:** the page `…/provider-taxonomy-mainmenu-40/csv-mainmenu-57` on nucc.org lists every release as `/images/stories/CSV/nucc_taxonomy_<ver>.csv`.
  - The newest release is the highest number (`261` = 26.1: 883 codes, 245 classifications). UTF-8.
  - `run` fetches the page each time and reloads when the version changes.
  - The load is an **upsert**; retired codes keep their last `Nucc_Version`.
- **HUD USPS ZIP–COUNTY crosswalk:** `https://www.huduser.gov/hudapi/public/usps?type=2&query=All` (type 2 = zip-county), header `Authorization: Bearer <HudApiToken>`.
  - It returns county **FIPS (geoid)**, not names: about 54,570 rows, and 11,379 ZIPs lie in more than one county.
  - 9 rows with a 2-digit state-level geoid are skipped.
  - The version (year/quarter) is only visible after downloading everything. So `run` re-downloads at most every `HudRefreshDays` (14) and replaces `zip_county` only when the quarter changes.
- **County names:** primarily the **Census Gazetteer counties file** (`…/gazetteer/<year>_Gazetteer/<year>_Gaz_counties_national.zip`, newest year from the directory listing).
  - It has current boundaries, including Connecticut's 2022 planning regions, which HUD uses.
  - The 2020 national county codes file is the fallback for FIPS the Gazetteer lacks (GU, MP, VI, AS); `county.source` records which file supplied each name.
- **ZIP centroids (radius search):** Census Gazetteer ZCTA file (33,791 ZCTAs in 2026; INTPTLAT/INTPTLONG). It's loaded together with the counties as one `census_gazetteer` version. ZIPs without a ZCTA can't use radius search, and the validation message says so.
- **Loading:** `zip_county`, `county` and `zip_centroid` are replaced in full via staging + atomic RENAME, with the same ≥ 95% row check as NPPES. Any MySQL warning fails a load.

---

## 7. Stage plan and as-built notes

Work stage by stage, one branch per stage (§10). At the end of each stage, update the Status table and §12.

### Stage 0 — Repo hygiene & discovery (done)
Old copies, unrelated projects and stale files were removed. The `src/` + `tests/` skeleton, CI and `deploy/azure.md` were created. The owner's backup was restored as `npi_test`, and its schema became `db/migrations/001_baseline.sql`.

### Stage 1 — C# loader (done)
Commands (`Npi.Loader <command>`): `run` (default), `migrate`, `discover`, `load-file <zip>`, `reference`, `project`, `publish` (Stage 6; currently exits 1 "not implemented").

1. **Discover:** `HttpClient` GET of `NPI_Files.html`. Hrefs are classified by regex, V2 only:
   - `^NPPES_Data_Dissemination_(?<month>[A-Za-z]+)_(?<year>\d{4})_V2\.zip$` → monthly
   - `^NPPES_Data_Dissemination_(?<from>\d{6})_(?<to>\d{6})_Weekly_V2\.zip$` → weekly
   - `^NPPES_Deactivated_NPI_Report_(?<date>\d{6})_V2\.zip$` → deactivation

   Anything else is logged and ignored. Order: the newest unprocessed monthly, then the weeklies chronologically, then the newest deactivation report. Only the newest monthly and the newest report are loaded.
2. **Bookkeeping:** a file counts as done only when `downlog.status = 'Completed'`, so failed or partial files are retried. All SQL is parameterized.
3. **Download:** streamed to `WorkFolder` under a temp name, renamed on success, and the zip is verified to open. Retries with backoff (`DownloadAttempts`). Never reports success on failure. `KeepLastZip` keeps the newest zip.
4. **Bulk load:** each CSV is streamed straight from the zip entry into `MySqlBulkLoader` (`SourceStream`, `Local = true`), with the column list taken from the header row (§4), into a staging table. Dates and empties are converted in SQL, never by rewriting the CSV (defects #1, #2).
   - `MySqlBulkLoader` can't emit `ESCAPED BY ''`, so the escape character is **U+0001**, which never occurs in NPPES text; a backslash fixture test guards this.
   - Values load into user variables (`SET col = NULLIF(@c0, '')`); DATE columns use `STR_TO_DATE(…, '%m/%d/%Y')`.
   - `LOAD DATA LOCAL` downgrades errors to warnings, so **any warning fails the load**.
5. **Monthly full:** `npidata`, `other_names` and `practice_locations` load into `*_staging`. After the ≥ `MinRowRatio` (95%) check, they are swapped in atomically with one RENAME (defect #11).
6. **Weekly:** upsert into `npidata` only where the incoming `Last_Update_Date` ≥ the stored one (compared as dates; defect #9). Other names and practice locations of those NPIs are replaced. One transaction.
7. **Deactivations:** ExcelDataReader reads the report; data starts at the first row whose first cell is a 10-digit NPI (defect #6). The report is the full list, so it **replaces** the table (staging + RENAME, ≥ 95% check), then flags follow §6.1.
8. **`local_infile`:** the loader never runs `SET GLOBAL`. It checks `@@local_infile` and fails with instructions for `my.ini` if it's off.
9. **Logging and exit codes:** a Serilog rolling file `getnpidata-YYYYMMDD.log` in `LogFolder`, kept 60 days, with a summary line per file (rows, duration). Exit code 0 = all completed, 1 = something failed, 2 = bad command line.
10. **Parity:** the VB loader couldn't run on the loader PC, so parity was checked against the VB-loaded data restored from the owner's backup (§12, Stage 1). `legacy/` was deleted afterwards.

Configuration order: `appsettings.json` → `appsettings.Local.json` → user-secrets (loaded in every environment, because Task Scheduler runs the loader as the owner's account) → environment variables with the prefix `NPI_`.

### Stage 2 — Reference data (done)
NUCC → `taxonomy_codes` (upsert, only when the version changed), HUD → `zip_county` (quarterly), Census Gazetteer → `county` + `zip_centroid`. All idempotent; `run` refreshes them when stale, and `reference` forces a reload. Details in §6.3.

### Stage 3 — Search projection + `Npi.Core` search service (done)
1. **Projection:** `project` builds the §6.2 tables:
   - the 15 taxonomy slots are unpivoted, skipping blanks;
   - locations are the primary practice address plus each `practice_locations` row, with zip5/zip4 normalized;
   - deactivated NPIs are excluded, and `row_hash` is computed.
2. **`SearchFilter`:** Classification, Specialization, TaxonomyCode, State, CountyFips, City, Zip5, RadiusMiles, LastName, FirstName, OrgName, Npi, EntityType, Gender, Credential, Sort, Page, PageSize.
3. **SQL builder (`SearchQuery`):** parameterized only; the sort whitelist is `name|npi|credential|city|state|zip|lastUpdate|enumeration`, and a `-` prefix means descending.
   - **Specialty:** `TaxonomyCatalog.ResolveAsync` maps Classification/Specialization to taxonomy codes, cached hourly.
   - **Location:** **any** location of the NPI matches. County = `zip5 IN (zip_county for the FIPS)`. Radius = a bounding box on `zip_centroid`, then haversine.
   - **Names:** prefix match (`LIKE x%`), wildcards escaped, no leading wildcards.
   - **Driver:** the most selective filter becomes a derived table of candidate NPIs, and MySQL is pinned to start from it with `/*+ JOIN_ORDER(c, p) */`. Priority: NPI → specialty + location (`provider_search`) → specialty (`provider_taxonomy`) → name prefix → location (`provider_location`) → credential. The other filters check the candidates. Without the pin, MySQL scanned the whole `sort_name` index to satisfy `ORDER BY … LIMIT`.
   - **Queries:** first, the page's NPIs with the total via `COUNT(*) OVER ()`; a separate count runs only for a page past the end. Then, per NPI: the matching location (primary first), the county name (the filtered county, else the ZIP's largest-share county) and the primary specialty. `SearchAllAsync` streams every match for CSV.
   - **Broad searches** (§11 item 6): location-only searches are counted first, without a join. At ≥ 200,000 matches (`BroadSearchThreshold`), the page walks the sort index (`FORCE INDEX FOR ORDER BY`, with `NO_SEMIJOIN` on the location EXISTS).
4. **Validation (`SearchValidation`):**
   - At least one filter.
   - NPI 10 digits, ZIP 5 digits, radius 1–100 miles (needs a ZIP with a centroid), page size 1–200.
   - Text filters at most 60 characters.
   - Paging stops at 10,000 rows (`MaxResultWindow`; use the CSV beyond that).
   - A 30 s search timeout (`SearchTimeoutSeconds`); past it the user is told the search is too broad and to add a filter or download the CSV.
5. **Performance target:** < 2 s for "Classification + county" and "Classification + state" on full data. **Met** (§12): Chiropractor + Suffolk 0.27 s, Chiropractor + NY 0.08 s.

### Stage 4 — Website (`Npi.Web`, Razor Pages) (done)
- **Search form:** a GET form, so every search URL is shareable. `Npi.Web.Search.SearchQueryString` maps the API parameter names (`classification`, `specialization`, `taxonomy`, `state`, `county`, `city`, `zip`, `radius`, `lastName`, `firstName`, `orgName`, `npi`, `entityType`, `gender`, `credential`, `sort`, `page`, `pageSize`) to `SearchFilter` and back. The page, `/export.csv` and the API all use it.
- **Dropdowns:** filled server-side. `wwwroot/js/search.js` reloads the dependent lists from `/lookup/specializations?classification=` and `/lookup/counties?state=`. Radius is enabled only with a 5-digit ZIP, and empty fields are left out of the URL. The page works without JavaScript and doesn't scroll sideways at 375 px.
- **Grid:** sortable headers (with `aria-sort`), previous/next paging, page size 50, the total, and Download CSV.
- **Detail:** `/provider/{npi}` shows all taxonomies with licenses, all practice locations and other names. 404 for unknown or deactivated NPIs.
- **Export:** `/export.csv` and `/api/v1/providers.csv` share `CsvExport`. It validates first (400 problem), then streams `SearchAllAsync` through `ProviderCsv` with no cap. UTF-8 with BOM, named `npi_search_<yyyyMMdd>.csv`.
- **Footer:** "Data as of", the active provider count from `data_version`, source attribution, and a note that this is public NPPES data.
- **Network:** the launch profile binds `0.0.0.0` (http 5000, https 5001). LAN clients need a Windows Firewall inbound rule for the port, which the owner adds. They use http, because the dev certificate is trusted only for localhost.
- **Connection:** the site reads `ConnectionStrings:RemoteMySql`. Locally that's `workplace` via a read-only login (§8), run with `ASPNETCORE_ENVIRONMENT=Development`.

#### 7.3 Summary columns (grid & CSV)
NPI, Entity Type, Name (Last, First Middle Suffix *or* Organization), Credential, Primary Specialty (Classification –
Specialization), Address 1, Address 2, City, State, ZIP, County, Phone, Gender, Enumeration Date, Last Update Date.

### Stage 5 — REST API (`/api/v1`, same app) and `Npi.Client` (done)

**Endpoints** (`src/Npi.Web/Api`), all in one route group with rate limiting, CORS and the key filter:

| Endpoint | Returns |
|---|---|
| `GET /providers?…` | `SearchResult` `{ items, page, pageSize, totalCount, dataAsOf }`; items are the §7.3 columns plus `entityTypeName` |
| `GET /providers.csv?…` | Every match as CSV, streamed (byte-identical to `/export.csv`) |
| `GET /providers/{npi}` | `ProviderDetail`; 404 problem if unknown or deactivated |
| `GET /taxonomy/classifications` | All classifications |
| `GET /taxonomy/classifications/{c}/specializations` | Specializations; 404 if the classification is unknown |
| `GET /states` | States and territories |
| `GET /states/{st}/counties` | Counties with FIPS; 404 if the state is unknown |
| `GET /meta` | `dataAsOf` as a date; `projectedAt`/`publishedAt` in UTC |

- **Same results as the page:** the API and the pages use the same `Npi.Core` services and the same parser (`SearchQueryString.Parse`). `SearchQueryString.Parameters` lists every parameter once; it feeds the OpenAPI document and maps validation errors to parameter names (`radius`, not `RadiusMiles`). Tests check the table against the parser.
- **Errors:** everything under `/api` answers with RFC 7807 problem details, including unhandled errors and empty 404/405s (`UseExceptionHandler` + `UseStatusCodePages` for `/api` only; the pages keep `/Error`).
- **Docs:** OpenAPI at `/openapi/v1.json` (API routes only), Swagger UI at `/swagger`.
- **Rate limiting:** a fixed window per client IP, `Api:PermitLimit` requests per `Api:WindowSeconds` (default 60 per 60 s), across all of `/api/v1`. Over the limit → 429 problem with `Retry-After`. The pages and `/lookup/*` are not limited. On App Service, set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`.
- **CORS:** any origin, GET only.
- **Output cache:** the lookups and `/meta`, 10 minutes.
- **API key:** `Api:RequireKey` (default false) plus `Api:Keys` (secrets). The `X-Api-Key` header is compared in fixed time against every key. Keys required but none configured → the site fails at startup.
- **`Npi.Client`** (netstandard2.0, hand-written, System.Text.Json):
  - `NpiClient`: one method per endpoint. `GetProviderAsync` returns null on 404.
  - `ProviderSearch`: the filters, one property per API parameter.
  - `NpiApiException`: status, title, detail, errors by parameter name, `RetryAfter`.
  - Optional API key; its own `HttpClient` or an injected one. Dates are `DateTime`, because netstandard2.0 has no `DateOnly`.
  - Contract tests fail when the server gains a field or parameter the client lacks.
  - Usage is in `src/Npi.Client/README.md`. `dotnet pack src/Npi.Client` builds the package (not published to nuget.org).

### Stage 5.5 — More data and features (in progress)

**Goal:** more value per search, by joining other public datasets to NPIs and adding website and search features. The owner chose all of the items below (2026-10-08), as one PR on `stage-5.5-features`.

**Principles:**
- Every source is public and free, keyed by NPI (or by CCN through a CMS enrollment crosswalk).
- Each source is refreshed by `run` when its version changes, with versions tracked in `reference_data`.
- Each is loaded with the staging + RENAME pattern and the ≥ 95% check (§6.3). Any MySQL warning fails a load.
- Each failure is isolated, so one broken source doesn't stop the others.
- Big sources are **summarized to one row (or a few rows) per NPI** before the projection, so the Azure database stays small (§7 Stage 6.3).
- `row_hash` covers the new per-NPI rows, so the publisher picks up their changes.
- Source discovery uses the catalogs below, never hard-coded release URLs: the newest release is chosen by its `temporal` end date, not by `modified`.

**Sources (verified Oct 2026):**

| # | Feature | Source | Discovery / URL | Key | Size, refresh |
|---|---|---|---|---|---|
| 1 | Unused NPPES fields | `npidata` (other identifiers, authorized official, parent org/subpart, mailing address) and the **`endpoint_pfile`** in each NPPES zip (now loaded) | already downloaded | NPI | weekly/monthly |
| 2a | Exclusions | OIG **LEIE** | `https://oig.hhs.gov/exclusions/downloadables/UPDATED.csv` (full list; send a User-Agent) | NPI where present (many rows have none; those are not matched, never guessed by name) | 15 MB, monthly |
| 2b | Medicare opt-out | CMS **Opt Out Affidavits** | data.cms.gov catalog `https://data.cms.gov/data.json`, title "Opt Out Affidavits" | `npi` | small, monthly |
| 2c | Order & refer eligibility | CMS **Order and Referring** | catalog, "Order and Referring" (cols NPI, PARTB, DME, HHA, PMD, HOSPICE) | NPI | ~2M rows, weekly |
| 3a | Care Compare clinicians | **Doctors and Clinicians National Downloadable File** | Provider Data Catalog `https://data.cms.gov/provider-data/api/1/metastore/schemas/dataset/items/mj5m-pzi6` | NPI (+ Ind_PAC_ID, org_pac_id) | 800 MB, monthly |
| 3b | Hospital affiliations | **Facility Affiliation Data** | same catalog, `27ea-46a8` | NPI → facility CCN | 126 MB, monthly |
| 4a | Facilities | **Hospital General Information** (`xubh-q36u`), **Nursing Home Provider Information** (`4pq5-n9py`) | Provider Data Catalog | CCN | small, monthly |
| 4b | CCN ↔ NPI | **Hospital Enrollments**, **Skilled Nursing Facility Enrollments** | data.cms.gov catalog | NPI + CCN | small, quarterly |
| 5a | Medicare services | **Medicare Physician & Other Practitioners – by Provider** and **– by Provider and Service** | data.cms.gov catalog (data year 2024 in Oct 2026) | Rndrng_NPI | yearly; keep totals + top 5 services per NPI |
| 5b | Prescribing | **Medicare Part D Prescribers – by Provider** | data.cms.gov catalog | Prscrbr_NPI | yearly; totals, brand/generic, opioid rate |
| 6 | Industry payments | **Open Payments General Payment Data** (yearly detailed file) | `https://openpaymentsdata.cms.gov/api/1/metastore/schemas/dataset/items` ("<year> General Payment Data") | Covered_Recipient_NPI | several GB per year, streamed and aggregated; newest year: totals by nature of payment + top 3 payers per NPI |
| 7a | Shortage areas | HRSA **HPSA** primary care / dental / mental health | `https://data.hrsa.gov/DataDownload/DD_Files/BCD_HPSA_FCT_DET_{PC,DH,MH}.csv` | county FIPS (geographic HPSAs, status Designated) | small, weekly |
| 7b | Population | Census **county population estimates** | `https://www2.census.gov/programs-surveys/popest/datasets/<years>/counties/totals/co-est<year>-alldata.csv` | county FIPS | yearly |

**Website and API features:**

| # | Feature | Notes |
|---|---|---|
| 8 | Bulk NPI lookup | Upload a CSV or paste NPIs → enriched CSV (all summary columns + new flags). `POST /api/v1/providers/lookup` (JSON list, capped per request) |
| 9 | Smarter name search | Typo-tolerant names (phonetic key column + ranking) and word-anywhere organization search (FULLTEXT); prefix search stays the default |
| 10 | Map search | A dedicated `/map` page (owner, 2026-10-08): one pin per practice street address (Census-geocoded; unmatched → ZIP centroid, shown as approximate), hover lists everyone at the address, "Search in this area" with the search page's filters, a sortable table of the pins in view, near me. OpenStreetMap tiles (configurable). The search page links to it ("View on map") instead of showing a map |
| 11 | Change tracking | A `provider_change` log written by the weekly loads (address, name, taxonomy and deactivation changes, from now on), shown on the detail page; "new providers" filter and feed (by enumeration date) per area |
| 12 | Standardized credentials (owner's addition, 2026-10-08) | One standard spelling per credential for display, grid and CSV (`M.D.`, `MD`, `m.d.` → `MD`; also split multi-credential text such as `MD, PhD`), built on the existing `credential_key`. The search form's credential text box becomes a **dropdown** of the standardized credentials (most common first, with counts); the API keeps accepting free text |

**Filters and display:**
- **New filters:** not excluded, accepts Medicare assignment, telehealth, can order/refer, opted out, minimum years in practice, in a shortage area.
- **Badges:** on the grid and the detail page.
- **New detail-page sections:** Care Compare, affiliations, Medicare activity, payments, endpoints, other identifiers, change history.

**Build order:** 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 9 → 10 → 11 → 12. Record actual sizes, row counts and timings in §12 as each item lands.

**As built (items 1–8, migrations 017–044):**
- **Loader:** `Npi.Loader datasets [name]` force-reloads the datasets; `run` refreshes them after the reference data. Each is a `DatasetSource` (`src/Npi.Loader/Datasets`) with a name, a check interval (default 20 h; HRSA, Census and Open Payments weekly) and a version from its catalog; versions are kept in `reference_data`. `CsvTableLoader` streams a CSV into a staging table with typed column conversions in SQL; some CMS files are latin1. `TableSwap.ReplaceAsync` does the staging + RENAME + ≥ 95% check. Raw templates (`*_raw`) are empty `LIKE` sources for staging.
- **Item 1:** `endpoints` (raw, from `endpoint_pfile`), projections `provider_profile`, `provider_identifier` (50 other-identifier slots unpivoted), `provider_endpoint`; all covered by `row_hash`. Monthly + weekly loads include endpoints from now on. **Backfill pending:** `workplace` needs the September monthly and the weeklies reloaded (`load-file`) and one `project` (~45 min).
- **Items 2–7:** per-NPI tables `oig_exclusion`, `medicare_opt_out`, `medicare_order_referring`, `cc_clinician`, `cc_group`, `cc_facility_affiliation`, `cms_hospital`, `cms_nursing_home`, `cms_facility_npi`, `medicare_utilization`, `medicare_top_service` (top 5 per NPI by ROW_NUMBER), `medicare_part_d`, `open_payments_summary`/`_nature`/`_payer` (top 3), and per county `county_shortage`, `county_population`.
- **Search:** `ProviderFlags` (Npi.Core `Enrichment.cs`) drive the badges, CSV flag columns and filters `excluded`, `optedOut`, `orderRefer`, `acceptsAssignment`, `telehealth`, `minYears`, `medicareActive`, `shortage` (a location filter). `excluded/optedOut=true` become the driver; Care Compare conditions join into the location driver; the count skips the provider join when only NPI-level flags remain. Migration 032 replaced `provider_location (npi)` with a covering (npi, state, zip5, city) index.
- **Item 8:** `SearchService.LookupAsync` (≤ `MaxLookupBatch` 1,000; dedupes, keeps order, Found/NotFound/Invalid with the Luhn check digit, `InputFormats.HasValidCheckDigit`), `/lookup` page (≤ 50,000 NPIs, 10 MB, streamed in batches), `POST /api/v1/providers/lookup[.csv]`, `NpiClient.LookupProvidersAsync`. Enums are camelCase strings in the API JSON.
- **Item 10 (map search, migrations 045–048).** The owner chose (2026-10-08): pins per street address via the free Census geocoder with ZIP-centroid fallback, a separate map page with "View on map" from the search page, a 1,000-result cap with "zoom in", the same filters as the search page, OpenStreetMap tiles. A first version (ZIP-level pins on the search page, PR 14) was replaced.
  - **Address key:** `provider_location.addr_key` (045) is a VIRTUAL column, MD5 of the normalized street + city + state + ZIP, so `project` is unchanged and adding it was instant.
  - **Geocoding:** `address_geocode` (046) caches the Census answers per addr_key (Match with lat/lon, No_Match, Tie); rows are only added. `AddressGeocoder` snapshots the backlog into `geocode_pending`, then sends batches of 10,000 (`CensusGeocoder`, multipart CSV to `geocoding.geo.census.gov/geocoder/locations/addressbatch`, benchmark `Public_AR_Current`), two at a time, saving each answer at once, so a stopped run resumes. Measured: 10,000 addresses ≈ 50 s, ~91% matched. `run` geocodes at most `GeocodeBatchesPerRun` (20 = 200k) new addresses; `Npi.Loader geocode` clears any backlog (it builds the map tables first, so the page works meanwhile); `geocode <county FIPS>` does one county's addresses first (Suffolk, 36103, was done first on 2026-10-09 for testing).
  - **Overture upgrade (owner, 2026-10-09; migrations 049–050):** the Census points are street interpolations (median 48 m from the building in a Suffolk sample, 1 in 10 more than 200 m off). `OvertureMatcher` reads the newest Overture release's public GeoParquet in place on S3 with **DuckDB** (DuckDB.NET.Data.Full; the httpfs extension is installed on first use), only the area's bounding box, and `OvertureIndex` matches in order: (1) address point (Overture addresses = US DOT National Address Database) by house number + normalized street (`AddressNormalizer`) + ZIP or city; (2) the address of a place (a hospital's "101 Nicolls Rd", median of the places using it); (3) for addresses that are only a building name, a place in the same ZIP whose name contains ≥ 75% (and ≥ 2) of the address's words, closest name first. Results go to `address_point` (source, lat/lon, what matched, release); `MapBuilder` prefers them over the Census answer and the ZIP centroid, and `provider_map.source` (050) records which won (address, place, place_name, census, zip), shown on the map as solid / white / gray pins. `Npi.Loader overture [state | county FIPS]` (nothing = every state, one state's area per DuckDB read; only the points its addresses can use are kept in memory, because MySQL leaves ~3.5 GB free). Map rebuilds take a MySQL user lock (one at a time across processes) and run READ COMMITTED (an INSERT … SELECT under REPEATABLE READ locked address_point/address_geocode and made concurrent saves time out); `SkipMapBuild` (env `NPI_SkipMapBuild=true`) lets a script run several steps and rebuild once. Stopping a loader doesn't cancel a statement already running in MySQL: KILL the orphan and wait for its rollback. OpenAddresses was tested too (NY statewide file) but added only 0.3% over Overture, so it isn't used.
  - **Map tables:** `provider_map` (047): one row per (npi, addr_key) with lat/lon, `approximate` and a `POINT SRID 0` with a SPATIAL index. `provider_map_specialty` (048): (taxonomy_code, 0.1° grid cell, npi), because a spatial index can't be combined with a taxonomy column and starting from every dentist nationwide took 8 s. `MapBuilder` rebuilds both together (staging + RENAME) when the projection or the geocodes changed (`reference_data` source `provider_map`). Stage 6 must publish `address_geocode` and rebuild the two map tables on Azure.
  - **Search** (`SearchService.SearchAreaAsync`, `SearchQuery.AreaSql`, `MapBounds`): the area replaces the location filters; at most 1,000 results (`MaxAreaResults`), nearest the centre first; areas larger than 2.5° × 3.5° are refused ("zoom in"). A specialty drives from `provider_map_specialty` by the area's cells; an NPI, exclusion/opt-out list, name or credential drives from its candidates; otherwise the spatial index drives, and for a busy area the search first finds the smallest centred box (⅛, ¼, ½ of the sides) that holds more than the limit, so it sorts thousands of points, not hundreds of thousands.
  - **Web:** `/map` (`Map.cshtml`, `wwwroot/js/map.js`), `GET /map/search?bbox=west,south,east,north&…filters` (JSON, internal, not rate-limited), the specialty/provider fields shared with the search page (`_SpecialtyFields`, `_ProviderFields`, `FilterFields`), `MapService.GetStartAreaAsync` (the area of a search's ZIP/radius, county, city or state for "View on map"). Leaflet 1.9.4 from cdnjs (pinned, SRI). Pins group providers at the same point; tooltips and popups are built with textContent. Near me uses the browser's location only to move the map.
- **Item 12 (standardized credentials, migrations 051–052):** `provider.credential` is free text (4.85M providers, 173,486 distinct values). `Credentials` (Npi.Core) splits a value at separators (`,` `;` `/` `&` `+` `|` AND; a slash before one letter stays: OTR/L), cleans each piece (case, periods, spacing), maps spelled-out titles (NURSE PRACTITIONER → NP) and resolves pieces against the **known credentials**, which `CredentialBuilder` learns from the data: every spelling used on its own by ≥ 25 providers, compared by key (letters/digits only: PA-C = PAC), shown in its most used spelling or its conventional one (PhD, PharmD, PsyD, RPh, LAc …). A key that is two more common credentials run together ("MSCCC-SLP", "MSPT", "MD-PHD") stands for both; two pieces that are one credential split at a separator are rejoined (CCC/SLP → CCC-SLP); spaced abbreviations join (PHARM D → PharmD) but long phrases keep their words (CASE MANAGER); a piece with a word that is no credential stays as written (LPC ASSOCIATE). Live: 1,753 known credentials, 5,714,885 provider credentials, the Credential box lists those held by ≥ 100 providers (owner, 2026-10-09; it was ≥ 25 / 1,561 at first) and narrows by prefix as you type (`combo.js`, an ARIA combobox over a plain text input); **Other** (last) covers everyone holding a credential below that cutoff: CredentialBuilder adds an `Other` row (ord 100, skipped for display) per such provider, so it is searched like any credential; MD 1,128,477 (M.D. alone was 438,080). Tables: `credential_map` (raw → standard), `provider_credential` (NPI → standard, display "MD, PhD" in grid/CSV/detail/API), `credential_list` (dropdown, `GET /api/v1/credentials`), `credential_search` (credential × location, like provider_search, so "MDs in NY" is one index range), `provider_map_credential` (credential × map cell, rebuilt with the credential and the map tables). Built after each projection in `run`/`project`, or `Npi.Loader credentials`. A typed filter resolves through `CredentialCatalog` to a standard credential (exact match) or falls back to the old raw-credential prefix match. Map searches check the credential per point (MD alone is 1.1M candidates).
- **Known slower combinations** (under the 30 s timeout): a state + Care Compare flag, e.g. NY + telehealth + accepts assignment 5.1 s, CA + accepts + minYears 6.8 s, telehealth alone 5.2 s.

### Stage 6 — Publish to Azure & deploy (not started; wait for the owner)
1. **Publisher** (`Npi.Loader publish`): connect to `RemoteMySql` and apply the migrations remotely.
   - **Diff sync:** compare local `row_hash` with a local `publish_state(npi, hash)` table. Batch-upsert changed rows with multi-row `INSERT … ON DUPLICATE KEY UPDATE` (~1000 rows per batch, in transactions), and delete removed NPIs.
   - **Child tables:** replace all rows of each changed NPI.
   - **Reference tables:** replaced in full, only when their version changes.
   - **Order:** update `data_version` last. This keeps the site up and nightly transfers small.
   - **First full publish:** bulk-load into `_staging` with `LOAD DATA LOCAL` (enable the `local_infile` server parameter on Azure) and swap with `RENAME TABLE`. Fall back to batched inserts if that isn't available.
   - **`provider_search`:** rebuild it on Azure from the published child tables instead of transferring it.
2. **Deploy the site:** a GitHub Actions workflow builds, tests and deploys `src/Npi.Web` to App Service on merge to `master`.
   - Authentication: OIDC federated credentials, or a publish profile stored as a GitHub secret.
   - The production connection string lives in App Service Configuration (`RemoteMySql`), never in the repo.
   - Turn on HTTPS-only and Always On; health check `/health`. Document everything in `deploy/azure.md`.
3. **Cost guard:** start at App Service B1 + MySQL B1ms with 20–32 GB storage (≈ $30/month). Before the owner creates anything, measure the projection size and give a monthly cost estimate. Scale up only if the Stage 3 targets aren't met, and report the new cost first.
4. **Scheduling:** `deploy/register-task.ps1` registers a Task Scheduler task that runs `Npi.Loader run && Npi.Loader publish` **daily at ~02:30**.
   - The loader skips files already completed, so daily runs are safe.
   - Task settings: run whether the user is logged on or not; wake the computer; run as soon as possible after a missed start; stop after 12 h; don't start a new instance while one runs.
   - The task uses the loader's scoped login (§8).

---

## 8. Commands and environment

```powershell
dotnet build getnpidata.sln
dotnet test                                         # MySQL integration tests run only when NPI_TEST_MYSQL is set
dotnet run --project src/Npi.Loader -- discover
dotnet run --project src/Npi.Loader -- migrate
dotnet run --project src/Npi.Loader -- run
dotnet run --project src/Npi.Web                    # all interfaces: http://<this-PC>:5000, https://<this-PC>:5001; API docs at /swagger
dotnet pack src/Npi.Client -o <folder>              # Npi.Client.<version>.nupkg
dotnet user-secrets --project src/Npi.Loader list   # shows the local MySQL logins (secrets!)
$env:NPI_TEST_MYSQL = "Server=localhost;User ID=…;Password=…"   # enables the MySQL integration tests
```

**Loader PC setup:**
- **Install:** `winget install Oracle.MySQL --version 8.4.9`, then run `deploy/setup-local-mysql.ps1` from an elevated PowerShell, and set a root password right away.
- **What the script configures:** bind to 127.0.0.1 only, `local_infile=ON`, binary logging off, a large buffer pool.

**MySQL logins on the loader PC:** each one is scoped, with its password kept only in the owner's user-secrets.

| Login | Privileges | Used by | User-secret |
|---|---|---|---|
| Loader login | All privileges on `workplace` + `SESSION_VARIABLES_ADMIN` | The loader and the scheduled task | Npi.Loader `ConnectionStrings:LocalMySql` |
| Test login | All privileges on `npi_test` and `npi\_test\_%` | Integration tests (`NPI_TEST_MYSQL`); `npi_test` is the scratch copy of the data | Npi.Loader `NpiTestMySql` |
| Admin | root | Admin tasks only | Npi.Loader `LocalMySqlAdmin` |
| Site login | `SELECT` on `workplace` only | The local site | Npi.Web `ConnectionStrings:RemoteMySql` |

**Working from a Claude Code session on the loader PC:**
- Claude's shells may see a private `%APPDATA%`, so they can't read or write the owner's user-secrets. Scripts that need the secrets run in the owner's Terminal panel. They read `secrets.json` there, write any MySQL client option file to a temp path, and delete it afterwards.
- Windows PowerShell 5.1 redirection (`*>`) writes UTF-16, so decode before grepping.
- A running site locks `src/Npi.Web/bin`. Stop it before building the solution, or build to another `OutDir`.

---

## 9. Conventions & guardrails

- C# 14 (the net10.0 default), nullable enabled, warnings as errors in `src/`. Async all the way down, with a `CancellationToken` everywhere in the loader and web.
- **All SQL parameterized.** Table and column identifiers come only from whitelists or the validated header mapping, backtick-quoted.
- Never run destructive SQL (TRUNCATE/DROP/RENAME, mass DELETE) against anything but `npi_test` without the owner's explicit OK. Production (`workplace`, Azure) changes go through migrations and the loader/publisher only.
- **Never edit an applied migration** (its checksum is verified); add a new numbered one.
- Tests: every defect class in §3.2 has a regression test using `tests/fixtures`. Integration tests read `NPI_TEST_MYSQL` and are skipped when it's not set (as in CI).
- Don't download the 1.1 GB monthly file in tests. Use fixtures, or a weekly file (~7 MB) for manual end-to-end runs.
- Keep this file and README.md current. If a decision changes, a fact turns out wrong, or a capability is added, fix it here (and in README.md if users see it) in the same commit.

## 10. Git workflow

- Use one branch per stage or topic (`stage-6-deploy`, …), commit early with clear messages, and push.
- Open a PR to `master` with a summary and a test evidence section, and merge when CI is green and the work is complete.
- Never commit secrets, private details (see the top of this file), data files, zips, logs or build output.
- If a secret is ever found in history, tell the owner to rotate it.

## 11. Open items

1. ~~Test DB backup~~ Done 2026-10-07 (restored as `npi_test`, schema in `001_baseline.sql`).
2. **Stage 6 (waits for the owner's go-ahead).** The owner has no Azure subscription yet and will create one, then the resources in the portal (East US, no custom domain).
   - When the owner says to start: measure the projection size and give the cost estimate first.
   - Then build the publisher, the deploy workflow and `deploy/register-task.ps1` (§7 Stage 6).
3. **HUD API token:** issued. It must be present as the `HudApiToken` user-secret on the loader PC (never in the repo).
4. ~~Local MySQL~~ Done 2026-10-07 (MySQL 8.4.9 on the loader PC, §8).
5. **API keys at launch:** off (rate-limited only). The owner can switch them on with `Api:RequireKey=true` + `Api:Keys`.
6. ~~Broad location-only searches are slow~~ Resolved in Stage 4 (§7 Stage 3.3, "Broad searches"): State CA 19.7 s → 2.6 s.
   - Still slow but under the 30 s timeout: a whole state sorted by a non-indexed column (CA by last update, 17.7 s), and state + entity type only (TX organizations, 13 s).
7. **Git history rewritten (2026-10-08, owner's request).** Old credentials, personal identities and private hosts were scrubbed from every commit, and all branches were force-pushed, so every commit ID changed. Clones made before that date must be re-cloned; never push old history back. GitHub still serves the pre-rewrite commits by their exact ID through its pull-request refs until GitHub Support purges them. Rotating any exposed credentials is still the owner's job.

## 12. Progress log

| Date | Stage | Notes |
|---|---|---|
| 2026-10-07 | — | CLAUDE.md created from analysis of the VB loader + owner Q&A. Hosting changed from GoDaddy to Azure App Service + Azure Database for MySQL; target .NET 10 LTS. |
| 2026-10-07 | 0 | **Cleanup.** Owner's uncommitted VB work committed first (passwords → placeholders); nested old copy, `npidata.cs`, ClickOnce `.pfx`, Copilot instructions and 8 missing sibling projects removed; VB moved to `legacy/`. **Setup.** .NET SDK 10.0.401 + gh installed; `src/` + `tests/` skeleton (xunit.v3 on MTP), CI workflow, `deploy/azure.md`. 29/29 tests. |
| 2026-10-07 | 0.5 | **Restore.** Owner's backup (23 per-table mysqldumps from MySQL 8.0.37, 11.8 GB of SQL) restored into `npi_test` on local MySQL 8.4.9 in ~35 min (8.1 GB on disk). `npidata` needed `innodb_strict_mode=OFF`. **Baseline.** `001_baseline.sql` verified: applying it twice to an empty DB gives 22 tables matching the restore. **Row counts:** npidata 9,726,865; practice_locations 1,244,942; other_names 856,161; taxonomy_codes 879; downlog 93. |
| 2026-10-08 | 1 | **Loader.** C# loader (`migrate`, `discover`, `run`, `load-file`); migrations 002–005 (002 rebuilt npidata in 451 s). **Live run against CMS:** 22 min, 6/6 files, exit 0. September monthly: 1.16 GB download in 16 s, 9,798,758 npidata rows in 814 s. Weeklies 090726–100426: 11–14 s each. Deactivation report 091426: 355,330 NPIs. No MySQL warnings. Result: npidata 9,839,369, newest update 2026-10-04, 357,270 deactivated; V2 widths matter (360 LBNs > 70 chars, 232 first names > 20). **Parity** on a 1,928-NPI sample: none missing; the compared fields match for all 1,823 unchanged NPIs; 3 NPIs have one more child row in the C# load (the VB branches were half-disabled). `legacy/` deleted. 113 tests. |
| 2026-10-08 | 2 | **Reference data:** `reference` + automatic refresh in `run`; migrations 006–010. **Live load ~8 s:** NUCC 261 = 883 codes; HUD 2026Q2 = 54,561 ZIP/county pairs; Census 2026 = 3,236 counties + 33,791 ZIP centroids. 5,759 HUD ZIPs have no centroid. Raw-data spot check: 941 chiropractors in Suffolk County, NY (slot 1, primary location only). 128 tests. |
| 2026-10-08 | 3 | **Projection:** `project` + migrations 011–016. Full build 35 min: 9,482,099 providers, 12,308,777 taxonomy rows, 10,745,988 locations, 812,104 other names, 13,229,632 search rows. CMS writes the literal "NULL" in ~16,500 license states (treated as empty). **Benchmarks (warm):** Chiropractor + Suffolk 961 matches 0.28 s; Chiropractor + NY 0.08 s; Dentist within 10 mi of 10001 0.78 s; "smith" + NY 0.49 s. The first design (EXISTS subqueries) took 1.5–13.6 s, which led to `provider_search`, the driver + JOIN_ORDER and the windowed count. 176 tests. |
| 2026-10-08 | 4 | **Website:** search form, sortable paged grid, CSV export, provider detail, footer. Broad-search plan: State CA 19.7 s → 2.6 s, NY 1.4 s; 30 s timeout. **Browser checks:** Chiropractor + Suffolk = 961 results, matching the CSV (962 lines); Dentist within 10 mi of 11701 = 1,250; bad ZIP → 400; unknown NPI → 404; mobile overflow fixed. **Network:** launch profile bound to 0.0.0.0 for LAN access. 190 tests. |
| 2026-10-08 | — | **`workplace` created** on the loader PC (owner's OK) from the owner's backup, then `migrate` + `run` (all 6 files, exit 0, projection 33 min). Same counts as `npi_test`. Scoped MySQL logins created for the loader, the site and the tests (§8), with random passwords stored only in the owner's user-secrets. The site now reads `workplace`. |
| 2026-10-08 | 5 | **REST API** `/api/v1` with RFC 7807 errors, OpenAPI + Swagger UI, per-IP rate limit, CORS, output cache, optional API key. **Checked against `workplace`:** same results as the page; CSV byte-identical to `/export.csv`. Warm timings: Chiropractor + Suffolk 0.27 s, "smith" + NY 0.50 s, Dentist within 10 mi of 10001 0.89 s, State CA 3.6 s. 207 tests. |
| 2026-10-08 | 5 | **`Npi.Client`:** typed netstandard2.0 client + `tests/Npi.Client.Tests` (16 tests, including client/server contract checks). Checked against the live site: every call works; it packs into `Npi.Client.1.0.0.nupkg`. 223 tests. |
| 2026-10-08 | — | **Owner's Azure answers** recorded (§2, §11 item 2). |
| 2026-10-08 | — | **Repository cleanup.** Removed `db/reference/` (the 2021 schema dump, superseded by `001_baseline.sql`; still in history); replaced the Visual Studio template `.gitignore`/`.gitattributes` with project-specific ones; rewrote README.md as the user-facing description of the service; reorganized this file (section numbers kept, because code and migrations cite them) and removed private details. |
| 2026-10-08 | — | **History rewrite** (owner's request): `git filter-branch` over all 33 commits; only the blobs containing private strings changed (all other files byte-identical), and all personal author/committer identities were mapped to the owner's GitHub noreply address. All 8 branches force-pushed; local repo re-pointed and pruned. The repo now commits with the noreply address. See §11 item 7. |
| 2026-10-08 | 5.5 | **Items 1–8** (migrations 017–044), merged as the first Stage 5.5 PR. **Live loads into `workplace`:** LEIE 84,001 rows (5 s); opt-out 57,780; order/refer 2,058,209 (22 s); Care Compare 3,388,628 rows → 1,627,468 clinicians (160 s), affiliations 2,254,034, hospitals 5,419, nursing homes 14,690, enrollments 23,571; Medicare by provider 1,296,739, by service 9,781,673 → 4,685,762 top rows (290 s), Part D 1,416,883; HPSA 8,402 county/discipline rows; population 3,144 counties (PR not covered); Open Payments 2025: 9.2 GB, 16,131,856 records → 1,020,608 NPIs, $2.89 B, 1,277,309 by-kind rows, 2,166,183 payer rows (865 s). **Timings:** excluded=true 7,756 matches 0.11 s; Chiropractor + Suffolk 0.26 s (961; 199 accept assignment); a 27.5 s state + Care Compare search → 4–7 s after the driver join and index 032; bulk lookup of 1,000 NPIs 0.43 s. Tests: Core 81, Client 40, Web 28, Loader 130 (with MySQL). Item 1 data backfill still pending. |
| 2026-10-08 | 5.5 | **Item 10: map + near me** (owner chose OpenStreetMap tiles). Leaflet map of each results page at ZIP centroids; "near me" via the browser's location → nearest ZIP → 10-mile radius search. **Checked in the browser against `workplace`:** Chiropractor + Suffolk page = 50 providers on 24 pins; popups list providers with links; a simulated position in Hauppauge → ZIP 11788, 475 chiropractors within 10 miles; nearest-ZIP lookup 0.3 s; no sideways scroll at 375 px. Tests: Core 94, Web 34, Client 40, Loader 131 (with MySQL). |
| 2026-10-09 | 5.5 | **Item 10 reworked into a map search page** (owner's spec: pins per street address, hover lists everyone there, "Search in this area", sortable table of the pins in view, no map on the search page). Census geocoder test: 10,000 NY dental addresses in 49 s, 94% matched (retrying misses without the suite fixed 1 of 96, so addresses are sent as-is). **On `workplace`:** 3,307,206 distinct practice street addresses; geocoding ~24,000–29,000/min, 91% matched (full backfill ~2 h, running at merge time); `provider_map` 10,583,810 provider addresses, built in 16–21 min. **Area search (warm):** Manhattan with no filter 0.6 s (1,000 of many, nearest first), dentists in Manhattan 0.5 s (was 7.6 s before `provider_map_specialty`), nurse practitioners in a 2° × 3° Texas area 0.5 s (was 12.5 s), chiropractors across NYC 0.4 s, telehealth in a 2° × 3° area 1.5 s; first (cold) runs 1–4 s. **Browser:** auto-search from "View on map" (Chiropractor + Suffolk), hover lists and row highlighting, sorting both ways, 375 px stacked layout with no sideways scroll. Tests: Core 94, Web 34, Client 40, Loader 133 (with MySQL). |
| 2026-10-09 | 5.5 | **Overture upgrade, Suffolk County first** (owner's go-ahead; the rest of the country waits for the owner's confirmation). **Test:** of 25,800 Suffolk practice addresses, Overture address points matched 83.6%, OpenAddresses 78.2%, both 83.9% (so OpenAddresses isn't used); Census points were a median 48 m from the Overture point. ZIP 11794 (Stony Brook University's own ZIP: "101 Nicolls Rd" is a mailing address on no street range, a third are building names) was ~10% placed by Census. **Live (`overture 36103`):** 26,187 addresses, Overture area (2.53M address keys, 182,143 places) read from S3 in 42 s; **23,806 placed (90.9%)**: 21,884 address point, 1,556 place address, 366 place name; 11794: 2,474 of 2,947 providers (84%), name matches all on campus. Map rebuild 23 min. Browser: 3 Tinker Ln on the house; the Stony Brook campus as 7 building pins. Tests: Loader 151 (with MySQL), Core 94, Web 34, Client 40. |
| 2026-10-09 | 5.5 | **Item 12: standardized credentials** (migrations 051–053). 173,486 raw values → 1,753 known credentials; 5,714,885 provider credentials; dropdown of 1,561 (≥ 25 providers, most common first, with counts). MD 1,128,477 (M.D. alone 438,080); "MS, CCC/SLP", "M.S.CCC-SLP", "MS CCC SLP" → MS + CCC-SLP. Build 2–7 min (credential_search and the map cells add the most). **Timings (warm):** MD + NY 101,280 in 1.1 s (15–19 s before `credential_search`), MD + CA 2.4 s, DC + Suffolk 0.3 s, Chiropractor + DC + NY 0.5 s; map: CRNA in Manhattan 0.3 s (14.5 s before `provider_map_credential`), MD 1.1 s. Typed "M.D." selects MD in the dropdown. Tests: Core 117, Web 34, Client 41, Loader 156 (with MySQL). |
