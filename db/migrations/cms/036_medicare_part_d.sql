-- 036_medicare_part_d.sql
--
-- CMS Medicare Part D Prescribers - by Provider: one row per prescriber NPI in data_year (CLAUDE.md §7
-- Stage 5.5 item 5b). Suppressed values are NULL.
--   opioid_rate  percent of the prescriber's claims that are for opioids

CREATE TABLE IF NOT EXISTS `medicare_part_d` (
  `npi` CHAR(10) NOT NULL,
  `data_year` SMALLINT NOT NULL,
  `prescriber_type` VARCHAR(100) NULL,
  `claims` INT NULL,
  `fills_30day` DOUBLE NULL,
  `drug_cost` DOUBLE NULL,
  `day_supply` BIGINT NULL,
  `beneficiaries` INT NULL,
  `brand_claims` INT NULL,
  `generic_claims` INT NULL,
  `opioid_claims` INT NULL,
  `opioid_rate` DOUBLE NULL,
  `antibiotic_claims` INT NULL,
  `avg_beneficiary_age` DOUBLE NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
