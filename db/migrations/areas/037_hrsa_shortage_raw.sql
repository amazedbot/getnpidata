-- 037_hrsa_shortage_raw.sql
--
-- Template for the raw HRSA Health Professional Shortage Area (HPSA) detail files, one row per HPSA
-- component (census tract, county subdivision or whole county) (CLAUDE.md §7 Stage 5.5 item 7a). Holds no
-- data: each load fills `hrsa_shortage_raw_staging`, summarizes it per county into county_shortage, and
-- drops it.
--   discipline  PC primary care, DH dental, MH mental health

CREATE TABLE IF NOT EXISTS `hrsa_shortage_raw` (
  `discipline` VARCHAR(2) NOT NULL,
  `hpsa_id` VARCHAR(20) NULL,
  `designation_type` VARCHAR(100) NULL,
  `status` VARCHAR(50) NULL,
  `score` INT NULL,
  `component_type` VARCHAR(50) NULL,
  `county_fips` VARCHAR(5) NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
