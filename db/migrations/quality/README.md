# Migrations: Record quality

The current / check / stale label per provider and its search table, and the Medicaid data that counts as a sign of practice.

Applied in number order together with the other folders (see [../README.md](../README.md)).

| File | What |
|---|---|
| `083_provider_quality.sql` | Record quality per provider: current / check / stale, with the reasons and signs of practice (Phase 3 step 3.1). |
| `084_quality_search.sql` | Record quality × practice location, so a label in a state or county is one index range (Phase 3 step 3.1). |
| `097_state_medicaid.sql` | NY and CA Medicaid enrolled providers, a sign of current practice (Phase 3). |
| `118_provider_quality_other_npi.sql` | The current NPI behind the "Another NPI of this organization is current" reason (step 3.8), for the link. |
| `119_medicaid_tmsis.sql` | Medicaid in every state from T-MSIS (HHS open data): enrollment periods per NPI and state, billing per NPI and year, the top procedures (step 3.7). |
| `120_provider_quality_activity_int.sql` | `provider_quality.activity` widened to INT for the Medicaid billing sign, bit 32768 (step 3.7). |
