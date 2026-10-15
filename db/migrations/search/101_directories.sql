-- 101_directories.sql
--
-- The Facilities & hospitals and Groups & organizations pages (docs/PRODUCT_PLAN.md step 3.6), built by the loader
-- (DirectoryBuilder, after the record quality) so the site reads a few small tables instead of aggregating millions of rows
-- when it starts. Each is replaced in full (staging + RENAME) when one of its inputs changed.
--   directory_facility     every Medicare-certified facility: the Provider of Services file merged with the Care Compare
--                          hospital, nursing home, home health and hospice listings (FacilityDirectory.Merge); lat/lon = the
--                          centre of its ZIP; billing_npis = active NPIs cms_facility_npi links to it
--   directory_group        every Medicare group (PAC ID) with an active clinician: clinicians reassigned to it or listed
--                          with it in Care Compare, organization NPIs enrolled under it, its group MIPS score
--   directory_org_count    active organization NPIs by primary taxonomy, primary practice state ('' when not a 2-letter
--                          code) and record-quality label (0 = not labelled)
--   directory_org_billing  active organization NPIs paid by Medicare Part B under their own NPI

CREATE TABLE IF NOT EXISTS `directory_facility` (
  `ccn` VARCHAR(10) NOT NULL,
  `type` VARCHAR(20) NOT NULL,
  `kind` VARCHAR(80) NULL,
  `name` VARCHAR(200) NOT NULL,
  `address` VARCHAR(200) NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  `zip5` CHAR(5) NULL,
  `phone` VARCHAR(20) NULL,
  `ownership` VARCHAR(100) NULL,
  `beds` INT NULL,
  `rating` DOUBLE NULL,
  `active` TINYINT NOT NULL,
  `termination_date` DATE NULL,
  `termination_reason` VARCHAR(80) NULL,
  `billing_npis` INT NOT NULL,
  `lat` DOUBLE NULL,
  `lon` DOUBLE NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `directory_group` (
  `pac_id` VARCHAR(10) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  `state` VARCHAR(2) NULL,
  `clinicians` INT NOT NULL,
  `organization_npis` INT NOT NULL,
  `accepts_assignment` TINYINT NULL,
  `mips_score` DOUBLE NULL,
  `mips_year` SMALLINT NULL,
  PRIMARY KEY (`pac_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `directory_org_count` (
  `taxonomy_code` VARCHAR(10) NOT NULL,
  `state` VARCHAR(2) NOT NULL,
  `label` TINYINT NOT NULL,
  `organizations` INT NOT NULL,
  PRIMARY KEY (`taxonomy_code`, `state`, `label`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `directory_org_billing` (
  `npi` CHAR(10) NOT NULL,
  `data_year` SMALLINT NOT NULL,
  `payments` DOUBLE NOT NULL,
  `beneficiaries` INT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
