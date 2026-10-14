-- 076_building_search.sql
--
-- Practice building × practice location (CLAUDE.md §7 Stage 5.5 item 18), like provider_search for specialties:
-- "residential practice addresses in NY" is one index range here, instead of checking the building of each of New York's
-- ~1M locations (16 s). Rebuilt with address_type.

CREATE TABLE IF NOT EXISTS `building_search` (
  `building_type` VARCHAR(12) NOT NULL,
  `state` VARCHAR(40) NOT NULL DEFAULT '',
  `city` VARCHAR(60) NOT NULL DEFAULT '',
  `zip5` CHAR(5) NOT NULL DEFAULT '',
  `npi` CHAR(10) NOT NULL,
  PRIMARY KEY (`building_type`, `state`, `city`, `zip5`, `npi`),
  KEY `ix_building_search_zip` (`building_type`, `zip5`, `npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
