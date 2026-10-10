-- 074_product_prescribing_cache.sql
--
-- The prescribing overlap precomputed (CLAUDE.md §7 Stage 5.5 item 19, part 4): joining every paid provider of a drug with
-- its Part D prescribers when a page is read took 1–11 s, so the loader rebuilds these after either half reloads
-- (`open_payments`, `part_d_prescribers`):
--   `product_part_d_brand`: each drug product's Part D brands (its name or FDA brand name, or one of them plus more words);
--   `product_prescriber`: the active providers paid in payments naming the product who prescribed it, with both sides' numbers;
--   `product_prescribing`: per product, all its prescribers and claims and the paid providers' share.

CREATE TABLE IF NOT EXISTS `product_part_d_brand` (
  `slug` VARCHAR(200) NOT NULL,
  `brand` VARCHAR(255) NOT NULL,
  PRIMARY KEY (`slug`, `brand`),
  KEY `ix_product_part_d_brand_brand` (`brand`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `product_prescriber` (
  `slug` VARCHAR(200) NOT NULL,
  `npi` CHAR(10) NOT NULL,
  `amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  `claims` INT NOT NULL,
  `drug_cost` DOUBLE NULL,
  `beneficiaries` INT NULL,
  PRIMARY KEY (`slug`, `npi`),
  KEY `ix_product_prescriber_amount` (`slug`, `amount`),
  KEY `ix_product_prescriber_claims` (`slug`, `claims`),
  KEY `ix_product_prescriber_cost` (`slug`, `drug_cost`),
  KEY `ix_product_prescriber_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `product_prescribing` (
  `slug` VARCHAR(200) NOT NULL,
  `data_year` SMALLINT NOT NULL,
  `payment_year` SMALLINT NOT NULL,
  `brands` VARCHAR(2000) NOT NULL,
  `prescribers` INT NOT NULL,
  `claims` BIGINT NOT NULL,
  `drug_cost` DOUBLE NULL,
  `paid_providers` INT NOT NULL,
  `paid_prescribers` INT NOT NULL,
  `paid_claims` BIGINT NOT NULL,
  `paid_drug_cost` DOUBLE NULL,
  PRIMARY KEY (`slug`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
