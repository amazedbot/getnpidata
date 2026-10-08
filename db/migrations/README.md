# Database migrations

Numbered, idempotent `.sql` files (`001_baseline.sql`, `002_…`), applied in order by
`Npi.Loader migrate` against `LocalMySql` and, during `publish`, against `RemoteMySql`.

Rules:

- Every file must be safe to run twice (`CREATE TABLE IF NOT EXISTS`, guarded `ALTER`s).
- Never edit a migration that has been applied anywhere; add a new one.
- utf8mb4 and InnoDB everywhere.

## Status

`001_baseline.sql` is **pending**: it will be produced with `mysqldump --no-data` from the
owner's test DB backup once it arrives (CLAUDE.md §7 Stage 0.5, §11 item 1).

Until then the only schema reference is the 2021 dump in
[`../reference/workplace_20210830.sql`](../reference/workplace_20210830.sql). It has V1 field
lengths and lacks `other_names`, `practice_locations`, their `_temp` tables, and `extractlog`.
