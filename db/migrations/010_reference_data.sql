-- 010_reference_data.sql
--
-- Which version of each reference source is loaded (CLAUDE.md §7 Stage 2). `run` compares the
-- published version with this table and reloads only when it changed (or, for HUD, when the last
-- check is older than the refresh interval).
--   source: nucc | hud_zip_county | census_gazetteer
--   version: NUCC release (e.g. 261), HUD year-quarter (e.g. 2026Q2), Gazetteer year (e.g. 2026)

CREATE TABLE IF NOT EXISTS `reference_data` (
  `source` VARCHAR(30) NOT NULL,
  `version` VARCHAR(40) NOT NULL,
  `source_url` VARCHAR(500) NOT NULL,
  `rows_loaded` INT NOT NULL,
  `loaded_at` DATETIME NOT NULL,
  `checked_at` DATETIME NOT NULL,
  PRIMARY KEY (`source`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
