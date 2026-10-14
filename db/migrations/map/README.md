# Migrations: Map, geocoding and buildings

Census geocoder results, Overture Maps building points and listed businesses, the map tables, and the FEMA building type of each practice address.

Applied in number order together with the other folders (see [../README.md](../README.md)).

| File | What |
|---|---|
| `046_address_geocode.sql` | Census geocoder results per street address (a cache; rows only added). |
| `047_provider_map.sql` | One map point per provider and street address, with a SPATIAL index. |
| `048_provider_map_specialty.sql` | Specialty × 0.1° grid cell → NPI, for specialty map searches. |
| `049_address_point.sql` | Building-level locations from Overture Maps (address points, place addresses, place names). |
| `050_provider_map_source.sql` | `provider_map.source`: how each point was placed (instant ADD COLUMN with a default). |
| `053_provider_map_credential.sql` | Credential × map grid cell → NPI, for credential map searches. |
| `075_address_building.sql` | Practice address type (item 18): the FEMA USA Structures building under each practice address, and the label per address (commercial, residential, care, unknown). |
| `076_building_search.sql` | Practice building × location (item 18), so a building type in a state or county is one index range. |
| `087_address_place.sql` | Businesses Overture Maps lists at each practice address, with operating status (Phase 3 step 3.1). |
| `093_address_building_point.sql` | `address_building.lat`/`lon`: the point each address was matched from, so only new or moved addresses are looked up in the FEMA files. |
| `094_overture_checked.sql` | `overture_checked`: every address an Overture pass looked at and its release, so `run` places only new addresses between releases. |
