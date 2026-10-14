-- 007_zip_county.sql
--
-- HUD USPS ZIP → county crosswalk (CLAUDE.md §2 "County search", §6.2). One row per (ZIP, county)
-- pair; a ZIP matches every county it overlaps. Ratios are the share of the ZIP's residential /
-- business / other / total addresses in that county. Replaced in full each quarter.

CREATE TABLE IF NOT EXISTS `zip_county` (
  `zip5` CHAR(5) NOT NULL,
  `county_fips` CHAR(5) NOT NULL,
  `res_ratio` DECIMAL(12,10) NOT NULL,
  `bus_ratio` DECIMAL(12,10) NOT NULL,
  `oth_ratio` DECIMAL(12,10) NOT NULL,
  `tot_ratio` DECIMAL(12,10) NOT NULL,
  `usps_city` VARCHAR(64) NULL,
  `usps_state` CHAR(2) NULL,
  `year` SMALLINT NOT NULL,
  `quarter` TINYINT NOT NULL,
  PRIMARY KEY (`zip5`, `county_fips`),
  KEY `ix_zip_county_county` (`county_fips`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
