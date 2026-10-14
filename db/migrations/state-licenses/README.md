# Migrations: State licenses

State license records and board actions matched to NPIs, loaded per state.

Applied in number order together with the other folders (see [../README.md](../README.md)).

| File | What |
|---|---|
| `063_state_license.sql` | State license records and board actions (NY, TX, WA, IL, CO open data) matched to NPIs, item 15. |
| `064_state_license_raw_keys.sql` | A last-digits license key, full names and an id on the state license template (more states, item 15). |
| `088_state_license_by_state.sql` | Per-state license versions, the queue of NPIs to relabel, an index for replacing one state's licenses. |
