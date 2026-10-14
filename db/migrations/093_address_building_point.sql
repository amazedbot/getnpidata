-- 093_address_building_point.sql
--
-- FEMA buildings for new practice addresses only (docs/PRODUCT_PLAN.md §7 speed item 4). address_building keeps the map
-- point each address was matched from, so `run` looks up only the addresses that are new or whose point moved (an Overture
-- or Census placement), reading the FEMA files in place, instead of downloading all ~21 GB again for every 50,000 new
-- addresses. The rows of the October 2026 load take the current points: no placement has moved since that load.
--   reference_data 'address_building'  loaded_at = the last full match, checked_at = the last match of new addresses.
-- The FEMA version no longer counts geocoded addresses ("; 3,300,000+ addresses"): new addresses don't need a reload.

ALTER TABLE `address_building`
  ADD COLUMN `lat` DOUBLE NULL,
  ADD COLUMN `lon` DOUBLE NULL,
  ALGORITHM=INSTANT;

UPDATE `address_building` b
JOIN (SELECT `addr_key`, MIN(`lat`) AS `lat`, MIN(`lon`) AS `lon` FROM `provider_map` WHERE `source` <> 'zip' GROUP BY `addr_key`) m
  ON m.`addr_key` = b.`addr_key`
SET b.`lat` = m.`lat`, b.`lon` = m.`lon`;

UPDATE `reference_data` SET `version` = SUBSTRING_INDEX(`version`, ';', 1) WHERE `source` = 'fema_structures';

INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
SELECT 'address_building', `version`, `source_url`, (SELECT COUNT(*) FROM `address_building`), `loaded_at`, `loaded_at`
FROM `reference_data` WHERE `source` = 'fema_structures';
