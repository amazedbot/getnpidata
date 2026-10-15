-- 099_address_place_sources.sql
--
-- Which sources back each Overture listing at a practice address (owner, 2026-10-15). Overture merges one source's record per
-- place (Meta, Microsoft/Bing, Foursquare, BrightQuery, AllThePlaces …) and adds its own rows ("Overture", "Overture-signals")
-- dated the release, so `updated` (the newest date of any of them) said every listing was confirmed days ago. A closed
-- chiropractor in Setauket stayed "open" on BrightQuery's business-registration record alone. `sources` lists the real
-- sources with their own dates, "dataset yyyy-mm-dd" joined by ";" (Overture's rows left out), and `updated` is now the newest
-- of those; the record-quality label counts an open listing only when such a source confirms it. NULL until the next
-- `Npi.Loader overture` pass rewrites the area's listings, and NULL counts as unconfirmed.

ALTER TABLE `address_place`
  ADD COLUMN `sources` VARCHAR(255) NULL AFTER `operating_status`,
  ALGORITHM=INSTANT;
