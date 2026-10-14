-- 054_provider_dates.sql
--
-- "New providers" and "recently updated" filters (CLAUDE.md §7 Stage 5.5 item 11): the last few weeks of
-- enumerations or updates are a small share of all providers, so they can drive a search from these indexes.
-- Online DDL; the site keeps reading. provider_staging is created LIKE provider, so the projection keeps them.

ALTER TABLE `provider`
  ADD INDEX `ix_provider_enumeration` (`enumeration_date`),
  ADD INDEX `ix_provider_last_update` (`last_update_date`);
