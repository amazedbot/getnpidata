-- 028_cc_facility_affiliation.sql
--
-- Care Compare Facility Affiliation Data: facilities (hospitals, nursing homes, home health,
-- hospice, dialysis, …) where a clinician works, by CMS Certification Number (CLAUDE.md §7 Stage 5.5
-- item 3b). parent_ccn is the hospital CCN for a unit with its own CCN.

CREATE TABLE IF NOT EXISTS `cc_facility_affiliation` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `npi` CHAR(10) NOT NULL,
  `facility_type` VARCHAR(50) NOT NULL,
  `ccn` VARCHAR(10) NOT NULL,
  `parent_ccn` VARCHAR(10) NULL,
  PRIMARY KEY (`id`),
  KEY `ix_cc_facility_affiliation_npi` (`npi`),
  KEY `ix_cc_facility_affiliation_ccn` (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
