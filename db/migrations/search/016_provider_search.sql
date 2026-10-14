-- 016_provider_search.sql
--
-- Search accelerator for the main use case, "specialty + where" (CLAUDE.md §7 Stage 3.5): one row
-- per (taxonomy code, practice location area, NPI), i.e. provider_taxonomy × provider_location with
-- only the searched columns. "Chiropractor + NY" becomes one index range read instead of looking up
-- the locations of every chiropractor in the country. Built by `project` with the other tables.
-- Empty strings instead of NULL because the columns are part of the primary key.

CREATE TABLE IF NOT EXISTS `provider_search` (
  `taxonomy_code` VARCHAR(10) NOT NULL,
  `state` VARCHAR(40) NOT NULL DEFAULT '',
  `city` VARCHAR(60) NOT NULL DEFAULT '',
  `zip5` CHAR(5) NOT NULL DEFAULT '',
  `npi` CHAR(10) NOT NULL,
  PRIMARY KEY (`taxonomy_code`, `state`, `city`, `zip5`, `npi`),
  KEY `ix_provider_search_zip` (`taxonomy_code`, `zip5`, `npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
