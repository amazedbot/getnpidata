# Migrations: NPPES raw data and load bookkeeping

The raw NPPES tables (`npidata`, other names, practice locations, endpoints, the deactivation report) and the tables that record what was loaded. `001_baseline.sql` is the owner's original schema (see the top-level README).

Applied in number order together with the other folders (see [../README.md](../README.md)).

| File | What |
|---|---|
| `001_baseline.sql` | The owner's 2026-10-07 schema, verbatim (see below). Idempotent. |
| `002_npidata_v2.sql` | `npidata` → utf8mb4, all non-key columns TEXT, old secondary indexes dropped, `Is_Deactivated` + `Loaded_From` added. Runs with `innodb_strict_mode=OFF` (the worst-case row-size check fails even for all-TEXT; real rows fit, see the file). Rebuilds the table (several minutes). |
| `003_downlog_status.sql` | `downlog` bookkeeping: kind, file_date, status (existing rows → `Legacy`), timings, rows, error; `filename` UNIQUE. |
| `004_extractlog_rows.sql` | `extractlog.rows_loaded`. |
| `005_deactivated_report_utf8mb4.sql` | `nppes_deactivated_npi_report` → utf8mb4. |
| `017_endpoints.sql` | Raw NPPES `endpoint_pfile` (Stage 5.5 item 1). |
| `090_npidata_source_hash.sql` | `npidata.Source_Hash`: a hash of the projected columns written while a file loads, so a monthly finds changed NPIs fast. |
| `095_deactivation_pending.sql` | The NPIs whose deactivation flag may change, queued by a monthly or report load before its swap, so only they are recomputed (speed item 10). |
