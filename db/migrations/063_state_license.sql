-- 063_state_license.sql
--
-- State license status and board actions (CLAUDE.md §7 Stage 5.5 item 15), from state open-data portals that publish
-- them for reuse: NY Board for Professional Medical Conduct actions; TX Medical Board, WA Department of Health, IL IDFPR
-- and CO DORA license records (status, expiration, discipline). `state_license_raw` is the empty template the load
-- fills with every state's rows; only rows matched to an NPI (license state + number + last name, the NPPES taxonomy
-- licenses) are kept, in `provider_state_license`. license_key = the number's digits without leading zeros, so
-- "DR.0042345", "DR0042345" and "42345" compare equal.

CREATE TABLE IF NOT EXISTS `state_license_raw` (
  `state` CHAR(2) NOT NULL,
  `kind` VARCHAR(10) NOT NULL,
  `source` VARCHAR(20) NOT NULL,
  `license_number` VARCHAR(60) NULL,
  `license_key` VARCHAR(60) GENERATED ALWAYS AS (NULLIF(TRIM(LEADING '0' FROM REGEXP_REPLACE(COALESCE(`license_number`, ''), '[^0-9]', '')), '')) STORED,
  `last_name` VARCHAR(200) NULL,
  `first_name` VARCHAR(200) NULL,
  `license_type` VARCHAR(300) NULL,
  `status` VARCHAR(300) NULL,
  `expiration_date` DATE NULL,
  `discipline` VARCHAR(300) NULL,
  `action_date` DATE NULL,
  `action` TEXT NULL,
  `description` TEXT NULL,
  `verify_url` VARCHAR(1000) NULL,
  KEY `ix_state_license_raw_key` (`state`, `license_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `provider_state_license` (
  `id` BIGINT NOT NULL AUTO_INCREMENT,
  `npi` CHAR(10) NOT NULL,
  `state` CHAR(2) NOT NULL,
  `kind` VARCHAR(10) NOT NULL,
  `source` VARCHAR(20) NOT NULL,
  `license_number` VARCHAR(60) NULL,
  `license_type` VARCHAR(300) NULL,
  `status` VARCHAR(300) NULL,
  `expiration_date` DATE NULL,
  `discipline` VARCHAR(300) NULL,
  `action_date` DATE NULL,
  `action` TEXT NULL,
  `description` TEXT NULL,
  `verify_url` VARCHAR(1000) NULL,
  PRIMARY KEY (`id`),
  KEY `ix_provider_state_license_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
