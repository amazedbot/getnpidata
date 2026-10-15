# Migrations: Area facts

HRSA shortage areas, Census county population, and the provider counts per area and specialty for the area pages.

Applied in number order together with the other folders (see [../README.md](../README.md)).

| File | What |
|---|---|
| `037_hrsa_shortage_raw.sql` … `040_county_population_raw.sql` | Area insights: HPSA raw template, county shortage areas, county population + raw template (item 7). |
| `079_area_counts.sql`, `080_area_new_providers.sql` | Provider counts per area and specialty for the area pages (redesign step 1.6). |
| `102_area_history.sql` | Areas over time: monthly figures per area and specialty, the providers behind the change counts, where each provider practised at the last build, and the series' first months (docs/PRODUCT_PLAN.md step 3.3). |
