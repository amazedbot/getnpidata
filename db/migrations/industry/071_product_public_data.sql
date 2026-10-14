-- 071_product_public_data.sql
--
-- Public data about the products (CLAUDE.md §7 Stage 5.5 item 19, part 3):
--   Medicare Part D, Part B and Medicaid spending by drug (CMS, per brand name and year), NADAC pharmacy acquisition
--   prices (Medicaid, per NDC), FDA drug shortages, and two caches filled from public APIs a slice at a time:
--   adverse event report counts (openFDA FAERS / MAUDE) and ClinicalTrials.gov study counts.
-- FDA recalls naming a product are found in `fda_enforcement` by a FULLTEXT search of the product description.
-- `*_raw` tables are the empty templates of the loads' staging tables.

ALTER TABLE `fda_enforcement` ADD FULLTEXT KEY `ft_fda_enforcement_product` (`product_description`);

-- One CMS spending file, wide: one column group per year (CMS shifts the years each release), so five slots.
CREATE TABLE IF NOT EXISTS `drug_spending_raw` (
  `hcpcs` VARCHAR(20) NULL,
  `brand_name` VARCHAR(255) NULL,
  `generic_name` VARCHAR(500) NULL,
  `manufacturer` VARCHAR(255) NULL,
  `spending1` DOUBLE NULL, `units1` DOUBLE NULL, `claims1` DOUBLE NULL, `benes1` DOUBLE NULL,
  `spending2` DOUBLE NULL, `units2` DOUBLE NULL, `claims2` DOUBLE NULL, `benes2` DOUBLE NULL,
  `spending3` DOUBLE NULL, `units3` DOUBLE NULL, `claims3` DOUBLE NULL, `benes3` DOUBLE NULL,
  `spending4` DOUBLE NULL, `units4` DOUBLE NULL, `claims4` DOUBLE NULL, `benes4` DOUBLE NULL,
  `spending5` DOUBLE NULL, `units5` DOUBLE NULL, `claims5` DOUBLE NULL, `benes5` DOUBLE NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Per program ("Part D", "Part B", "Medicaid"), brand, generic name and year; Part D and Medicaid from the "Overall"
-- (all manufacturers) rows, Part B summed over the brand's HCPCS codes. Beneficiaries are not in the Medicaid file.
CREATE TABLE IF NOT EXISTS `drug_spending` (
  `program` VARCHAR(10) NOT NULL,
  `brand_name` VARCHAR(255) NOT NULL,
  `generic_name` VARCHAR(255) NOT NULL,
  `year` SMALLINT NOT NULL,
  `spending` DOUBLE NOT NULL,
  `units` DOUBLE NULL,
  `claims` DOUBLE NULL,
  `beneficiaries` DOUBLE NULL,
  PRIMARY KEY (`program`, `brand_name`, `generic_name`, `year`),
  KEY `ix_drug_spending_brand` (`brand_name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `nadac_raw` (
  `description` VARCHAR(255) NULL,
  `ndc` VARCHAR(20) NULL,
  `per_unit` DOUBLE NULL,
  `effective_date` DATE NULL,
  `pricing_unit` VARCHAR(10) NULL,
  `otc` VARCHAR(2) NULL,
  `classification` VARCHAR(2) NULL,
  `as_of` DATE NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The newest NADAC per package NDC (11 digits); `ndc_key` is its labeler + product (9 digits), like op_product.ndc_key.
CREATE TABLE IF NOT EXISTS `nadac` (
  `ndc` CHAR(11) NOT NULL,
  `ndc_key` CHAR(9) GENERATED ALWAYS AS (LEFT(`ndc`, 9)) STORED,
  `description` VARCHAR(255) NULL,
  `per_unit` DOUBLE NOT NULL,
  `pricing_unit` VARCHAR(10) NULL,
  `effective_date` DATE NULL,
  `classification` VARCHAR(2) NULL,
  `otc` VARCHAR(2) NULL,
  `as_of` DATE NULL,
  PRIMARY KEY (`ndc`),
  KEY `ix_nadac_key` (`ndc_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `drug_shortage` (
  `id` INT NOT NULL,
  `generic_name` VARCHAR(500) NULL,
  `substance` VARCHAR(500) NULL,
  `company` VARCHAR(255) NULL,
  `presentation` VARCHAR(1000) NULL,
  `status` VARCHAR(40) NULL,
  `availability` VARCHAR(100) NULL,
  `reason` VARCHAR(500) NULL,
  `related_info` VARCHAR(2000) NULL,
  `initial_date` DATE NULL,
  `update_date` DATE NULL,
  PRIMARY KEY (`id`),
  KEY `ix_drug_shortage_substance` (`substance`(100))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `drug_shortage_ndc` (
  `ndc_key` CHAR(9) NOT NULL,
  `shortage_id` INT NOT NULL,
  PRIMARY KEY (`ndc_key`, `shortage_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Caches (rows are upserted, oldest refreshed first; never swapped): per product slug, what was asked and the answer.
CREATE TABLE IF NOT EXISTS `product_adverse_events` (
  `slug` VARCHAR(200) NOT NULL,
  `kind` VARCHAR(10) NOT NULL,
  `query_name` VARCHAR(500) NOT NULL,
  `reports` INT NULL,
  `serious` INT NULL,
  `deaths` INT NULL,
  `injuries` INT NULL,
  `malfunctions` INT NULL,
  `fetched_at` DATETIME NOT NULL,
  PRIMARY KEY (`slug`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `product_trials` (
  `slug` VARCHAR(200) NOT NULL,
  `query_name` VARCHAR(500) NOT NULL,
  `studies` INT NULL,
  `recruiting` INT NULL,
  `fetched_at` DATETIME NOT NULL,
  PRIMARY KEY (`slug`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
