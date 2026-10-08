# Database migrations

Numbered, idempotent `.sql` files (`001_baseline.sql`, `002_…`), applied in order by
`Npi.Loader migrate` against `LocalMySql`. The Azure database gets only the projection and
reference tables (CLAUDE.md §6); which migrations run remotely is settled in Stage 6.

Rules:

- Every file must be safe to run twice (`CREATE TABLE IF NOT EXISTS`, guarded `ALTER`s).
- Never edit a migration that has been applied anywhere; add a new one.
- New tables: utf8mb4 and InnoDB.

## 001_baseline.sql

A faithful copy of the owner's `workplace` schema (MySQL 8.0.37) taken on 2026-10-07 from the
per-table mysqldump backup `workplace.zip`. Only three things changed: `CREATE TABLE` became
`CREATE TABLE IF NOT EXISTS`, the `AUTO_INCREMENT=` counters were dropped, and the file starts
with `SET SESSION innodb_strict_mode = OFF`. Without that, MySQL 8.4 rejects `npidata`:
330 V1 varchar columns exceed InnoDB's worst-case row size (ERROR 1118). Running it therefore
needs `SESSION_VARIABLES_ADMIN` (root locally). Verified on MySQL 8.4.9: two runs on an empty
database create the same 22 tables as the restored backup. Everything else is exactly as found
and is fixed by later migrations:

- `npidata` has the **V1 field widths** (e.g. `Provider_First_Name varchar(20)`,
  `Provider_Organization_Name_Legal_Business_Name varchar(70)`) and no `Is_Deactivated` / `Loaded_From`.
- `npidata`, `downlog`, `taxonomy_codes`, `nppes_deactivated_npi_report`, `statelookup`,
  `us_city_populations`, `dicomhosts` and `taxonomy_codes_old` are **latin1**; the rest are utf8mb4.
- `downlog` has only (id, filename, received_date); the status/kind columns of §6.1 are not there yet.
- `other_names` / `practice_locations` have no unique key, so duplicate rows are possible (defect #5).
- Unrelated or obsolete tables are included because they exist: `animals`, `dicomhosts`,
  `taxonomy_codes_old`. Dropping them needs the owner's OK.

The 2021 dump in [`../reference/workplace_20210830.sql`](../reference/workplace_20210830.sql) is
kept only as history.
