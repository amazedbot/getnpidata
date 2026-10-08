-- 008_county.sql
--
-- County names for the State → County dropdown (CLAUDE.md §6.2, §6.3). Primary source: the Census
-- Gazetteer counties file (current boundaries, e.g. Connecticut's planning regions since 2022).
-- Codes the Gazetteer lacks (Guam, Northern Mariana Islands, US Virgin Islands, American Samoa)
-- come from the Census 2020 county codes file; `source` says which.

CREATE TABLE IF NOT EXISTS `county` (
  `county_fips` CHAR(5) NOT NULL,
  `state` CHAR(2) NOT NULL,
  `county_name` VARCHAR(100) NOT NULL,
  `source` VARCHAR(40) NOT NULL,
  PRIMARY KEY (`county_fips`),
  KEY `ix_county_state_name` (`state`, `county_name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
