-- 049_address_point.sql
--
-- Building-level locations of practice addresses from Overture Maps (CLAUDE.md §7 Stage 5.5 item 10), preferred over
-- the Census geocoder's street interpolation (address_geocode). source: address (an address point from the US DOT
-- National Address Database), place (the address of an Overture place, e.g. a hospital's mailing address) or
-- place_name (an address that is only a building name, matched to a place in the same ZIP). matched: the address
-- point or place name it matched, for checking. Written by `Npi.Loader overture`.

CREATE TABLE IF NOT EXISTS `address_point` (
  `addr_key` CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `source` VARCHAR(12) NOT NULL,
  `lat` DECIMAL(9,6) NOT NULL,
  `lon` DECIMAL(10,6) NOT NULL,
  `matched` VARCHAR(255) NULL,
  `release` VARCHAR(40) NOT NULL,
  `matched_at` DATETIME NOT NULL,
  PRIMARY KEY (`addr_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
