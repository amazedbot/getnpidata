# Migrations: Incremental loading queues

The queues that let a load rebuild only the providers it changed (docs/PRODUCT_PLAN.md §7 speed work); other speed-work migrations sit with the tables they change.

Applied in number order together with the other folders (see [../README.md](../README.md)).

| File | What |
|---|---|
| `089_projection_pending.sql` | The queue of NPIs whose NPPES data changed, so the projection rebuilds only them. |
| `091_builder_pending.sql` | Queues and old-row snapshots so the builders after an incremental projection update only its NPIs. |
| `092_builder_old_location_label.sql` | `builder_old_location.label_input` (suite or floor, organization), so address type labels are redone only where an input changed. |
