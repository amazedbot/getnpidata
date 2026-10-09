# Database migrations

Numbered `.sql` files (`001_baseline.sql`, `002_…`), embedded in `Npi.Loader` and applied in
order by `Npi.Loader migrate` against `LocalMySql`. Applied versions are recorded in
`schema_migrations` (version, name, SHA-256, applied_at); each file runs once. The Azure database
gets only the projection and reference tables (CLAUDE.md §6); which migrations run remotely is
settled in Stage 6.

Rules:

- **One DDL statement per file** where possible. MySQL DDL is not transactional, but a single
  `ALTER TABLE` is atomic in MySQL 8, so a failed migration leaves nothing half-applied and can
  simply be re-run.
- Never edit a migration that has been applied anywhere (the runner refuses when the checksum
  changed); add a new one.
- New tables: utf8mb4 and InnoDB.

| File | What |
|---|---|
| `001_baseline.sql` | The owner's 2026-10-07 schema, verbatim (see below). Idempotent. |
| `002_npidata_v2.sql` | `npidata` → utf8mb4, all non-key columns TEXT, old secondary indexes dropped, `Is_Deactivated` + `Loaded_From` added. Runs with `innodb_strict_mode=OFF` (the worst-case row-size check fails even for all-TEXT; real rows fit, see the file). Rebuilds the table (several minutes). |
| `003_downlog_status.sql` | `downlog` bookkeeping: kind, file_date, status (existing rows → `Legacy`), timings, rows, error; `filename` UNIQUE. |
| `004_extractlog_rows.sql` | `extractlog.rows_loaded`. |
| `005_deactivated_report_utf8mb4.sql` | `nppes_deactivated_npi_report` → utf8mb4. |
| `006_taxonomy_codes_utf8mb4.sql` | `taxonomy_codes` → utf8mb4, Specialization widened to 150, `Nucc_Version`, index (Classification, Specialization). |
| `007_zip_county.sql` | HUD ZIP → county crosswalk table. |
| `008_county.sql` | County names (Gazetteer, 2020 codes fallback). |
| `009_zip_centroid.sql` | ZCTA centroids for radius search. |
| `010_reference_data.sql` | Loaded version of each reference source. |
| `011_provider.sql` … `014_provider_other_name.sql` | Search projection: provider, provider_taxonomy, provider_location, provider_other_name. |
| `015_data_version.sql` | What the projection was built from ("Data as of"). |
| `016_provider_search.sql` | (taxonomy_code, state, city, zip5, npi) accelerator for specialty + location searches. |
| `017_endpoints.sql` | Raw NPPES `endpoint_pfile` (Stage 5.5 item 1). |
| `018_provider_profile.sql` … `020_provider_endpoint.sql` | Projection: registration details (mailing address, authorized official, parent org), other identifiers, endpoints. |
| `021_reference_data_widen.sql` | `reference_data.version` 200, `source_url` 1000 (dataset file names and URLs are long). |
| `022_oig_exclusion.sql` … `024_medicare_order_referring.sql` | Compliance: OIG LEIE, Medicare opt-out, order & referring (item 2). |
| `025_cc_dac_raw.sql` … `028_cc_facility_affiliation.sql` | Care Compare: raw DAC template, clinicians, group practices, facility affiliations (item 3). |
| `029_cms_hospital.sql` … `031_cms_facility_npi.sql` | Facilities: hospitals, nursing homes, CCN ↔ NPI from the enrollments (item 4). |
| `032_provider_location_covering.sql` | Replaces `provider_location (npi)` with a covering (npi, state, zip5, city) index. |
| `033_medicare_utilization.sql` … `036_medicare_part_d.sql` | Medicare Part B totals, raw by-service template, top 5 services, Part D (item 5). |
| `037_hrsa_shortage_raw.sql` … `040_county_population_raw.sql` | Area insights: HPSA raw template, county shortage areas, county population + raw template (item 7). |
| `041_open_payments_raw.sql` … `044_open_payments_payer.sql` | Open Payments: raw template, per-NPI summary, by nature of payment, top 3 payers (item 6). |
| `045_provider_location_addr_key.sql` | VIRTUAL street-address key on `provider_location` (item 10, map search); metadata-only change. |
| `046_address_geocode.sql` | Census geocoder results per street address (a cache; rows only added). |
| `047_provider_map.sql` | One map point per provider and street address, with a SPATIAL index. |
| `048_provider_map_specialty.sql` | Specialty × 0.1° grid cell → NPI, for specialty map searches. |
| `049_address_point.sql` | Building-level locations from Overture Maps (address points, place addresses, place names). |
| `050_provider_map_source.sql` | `provider_map.source`: how each point was placed (instant ADD COLUMN with a default). |
| `051_credentials.sql` | Standardized credentials (item 12): raw → standard map, provider credentials, the dropdown list. |
| `052_credential_search.sql` | Credential × practice location, for fast credential + location searches. |
| `053_provider_map_credential.sql` | Credential × map grid cell → NPI, for credential map searches. |

## 001_baseline.sql

A faithful copy of the owner's `workplace` schema (MySQL 8.0.37) taken on 2026-10-07 from the
per-table mysqldump backup `workplace.zip`. Only three things changed: `CREATE TABLE` became
`CREATE TABLE IF NOT EXISTS`, the `AUTO_INCREMENT=` counters were dropped, and the file starts
with `SET SESSION innodb_strict_mode = OFF`. Without that, MySQL 8.4 rejects `npidata`:
330 V1 varchar columns exceed InnoDB's worst-case row size (ERROR 1118). Running it therefore
needs `SESSION_VARIABLES_ADMIN`. Verified on MySQL 8.4.9: two runs on an empty database create
the same 22 tables as the restored backup. As found (later migrations fix some of this):

- `npidata` had the **V1 field widths** (e.g. `Provider_First_Name varchar(20)`,
  `Provider_Organization_Name_Legal_Business_Name varchar(70)`) → fixed by 002.
- `npidata`, `downlog`, `taxonomy_codes`, `nppes_deactivated_npi_report`, `statelookup`,
  `us_city_populations`, `dicomhosts` and `taxonomy_codes_old` are **latin1**; the rest are utf8mb4.
- `other_names` / `practice_locations` have no unique key. The C# loader replaces their rows
  (whole table monthly, per NPI weekly) instead of de-duplicating, so none is needed.
- Unrelated or obsolete tables are included because they exist: `animals`, `dicomhosts`,
  `taxonomy_codes_old`. Dropping them needs the owner's OK.

An older 2021 schema dump (V1 widths, missing several tables) was kept in `db/reference/` until
the 2026 repository cleanup; it is still in git history.
