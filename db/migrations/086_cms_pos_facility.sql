-- 086_cms_pos_facility.sql
--
-- CMS Provider of Services file (both parts: "Quality Improvement and Evaluation System" and "Internet Quality Improvement
-- and Evaluation System"), one row per Medicare-certified facility (CCN), active or terminated, for the record-quality
-- label (docs/PRODUCT_PLAN.md step 3.1). The file has no NPI: a closed facility is matched to an organization NPI at the
-- same address with a similar name by the loader (QualityBuilder). Replaced in full (staging + RENAME).
--   active            1 = certified now, 0 = terminated
--   termination_code  PGM_TRMNTN_CD (00 active; 07 a change of status such as an ownership change, not a closure)

CREATE TABLE IF NOT EXISTS `cms_pos_facility` (
  `ccn` VARCHAR(10) NOT NULL,
  `name` VARCHAR(150) NULL,
  `category` VARCHAR(10) NULL,
  `address` VARCHAR(150) NULL,
  `city` VARCHAR(60) NULL,
  `state` VARCHAR(2) NULL,
  `zip5` CHAR(5) NULL,
  `active` TINYINT NOT NULL,
  `termination_code` VARCHAR(4) NULL,
  `termination_date` DATE NULL,
  PRIMARY KEY (`ccn`),
  KEY `ix_cms_pos_facility_zip` (`zip5`, `active`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
