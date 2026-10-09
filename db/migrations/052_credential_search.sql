-- 052_credential_search.sql
--
-- Standardized credential × practice location (CLAUDE.md §7 Stage 5.5 item 12), like provider_search for specialties:
-- "MDs in NY" is one index range here, instead of checking each of the ~1M New York providers for MD. Rebuilt with the
-- other credential tables.

CREATE TABLE IF NOT EXISTS `credential_search` (
  `credential` VARCHAR(60) NOT NULL,
  `state` VARCHAR(40) NOT NULL DEFAULT '',
  `city` VARCHAR(60) NOT NULL DEFAULT '',
  `zip5` CHAR(5) NOT NULL DEFAULT '',
  `npi` CHAR(10) NOT NULL,
  PRIMARY KEY (`credential`, `state`, `city`, `zip5`, `npi`),
  KEY `ix_credential_search_zip` (`credential`, `zip5`, `npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
