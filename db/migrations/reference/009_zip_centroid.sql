-- 009_zip_centroid.sql
--
-- ZIP centroids for radius search (CLAUDE.md §6.2, §6.3), from the Census Gazetteer ZCTA file
-- (internal point). ZCTA ≈ ZIP; ZIPs without a ZCTA (PO boxes, single buildings) have no row and
-- can't be used for radius search.

CREATE TABLE IF NOT EXISTS `zip_centroid` (
  `zip5` CHAR(5) NOT NULL,
  `lat` DECIMAL(9,6) NOT NULL,
  `lon` DECIMAL(9,6) NOT NULL,
  PRIMARY KEY (`zip5`),
  KEY `ix_zip_centroid_lat_lon` (`lat`, `lon`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
