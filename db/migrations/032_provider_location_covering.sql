-- 032_provider_location_covering.sql
--
-- Per-candidate location checks ("does this NPI have a location in NY?") look up provider_location by
-- npi. With only (npi) indexed, each lookup also reads the row to get state/zip5/city; a covering
-- (npi, state, zip5, city) index answers it from the index alone (CLAUDE.md §7 Stage 5.5: Care Compare
-- and other per-NPI filters combined with a location). Online DDL; the site keeps reading.

ALTER TABLE `provider_location`
  DROP INDEX `ix_provider_location_npi`,
  ADD INDEX `ix_provider_location_npi` (`npi`, `state`, `zip5`, `city`);
