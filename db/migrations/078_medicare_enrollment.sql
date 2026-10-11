-- 078_medicare_enrollment.sql
--
-- Medicare fee-for-service enrollment (owner's OK 2026-10-11; docs/PRODUCT_PLAN.md step 1.5), from data.cms.gov:
--   medicare_enrollment: "Medicare Fee-For-Service Public Provider Enrollment" (PPEF), one row per enrollment, linking an NPI
--     to its PECOS associate control ID (PAC ID, the ID Care Compare calls org_pac_id / ind_pac_id);
--   medicare_reassignment: "Revalidation Clinic Group Practice Reassignment", one row per clinician who reassigns benefits to
--     (bills through) a group, or is a physician assistant employed by it.
-- Together they answer "which clinicians work for this organization": organization NPI → PAC ID → reassigned clinicians.
-- Both are replaced in full by the loader (staging + RENAME).

CREATE TABLE IF NOT EXISTS `medicare_enrollment` (
  `npi` CHAR(10) NOT NULL,
  `enrollment_id` VARCHAR(20) NOT NULL,
  `pac_id` VARCHAR(10) NULL,
  `provider_type` VARCHAR(120) NULL,
  `state` VARCHAR(2) NULL,
  `org_name` VARCHAR(200) NULL,
  PRIMARY KEY (`npi`, `enrollment_id`),
  KEY `ix_medicare_enrollment_pac` (`pac_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `medicare_reassignment` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `group_pac_id` VARCHAR(10) NOT NULL,
  `group_enrollment_id` VARCHAR(20) NULL,
  `group_name` VARCHAR(200) NULL,
  `group_state` VARCHAR(2) NULL,
  `record_type` VARCHAR(30) NULL,
  `npi` CHAR(10) NULL,
  `specialty` VARCHAR(120) NULL,
  PRIMARY KEY (`id`),
  KEY `ix_medicare_reassignment_group` (`group_pac_id`, `npi`),
  KEY `ix_medicare_reassignment_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
