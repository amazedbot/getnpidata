-- 106_facility_ownership_change.sql
--
-- Facilities' ownership history (docs/PRODUCT_PLAN.md step 3.6 part 2), from the data.cms.gov Hospital and Skilled Nursing
-- Facility "Change of Ownership" files (every change since 2016) and their "Owner Information" files (the cms_ownership_changes
-- source). Both tables are replaced in full together (staging + RENAME).
--   facility_ownership_change        one row per change: the buyer and the seller (enrollment, CCN, NPI, associate ID, names),
--                                    chow_type_code CH = change of ownership, AM = acquisition or merger; period_end = the end of the
--                                    release's period
--   facility_ownership_change_owner  the owners of each change's seller (its enrollment) in an ownership or control role, i.e. the
--                                    previous owners; the buyers' owners are the current ones (facility_owner)

CREATE TABLE IF NOT EXISTS `facility_ownership_change` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `kind` VARCHAR(20) NOT NULL,
  `chow_type_code` VARCHAR(4) NULL,
  `chow_type` VARCHAR(60) NULL,
  `effective_date` DATE NULL,
  `buyer_enrollment_id` VARCHAR(20) NULL,
  `buyer_ccn` VARCHAR(10) NULL,
  `buyer_npi` CHAR(10) NULL,
  `buyer_associate_id` CHAR(10) NULL,
  `buyer_name` VARCHAR(200) NULL,
  `buyer_dba_name` VARCHAR(200) NULL,
  `seller_enrollment_id` VARCHAR(20) NULL,
  `seller_ccn` VARCHAR(10) NULL,
  `seller_npi` CHAR(10) NULL,
  `seller_associate_id` CHAR(10) NULL,
  `seller_name` VARCHAR(200) NULL,
  `seller_dba_name` VARCHAR(200) NULL,
  `period_end` DATE NULL,
  PRIMARY KEY (`id`),
  KEY `ix_facility_ownership_change_buyer_ccn` (`buyer_ccn`),
  KEY `ix_facility_ownership_change_seller_ccn` (`seller_ccn`),
  KEY `ix_facility_ownership_change_buyer` (`buyer_associate_id`),
  KEY `ix_facility_ownership_change_seller` (`seller_associate_id`),
  KEY `ix_facility_ownership_change_seller_enrollment` (`seller_enrollment_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `facility_ownership_change_owner` (
  `enrollment_id` VARCHAR(20) NOT NULL,
  `owner_id` CHAR(10) NOT NULL,
  `role_code` VARCHAR(4) NOT NULL,
  `owner_type` CHAR(1) NOT NULL,
  `name` VARCHAR(200) NULL,
  `role_text` VARCHAR(100) NULL,
  `percentage` DOUBLE NULL,
  PRIMARY KEY (`enrollment_id`, `owner_id`, `role_code`),
  KEY `ix_facility_ownership_change_owner_owner` (`owner_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
