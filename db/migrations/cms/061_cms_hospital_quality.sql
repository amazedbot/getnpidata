-- 061_cms_hospital_quality.sql
--
-- Hospital outcomes (CLAUDE.md §7 Stage 5.5 item 14). From Hospital General Information: how many of the hospital's
-- mortality, safety (infections, complications) and readmission measures CMS rates better or worse than the national
-- rate (NULL = not available). From "Patient survey (HCAHPS) - Hospital": the HCAHPS summary star rating, the
-- number of completed surveys and the response rate. `cms_hcahps_raw` is the empty template for that load.

ALTER TABLE `cms_hospital`
  ADD COLUMN `mort_measures` SMALLINT NULL,
  ADD COLUMN `mort_better` SMALLINT NULL,
  ADD COLUMN `mort_worse` SMALLINT NULL,
  ADD COLUMN `safety_measures` SMALLINT NULL,
  ADD COLUMN `safety_better` SMALLINT NULL,
  ADD COLUMN `safety_worse` SMALLINT NULL,
  ADD COLUMN `readm_measures` SMALLINT NULL,
  ADD COLUMN `readm_better` SMALLINT NULL,
  ADD COLUMN `readm_worse` SMALLINT NULL,
  ALGORITHM=INSTANT;

CREATE TABLE IF NOT EXISTS `cms_hcahps_raw` (
  `ccn` VARCHAR(10) NULL,
  `measure_id` VARCHAR(40) NULL,
  `star_rating` TINYINT NULL,
  `surveys` INT NULL,
  `response_rate` DOUBLE NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `cms_hospital_survey` (
  `ccn` VARCHAR(10) NOT NULL,
  `star_rating` TINYINT NULL,
  `surveys` INT NULL,
  `response_rate` DOUBLE NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
