-- 060_cc_mips.sql
--
-- Clinician MIPS performance (CLAUDE.md §7 Stage 5.5 item 14), from Care Compare's "PY <year> Clinician Public
-- Reporting: Overall MIPS Performance": a clinician can have several scores (individual, group, APM entity,
-- subgroup), one row each. Scores are 0-100; a category CMS didn't score is NULL.

CREATE TABLE IF NOT EXISTS `cc_mips` (
  `id` BIGINT NOT NULL AUTO_INCREMENT,
  `npi` CHAR(10) NULL,
  `program_year` SMALLINT NOT NULL,
  `source` VARCHAR(20) NULL,
  `org_pac_id` VARCHAR(20) NULL,
  `facility_name` VARCHAR(200) NULL,
  `quality_score` DOUBLE NULL,
  `pi_score` DOUBLE NULL,
  `ia_score` DOUBLE NULL,
  `cost_score` DOUBLE NULL,
  `final_score` DOUBLE NULL,
  PRIMARY KEY (`id`),
  KEY `ix_cc_mips_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
