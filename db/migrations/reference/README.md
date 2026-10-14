# Migrations: Reference data

NUCC taxonomy codes, the HUD ZIP–county crosswalk, Census counties and ZIP centroids, and the version of every loaded source (`reference_data`).

Applied in number order together with the other folders (see [../README.md](../README.md)).

| File | What |
|---|---|
| `006_taxonomy_codes_utf8mb4.sql` | `taxonomy_codes` → utf8mb4, Specialization widened to 150, `Nucc_Version`, index (Classification, Specialization). |
| `007_zip_county.sql` | HUD ZIP → county crosswalk table. |
| `008_county.sql` | County names (Gazetteer, 2020 codes fallback). |
| `009_zip_centroid.sql` | ZCTA centroids for radius search. |
| `010_reference_data.sql` | Loaded version of each reference source. |
| `021_reference_data_widen.sql` | `reference_data.version` 200, `source_url` 1000 (dataset file names and URLs are long). |
