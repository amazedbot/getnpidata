-- 058_open_payments_year.sql
--
-- Open Payments by program year (CLAUDE.md §7 Stage 5.5 item 13): per recipient NPI and year, general payments,
-- research payments, research funding as a principal investigator ("associated research") and ownership or
-- investment interests, from CMS's "Payments grouped by physician (distinct) for all years" summary (2019 on).
-- `open_payments_year_raw` is the empty template for the load's staging table.

CREATE TABLE IF NOT EXISTS `open_payments_year_raw` (
  `npi` CHAR(10) NULL,
  `program_year` VARCHAR(10) NULL,
  `general_amount` DOUBLE NULL,
  `general_records` INT NULL,
  `research_amount` DOUBLE NULL,
  `research_records` INT NULL,
  `associated_research_amount` DOUBLE NULL,
  `associated_research_records` INT NULL,
  `invested_amount` DOUBLE NULL,
  `interest_value` DOUBLE NULL,
  `ownership_records` INT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `open_payments_year` (
  `npi` CHAR(10) NOT NULL,
  `program_year` SMALLINT NOT NULL,
  `general_amount` DOUBLE NOT NULL,
  `general_records` INT NOT NULL,
  `research_amount` DOUBLE NOT NULL,
  `research_records` INT NOT NULL,
  `associated_research_amount` DOUBLE NOT NULL,
  `associated_research_records` INT NOT NULL,
  `invested_amount` DOUBLE NOT NULL,
  `interest_value` DOUBLE NOT NULL,
  `ownership_records` INT NOT NULL,
  PRIMARY KEY (`npi`, `program_year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
