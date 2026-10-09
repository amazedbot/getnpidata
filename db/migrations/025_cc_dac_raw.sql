-- 025_cc_dac_raw.sql
--
-- Template for the raw Doctors and Clinicians National Downloadable File (Care Compare), one row per
-- clinician × enrollment × group × address (CLAUDE.md §7 Stage 5.5 item 3a). Holds no data: each load
-- fills a `cc_dac_raw_staging` copy, summarizes it into cc_clinician and cc_group, and drops it.

CREATE TABLE IF NOT EXISTS `cc_dac_raw` (
  `npi` CHAR(10) NOT NULL,
  `ind_pac_id` VARCHAR(10) NULL,
  `medical_school` VARCHAR(150) NULL,
  `graduation_year` SMALLINT NULL,
  `primary_specialty` VARCHAR(100) NULL,
  `secondary_specialties` VARCHAR(300) NULL,
  `telehealth` VARCHAR(1) NULL,
  `group_name` VARCHAR(200) NULL,
  `org_pac_id` VARCHAR(10) NULL,
  `group_members` INT NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  `ind_assignment` VARCHAR(1) NULL,
  `grp_assignment` VARCHAR(1) NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
