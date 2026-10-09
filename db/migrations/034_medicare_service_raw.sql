-- 034_medicare_service_raw.sql
--
-- Template for the raw Medicare Physician & Other Practitioners - by Provider and Service file (~10M
-- rows; CLAUDE.md §7 Stage 5.5 item 5a). Holds no data: each load fills `medicare_service_raw_staging`,
-- keeps each NPI's top services in medicare_top_service, and drops it.

CREATE TABLE IF NOT EXISTS `medicare_service_raw` (
  `npi` CHAR(10) NOT NULL,
  `hcpcs` VARCHAR(10) NOT NULL,
  `description` VARCHAR(300) NULL,
  `is_drug` TINYINT NULL,
  `place_of_service` VARCHAR(1) NULL,
  `beneficiaries` INT NULL,
  `services` DOUBLE NULL,
  `avg_payment` DOUBLE NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
