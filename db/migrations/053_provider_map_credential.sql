-- 053_provider_map_credential.sql
--
-- Standardized credential × map grid cell → NPI (CLAUDE.md §7 Stage 5.5 items 10 + 12), like provider_map_specialty:
-- a map search for a credential reads only the cells the map shows. cell = FLOOR((lat + 90) * 10) * 3600 +
-- FLOOR((lon + 180) * 10). Rebuilt with the credential tables and with the map tables.

CREATE TABLE IF NOT EXISTS `provider_map_credential` (
  `credential` VARCHAR(60) NOT NULL,
  `cell` INT NOT NULL,
  `npi` CHAR(10) NOT NULL,
  PRIMARY KEY (`credential`, `cell`, `npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
