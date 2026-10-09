-- 031_cms_facility_npi.sql
--
-- CCN ↔ organization NPI, from the CMS Hospital Enrollments and Skilled Nursing Facility Enrollments
-- files (CLAUDE.md §7 Stage 5.5 item 4b). Links an organization NPI to its Care Compare facility data,
-- and a clinician's affiliated facility to that facility's NPI.

CREATE TABLE IF NOT EXISTS `cms_facility_npi` (
  `ccn` VARCHAR(10) NOT NULL,
  `npi` CHAR(10) NOT NULL,
  `kind` VARCHAR(20) NOT NULL,
  PRIMARY KEY (`ccn`, `npi`),
  KEY `ix_cms_facility_npi_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
