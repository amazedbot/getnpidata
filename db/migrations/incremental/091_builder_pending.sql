-- 091_builder_pending.sql
--
-- Incremental builders after the projection (owner, 2026-10-13: "optimize updates and runs"). An incremental projection
-- (migration 089) rebuilds a batch of NPIs; the tables built on the projection then update only those NPIs instead of
-- everyone:
--   builder_pending       the NPIs each builder still has to update (builder = credentials, address_types, areas, map);
--                         record quality keeps using quality_pending (088).
--   builder_old_location  the batch's practice locations as they were before the projection replaced them (earliest
--                         state while an NPI is queued; a row with is_primary = -1 marks an NPI as snapshotted, also when
--                         it had no location), so the builders find the rows they wrote for the old locations by
--                         their exact keys (credential_search, building_search, quality_search, the area counts).
--   builder_old_taxonomy  likewise the old taxonomy codes (provider_map_specialty, the area counts).
--   address_pending       practice addresses (old and new) of the batch, for the address type labels; with the ZIP, because
--                         provider_location has no index on its (virtual) addr_key but one on zip5, which is part of the key.
--   reference_data 'projection'  loaded_at = the last FULL projection build: newer than a builder's own build means
--                         that builder rebuilds in full; data_version.projected_at keeps changing with every build.

CREATE TABLE IF NOT EXISTS `builder_pending` (
  `builder` VARCHAR(16) NOT NULL,
  `npi` CHAR(10) NOT NULL,
  PRIMARY KEY (`builder`, `npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `builder_old_location` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `npi` CHAR(10) NOT NULL,
  `addr_key` CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `state` VARCHAR(40) NOT NULL DEFAULT '',
  `city` VARCHAR(60) NOT NULL DEFAULT '',
  `zip5` CHAR(5) NOT NULL DEFAULT '',
  `is_primary` TINYINT NOT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_builder_old_location_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `builder_old_taxonomy` (
  `npi` CHAR(10) NOT NULL,
  `taxonomy_code` VARCHAR(10) NOT NULL,
  PRIMARY KEY (`npi`, `taxonomy_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `address_pending` (
  `addr_key` CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `zip5` CHAR(5) NOT NULL,
  PRIMARY KEY (`addr_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
SELECT * FROM (SELECT 'projection' AS `source`, 'full build' AS `version`, '' AS `source_url`, `provider_count` AS `rows_loaded`,
  `projected_at` AS `loaded_at`, `projected_at` AS `checked_at` FROM `data_version` WHERE `id` = 1) AS new
ON DUPLICATE KEY UPDATE `loaded_at` = new.`loaded_at`;
