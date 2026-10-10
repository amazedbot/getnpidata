-- 059_open_payments_company.sql
--
-- The five companies that paid a recipient NPI the most over all published program years (CLAUDE.md §7 Stage 5.5
-- item 13), split by payment type, from CMS's "Payments grouped by covered recipient and reporting entities for all
-- years" summary. `open_payments_company_raw` is the empty template for the load's staging table.

CREATE TABLE IF NOT EXISTS `open_payments_company_raw` (
  `npi` CHAR(10) NULL,
  `payment_type` VARCHAR(40) NULL,
  `company` VARCHAR(255) NULL,
  `records` INT NULL,
  `amount` DOUBLE NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `open_payments_company` (
  `npi` CHAR(10) NOT NULL,
  `company_rank` TINYINT NOT NULL,
  `company` VARCHAR(255) NOT NULL,
  `total_amount` DOUBLE NOT NULL,
  `general_amount` DOUBLE NOT NULL,
  `research_amount` DOUBLE NOT NULL,
  `associated_research_amount` DOUBLE NOT NULL,
  `ownership_amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  PRIMARY KEY (`npi`, `company_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
