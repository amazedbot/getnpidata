-- 107_nursing_home_inspections.sql
--
-- Nursing home inspections and penalties (docs/PRODUCT_PLAN.md step 3.6 part 2), from the Provider Data Catalog "Health
-- Deficiencies" (r5ix-sfxw), "Fire Safety Deficiencies" (ifjz-ge4w) and "Penalties" (g6vv-u9sr), the last three standard
-- inspection cycles (the cc_nursing_home_inspections source). The ~620,000 cited deficiencies are summarized per CCN; every
-- table is replaced in full together (staging + RENAME).
--   nh_survey            one row per inspection: kind = health | fire, the cycle (1 = newest), deficiencies, complaint ones, the
--                        most serious scope and severity (A–L; a later letter is more serious)
--   nh_deficiency_count  deficiencies per CCN, kind, cycle and scope and severity
--   nh_citation          per CCN the ten most serious and the ten newest citations (tag, scope and severity, correction)
--   nh_deficiency_tag    CMS's category and wording of each tag (F0689, K0345 …)
--   nh_penalty           every fine and denial of payment for new admissions

CREATE TABLE IF NOT EXISTS `nh_survey` (
  `ccn` VARCHAR(10) NOT NULL,
  `kind` VARCHAR(6) NOT NULL,
  `survey_date` DATE NOT NULL,
  `cycle` INT NOT NULL,
  `deficiencies` INT NOT NULL,
  `complaint` INT NOT NULL,
  `most_severe` CHAR(1) NULL,
  PRIMARY KEY (`ccn`, `kind`, `survey_date`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `nh_deficiency_count` (
  `ccn` VARCHAR(10) NOT NULL,
  `kind` VARCHAR(6) NOT NULL,
  `cycle` INT NOT NULL,
  `scope_severity` CHAR(1) NOT NULL,
  `deficiencies` INT NOT NULL,
  `complaint` INT NOT NULL,
  PRIMARY KEY (`ccn`, `kind`, `cycle`, `scope_severity`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `nh_citation` (
  `ccn` VARCHAR(10) NOT NULL,
  `kind` VARCHAR(6) NOT NULL,
  `survey_date` DATE NOT NULL,
  `prefix` VARCHAR(2) NOT NULL,
  `tag` VARCHAR(6) NOT NULL,
  `cycle` INT NOT NULL,
  `scope_severity` CHAR(1) NOT NULL,
  `corrected` VARCHAR(80) NULL,
  `correction_date` DATE NULL,
  `complaint` TINYINT NOT NULL,
  `infection_control` TINYINT NOT NULL,
  PRIMARY KEY (`ccn`, `kind`, `survey_date`, `prefix`, `tag`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `nh_deficiency_tag` (
  `prefix` VARCHAR(2) NOT NULL,
  `tag` VARCHAR(6) NOT NULL,
  `category` VARCHAR(120) NULL,
  `description` VARCHAR(600) NULL,
  PRIMARY KEY (`prefix`, `tag`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `nh_penalty` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `ccn` VARCHAR(10) NOT NULL,
  `penalty_date` DATE NULL,
  `penalty_type` VARCHAR(40) NULL,
  `fine_amount` DOUBLE NULL,
  `denial_start` DATE NULL,
  `denial_days` INT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_nh_penalty_ccn` (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
