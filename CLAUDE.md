# CLAUDE.md — getnpidata

This file is the brief for any Claude Code session working in this repo. Read it fully before changing anything. It records decisions the owner has already made; do not re-ask them. Open items are listed in §11. Ask the owner about those, and about anything new and ambiguous.

---

## 1. What this project is

**Goal:** a public website and REST API for searching every US healthcare provider (medical, dental, etc.) in the CMS NPPES registry. Example query: "all Chiropractors in Suffolk County, NY", shown in a paged grid and downloadable as CSV.

**Pipeline:**

```
CMS NPPES files ──► Loader (Windows PC, scheduled) ──► Local MySQL (full data, system of record)
                                                          │
                                         build search projection (slim tables)
                                                          │
                                       Publisher (diff sync) ──► Azure Database for MySQL (search tables only)
                                                                          │
                                                   ASP.NET Core site + /api/v1 (Azure App Service, same region)
```

- **Data source:** https://download.cms.gov/nppes/NPI_Files.html, downloaded from https://download.cms.gov/nppes/<file>.zip
- **Repo:** https://github.com/amazedbot/getnpidata. The old remotes (`[old GitHub remote]`, `[old GitLab remote]`) are history only. Never push to them.

---

## 2. Decisions already made (do not re-ask)

| Topic | Decision |
|---|---|
| Website | ASP.NET Core on **.NET 10 (LTS)**, Razor Pages. Not .NET 8: its support ends Nov 2026 |
| Language | **C#** for all new code. The legacy VB loader is ported to C# (see §5) and then deleted |
| Loader runtime | C# .NET 10 console app, runs on the owner's Windows PC next to the local MySQL. Scheduled with **Windows Task Scheduler** |
| Hosting | **Azure App Service** (Linux, .NET 10) + **Azure Database for MySQL – Flexible Server** (Burstable B1ms to start, MySQL 8), both in the **same region** (East US unless the owner says otherwise). GoDaddy is no longer used |
| Access | Public site. API public too, protected by rate limiting; build it so an API-key requirement can be switched on later via config |
| County search | ZIP→county via the **HUD USPS ZIP-COUNTY crosswalk**. A ZIP matches **every** county it overlaps ("match any") |
| Specialty search | Match **all 15** taxonomy slots. UI dropdown by NUCC **Classification** (e.g., "Chiropractor"); optional dependent Specialization dropdown |
| Addresses searched | Primary practice location + secondary practice locations (`pl_pfile`). Not the mailing address |
| Filters | Specialty, state, county, city, ZIP, **ZIP + radius (miles)**, name (last/first or org), NPI, entity type (individual/org), gender, credential |
| Deactivated NPIs | **Flagged** in the DB (not deleted) and **never** returned by the site or API |
| Grid/CSV columns | Summary columns (§7.3). CSV is **streamed**, with **no row cap** |
| NUCC taxonomy | Loader refreshes `taxonomy_codes` automatically |
| Load cycle | Full monthly replace + weekly incremental updates. The site must stay up during reloads |
| Schema changes | Allowed: widen columns for V2, utf8mb4, new tables and indexes |
| Logging | Log file only (no email/alerts). Non-zero exit code on failure |
| Repo cleanup | Remove the sibling projects from the solution; delete the nested `getnpidata/getnpidata/` copy |
| Git | Claude Code may create branches and commit on its own (see §10) |

---

## 3. Current state of the repo (as received, Oct 2026)

```
getnpidata/                    ← repo root (VB.NET, .NET Framework 4.8.1 console, VS2022)
  Module1.vb                   ← ALL loader logic (~1300 lines, partially commented out mid-debug)
  getnpidata.vbproj / .sln     ← .sln also references 8 sibling projects NOT in the repo
  App.config                   ← connection strings + UnzipFolderName (C:\Users\[user]\Downloads\Unzips)
  My Project/Settings.settings ← an old db_connection setting (placeholder password)
  packages.config              ← MySql.Data 9.6, ExcelDataReader 3.8, plus unused Azure/SqlClient/WebView2 packages
  workplace_20210830.sql       ← 2021 schema dump (V1 field lengths, MISSING several tables, see §6)
  npidata.cs                   ← empty placeholder class (solution item). Delete
  getnpidata_TemporaryKey.pfx  ← ClickOnce signing key. Delete, and drop ClickOnce publish settings
  .github/copilot-instructions.md ← Azure Copilot rules, irrelevant. Delete
  getnpidata/getnpidata/       ← OLD nested copy with its own .git (GitLab). Delete (decided)
  bin/ obj/ .vs/ packages/     ← build output. Must stay git-ignored
```

> **Stage 0 (Oct 2026) restructured this, and Stage 1 deleted the VB project after the parity check (it remains in git history).** During Stage 0 the VB project lived in `legacy/getnpidata-vb/` (own `getnpidata-legacy.sln`, ClickOnce settings removed, passwords replaced by placeholders), the 2021 dump is in `db/reference/`, and the root `getnpidata.sln` holds only the new projects (§5). The tree above is kept as the record of what was received.

Sibling projects referenced by the `.sln` that are not present and are out of scope: Snowflake_access, UpdateAzureDB, TestAzureDB, getnpidata_SqlServer, SnowFlake_Load, npidata_loadToSnowflakeStage, npidata_loadToSnowFlakeStageODBC, npidata_LoadTableFromStage.

### 3.1 What the legacy VB loader does

1. Scrapes `NPI_Files.html` with the WinForms **`WebBrowser`** (IE) control and collects every `.zip` href.
2. For each zip not yet in `downlog`, it immediately inserts it into `downlog`. It then downloads the zip with `WebClient` to the working directory and unzips the `.csv` files (excluding `_FileHeader`) and the deactivation `.xlsx` into `UnzipFolderName`, logging each one to `extractlog`.
3. Per extracted file, **only the other-names branch is currently active** (the owner was debugging it). Everything else is commented out:
   - Monthly `npidata_pfile` → `LoadFile(... truncate=True)`: split the CSV into 100k-line chunks under `C:\temp\short.csv<n>.csv`, `MySqlBulkLoader` into `npidata_temp`, `TRUNCATE npidata`, then `INSERT … SELECT`.
   - Weekly `npidata_pfile` → `UPDATE … JOIN` on NPI, then insert new NPIs.
   - `othername_pfile` → `other_names_temp` → `INSERT … WHERE NOT EXISTS` into `other_names` (date column reformatted MM/dd/yyyy → yyyy-MM-dd).
   - `pl_pfile` → `practice_locations_temp` → `INSERT … WHERE NOT EXISTS` into `practice_locations`.
   - Deactivation xlsx → `NPPES_Deactivated_NPI_Report` → `UPDATE npidata SET NPI_Deactivation_Date`.
4. Runs `SET GLOBAL local_infile = 1` at start (needs admin privileges; works locally only).

### 3.2 Known defects in the legacy code. Do NOT carry these into the port

1. **Silent row loss:** `SplitCSVFile` calls `sw.WriteLine(sr.ReadLine(), True)`, which treats each data line as a *format string*. Any line containing `{` or `}` throws, and the bare `Catch` sleeps and drops it.
2. **Column shifting:** both newer `BulkLoad` functions parse with quote awareness, then `String.Join(",", cols)` *without re-quoting*. Any value containing a comma (common in org names and addresses) shifts the columns.
3. **No retry after failure:** `NPIfileSeen` writes to `downlog` *before* processing. A crash means that file is skipped forever.
4. **False success:** `GetNPIdata` swallows download exceptions, prints "Successfully Downloaded", and returns the **exe path**. The caller assumes CWD = exe directory.
5. **NULL never equals NULL:** the `other_names` dedupe uses `n.Created_Date = t.Created_Date`, which is never true for NULL dates, so re-runs insert duplicates. `practice_locations` has the same pattern.
6. **Deactivation branch** passes the zip *name*, not the extracted xlsx path, to the Excel reader. It also skips exactly 2 header rows by assumption.
7. **Case-sensitive file matching:** `"_FileHeader"` in `UnZip` vs `"_fileheader"` in `Main`.
8. **Old file naming:** V1 is gone (§4). V2 widened the first-name and legal-business-name fields beyond the 2021 schema (`varchar(20)` / `varchar(70)`).
9. **Weekly before monthly:** weeklies older than the monthly they follow can overwrite newer data (no `Last_Update_Date` guard).
10. **Unsafe SQL:** filenames and table names are concatenated into SQL (`NPIfileSeen`, `DoesTableExist`).
11. **Site outage during monthly load:** `TRUNCATE npidata` before reload leaves the table empty for the duration.
12. **Minor:** the module-level `sMySqlConnectionString` is shadowed by a local; `LoadCtr` is meaningless; `FindAnyInString`/`GetMonthNamesInList` are unused; `DB.Dispose` does nothing; DEBUG builds wait on `Console.ReadLine()`.

---

## 4. Source data facts (verified against the CMS page, Oct 2026)

- **V1 retired 03/03/2026.** Only V2 files are published. Accept only `_V2` names.
- Files on `NPI_Files.html` (match by regex, case-insensitive, on the href file name):
  - Monthly full: `NPPES_Data_Dissemination_<MonthName>_<YYYY>_V2.zip`, ~1.1 GB zipped, roughly 10 GB of CSV unzipped
  - Weekly incremental: `NPPES_Data_Dissemination_<MMDDYY>_<MMDDYY>_Weekly_V2.zip` (~6–8 MB; the page keeps about 4)
  - Monthly deactivations: `NPPES_Deactivated_NPI_Report_<MMDDYY>_V2.zip` (contains an .xlsx)
- Each data zip contains `npidata_pfile_*.csv`, `othername_pfile_*.csv`, `pl_pfile_*.csv`, `endpoint_pfile_*.csv`, matching `*_fileheader.csv` files, and a Readme PDF. Match inner names case-insensitively. **Endpoints are out of scope** (skip them, but log it).
- The `npidata` CSV has ~330 columns, every field double-quoted, and dates as `MM/DD/YYYY`. Treat the header row as the source of truth for column order.
- **Header → DB column rule** (matches the existing schema): replace each run of non-alphanumeric characters with `_` and trim `_`. Examples: `Provider Organization Name (Legal Business Name)` → `Provider_Organization_Name_Legal_Business_Name`, `Employer Identification Number (EIN)` → `Employer_Identification_Number_EIN`. **Verified against the real V2 headers (weekly 092826_100426):** all 330 `npidata` columns match in order, given these aliases (`HeaderMapper`): the suffix `_If_outside_U_S` is dropped (`… Country Code (If outside U.S.)` → `…_Country_Code`; the full name would also exceed MySQL's 64-character limit); V2's `Provider Sex Code` → `Provider_Gender_Code`; and `pl_pfile`'s `Provider Secondary Practice Location Address- Address Line 1/2` → `Provider_Secondary_Practice_Location_Address_Line_1/2`. An unmapped header fails the load (new CMS columns need a migration).
- **Field lengths (V2 Readme):** LBN and other organization name 100, first names 35, last names 35, middle 20, credential 20, addresses 55, city/state 40, postal 20. Raw `npidata` stores everything as TEXT anyway (§6.1).
- **CSV format (verified):** `\n` line endings, every field double-quoted. Per the Readme, double quotes inside values are replaced by single quotes, so no escaping occurs. The data file's header equals its `_fileheader.csv`.
- **NPI_Files.html** links are single-quoted relative hrefs with a `./` prefix (`href='./NPPES_…_V2.zip'`).
- **Deactivation report:** one `.xlsx` with a title row (`NPPES Deactivated Records as of …`), a header row, then NPI (text) + date (text `MM/DD/YYYY`). It is the **full current list** (355,330 NPIs on 2026-09-14, back to 2005), not a delta.
- **NPPES has no county field.** County comes from the ZIP crosswalk (§6.3).

---

## 5. Target solution layout

```
getnpidata/
  CLAUDE.md
  getnpidata.sln                      ← only the projects below
  global.json                         ← pins the .NET 10 SDK band; selects Microsoft.Testing.Platform for `dotnet test`
  Directory.Build.props               ← net10.0, nullable, implicit usings (src/ adds warnings-as-errors)
  Directory.Packages.props            ← central package versions (no Version= in project files)
  src/
    Npi.Core/        (net10.0 classlib) models, search filter + SQL builder, DB access, CSV writer
    Npi.Loader/      (net10.0 console) discover → download → load → reference data → projection → publish
    Npi.Web/         (net10.0 ASP.NET Core) Razor Pages UI + /api/v1 endpoints in ONE deployable app
  tests/
    Npi.Core.Tests/    (xUnit)  query builder, filters, CSV
    Npi.Loader.Tests/  (xUnit)  file-name classification, header mapping, fixture loads
    fixtures/          tiny hand-made CSV/XLSX files with nasty values (commas, quotes, { }, backslashes, empty dates, unicode)
  db/
    migrations/        numbered idempotent .sql files (001_baseline.sql, 002_…), applied by the loader's `migrate` command
    reference/         workplace_20210830.sql (2021 schema dump, reference only, never applied)
  deploy/
    register-task.ps1  Task Scheduler registration
    azure.md           Azure setup + deploy notes
  .github/workflows/   CI (build+test on PR) and deploy-to-App-Service on merge
  (legacy/getnpidata-vb/ held the VB project until Stage 1 parity; deleted 2026-10-08, still in git history before that commit)
```

**Tests** use **xUnit v3** on **Microsoft.Testing.Platform** (MTP). The .NET 10 SDK no longer runs xunit.v3 4.x through VSTest, so `global.json` opts `dotnet test` into MTP. Consequences: run tests with `dotnet test` or `dotnet test --solution getnpidata.sln` (not `dotnet test getnpidata.sln`); a test project with zero tests fails (exit code 8); no `Microsoft.NET.Test.Sdk` / `xunit.runner.visualstudio` packages are needed.

**Libraries:** `MySqlConnector` (not Oracle `MySql.Data`; it is async and supports `MySqlBulkLoader.SourceStream`), `Dapper`, `ExcelDataReader` (+ `System.Text.Encoding.CodePages`), `CsvHelper`, `Serilog` (+ File sink), `AngleSharp` or a strict regex for href scraping, `Microsoft.AspNetCore.OpenApi` (built-in OpenAPI) + Swagger UI or Scalar for the docs page, built-in ASP.NET Core rate limiting and output caching.

**Config & secrets:** `appsettings.json` holds non-secret defaults. Connection strings, the HUD token and the Azure credentials go in **user-secrets (loader/dev) / environment variables / `appsettings.Local.json` (git-ignored)**, and in **App Service → Configuration** for production. **Never commit a real password or token.** The HUD token has already been issued to the owner. If it is missing on your machine, ask the owner to set it (`dotnet user-secrets --project src/Npi.Loader set "HudApiToken" "<token>"`); never paste it into a tracked file. Connection names: `LocalMySql`, `RemoteMySql`. Settings: `WorkFolder` (downloads/temp, default `%ProgramData%\getnpidata\work`), `LogFolder`, `HudApiToken`.

---

## 6. Database design

**Local MySQL 8** (database `workplace` on the owner's PC) holds the full raw data and the search projection. **Azure Database for MySQL** holds only the search projection plus reference tables. It requires TLS (`SslMode=Required`) and firewall rules for the owner's home IP and the App Service outbound IPs (or "Allow Azure services"). Use utf8mb4 everywhere and InnoDB.

### 6.1 Raw tables (local only)

| Table | Notes |
|---|---|
| `npidata` | 1 row per NPI, all 330 CSV columns, **all TEXT** utf8mb4 except `NPI CHAR(10)` PK (migration 002). Raw dates kept as text; empty → NULL. `Is_Deactivated TINYINT`, `Loaded_From`. No secondary indexes (search uses the projection). InnoDB's worst-case row-size check rejects 330 columns even as TEXT (counts 40 B each), so DDL creating/altering it runs with `innodb_strict_mode=OFF`. Real rows always fit (long TEXT goes off-page with a 20-byte pointer, so even a full row is ~7.3 KB). The loader's DB user therefore needs `SESSION_VARIABLES_ADMIN` |
| `other_names` | from `othername_pfile`. No unique key needed: the loader **replaces** rows (whole table monthly, per NPI weekly) instead of de-duplicating, which removes defect #5 by construction |
| `practice_locations` | from `pl_pfile` (secondary locations). Add surrogate `id` |
| `nppes_deactivated_npi_report` | NPI, deactivation date. **Replaced in full** by each report (staging + RENAME), so reactivated NPIs drop out |
| `downlog` | **extended (003):** `filename` UNIQUE, `kind` (Monthly/Weekly/Deactivation), `file_date`, `status` (Downloading/Loading/Completed/Failed; VB-era rows = `Legacy`, which does not count as done), `started_at`, `completed_at`, `rows_loaded`, `error` |
| `schema_migrations` | version, name, sha256, applied_at; written by `Npi.Loader migrate` |

**Deactivation flag rule** (`Deactivations.ApplyAsync`): `Is_Deactivated = 1` when the NPI has a deactivation date (report date, else `NPI_Deactivation_Date`) and no `NPI_Reactivation_Date` on or after it. An empty `NPI_Deactivation_Date` is filled from the report. It is recomputed after every monthly and deactivation load (all rows) and every weekly (that week's NPIs). Date comparisons use `YYYYMMDD` strings built with SUBSTRING, because `STR_TO_DATE` errors on malformed values in strict SQL mode.
| `extractlog` | keep (zip, extracted file, rows) |
| `*_staging` | per-load staging tables, created `LIKE` the target, dropped after swap |

**Actual schema (owner's backup, 2026-10-07)** is in `db/migrations/001_baseline.sql`, with a faithful copy and caveats in `db/migrations/README.md`. Key facts for Stage 1:
- `npidata`: 330 columns, all `varchar` (dates as text, empty values as `''`, not NULL). **V1 widths** (first name 20, LBN 70) and **latin1**, although `downlog` shows V2 files were loaded, so long V2 values were probably truncated. No `Is_Deactivated`/`Loaded_From`. Secondary indexes on entity type, taxonomy 1 and 2, last name, first name, mailing state and mailing country.
- `other_names` (ID, NPI, org name, type code, Created_Date DATE) and `practice_locations` (ID + 10 `Provider_Secondary_Practice_Location_*` columns) already have surrogate IDs, an NPI index and **no unique key**. Their `_temp` tables exist too.
- `downlog` is only (id, filename, received_date), latin1. Its last entries are the September 2026 monthly, the 091426 deactivation report and the 090726–091326 weekly. Because of defect #3 and the commented-out branches, a `downlog` row does **not** prove the file was loaded.
- `nppes_deactivated_npi_report`, `statelookup` and `us_city_populations` are **empty**. `taxonomy_codes` (879 rows; Display_Name and Section included) lacks Effective/Deactivation dates.
- NPPES code tables exist with seed rows: `entity_types`, `gender_codes`, `state_codes` (60), `country_codes` (236), `other_provider_name_type_codes`, `other_provider_identifier_issuer_codes`, `sole_proprietor_codes`, `subpart_codes`.
- Unrelated or obsolete: `animals`, `dicomhosts`, `taxonomy_codes_old` (empty). Leave them alone; dropping them needs the owner's OK. There is no `npidata_dev`.

### 6.2 Search projection (local, then published to Azure)

| Table | Columns (indicative) | Key indexes |
|---|---|---|
| `provider` | npi PK, entity_type (1=individual, 2=org), last/first/middle name, prefix/suffix, credential, org_name, gender, primary_taxonomy_code, phone, enumeration_date DATE, last_update_date DATE, row_hash | (last_name, first_name), (org_name), (credential) |
| `provider_taxonomy` | npi, slot 1–15, taxonomy_code, is_primary, license_no, license_state | (taxonomy_code, npi) |
| `provider_location` | id, npi, is_primary, address1, address2, city, state, zip5, zip4, phone | (state, city), (zip5), (npi) |
| `zip_county` | zip5, county_fips, res_ratio, bus_ratio, tot_ratio, year, quarter | PK (zip5, county_fips), (county_fips) |
| `county` | county_fips PK, state, county_name | (state, county_name) |
| `zip_centroid` | zip5 PK, lat, lon (Census ZCTA Gazetteer) | — |
| `taxonomy_codes` | existing NUCC table + `Display_Name` | (Classification) |
| `data_version` | as-of date of the monthly/weekly/deactivation files, published_at | — |

Deactivated NPIs are **excluded** from the projection, since they are never shown. Their flag stays in local `npidata`.

### 6.3 Reference data sources

- **NUCC taxonomy CSV:** linked from https://www.nucc.org (Code Sets → Taxonomy → CSV). The file name is versioned (`nucc_taxonomy_<ver>.csv`), so discover the link. Updated twice a year; refresh it when the version changes.
- **HUD USPS ZIP-COUNTY crosswalk:** API `https://www.huduser.gov/hudapi/public/usps?type=<n>&query=All`, header `Authorization: Bearer <HudApiToken>`. The type for zip-county is believed to be **2**; confirm in HUD docs. Updated quarterly. Token is configured as `HudApiToken` (secret, see §5). Returns county **GEOID (FIPS)**, not names.
- **County names:** Census national county file (e.g., `https://www2.census.gov/geo/docs/reference/codes2020/national_county2020.txt`).
- **ZIP centroids (radius search):** Census Gazetteer ZCTA file (`…/gazetteer/<year>_Gazetteer/<year>_Gaz_zcta_national.zip`). ZCTA ≈ ZIP. ZIPs without a ZCTA can't use radius search; say so in the UI.

---

## 7. Stage plan

Work stage by stage, one branch per stage (§10). At the end of each stage, update §12 (Progress log) in this file.

### Stage 0 — Repo hygiene & discovery
1. Make sure `origin` = `https://github.com/amazedbot/getnpidata`. Check that `.gitignore` covers `bin/ obj/ .vs/ packages/ *.user appsettings.Local.json logs/ work/`.
2. Delete `getnpidata/getnpidata/` (the nested repo), `npidata.cs`, `getnpidata_TemporaryKey.pfx`, `.github/copilot-instructions.md`. Remove the 8 sibling projects and the "Solution Items" folder from the `.sln`.
3. Move the VB project to `legacy/getnpidata-vb/`. Strip its unused packages only if that's needed to keep it building; otherwise leave it.
4. Create the `src/` and `tests/` skeleton (§5), add the projects to the `.sln`, and get `dotnet build` + `dotnet test` green.
5. When the test DB backup arrives: restore it locally as `npi_test`, dump its schema to `db/migrations/001_baseline.sql`, and record the row counts in §12.
6. Write `deploy/azure.md`: the resources to create (resource group, MySQL Flexible Server, App Service plan + web app, same region), firewall rules, server parameters (`require_secure_transport=ON`; `local_infile=ON` if bulk publish is used), and app settings. Provisioning is done by the owner in the portal, or by Claude Code via `az` CLI **only with the owner's OK** (it costs money).

### Stage 1 — C# loader (parity with, and replacement for, the VB loader)
Commands (`Npi.Loader <command>`): `migrate`, `run` (default; does everything new), `discover` (dry-run list), `load-file <zip>` (manual), `reference` (NUCC/HUD/Census), `project`, `publish`.

1. **Discover:** `HttpClient` GET of `NPI_Files.html`. Extract the hrefs and classify them by regex (V2 only):
   - `^NPPES_Data_Dissemination_(?<month>[A-Za-z]+)_(?<year>\d{4})_V2\.zip$` → monthly
   - `^NPPES_Data_Dissemination_(?<from>\d{6})_(?<to>\d{6})_Weekly_V2\.zip$` → weekly
   - `^NPPES_Deactivated_NPI_Report_(?<date>\d{6})_V2\.zip$` → deactivation
   - Anything else → log a warning and ignore it.
   Order: the newest unprocessed monthly first, then weeklies in chronological order, then deactivations.
2. **Bookkeeping:** a file counts as done only when `downlog.status = 'Completed'`. Failed or partial entries are retried on the next run. Use parameterized SQL everywhere.
3. **Download:** stream to `WorkFolder` via a temp name, rename on success, and verify the zip opens. Retry with backoff. Never report success on failure. Delete zips after a successful load (keep the last one with a config flag).
4. **Bulk load:** stream each CSV straight from the zip entry into `MySqlBulkLoader` (`SourceStream`, `Local = true`). Use `FIELDS TERMINATED BY ',' ENCLOSED BY '"' ESCAPED BY ''` and the line terminator detected from the header. Build the column list from the **header row** (§4 mapping). Load the raw text into a staging table, and convert dates/empties to NULL **in SQL**, not by rewriting the CSV in C#. This removes defects #1 and #2 by construction.
   *As built:* `MySqlBulkLoader` can't emit `ESCAPED BY ''` (it omits the clause, and MySQL then defaults to `\`). So the escape character is **U+0001**, which never occurs in NPPES text and disables escaping in practice; the backslash fixture test guards this. Columns load into user variables: `(@c0, …) SET col = NULLIF(@c0, '')`, and DATE columns use `STR_TO_DATE(…, '%m/%d/%Y')`. `LOAD DATA LOCAL` downgrades errors to warnings (short rows, truncation, duplicate keys), so **any warning fails the load**.
5. **Monthly full:** load `npidata_staging`, `other_names_staging` and `practice_locations_staging`. Sanity-check the row counts (e.g., ≥ 95% of the current count). Then atomically `RENAME TABLE npidata TO npidata_old, npidata_staging TO npidata` (same for the other tables) and drop the `_old` tables.
6. **Weekly:** load into staging, then `INSERT … ON DUPLICATE KEY UPDATE` **only where the incoming `Last_Update_Date` ≥ the existing one** (compare as dates). For other names and practice locations: delete the rows for the NPIs present in the weekly file, then insert theirs.
7. **Deactivations:** read the xlsx with ExcelDataReader (register CodePages). Find the first row whose first cell is a 10-digit NPI (don't hard-code 2 header rows). Upsert into `nppes_deactivated_npi_report`, then set `npidata.Is_Deactivated = 1` and the deactivation date.
   *As built:* the report is the full current list, so it **replaces** the table (staging + RENAME, with the same ≥ 95% sanity check) instead of upserting; flags follow the rule in §6.1. Only the newest report on the page is loaded, and likewise only the newest monthly.
8. `local_infile`: do **not** run `SET GLOBAL`. Check `@@local_infile`; if it's off, fail with a clear message telling the owner how to enable it in `my.ini`.
9. **Logging:** Serilog rolling file `logs/getnpidata-YYYYMMDD.log`, kept 60 days. Write a summary line per file (rows, duration). Exit code 0 means everything completed; non-zero means something failed.
10. **Parity check:** run both loaders against `npi_test` with the same files and compare counts and spot rows. Then delete `legacy/`.
   *As built:* the VB loader can't run on this PC (it needs Visual Studio/MSBuild for .NET Framework 4.8.1, and its load branches are commented out). Parity is therefore checked against the **VB-loaded data restored in `npi_test`**: load the same CMS files with the C# loader and compare counts and spot rows (§12).

### Stage 2 — Reference data
NUCC → `taxonomy_codes` (upsert; only when the version changed), HUD → `zip_county` (quarterly), Census → `county`, Gazetteer → `zip_centroid`. All idempotent, all called by `run` when stale.

### Stage 3 — Search projection + `Npi.Core` search service
1. `project` builds `provider`, `provider_taxonomy` (unpivot the 15 slots, skip blanks) and `provider_location` (primary practice address from `npidata` plus each `practice_locations` row; normalize zip5/zip4). Exclude deactivated NPIs. Compute `row_hash` per row for the publisher.
2. `SearchFilter` record: Classification, Specialization, TaxonomyCode, State, CountyFips, City, Zip5, RadiusMiles, LastName, FirstName, OrgName, Npi, EntityType, Gender, Credential, Sort, Page, PageSize.
3. **SQL builder** (parameterized only; sort columns whitelisted):
   - Specialty → `EXISTS (SELECT 1 FROM provider_taxonomy t JOIN taxonomy_codes c … WHERE t.npi = p.npi AND c.Classification = @cls [AND c.Specialization = @spec])`
   - Location filters apply to `provider_location`, where **any** location of the NPI matches. County → `l.zip5 IN (SELECT zip5 FROM zip_county WHERE county_fips = @fips)`. Radius → bounding box on `zip_centroid`, then a haversine filter.
   - Name → prefix match (`LIKE @x%`), case-insensitive collation. No leading wildcards.
   - Return one row per NPI. The grid shows the **matching** location (the first by is_primary desc).
4. **Validation:** require at least one filter. NPI must be 10 digits; ZIP must be 5 digits; radius 1–100 miles; page size ≤ 200.
5. **Performance target:** < 2 s for "Classification + county" and "Classification + state" on full data. Check with `EXPLAIN` and add indexes as needed. Record the results in §12.

### Stage 4 — Website (`Npi.Web`, Razor Pages)
- `/`: search form. Dropdowns: Classification (cached), Specialization (dependent), State → County (dependent, from `county`). Text inputs for the other filters.
- Results grid: server-side paging and sorting, page size 50, total count, "Download CSV" button carrying the same query string.
- `/provider/{npi}`: detail page (all taxonomies, all practice locations, other names).
- `/export.csv`: streams via `MySqlDataReader` + CsvHelper straight to `Response.Body` (no buffering, no cap). UTF-8 with BOM for Excel. File name `npi_search_<yyyyMMdd>.csv`. Long command timeout.
- Footer: "Data as of <data_version>", source attribution to CMS NPPES, and a note that the data is public NPPES information.
- Simple, accessible, mobile-friendly. No heavy JS grid framework unless needed.

#### 7.3 Summary columns (grid & CSV)
NPI, Entity Type, Name (Last, First Middle Suffix *or* Organization), Credential, Primary Specialty (Classification – Specialization), Address 1, Address 2, City, State, ZIP, County, Phone, Gender, Enumeration Date, Last Update Date.

### Stage 5 — REST API (`/api/v1`, same app)
- `GET /api/v1/providers?classification=&specialization=&taxonomy=&state=&county=&city=&zip=&radius=&lastName=&firstName=&orgName=&npi=&entityType=&gender=&credential=&sort=&page=&pageSize=` → `{ items, page, pageSize, totalCount, dataAsOf }`
- `GET /api/v1/providers/{npi}` → full detail (404 if unknown or deactivated)
- `GET /api/v1/providers.csv?...`: same filters, streamed CSV
- `GET /api/v1/taxonomy/classifications`, `GET /api/v1/taxonomy/classifications/{c}/specializations`
- `GET /api/v1/states`, `GET /api/v1/states/{st}/counties`
- `GET /api/v1/meta`: data versions
- RFC 7807 problem details on errors. OpenAPI/Swagger at `/swagger`. Per-IP rate limiting (fixed window, configurable). CORS open for GET. An optional `X-Api-Key` check behind config flag `Api:RequireKey` (default false).
- The API and pages use the **same** `Npi.Core` search service, so results are identical.
- Optional: a small `Npi.Client` (netstandard2.0) typed client, or NSwag-generated, so .NET apps can consume the API.

### Stage 6 — Publish to Azure & deploy
1. **Publisher** (`Npi.Loader publish`): connect to `RemoteMySql`. Apply migrations remotely. Sync the projection tables **by diff**: compare local `row_hash` with a local `publish_state(npi, hash)` table, then batch-upsert changed rows (multi-row `INSERT … ON DUPLICATE KEY UPDATE`, ~1000 rows per batch, in transactions) and delete removed NPIs. For child tables, replace all rows for each changed NPI. Reference tables are replaced in full only when their version changes. Update `data_version` last. This keeps the site up and keeps nightly transfers small. For the **first** full publish, bulk-load into `_staging` tables with `LOAD DATA LOCAL` (enable the `local_infile` server parameter on Azure) and swap with `RENAME TABLE`. Fall back to batched inserts if that's unavailable.
2. **Deploy the site:** GitHub Actions workflow builds, runs the tests and deploys `src/Npi.Web` to App Service on merge to the default branch. Use OIDC federated credentials or a publish profile stored as a GitHub secret. The production connection string lives in App Service Configuration (connection string `RemoteMySql`), never in the repo. Turn on HTTPS-only and Always On (B1 or higher). Health check endpoint: `/health`. Document everything in `deploy/azure.md`.
3. **Cost guard:** start at App Service B1 + MySQL B1ms with 20–32 GB storage. Measure the projection size and query times, and scale up only if the §7 Stage 3 targets aren't met. Report monthly cost estimates to the owner before any scale-up.
4. **Scheduling:** `deploy/register-task.ps1` registers a Task Scheduler task that runs `Npi.Loader run && Npi.Loader publish` **daily at ~02:30**. The loader skips files already completed, so daily runs are safe and catch weeklies and monthlies promptly. Task settings: run whether the user is logged on or not, wake the computer, run as soon as possible after a missed start, stop after 12 h, don't start a new instance if one is running.

---

## 8. Commands

```powershell
dotnet build getnpidata.sln
dotnet test
dotnet run --project src/Npi.Loader -- discover
dotnet run --project src/Npi.Loader -- migrate
dotnet run --project src/Npi.Loader -- run
dotnet run --project src/Npi.Web          # https://localhost:5001
dotnet user-secrets --project src/Npi.Loader set "ConnectionStrings:LocalMySql" "server=localhost;database=npi_test;user=…;password=…;AllowLoadLocalInfile=true"
dotnet user-secrets --project src/Npi.Loader list          # shows the local MySQL logins (secrets!)
$env:NPI_TEST_MYSQL = "server=localhost;user=npi_dev;password=…"   # enables the MySQL integration tests
```

Loader configuration: `src/Npi.Loader/appsettings.json` holds the defaults. Overrides come from `appsettings.Local.json` (git-ignored), user-secrets (loaded in every environment, because Task Scheduler runs the loader as the owner's account), or environment variables with the prefix `NPI_` (e.g. `NPI_ConnectionStrings__LocalMySql`). Exit codes: 0 = all completed, 1 = something failed, 2 = bad command line. The loader's DB user needs all privileges on its database plus `SESSION_VARIABLES_ADMIN` (§6.1); the integration tests also create and drop `npi_test_it_*` databases.

Local MySQL on a new loader PC: `winget install Oracle.MySQL --version 8.4.9`, then run `deploy/setup-local-mysql.ps1` from an elevated PowerShell and set a root password right away.
The legacy VB project was deleted in Stage 1 (2026-10-08) after the parity check. To look at it, check out a commit before `stage-1-loader` was merged (it lived in `legacy/getnpidata-vb/`).

---

## 9. Conventions & guardrails

- C# 14 (the net10.0 default), nullable enabled, warnings as errors in `src/`. Async all the way down. `CancellationToken` everywhere in the loader and web.
- **All SQL parameterized.** Table/column identifiers come only from whitelists or the validated header mapping, backtick-quoted.
- Never run destructive SQL (TRUNCATE/DROP/RENAME, mass DELETE) against anything but `npi_test` without the owner's explicit OK. Production (`workplace`, Azure) changes go through migrations and the loader/publisher only.
- Tests: every bug class in §3.2 gets a regression test using `tests/fixtures`. Integration tests read the connection string from env var `NPI_TEST_MYSQL` and are skipped when it's not set.
- Do not download the 1.1 GB monthly file in tests. Use fixtures, or a weekly file (~7 MB) for manual end-to-end runs.
- Keep this CLAUDE.md current: if a decision changes or a fact turns out wrong, fix it here in the same commit.

## 10. Git workflow

- Branch per stage: `stage-0-cleanup`, `stage-1-loader`, `stage-2-reference`, `stage-3-search`, `stage-4-web`, `stage-5-api`, `stage-6-deploy`. Smaller topic branches are fine.
- Commit early and often with clear messages. Push the branch. Open a PR to the default branch with a summary and a test evidence section. Merge when green and the stage is complete (the owner has authorized Claude Code to commit and create branches).
- Never commit secrets, data files, zips, logs or build output. If a secret is ever found in history, tell the owner to rotate it.

## 11. Open items (ask the owner / verify)

1. ~~**Test DB backup**~~ **Done 2026-10-07:** restored as `npi_test` on this PC; schema in `001_baseline.sql`; row counts in §12.
2. **Azure setup (blocks Stage 6):** does the owner have an Azure subscription? Which region? Who creates the resources: the owner in the portal, or Claude Code via `az` with approval? Custom domain name for the site? Measure the projection size locally after Stage 3 and report it with a cost estimate.
3. **HUD API token:** issued. Must be present as the `HudApiToken` secret on the loader machine (never in the repo).
4. ~~Local MySQL~~ **Done 2026-10-07:** this PC is the loader PC. **MySQL 8.4.9 LTS**, Windows service `MySQL84`, installed by `deploy/setup-local-mysql.ps1`. It binds to 127.0.0.1 only, with `local_infile=ON`, binary logging off, an 8 GB buffer pool and `my.ini` in `C:\ProgramData\MySQL\MySQL Server 8.4\`. The owner's old server was 8.0.37 (end of life April 2026). Logins are kept in Npi.Loader user-secrets: `ConnectionStrings:LocalMySql` = `npi_dev` (all privileges on `npi_test` only) and `LocalMySqlAdmin` = root. The owner should copy the root password into a password manager.
5. Whether the API should require keys at launch (default: no, rate-limited).

## 12. Progress log

| Date | Stage | Notes |
|---|---|---|
| 2026-10-07 | — | CLAUDE.md created from analysis of the VB loader + owner Q&A. No code changed yet. |
| 2026-10-07 | — | Hosting changed from GoDaddy to Azure App Service + Azure Database for MySQL; target .NET 10 LTS. |
| 2026-10-07 | 0 | Branch `stage-0-cleanup`. `origin` → amazedbot. Owner's uncommitted VB work committed first (passwords → placeholders). Nested copy (sent to Recycle Bin), `npidata.cs`, `.pfx`, copilot instructions removed. VB → `legacy/getnpidata-vb/`. Installed .NET SDK 10.0.401 + gh via winget. Skeleton `src/` + `tests/` (xunit.v3 on MTP), CI workflow, `deploy/azure.md`. `dotnet build` 0 warnings; `dotnet test` 29/29 pass; `/health` returns Healthy. **Pending:** 0.5 (test DB backup → `001_baseline.sql`, row counts). |
| 2026-10-07 | 0.5 | Owner's backup `workplace.zip` (23 per-table mysqldumps from MySQL 8.0.37, 11.8 GB of SQL, dump-completed trailers present) restored into `npi_test` on local **MySQL 8.4.9** in about 35 min (`npidata` alone 1,973 s). 8.1 GB on disk, `npidata.ibd` 7.8 GB. `npidata` needed `innodb_strict_mode=OFF` (row size > 8126 on 8.4; see `db/migrations/README.md`). `001_baseline.sql` was verified: applying it twice to an empty DB gives 22 tables whose `SHOW CREATE TABLE` matches the restored DB exactly. **Row counts** (`COUNT(*)`, equal to the tuple counts streamed from the zip): npidata **9,726,865**; practice_locations 1,244,942; other_names 856,161; practice_locations_temp 11,935; extractlog 2,752; other_names_temp 2,587; taxonomy_codes 879; country_codes 236; downlog 93; state_codes 60; animals 8; other_provider_name_type_codes 5; gender_codes 4; sole_proprietor_codes 3; subpart_codes 3; entity_types 2; other_provider_identifier_issuer_codes 2. Empty: nppes_deactivated_npi_report, statelookup, us_city_populations, taxonomy_codes_old, dicomhosts. **Stage 0 complete.** |
| 2026-10-08 | 1 | Branch `stage-1-loader`. C# loader built: `migrate`, `discover`, `run`, `load-file` (`reference`/`project`/`publish` exit 1 "not implemented" until Stages 2–6). Migrations 002–005 applied to `npi_test`; 002 rebuilt the 9.7M-row `npidata` in 451 s. **Live end-to-end `run` against CMS, 22 min, 6/6 files Completed, exit 0:** September 2026 monthly (1.16 GB download in 16 s; npidata 9,798,758 rows loaded in 814 s, other_names 862,485, practice_locations 1,259,509; ≥ 95% check, atomic swap, deactivation flags; 17 min 20 s total), weeklies 090726–100426 (11–14 s each, ~30–43k NPIs each), deactivation report 091426 (355,330 NPIs, 3 min 29 s). No MySQL warnings on any load. After the run: npidata 9,839,369 rows, newest Last_Update_Date 2026-10-04, 357,270 flagged deactivated; other_names 864,191; practice_locations 1,270,671. V2 widths matter: 360 LBNs > 70 chars and 232 first names > 20 chars (V1 would truncate them). **Parity** against the VB-loaded data (deterministic sample of 1,928 NPIs saved as `npi_test.parity_before` before the load): none missing; for the 1,823 whose Last_Update_Date was unchanged, last/first name, LBN, practice address/city/state/ZIP, taxonomy 1, enumeration date and gender match exactly. Only 3 NPIs differ, each with one *more* other-name/practice-location row in the C# load (the VB loader's child-table branches were half-disabled). 98 report NPIs are correctly not flagged: all were reactivated in later weeklies (newest 10/02/2026). Tests: 107 unit + 6 MySQL integration, all passing locally (integration skipped in CI). `legacy/` deleted after parity. **Stage 1 complete.** |
