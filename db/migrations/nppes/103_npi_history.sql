-- 103_npi_history.sql
--
-- NPPES history before October 2026 (docs/PRODUCT_PLAN.md Phase 3 step 3.4), from the archived monthly files: CMS's own
-- from June 2015 (still served at their usual URLs), NBER's copies before that (https://doi.org/10.60592/bjwk-8n62), and from
-- now on every monthly file the loader loads. Written by `Npi.Loader history` (HistoryBuilder), one file at a time, oldest first.
--   history_period: one row per month from November 2007: the file (source, URL, its data date, size, columns, rows) and its
--     status: Completed, Failed, or Missing (no file was published or kept for that month). stats_done = its area figures
--     are written; locations = the file listed secondary practice locations (pl_pfile, from September 2018). Completed months are skipped, so a stopped run goes on from the first month not done.
--   npi_history: one row per NPI per version of the fields that matter, from the file it first appeared in (from_period, the
--     file's data date) to the file it no longer held in (to_period; NULL = still current). status 0 = active, 1 = listed as
--     deactivated (the files list deactivated NPIs with their date from November 2012), 2 = no longer in the file (before
--     November 2012 a deactivated NPI simply dropped out: the deactivation is inferred). The location is the primary practice
--     location (state as written, ZIP for US addresses, country); taxonomy_set points to the NPI's distinct taxonomy codes and
--     location_set to its secondary practice locations (from the files that list them, September 2018 on; NULL = none).
--     npi is a number (INT UNSIGNED, 4 bytes instead of 10) to keep ~25M rows small.
--   npi_history_taxonomy_set: each distinct set of taxonomy codes (sorted, space-separated) and npi_history_set_category the
--     plain specialty categories its codes belong to (codes not in the list, e.g. retired ones, under Other).
--   npi_history_location_set: each distinct set of secondary practice locations, one row per (state, ZIP) in it: states of at
--     most 5 characters (the ones the area figures count), the ZIP '' when not a US one.
--   history_area_count: the active providers per area (US, state, county by today's HUD crosswalk) and category at the last
--     month whose figures were written, carried from month to month (each version counts +1 where it starts, -1 where it ends).
--   area_history_meta.archive_from / archive_to: the months of area_month_stat written from the archive (basis 3);
--     any_location_from: the first of them counted by every practice location (before it, the primary location only).

CREATE TABLE IF NOT EXISTS `history_period` (
  `period` DATE NOT NULL,
  `status` VARCHAR(10) NOT NULL,
  `source` VARCHAR(8) NULL,
  `file_name` VARCHAR(200) NULL,
  `source_url` VARCHAR(400) NULL,
  `file_date` DATE NULL,
  `bytes` BIGINT NULL,
  `columns_count` SMALLINT NULL,
  `rows_read` INT NULL,
  `deactivated_rows` INT NULL,
  `active` INT NULL,
  `versions` INT NULL,
  `new_npis` INT NULL,
  `gone` INT NULL,
  `zip_unmatched` INT NULL,
  `zip_unmatched_providers` INT NULL,
  `stats_done` TINYINT NOT NULL DEFAULT 0,
  `locations` TINYINT NOT NULL DEFAULT 0,
  `secondary_rows` INT NULL,
  `started_at` DATETIME NULL,
  `completed_at` DATETIME NULL,
  `seconds` INT NULL,
  `error` TEXT NULL,
  PRIMARY KEY (`period`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `npi_history` (
  `npi` INT UNSIGNED NOT NULL,
  `from_period` DATE NOT NULL,
  `to_period` DATE NULL,
  `status` TINYINT NOT NULL,
  `entity_type` TINYINT NULL,
  `state` VARCHAR(40) NULL,
  `zip5` CHAR(5) NULL,
  `country` CHAR(2) NULL,
  `primary_taxonomy` VARCHAR(10) NULL,
  `taxonomy_set` INT UNSIGNED NULL,
  `location_set` INT UNSIGNED NULL,
  `enumeration_date` DATE NULL,
  `last_update_date` DATE NULL,
  `deactivation_date` DATE NULL,
  `reactivation_date` DATE NULL,
  PRIMARY KEY (`npi`, `from_period`),
  KEY `ix_npi_history_from` (`from_period`),
  KEY `ix_npi_history_enumeration` (`enumeration_date`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `npi_history_taxonomy_set` (
  `id` INT UNSIGNED NOT NULL,
  `codes` VARCHAR(200) NOT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `npi_history_set_category` (
  `set_id` INT UNSIGNED NOT NULL,
  `category` VARCHAR(80) NOT NULL,
  PRIMARY KEY (`set_id`, `category`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `npi_history_location_set` (
  `set_id` INT UNSIGNED NOT NULL,
  `state` VARCHAR(5) NOT NULL,
  `zip5` CHAR(5) NOT NULL DEFAULT '',
  PRIMARY KEY (`set_id`, `state`, `zip5`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `history_area_count` (
  `area_type` VARCHAR(6) NOT NULL,
  `area_id` VARCHAR(5) NOT NULL,
  `category` VARCHAR(80) NOT NULL,
  `providers` INT NOT NULL,
  PRIMARY KEY (`area_type`, `area_id`, `category`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

ALTER TABLE `area_history_meta`
  ADD COLUMN `archive_from` DATE NULL,
  ADD COLUMN `archive_to` DATE NULL,
  ADD COLUMN `any_location_from` DATE NULL;
