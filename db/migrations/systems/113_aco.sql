-- 113_aco.sql
--
-- Medicare Shared Savings Program accountable care organizations (docs/PRODUCT_PLAN.md step 3.6 part 2), from the data.cms.gov
-- catalog's "Accountable Care Organizations", "Accountable Care Organization Participants" and "Accountable Care Organization
-- Skilled Nursing Facility Affiliates" (newest performance year), replaced together (staging + RENAME).
--   aco                 one row per ACO (CMS's ACO ID, e.g. A1234): name, service area (states), track, agreement dates
--   aco_participant     the ACO's participants by legal business name, as CMS publishes them: the file has no TIN, NPI,
--                       PAC ID or CCN
--   aco_participant_npi the active organization NPIs whose NPPES legal business name is the participant's name, compared
--                       after folding case, spacing and punctuation only (owner, 2026-10-15): a NAME match, shown as one.
--                       candidates = how many organizations have that name; a name shared by more than 5 is not linked,
--                       unless they are a hospital (a 27/28 NUCC hospital or hospital unit taxonomy, or enrolled by CMS as a
--                       hospital) and its own NPIs: every other one shares the hospital's CCN or names the hospital, or its
--                       parent organization, as parent organization in NPPES. Then up to 50 (hospital_group = 1; owner,
--                       2026-10-15). grouped_by says why they belong together: 'ccn' (they share a CCN, grouped_value),
--                       'parent' (the same parent organization, grouped_value = its legal business name), 'parent_tin' (the
--                       same parent tax ID, not stored) or 'hospital' (some share its CCN, others name it; grouped_value =
--                       the hospital's legal name); NULL when none applies.
--                       Built by the loader with the directory tables (DirectoryBuilder), after each projection.
--   aco_snf_affiliate   the skilled nursing facilities the ACO works with under the 3-day rule waiver, by legal business name
--                       (no CCN in the file)
-- The files' contact people (names, e-mail addresses, phones) are not loaded.

CREATE TABLE IF NOT EXISTS `aco` (
  `aco_id` VARCHAR(10) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  `program_year` SMALLINT NOT NULL,
  `service_area` VARCHAR(200) NULL,
  `agreement_period` TINYINT NULL,
  `initial_start_date` DATE NULL,
  `current_start_date` DATE NULL,
  `track` VARCHAR(40) NULL,
  `high_revenue` TINYINT NULL,
  `address` VARCHAR(255) NULL,
  `website` VARCHAR(255) NULL,
  `lat` DOUBLE NULL,
  `lon` DOUBLE NULL,
  PRIMARY KEY (`aco_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `aco_participant` (
  `aco_id` VARCHAR(10) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  PRIMARY KEY (`aco_id`, `name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `aco_participant_npi` (
  `aco_id` VARCHAR(10) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  `npi` CHAR(10) NOT NULL,
  `candidates` TINYINT NOT NULL,
  `hospital_group` TINYINT NOT NULL DEFAULT 0,
  `grouped_by` VARCHAR(12) NULL,
  `grouped_value` VARCHAR(200) NULL,
  PRIMARY KEY (`aco_id`, `name`, `npi`),
  KEY `ix_aco_participant_npi_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `aco_snf_affiliate` (
  `aco_id` VARCHAR(10) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  PRIMARY KEY (`aco_id`, `name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
