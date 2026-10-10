-- 073_product_prescribing.sql
--
-- Prescribing overlap on the product pages (CLAUDE.md §7 Stage 5.5 item 19, part 4): which providers paid in payments
-- naming a drug also prescribed it to Medicare patients. Two halves, joined when a page is read so either can reload:
--   `op_product_npi`: every (product, NPI) pair of the newest year's general payments, drugs and biologicals only (the
--   Open Payments load already computed them for the top lists; now kept);
--   `part_d_brand_prescriber`: CMS Medicare Part D Prescribers by Provider and Drug, the brand-name rows (a generic's
--   rows, whose brand name is its generic name, are left out), per brand and NPI; `part_d_brand` sums each brand.
-- `part_d_drug_raw` is the empty template of the load's staging table.

CREATE TABLE IF NOT EXISTS `op_product_npi` (
  `slug` VARCHAR(200) NOT NULL,
  `npi` CHAR(10) NOT NULL,
  `amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  PRIMARY KEY (`slug`, `npi`),
  KEY `ix_op_product_npi_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `part_d_drug_raw` (
  `npi` CHAR(10) NULL,
  `brand` VARCHAR(255) NULL,
  `generic` VARCHAR(255) NULL,
  `claims` INT NULL,
  `fills` DOUBLE NULL,
  `drug_cost` DOUBLE NULL,
  `beneficiaries` INT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `part_d_brand_prescriber` (
  `brand` VARCHAR(255) NOT NULL,
  `npi` CHAR(10) NOT NULL,
  `claims` INT NOT NULL,
  `fills` DOUBLE NULL,
  `drug_cost` DOUBLE NULL,
  `beneficiaries` INT NULL,
  PRIMARY KEY (`brand`, `npi`),
  KEY `ix_part_d_brand_prescriber_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `part_d_brand` (
  `brand` VARCHAR(255) NOT NULL,
  `generic` VARCHAR(255) NULL,
  `data_year` SMALLINT NOT NULL,
  `prescribers` INT NOT NULL,
  `claims` BIGINT NOT NULL,
  `drug_cost` DOUBLE NULL,
  `beneficiaries` BIGINT NULL,
  PRIMARY KEY (`brand`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
