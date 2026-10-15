# Database migrations

Numbered `.sql` files (`001_baseline.sql`, `002_…`), grouped by topic in the folders below, embedded in `Npi.Loader` and
applied by `Npi.Loader migrate` in **number order across all folders**. Applied versions are recorded in
`schema_migrations` (version, name, SHA-256, applied_at); each file runs once. The loader reads only a file's number,
name and contents, so a file may move between folders; its name and contents never change once applied.

[`../schema.sql`](../schema.sql) shows every table as the migrations leave it, in one file, for reading. It is generated
from a freshly migrated database and checked by the MySQL tests (`SchemaFileTests`): after adding a migration, run them
with `NPI_UPDATE_SCHEMA=1` and commit the new file.

Rules:

- **One DDL statement per file** where possible. MySQL DDL is not transactional, but a single `ALTER TABLE` is atomic in
  MySQL 8, so a failed migration leaves nothing half-applied and can simply be re-run.
- Never edit a migration that has been applied anywhere (the runner refuses when the checksum changed); add a new one,
  with the next free number, in the folder of its topic.
- New tables: utf8mb4 and InnoDB.

| Folder | What |
|---|---|
| [`nppes/`](nppes/README.md) | NPPES raw data, load bookkeeping and the NPPES history: 9 files (001, 002, 003, 004 …) |
| [`reference/`](reference/README.md) | Reference data: 6 files (006, 007, 008, 009 …) |
| [`search/`](search/README.md) | Search projection and search features: 18 files (011, 012, 013, 014 …) |
| [`map/`](map/README.md) | Map, geocoding and buildings: 11 files (046, 047, 048, 049 …) |
| [`cms/`](cms/README.md) | CMS and OIG datasets: 27 files (022, 023, 024, 025 …) |
| [`areas/`](areas/README.md) | Area facts: 7 files (037, 038, 039, 040 …) |
| [`industry/`](industry/README.md) | Open Payments, companies and products: 16 files (041, 042, 043, 044 …) |
| [`state-licenses/`](state-licenses/README.md) | State licenses: 3 files (063, 064, 088) |
| [`quality/`](quality/README.md) | Record quality: 2 files (083, 084) |
| [`incremental/`](incremental/README.md) | Incremental loading queues: 3 files (089, 091, 092) |

## nppes/001_baseline.sql

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
