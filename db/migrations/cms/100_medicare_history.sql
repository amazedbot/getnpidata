-- 100_medicare_history.sql
--
-- Older Medicare years, 2013 to the year before the newest (docs/PRODUCT_PLAN.md Phase 3 "Older Medicare years"; owner's OK
-- 2026-10-13): which NPIs billed Medicare Part B ("Medicare Physician & Other Practitioners - by Provider") or prescribed under
-- Part D ("Medicare Part D Prescribers - by Provider") in each data year, for "Last billed Medicare in [year]" and the
-- record-quality reason MedicareStopped. The newest year stays in medicare_utilization and medicare_part_d.
--   medicare_activity_year    one row per data year and NPI: parts = 1 Part B, 2 Part D (both = 3); the Part B patients and the
--                             Part D claims of that year (NULL when CMS suppressed them or the NPI isn't in that part)
--   medicare_history_version  the file each (part, data year) was loaded from: a year is loaded once and again only when CMS
--                             publishes another file for it

CREATE TABLE IF NOT EXISTS `medicare_activity_year` (
  `data_year` SMALLINT NOT NULL,
  `npi` CHAR(10) NOT NULL,
  `parts` TINYINT NOT NULL,
  `part_b_beneficiaries` INT NULL,
  `part_d_claims` INT NULL,
  PRIMARY KEY (`data_year`, `npi`),
  KEY `ix_medicare_activity_year_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `medicare_history_version` (
  `part` CHAR(1) NOT NULL,
  `data_year` SMALLINT NOT NULL,
  `version` VARCHAR(500) NOT NULL,
  `rows_loaded` BIGINT NOT NULL,
  `loaded_at` DATETIME NOT NULL,
  PRIMARY KEY (`part`, `data_year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
