-- 046_address_geocode.sql
--
-- Census Geocoder results per street address (CLAUDE.md §7 Stage 5.5 item 10). A cache, not a projection:
-- rows are only added, so each address is sent once; new addresses from the weekly files are geocoded by
-- `run`, the backlog by `geocode`. status: Match (lat/lon set), No_Match or Tie (lat/lon NULL; the map
-- falls back to the ZIP centroid).

CREATE TABLE IF NOT EXISTS `address_geocode` (
  `addr_key` CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `status` VARCHAR(10) NOT NULL,
  `match_type` VARCHAR(10) NULL,
  `matched_address` VARCHAR(255) NULL,
  `lat` DECIMAL(9,6) NULL,
  `lon` DECIMAL(10,6) NULL,
  `benchmark` VARCHAR(40) NOT NULL,
  `geocoded_at` DATETIME NOT NULL,
  PRIMARY KEY (`addr_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
