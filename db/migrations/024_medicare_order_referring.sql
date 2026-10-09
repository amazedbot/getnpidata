-- 024_medicare_order_referring.sql
--
-- CMS Order and Referring: providers eligible to order or refer in Medicare, per program (CLAUDE.md
-- §7 Stage 5.5 item 2c). Replaced in full by each (weekly) release. A handful of NPIs are listed twice.
--   part_b, dme, hha (home health), pmd (power mobility devices), hospice: 1 = eligible

CREATE TABLE IF NOT EXISTS `medicare_order_referring` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `npi` CHAR(10) NOT NULL,
  `part_b` TINYINT NULL,
  `dme` TINYINT NULL,
  `hha` TINYINT NULL,
  `pmd` TINYINT NULL,
  `hospice` TINYINT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_medicare_order_referring_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
