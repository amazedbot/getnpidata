-- 069_fda_products.sql
--
-- What a product is (CLAUDE.md §7 Stage 5.5 item 19, part 2), from FDA's public data via openFDA:
--   drugs and biologicals: the NDC directory (every listed product: brand and generic name, ingredients, form, route,
--   labeler, marketing category, application, pharmacologic class), Drugs@FDA (application sponsor and first approval)
--   and the drug labels (indications and boxed warning) of the products named in Open Payments;
--   devices: the GUDID records of the device identifiers named in Open Payments (description, GMDN term, product code
--   and class) and their 510(k) / PMA decisions.
-- A product links to a drug by its NDC's labeler and product parts (`ndc_key`: 5 + 4 digits, zero-padded, so
-- "0003-0893-21" and "0003-0893" are 000030893) and to a device by its device identifier.

ALTER TABLE `op_product`
  ADD COLUMN `ndc_key` CHAR(9) GENERATED ALWAYS AS (
    IF(LENGTH(`ndc`) - LENGTH(REPLACE(`ndc`, '-', '')) IN (1, 2),
       CONCAT(LPAD(SUBSTRING_INDEX(`ndc`, '-', 1), 5, '0'), LPAD(SUBSTRING_INDEX(SUBSTRING_INDEX(`ndc`, '-', 2), '-', -1), 4, '0')), NULL)) VIRTUAL;
ALTER TABLE `op_product` ADD KEY `ix_op_product_ndc_key` (`ndc_key`), ADD KEY `ix_op_product_device_id` (`device_id`);

CREATE TABLE IF NOT EXISTS `fda_ndc_product` (
  `product_ndc` VARCHAR(20) NOT NULL,
  `ndc_key` CHAR(9) GENERATED ALWAYS AS (
    IF(LENGTH(`product_ndc`) - LENGTH(REPLACE(`product_ndc`, '-', '')) IN (1, 2),
       CONCAT(LPAD(SUBSTRING_INDEX(`product_ndc`, '-', 1), 5, '0'), LPAD(SUBSTRING_INDEX(SUBSTRING_INDEX(`product_ndc`, '-', 2), '-', -1), 4, '0')), NULL)) STORED,
  `brand_name` VARCHAR(500) NULL,
  `generic_name` VARCHAR(1000) NULL,
  `active_ingredients` VARCHAR(2000) NULL,
  `dosage_form` VARCHAR(200) NULL,
  `route` VARCHAR(200) NULL,
  `labeler` VARCHAR(255) NULL,
  `marketing_category` VARCHAR(100) NULL,
  `application_number` VARCHAR(40) NULL,
  `marketing_start` DATE NULL,
  `pharm_classes` VARCHAR(1000) NULL,
  `product_type` VARCHAR(100) NULL,
  `spl_set_id` VARCHAR(64) NULL,
  PRIMARY KEY (`product_ndc`),
  KEY `ix_fda_ndc_product_key` (`ndc_key`),
  KEY `ix_fda_ndc_product_generic` (`generic_name`(100))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `fda_application` (
  `application_number` VARCHAR(40) NOT NULL,
  `sponsor` VARCHAR(255) NULL,
  `approval_date` DATE NULL,
  PRIMARY KEY (`application_number`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `fda_drug_label` (
  `set_id` VARCHAR(64) NOT NULL,
  `effective_date` DATE NULL,
  `brand_name` VARCHAR(500) NULL,
  `indications` MEDIUMTEXT NULL,
  `boxed_warning` MEDIUMTEXT NULL,
  PRIMARY KEY (`set_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The labels of each product NDC (by the NDC directory's set ID or the label's own product NDCs).
CREATE TABLE IF NOT EXISTS `fda_drug_label_ndc` (
  `ndc_key` CHAR(9) NOT NULL,
  `set_id` VARCHAR(64) NOT NULL,
  PRIMARY KEY (`ndc_key`, `set_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `fda_device` (
  `device_id` VARCHAR(100) NOT NULL,
  `brand_name` VARCHAR(500) NULL,
  `company_name` VARCHAR(500) NULL,
  `description` TEXT NULL,
  `model` VARCHAR(500) NULL,
  `gmdn_term` VARCHAR(500) NULL,
  `gmdn_definition` TEXT NULL,
  `product_code` VARCHAR(20) NULL,
  `product_code_name` VARCHAR(500) NULL,
  `device_class` VARCHAR(10) NULL,
  `medical_specialty` VARCHAR(200) NULL,
  `is_rx` TINYINT NULL,
  `is_otc` TINYINT NULL,
  `implantable` TINYINT NULL,
  `distribution_status` VARCHAR(200) NULL,
  `submissions` VARCHAR(2000) NULL,
  PRIMARY KEY (`device_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 510(k) clearances and PMA approvals referenced by those devices.
CREATE TABLE IF NOT EXISTS `fda_premarket` (
  `number` VARCHAR(20) NOT NULL,
  `kind` VARCHAR(10) NOT NULL,
  `applicant` VARCHAR(500) NULL,
  `device_name` VARCHAR(1000) NULL,
  `decision_date` DATE NULL,
  `decision` VARCHAR(200) NULL,
  PRIMARY KEY (`number`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
