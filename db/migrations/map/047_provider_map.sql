-- 047_provider_map.sql
--
-- One map point per provider and street address (CLAUDE.md §7 Stage 5.5 item 10), for "what's in this map
-- area" queries through the spatial index. The point is the geocoded address, or the ZIP centroid
-- (approximate = 1) when the address hasn't been geocoded or didn't match. x = longitude, y = latitude,
-- SRID 0 (planar), which is all a bounding-box test needs. Rebuilt by the loader after `project` and
-- after geocoding (staging + RENAME).

CREATE TABLE IF NOT EXISTS `provider_map` (
  `npi` CHAR(10) NOT NULL,
  `addr_key` CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `lat` DOUBLE NOT NULL,
  `lon` DOUBLE NOT NULL,
  `approximate` TINYINT NOT NULL,
  `pt` POINT NOT NULL SRID 0,
  PRIMARY KEY (`npi`, `addr_key`),
  SPATIAL KEY `sx_provider_map_pt` (`pt`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
