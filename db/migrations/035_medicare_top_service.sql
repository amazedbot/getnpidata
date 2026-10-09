-- 035_medicare_top_service.sql
--
-- Each NPI's five most frequent Medicare Part B services in data_year (by number of services), from
-- the by Provider and Service file (CLAUDE.md §7 Stage 5.5 item 5a).
--   place_of_service  F = facility, O = office/non-facility

CREATE TABLE IF NOT EXISTS `medicare_top_service` (
  `npi` CHAR(10) NOT NULL,
  `service_rank` TINYINT NOT NULL,
  `data_year` SMALLINT NOT NULL,
  `hcpcs` VARCHAR(10) NOT NULL,
  `description` VARCHAR(300) NULL,
  `is_drug` TINYINT NULL,
  `place_of_service` VARCHAR(1) NULL,
  `beneficiaries` INT NULL,
  `services` DOUBLE NULL,
  `avg_payment` DOUBLE NULL,
  PRIMARY KEY (`npi`, `service_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
