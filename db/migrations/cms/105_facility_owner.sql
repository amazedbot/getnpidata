-- 105_facility_owner.sql
--
-- Who owns and runs each Medicare-certified facility (docs/PRODUCT_PLAN.md step 3.6 part 2), from the data.cms.gov Hospital,
-- Skilled Nursing Facility, Home Health Agency, Hospice, Federally Qualified Health Center and Rural Health Clinic "All Owners"
-- files (the cms_facility_owners source). Both tables are replaced in full together (staging + RENAME).
--   facility_owner         one row per facility enrollment, owner and role: owners with their stake, managing parties, officers,
--                          directors and managing employees; owner_id = the owner's associate ID (PAC ID), padded to ten digits;
--                          name = the organization's name, or the person's first, middle and last name; owner_kinds = the owner
--                          types CMS marks Y (private_equity, reit, holding_company …, Npi.Core OwnerKinds); period_end = the end
--                          of the release's period ("as of")
--   facility_owner_entity  one row per owner: facilities = the enrollments it is listed for in any role, owned = those with an
--                          ownership interest (role codes 34, 35, 38, 39, 85, 86)

CREATE TABLE IF NOT EXISTS `facility_owner` (
  `enrollment_id` VARCHAR(20) NOT NULL,
  `owner_id` CHAR(10) NOT NULL,
  `role_code` VARCHAR(4) NOT NULL,
  `kind` VARCHAR(20) NOT NULL,
  `facility_associate_id` CHAR(10) NULL,
  `facility_name` VARCHAR(200) NULL,
  `owner_type` CHAR(1) NOT NULL,
  `role_text` VARCHAR(100) NULL,
  `association_date` DATE NULL,
  `name` VARCHAR(200) NULL,
  `title` VARCHAR(100) NULL,
  `dba_name` VARCHAR(200) NULL,
  `city` VARCHAR(60) NULL,
  `state` VARCHAR(2) NULL,
  `percentage` DOUBLE NULL,
  `owner_kinds` VARCHAR(400) NULL,
  `period_end` DATE NULL,
  PRIMARY KEY (`enrollment_id`, `owner_id`, `role_code`),
  KEY `ix_facility_owner_owner` (`owner_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `facility_owner_entity` (
  `owner_id` CHAR(10) NOT NULL,
  `owner_type` CHAR(1) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  `dba_name` VARCHAR(200) NULL,
  `city` VARCHAR(60) NULL,
  `state` VARCHAR(2) NULL,
  `owner_kinds` VARCHAR(400) NULL,
  `facilities` INT NOT NULL,
  `owned` INT NOT NULL,
  PRIMARY KEY (`owner_id`),
  KEY `ix_facility_owner_entity_size` (`owned`, `facilities`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
