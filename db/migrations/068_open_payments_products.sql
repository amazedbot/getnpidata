-- 068_open_payments_products.sql
--
-- Product pages (CLAUDE.md §7 Stage 5.5 item 19, part 1): every drug, biological, device or medical supply named in the
-- newest program year's Open Payments, keyed by a slug of its name (lower-case letters and digits joined by "-", so
-- "ELIQUIS", "Eliquis" and " eliquis " are one product): who pays for it, for what, to whom, and the research that names
-- it. A payment names up to five products and counts fully for each. Per company and per provider, the top products link
-- to the pages. `*_raw` tables are the empty templates of the loads' staging tables.

-- The general payment detail file: the second to fifth product, and every product's NDC and device identifier.
ALTER TABLE `open_payments_raw`
  ADD COLUMN `ndc` VARCHAR(100) NULL,
  ADD COLUMN `device_id` VARCHAR(100) NULL,
  ADD COLUMN `product2` VARCHAR(500) NULL, ADD COLUMN `product_kind2` VARCHAR(40) NULL, ADD COLUMN `product_category2` VARCHAR(500) NULL,
  ADD COLUMN `ndc2` VARCHAR(100) NULL, ADD COLUMN `device_id2` VARCHAR(100) NULL,
  ADD COLUMN `product3` VARCHAR(500) NULL, ADD COLUMN `product_kind3` VARCHAR(40) NULL, ADD COLUMN `product_category3` VARCHAR(500) NULL,
  ADD COLUMN `ndc3` VARCHAR(100) NULL, ADD COLUMN `device_id3` VARCHAR(100) NULL,
  ADD COLUMN `product4` VARCHAR(500) NULL, ADD COLUMN `product_kind4` VARCHAR(40) NULL, ADD COLUMN `product_category4` VARCHAR(500) NULL,
  ADD COLUMN `ndc4` VARCHAR(100) NULL, ADD COLUMN `device_id4` VARCHAR(100) NULL,
  ADD COLUMN `product5` VARCHAR(500) NULL, ADD COLUMN `product_kind5` VARCHAR(40) NULL, ADD COLUMN `product_category5` VARCHAR(500) NULL,
  ADD COLUMN `ndc5` VARCHAR(100) NULL, ADD COLUMN `device_id5` VARCHAR(100) NULL;

-- The company page's top products link to the product pages.
ALTER TABLE `op_company_product` ADD COLUMN `slug` VARCHAR(200) NULL, ALGORITHM=INSTANT;

CREATE TABLE IF NOT EXISTS `op_product` (
  `slug` VARCHAR(200) NOT NULL,
  `name` VARCHAR(500) NOT NULL,
  `kind` VARCHAR(40) NULL,
  `category` VARCHAR(500) NULL,
  `ndc` VARCHAR(100) NULL,
  `device_id` VARCHAR(100) NULL,
  `program_year` SMALLINT NOT NULL,
  `amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  `companies` INT NOT NULL,
  `providers` INT NOT NULL,
  PRIMARY KEY (`slug`),
  KEY `ix_op_product_name` (`name`(100))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `op_product_company` (
  `slug` VARCHAR(200) NOT NULL,
  `company_id` VARCHAR(20) NOT NULL,
  `amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  PRIMARY KEY (`slug`, `company_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `op_product_nature` (
  `slug` VARCHAR(200) NOT NULL,
  `nature` VARCHAR(200) NOT NULL,
  `amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  PRIMARY KEY (`slug`, `nature`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `op_product_specialty` (
  `slug` VARCHAR(200) NOT NULL,
  `specialty_rank` TINYINT NOT NULL,
  `specialty` VARCHAR(255) NOT NULL,
  `providers` INT NOT NULL,
  `amount` DOUBLE NOT NULL,
  PRIMARY KEY (`slug`, `specialty_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `op_product_recipient` (
  `slug` VARCHAR(200) NOT NULL,
  `recipient_rank` SMALLINT NOT NULL,
  `npi` CHAR(10) NOT NULL,
  `amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  PRIMARY KEY (`slug`, `recipient_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The products named most in payments to each provider (general payments, newest year).
CREATE TABLE IF NOT EXISTS `op_provider_product` (
  `npi` CHAR(10) NOT NULL,
  `product_rank` TINYINT NOT NULL,
  `slug` VARCHAR(200) NOT NULL,
  `amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  PRIMARY KEY (`npi`, `product_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Research payments of the newest program year naming the product: totals and the largest studies.
CREATE TABLE IF NOT EXISTS `op_research_raw` (
  `company_id` VARCHAR(20) NULL,
  `amount` DOUBLE NULL,
  `study` TEXT NULL,
  `nct_id` VARCHAR(200) NULL,
  `product` VARCHAR(500) NULL,
  `product2` VARCHAR(500) NULL,
  `product3` VARCHAR(500) NULL,
  `product4` VARCHAR(500) NULL,
  `product5` VARCHAR(500) NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `op_product_research` (
  `slug` VARCHAR(200) NOT NULL,
  `program_year` SMALLINT NOT NULL,
  `amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  `studies` INT NOT NULL,
  PRIMARY KEY (`slug`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `op_product_study` (
  `slug` VARCHAR(200) NOT NULL,
  `study_rank` TINYINT NOT NULL,
  `study` TEXT NULL,
  `nct_id` VARCHAR(200) NULL,
  `company_id` VARCHAR(20) NULL,
  `amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  PRIMARY KEY (`slug`, `study_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
