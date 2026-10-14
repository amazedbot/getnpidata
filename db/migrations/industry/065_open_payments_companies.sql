-- 065_open_payments_companies.sql
--
-- Company pages (CLAUDE.md §7 Stage 5.5 item 17): every company (applicable manufacturer or GPO) that reports to Open
-- Payments, with its payments per program year, what they were for and which products they named in the newest
-- program year, the specialties it pays and the providers it paid the most over all years. The company's CMS ID
-- (AMGPO ID) is the key and is added to the per-NPI top payers and top companies so the provider page can link.
-- `*_raw` tables are the empty templates for the loads' staging tables.

CREATE TABLE IF NOT EXISTS `op_company_profile_raw` (
  `company_id` VARCHAR(20) NULL,
  `name` VARCHAR(255) NULL,
  `state` VARCHAR(40) NULL,
  `country` VARCHAR(100) NULL,
  `alt_name1` VARCHAR(255) NULL,
  `alt_name2` VARCHAR(255) NULL,
  `alt_name3` VARCHAR(255) NULL,
  `alt_name4` VARCHAR(255) NULL,
  `alt_name5` VARCHAR(255) NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `op_company_year_raw` (
  `company_id` VARCHAR(20) NULL,
  `name` VARCHAR(255) NULL,
  `state` VARCHAR(40) NULL,
  `country` VARCHAR(100) NULL,
  `general_amount` DOUBLE NULL,
  `research_amount` DOUBLE NULL,
  `invested_amount` DOUBLE NULL,
  `interest_value` DOUBLE NULL,
  `general_records` INT NULL,
  `research_records` INT NULL,
  `ownership_records` INT NULL,
  `program_year` VARCHAR(10) NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- One row per company: names, headquarters and totals over every published program year.
CREATE TABLE IF NOT EXISTS `op_company` (
  `company_id` VARCHAR(20) NOT NULL,
  `name` VARCHAR(255) NOT NULL,
  `other_names` VARCHAR(1300) NULL,
  `state` VARCHAR(40) NULL,
  `country` VARCHAR(100) NULL,
  `general_amount` DOUBLE NOT NULL,
  `research_amount` DOUBLE NOT NULL,
  `invested_amount` DOUBLE NOT NULL,
  `interest_value` DOUBLE NOT NULL,
  `first_year` SMALLINT NULL,
  `last_year` SMALLINT NULL,
  PRIMARY KEY (`company_id`),
  KEY `ix_op_company_name` (`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `op_company_year` (
  `company_id` VARCHAR(20) NOT NULL,
  `program_year` SMALLINT NOT NULL,
  `general_amount` DOUBLE NOT NULL,
  `research_amount` DOUBLE NOT NULL,
  `invested_amount` DOUBLE NOT NULL,
  `interest_value` DOUBLE NOT NULL,
  `general_records` INT NOT NULL,
  `research_records` INT NOT NULL,
  `ownership_records` INT NOT NULL,
  PRIMARY KEY (`company_id`, `program_year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- General payments of the newest program year by nature of payment, and the products they named (the first one).
CREATE TABLE IF NOT EXISTS `op_company_nature` (
  `company_id` VARCHAR(20) NOT NULL,
  `program_year` SMALLINT NOT NULL,
  `nature` VARCHAR(200) NOT NULL,
  `amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  PRIMARY KEY (`company_id`, `nature`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `op_company_product` (
  `company_id` VARCHAR(20) NOT NULL,
  `product_rank` SMALLINT NOT NULL,
  `program_year` SMALLINT NOT NULL,
  `product` VARCHAR(500) NOT NULL,
  `kind` VARCHAR(40) NULL,
  `category` VARCHAR(500) NULL,
  `amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  PRIMARY KEY (`company_id`, `product_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Over every published program year: how many providers (NPIs) the company paid, the specialties it paid the most
-- and the providers it paid the most, by payment type.
CREATE TABLE IF NOT EXISTS `op_company_reach` (
  `company_id` VARCHAR(20) NOT NULL,
  `providers` INT NOT NULL,
  `amount` DOUBLE NOT NULL,
  PRIMARY KEY (`company_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `op_company_specialty` (
  `company_id` VARCHAR(20) NOT NULL,
  `specialty_rank` TINYINT NOT NULL,
  `specialty` VARCHAR(255) NOT NULL,
  `providers` INT NOT NULL,
  `amount` DOUBLE NOT NULL,
  PRIMARY KEY (`company_id`, `specialty_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `op_company_recipient` (
  `company_id` VARCHAR(20) NOT NULL,
  `recipient_rank` SMALLINT NOT NULL,
  `npi` CHAR(10) NOT NULL,
  `total_amount` DOUBLE NOT NULL,
  `general_amount` DOUBLE NOT NULL,
  `research_amount` DOUBLE NOT NULL,
  `associated_research_amount` DOUBLE NOT NULL,
  `ownership_amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  PRIMARY KEY (`company_id`, `recipient_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The company ID in the raw templates and the per-NPI tables (instant: a nullable column at the end).
ALTER TABLE `open_payments_raw`
  ADD COLUMN `company_id` VARCHAR(20) NULL,
  ADD COLUMN `product` VARCHAR(500) NULL,
  ADD COLUMN `product_kind` VARCHAR(40) NULL,
  ADD COLUMN `product_category` VARCHAR(500) NULL;
ALTER TABLE `open_payments_company_raw` ADD COLUMN `company_id` VARCHAR(20) NULL;
ALTER TABLE `open_payments_payer` ADD COLUMN `company_id` VARCHAR(20) NULL, ALGORITHM=INSTANT;
ALTER TABLE `open_payments_company` ADD COLUMN `company_id` VARCHAR(20) NULL, ALGORITHM=INSTANT;
