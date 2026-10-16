# Migrations: Search projection and search features

The tables the site searches (`provider`, its taxonomies, locations, names and the `provider_search` accelerator), standardized credentials, the change log, similar-name search and the deactivated-NPI status list.

Applied in number order together with the other folders (see [../README.md](../README.md)).

| File | What |
|---|---|
| `011_provider.sql` … `014_provider_other_name.sql` | Search projection: provider, provider_taxonomy, provider_location, provider_other_name. |
| `015_data_version.sql` | What the projection was built from ("Data as of"). |
| `016_provider_search.sql` | (taxonomy_code, state, city, zip5, npi) accelerator for specialty + location searches. |
| `018_provider_profile.sql` … `020_provider_endpoint.sql` | Projection: registration details (mailing address, authorized official, parent org), other identifiers, endpoints. |
| `032_provider_location_covering.sql` | Replaces `provider_location (npi)` with a covering (npi, state, zip5, city) index. |
| `045_provider_location_addr_key.sql` | VIRTUAL street-address key on `provider_location` (item 10, map search); metadata-only change. |
| `051_credentials.sql` | Standardized credentials (item 12): raw → standard map, provider credentials, the dropdown list. |
| `052_credential_search.sql` | Credential × practice location, for fast credential + location searches. |
| `054_provider_dates.sql` | Indexes on `provider` enumeration and last-update dates, for the new / recently updated filters (item 11). |
| `055_provider_change.sql` | `provider_change`: the change log each projection appends to (item 11). |
| `056_provider_phonetic.sql` | Soundex keys of last/first names as indexed virtual columns on `provider`, for similar-name search (item 9). |
| `057_provider_org_name.sql` | `provider_org_name`: organization legal + other names with a FULLTEXT index, for word-anywhere search (item 9); filled once from the current projection. |
| `077_provider_deactivated.sql` | Every deactivated NPI with its date, for the "Deactivated on [date]" status page (redesign step 1.4). |
| `096_provider_change_kinds.sql` | `provider_change.change_type` widened to 20 characters for the kinds step 3.2 adds (phone, locations, taxonomies, other names, licenses, exclusions, opt-outs). |
| `098_search_total.sql` | Nationwide totals for a single broad filter (practice building type), so a nationwide list counts instantly. |
| `101_directories.sql` | The Facilities & hospitals and Groups & organizations lists (step 3.6), built by the loader (`DirectoryBuilder`): every certified facility, every Medicare group with its size and MIPS score, organization NPI counts by taxonomy, state and record quality, and organizations' own Medicare Part B payments. |
| `108_directory_facility_ownership.sql` | The facility list's "Changed ownership recently" and "Has penalties" filters (step 3.6 part 2): each facility's newest change of ownership and its nursing home penalties, filled by `DirectoryBuilder`. |
| `117_org_group_member.sql` | Combined organization profiles (step 3.8): the active organization NPIs grouped into organizations by the loader (`OrgGroupBuilder`), each with its group's main NPI, its CCN and the ties that joined it. |
