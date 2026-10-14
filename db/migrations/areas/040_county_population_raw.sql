-- 040_county_population_raw.sql
--
-- Template for the raw Census co-est<year>-alldata.csv rows (nation, state and county summary levels;
-- CLAUDE.md §7 Stage 5.5 item 7b). Holds no data: each load fills `county_population_raw_staging`, keeps
-- the county rows (sumlev 050) in county_population, and drops it.

CREATE TABLE IF NOT EXISTS `county_population_raw` (
  `sumlev` VARCHAR(3) NOT NULL,
  `state_fips` VARCHAR(2) NOT NULL,
  `county_code` VARCHAR(3) NOT NULL,
  `population` INT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
