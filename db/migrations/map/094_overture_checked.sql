-- 094_overture_checked.sql
--
-- Overture for new practice addresses only (docs/PRODUCT_PLAN.md §7 speed item 5). `run` places the addresses no Overture
-- pass has looked at yet, in one pass over the Overture files read in place, and does a full pass (every state) only when
-- Overture publishes a new release.
--   overture_checked                    every practice address an Overture pass looked at, and the release it used
--                                       (matched or not: address_point keeps only the matches).
--   reference_data 'overture_full'      version = the release of the last full pass.
-- The full pass of 2026-10-12 (release 2026-09-23.1, every state) looked at every address geocoded before it.

CREATE TABLE IF NOT EXISTS `overture_checked` (
  `addr_key` CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `release` VARCHAR(20) NOT NULL,
  `checked_at` DATETIME NOT NULL,
  PRIMARY KEY (`addr_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO `overture_checked` (`addr_key`, `release`, `checked_at`)
SELECT g.`addr_key`, SUBSTRING_INDEX(r.`version`, ' ', 1), r.`loaded_at`
FROM `address_geocode` g JOIN `reference_data` r ON r.`source` = 'overture'
WHERE g.`geocoded_at` <= r.`loaded_at`;

INSERT INTO `reference_data` (`source`, `version`, `source_url`, `rows_loaded`, `loaded_at`, `checked_at`)
SELECT 'overture_full', SUBSTRING_INDEX(`version`, ' ', 1), `source_url`, `rows_loaded`, `loaded_at`, `loaded_at`
FROM `reference_data` WHERE `source` = 'overture';
