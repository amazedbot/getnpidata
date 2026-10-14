-- 023_medicare_opt_out.sql
--
-- CMS Opt Out Affidavits: practitioners who opted out of Medicare (CLAUDE.md §7 Stage 5.5 item 2b).
-- Replaced in full by each release. An opt-out is active while end_date is today or later. A few NPIs
-- appear twice (successive affidavits), hence the surrogate id.

CREATE TABLE IF NOT EXISTS `medicare_opt_out` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `npi` CHAR(10) NOT NULL,
  `specialty` VARCHAR(100) NULL,
  `effective_date` DATE NULL,
  `end_date` DATE NULL,
  `can_order_refer` TINYINT NULL,
  `last_updated` DATE NULL,
  PRIMARY KEY (`id`),
  KEY `ix_medicare_opt_out_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
