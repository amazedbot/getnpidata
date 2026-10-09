-- 039_county_population.sql
--
-- Census Bureau county population estimates, newest vintage (CLAUDE.md §7 Stage 5.5 item 7b), for
-- "providers per 10,000 residents". county_fips = state FIPS + county FIPS (Connecticut planning regions
-- since the 2022 vintage, matching the HUD crosswalk and `county`).

CREATE TABLE IF NOT EXISTS `county_population` (
  `county_fips` VARCHAR(5) NOT NULL,
  `state_fips` VARCHAR(2) NOT NULL,
  `county_code` VARCHAR(3) NOT NULL,
  `population` INT NOT NULL,
  `year` SMALLINT NOT NULL,
  PRIMARY KEY (`county_fips`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
