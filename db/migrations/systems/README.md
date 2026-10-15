# Migrations: Health systems, ACOs and health center sites

Health systems (AHRQ Compendium), Medicare Shared Savings Program ACOs, HRSA health center sites and 340B covered entities, with the columns they
add to the Facilities & hospitals list (docs/PRODUCT_PLAN.md step 3.6 part 2).

Applied in number order together with the other folders (see [../README.md](../README.md)).

| File | What |
|---|---|
| `112_health_system.sql` | AHRQ Compendium of U.S. Health Systems: the systems and the facilities (by CCN) in each. |
| `113_aco.sql` | Medicare Shared Savings Program ACOs, their participants and SNF affiliates (by name, as CMS publishes them). |
| `114_hrsa_health_center_site.sql` | HRSA health center (FQHC and look-alike) service delivery sites, with their Medicare number and NPI. |
| `115_directory_facility_networks.sql` | The facility list's health system, health center, 340B and source columns; HRSA-only sites in the list. |
| `116_hrsa_340b.sql` | HRSA 340B covered entities (OPAIS export, from the operator's downloaded file) and their NPIs. |
