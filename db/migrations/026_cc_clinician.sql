-- 026_cc_clinician.sql
--
-- Care Compare facts per clinician NPI (CLAUDE.md §7 Stage 5.5 item 3a), summarized from cc_dac_raw.
--   accepts_assignment  1 = accepts Medicare's approved amount as full payment (any enrollment "Y"),
--                       0 = only "M" (may accept)
--   telehealth          1 = offers telehealth at any of its listed practices

CREATE TABLE IF NOT EXISTS `cc_clinician` (
  `npi` CHAR(10) NOT NULL,
  `ind_pac_id` VARCHAR(10) NULL,
  `medical_school` VARCHAR(150) NULL,
  `graduation_year` SMALLINT NULL,
  `primary_specialty` VARCHAR(100) NULL,
  `secondary_specialties` VARCHAR(300) NULL,
  `accepts_assignment` TINYINT NOT NULL,
  `telehealth` TINYINT NOT NULL,
  PRIMARY KEY (`npi`),
  KEY `ix_cc_clinician_grad` (`graduation_year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
