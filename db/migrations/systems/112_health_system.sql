-- 112_health_system.sql
--
-- Health systems (docs/PRODUCT_PLAN.md step 3.6 part 2): AHRQ's Compendium of U.S. Health Systems, newest year, from the CSV
-- files the owner downloads from ahrq.gov into AhrqCompendiumFolder (ahrq.gov answers programs with a browser check, so the
-- loader doesn't fetch them itself). Replaced in full (staging + RENAME) when the files change.
--   health_system           the systems file: one row per system (AHRQ's health_sys_id), with AHRQ's counts of physicians,
--                           nurse practitioners, physician assistants, group practices, hospitals, nursing homes and home
--                           health agencies, and the system's beds and discharges
--   health_system_facility  the linkage files (hospitals, and where published nursing homes and home health agencies): each
--                           facility's CMS Certification Number and the system it belongs to; kind = the facility type its
--                           CCN stands for (Npi.Core FacilityTypes.FromCcn)

CREATE TABLE IF NOT EXISTS `health_system` (
  `system_id` VARCHAR(20) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  `data_year` SMALLINT NOT NULL,
  `physicians` INT NULL,
  `primary_care_physicians` INT NULL,
  `nurse_practitioners` INT NULL,
  `physician_assistants` INT NULL,
  `group_practices` INT NULL,
  `hospitals` INT NULL,
  `acute_hospitals` INT NULL,
  `nursing_homes` INT NULL,
  `home_health_agencies` INT NULL,
  `beds` INT NULL,
  `discharges` INT NULL,
  `major_teaching` TINYINT NULL,
  `offers_insurance` TINYINT NULL,
  `medicare_advantage` TINYINT NULL,
  PRIMARY KEY (`system_id`),
  KEY `ix_health_system_state` (`state`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `health_system_facility` (
  `ccn` VARCHAR(10) NOT NULL,
  `system_id` VARCHAR(20) NOT NULL,
  `kind` VARCHAR(20) NOT NULL,
  `name` VARCHAR(200) NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  PRIMARY KEY (`ccn`, `system_id`),
  KEY `ix_health_system_facility_system` (`system_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
