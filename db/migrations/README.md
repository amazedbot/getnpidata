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

The 2021 dump in [`../reference/workplace_20210830.sql`](../reference/workplace_20210830.sql) is
kept only as history.
