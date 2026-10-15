-- 104_cms_facility_enrollment.sql
--
-- One row per Medicare enrollment of a facility, from the CMS Hospital, Skilled Nursing Facility, Home Health Agency, Hospice,
-- FQHC and Rural Health Clinic Enrollments files (the cms_facility_enrollments source, which also fills cms_facility_npi), for
-- the owners (docs/PRODUCT_PLAN.md step 3.6 part 2): CMS's All Owners files list owners by enrollment ID, and an owner's
-- associate ID is the same ID as a facility's here. Replaced in full with cms_facility_npi (staging + RENAME).
--   associate_id  the enrolled organization's PECOS associate control ID (PAC ID), padded to ten digits

CREATE TABLE IF NOT EXISTS `cms_facility_enrollment` (
  `enrollment_id` VARCHAR(20) NOT NULL,
  `kind` VARCHAR(20) NOT NULL,
  `ccn` VARCHAR(10) NULL,
  `npi` CHAR(10) NULL,
  `associate_id` CHAR(10) NULL,
  `org_name` VARCHAR(200) NULL,
  `state` VARCHAR(2) NULL,
  PRIMARY KEY (`enrollment_id`),
  KEY `ix_cms_facility_enrollment_ccn` (`ccn`),
  KEY `ix_cms_facility_enrollment_associate` (`associate_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
