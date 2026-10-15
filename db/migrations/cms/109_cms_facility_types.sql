-- 109_cms_facility_types.sql
--
-- More facility types for the Facilities & hospitals pages (docs/PRODUCT_PLAN.md step 3.6 part 2), from Medicare Care Compare
-- (Provider Data Catalog) unless noted. Each is replaced in full (staging + RENAME) by its source.
--   cms_dialysis            dialysis facilities (cc_dialysis: "Dialysis Facility - Listing by Facility"): address, profit status, chain,
--                           stations, services, the five-star rating and its period, the outcome categories (as expected, better or
--                           worse than expected) with their periods, and the ICH CAHPS patient survey stars ("Patient survey (ICH
--                           CAHPS) - Facility")
--   cms_dialysis_npi        the NPI of each dialysis facility (cms_dialysis_npis: data.cms.gov "Medicare Dialysis Facilities", the
--                           Dialysis Facility Report, read through its data API); kept apart from cms_facility_npi, which the
--                           record-quality label counts
--   cms_asc                 ambulatory surgery centers (cc_surgery_centers: "Ambulatory Surgical Center Quality Measures - Facility"),
--                           one row per CCN and the NPI it reports under ('' when none), with the measure year, hospital visits
--                           after colonoscopy (ASC-12), orthopedic (17), urology (18) and general surgery (19) procedures (cases,
--                           CMS's category, rate) and two process rates (ASC-9, ASC-13); the national rates ("- National") as
--                           ccn 'NATION', npi ''
--   cms_post_acute          inpatient rehabilitation facilities (kind 'rehab') and long-term care hospitals (kind 'ltch'), from each
--                           type's "General Information" (cc_post_acute); beds for long-term care hospitals
--   cms_post_acute_measure  their main measures from the long "Provider Data" files (Npi.Core FacilityMeasures.PostAcute: infections,
--                           preventable readmissions, discharge to the community, Medicare spending per patient), one row per CCN
--                           and measure (CMS's base code, e.g. I_006_01); the "National Data" values as ccn 'NATION'

CREATE TABLE IF NOT EXISTS `cms_dialysis` (
  `ccn` VARCHAR(10) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  `address` VARCHAR(200) NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  `zip` VARCHAR(10) NULL,
  `phone` VARCHAR(20) NULL,
  `ownership` VARCHAR(40) NULL,
  `chain` VARCHAR(200) NULL,
  `stations` SMALLINT NULL,
  `in_center` TINYINT NULL,
  `peritoneal` TINYINT NULL,
  `home_hemodialysis` TINYINT NULL,
  `five_star` DOUBLE NULL,
  `five_star_period` VARCHAR(40) NULL,
  `survey_rating` TINYINT NULL,
  `survival` VARCHAR(60) NULL,
  `survival_period` VARCHAR(40) NULL,
  `hospitalization` VARCHAR(60) NULL,
  `hospitalization_period` VARCHAR(40) NULL,
  `readmission` VARCHAR(60) NULL,
  `readmission_period` VARCHAR(40) NULL,
  `ed_visits` VARCHAR(60) NULL,
  `ed_visits_period` VARCHAR(40) NULL,
  `infection` VARCHAR(60) NULL,
  `infection_period` VARCHAR(40) NULL,
  `transfusion` VARCHAR(60) NULL,
  `transfusion_period` VARCHAR(40) NULL,
  `waitlist` VARCHAR(60) NULL,
  `waitlist_period` VARCHAR(40) NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `cms_dialysis_npi` (
  `ccn` VARCHAR(10) NOT NULL,
  `npi` CHAR(10) NOT NULL,
  PRIMARY KEY (`ccn`, `npi`),
  KEY `idx_cms_dialysis_npi_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `cms_asc` (
  `ccn` VARCHAR(10) NOT NULL,
  `npi` CHAR(10) NOT NULL,
  `name` VARCHAR(200) NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  `zip` VARCHAR(10) NULL,
  `data_year` SMALLINT NULL,
  `asc9_rate` DOUBLE NULL,
  `asc13_rate` DOUBLE NULL,
  `asc12_cases` INT NULL,
  `asc12_category` VARCHAR(60) NULL,
  `asc12_rate` DOUBLE NULL,
  `asc17_cases` INT NULL,
  `asc17_category` VARCHAR(60) NULL,
  `asc17_rate` DOUBLE NULL,
  `asc18_cases` INT NULL,
  `asc18_category` VARCHAR(60) NULL,
  `asc18_rate` DOUBLE NULL,
  `asc19_cases` INT NULL,
  `asc19_category` VARCHAR(60) NULL,
  `asc19_rate` DOUBLE NULL,
  PRIMARY KEY (`ccn`, `npi`),
  KEY `idx_cms_asc_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `cms_post_acute` (
  `ccn` VARCHAR(10) NOT NULL,
  `kind` VARCHAR(10) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  `address` VARCHAR(200) NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  `zip` VARCHAR(10) NULL,
  `phone` VARCHAR(20) NULL,
  `ownership` VARCHAR(100) NULL,
  `certified_since` DATE NULL,
  `beds` INT NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `cms_post_acute_measure` (
  `ccn` VARCHAR(10) NOT NULL,
  `measure_id` VARCHAR(20) NOT NULL,
  `score` DOUBLE NULL,
  `compared` VARCHAR(60) NULL,
  `cases` INT NULL,
  `start_date` DATE NULL,
  `end_date` DATE NULL,
  PRIMARY KEY (`ccn`, `measure_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
