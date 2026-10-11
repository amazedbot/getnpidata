-- 075_address_building.sql
--
-- Practice address type (CLAUDE.md §7 Stage 5.5 item 18): is a practice street address a commercial building, a home,
-- or a care facility?
--   `address_building`: the FEMA USA Structures building (Oak Ridge National Laboratory) under each mapped practice
--     address (or the nearest one within about 30 m); a row with matched = 'none' when no building was found, so the
--     address isn't looked up again until the next load. occ_cls / prim_occ: FEMA's occupancy class and type
--     ("Residential" / "Single Family Dwelling", "Commercial" / "Medical Office/Clinic", …).
--   `address_type`: the label per practice address: commercial, residential, care (nursing home, dormitory) or unknown,
--     with the basis shown on the provider page. Rebuilt from address_building and our own signals (a suite in the
--     address, an Overture place at it, several providers or an organization there) after each FEMA load and each
--     projection.

CREATE TABLE IF NOT EXISTS `address_building` (
  `addr_key` CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `matched` VARCHAR(8) NOT NULL,
  `distance_m` SMALLINT NULL,
  `occ_cls` VARCHAR(20) NULL,
  `prim_occ` VARCHAR(40) NULL,
  `sqft` INT NULL,
  PRIMARY KEY (`addr_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `address_type` (
  `addr_key` CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `building_type` VARCHAR(12) NOT NULL,
  `basis` VARCHAR(255) NOT NULL,
  PRIMARY KEY (`addr_key`),
  KEY `ix_address_type_type` (`building_type`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
