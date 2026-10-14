-- 038_county_shortage.sql
--
-- Per county and discipline: HRSA shortage areas still in force (status Designated or Proposed For
-- Withdrawal) that lie in the county (CLAUDE.md §7 Stage 5.5 item 7a).
--   whole_county  1 when a component covers the entire county (component type "Single County")
--   hpsa_count    distinct HPSAs with a component in the county; max_score = highest HPSA score (0-26)

CREATE TABLE IF NOT EXISTS `county_shortage` (
  `county_fips` VARCHAR(5) NOT NULL,
  `discipline` VARCHAR(2) NOT NULL,
  `whole_county` TINYINT NOT NULL,
  `hpsa_count` INT NOT NULL,
  `max_score` INT NULL,
  PRIMARY KEY (`county_fips`, `discipline`),
  KEY `ix_county_shortage_discipline` (`discipline`, `county_fips`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
