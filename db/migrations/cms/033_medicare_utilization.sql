-- 033_medicare_utilization.sql
--
-- CMS Medicare Physician & Other Practitioners - by Provider: one row per NPI that billed Medicare
-- Part B in data_year (CLAUDE.md §7 Stage 5.5 item 5a). Counts under 11 are suppressed by CMS (NULL).
-- Amounts and averages are DOUBLE because CMS publishes up to 8 decimals.

CREATE TABLE IF NOT EXISTS `medicare_utilization` (
  `npi` CHAR(10) NOT NULL,
  `data_year` SMALLINT NOT NULL,
  `provider_type` VARCHAR(100) NULL,
  `participating` TINYINT NULL,
  `distinct_services` INT NULL,
  `beneficiaries` INT NULL,
  `services` DOUBLE NULL,
  `submitted_charges` DOUBLE NULL,
  `allowed_amount` DOUBLE NULL,
  `payment_amount` DOUBLE NULL,
  `avg_beneficiary_age` DOUBLE NULL,
  `avg_risk_score` DOUBLE NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
