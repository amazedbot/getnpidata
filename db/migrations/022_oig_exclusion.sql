-- 022_oig_exclusion.sql
--
-- HHS-OIG List of Excluded Individuals/Entities (LEIE), the full current list (CLAUDE.md §7 Stage 5.5
-- item 2a). Replaced in full by each monthly release. Matched to providers by NPI only: many entries
-- have no NPI (npi NULL) and are never matched by name. Date of birth and UPIN are not loaded.
--   exclusion_type  OIG authority code, e.g. 1128a1, 1128b4 (described in Npi.Core ComplianceCodes)

CREATE TABLE IF NOT EXISTS `oig_exclusion` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `npi` CHAR(10) NULL,
  `last_name` VARCHAR(100) NULL,
  `first_name` VARCHAR(100) NULL,
  `middle_name` VARCHAR(100) NULL,
  `business_name` VARCHAR(200) NULL,
  `general_category` VARCHAR(100) NULL,
  `specialty` VARCHAR(100) NULL,
  `address` VARCHAR(200) NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  `zip` VARCHAR(10) NULL,
  `exclusion_type` VARCHAR(20) NULL,
  `exclusion_date` DATE NULL,
  `reinstatement_date` DATE NULL,
  `waiver_date` DATE NULL,
  `waiver_state` VARCHAR(2) NULL,
  PRIMARY KEY (`id`),
  KEY `ix_oig_exclusion_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
