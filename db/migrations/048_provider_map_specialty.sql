-- 048_provider_map_specialty.sql
--
-- Specialty × map grid cell → NPI (CLAUDE.md §7 Stage 5.5 item 10). A map search for a common specialty
-- (dentists: ~200k NPIs) can't start from every provider of that specialty nationwide, and a spatial index
-- can't be combined with a taxonomy column. So each provider_map point is also filed under its taxonomy
-- codes and a 0.1° grid cell (about 7 × 5 miles): cell = FLOOR((lat + 90) * 10) * 3600 + FLOOR((lon + 180) * 10).
-- The search reads only the cells the map shows. Rebuilt together with provider_map.

CREATE TABLE IF NOT EXISTS `provider_map_specialty` (
  `taxonomy_code` VARCHAR(10) NOT NULL,
  `cell` INT NOT NULL,
  `npi` CHAR(10) NOT NULL,
  PRIMARY KEY (`taxonomy_code`, `cell`, `npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
