-- 012_provider_taxonomy.sql
--
-- The 15 taxonomy slots of each provider, unpivoted (blank slots skipped). Specialty search matches
-- any slot (CLAUDE.md §2) via the (taxonomy_code, npi) index.

CREATE TABLE IF NOT EXISTS `provider_taxonomy` (
  `npi` CHAR(10) NOT NULL,
  `slot` TINYINT NOT NULL,
  `taxonomy_code` VARCHAR(10) NOT NULL,
  `is_primary` TINYINT NOT NULL,
  `license_no` VARCHAR(50) NULL,
  `license_state` VARCHAR(2) NULL,
  PRIMARY KEY (`npi`, `slot`),
  KEY `ix_provider_taxonomy_code` (`taxonomy_code`, `npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
