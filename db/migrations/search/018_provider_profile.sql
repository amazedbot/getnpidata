-- 018_provider_profile.sql
--
-- Projection: NPPES details shown on the provider detail page but not searched (CLAUDE.md §7
-- Stage 5.5 item 1): sole proprietor / organization subpart flags, parent organization, the
-- organization's authorized official, and the mailing address. One row per provider.

CREATE TABLE IF NOT EXISTS `provider_profile` (
  `npi` CHAR(10) NOT NULL,
  `is_sole_proprietor` TINYINT NULL,
  `is_subpart` TINYINT NULL,
  `parent_org_name` VARCHAR(200) NULL,
  `official_prefix` VARCHAR(20) NULL,
  `official_first_name` VARCHAR(100) NULL,
  `official_middle_name` VARCHAR(100) NULL,
  `official_last_name` VARCHAR(100) NULL,
  `official_suffix` VARCHAR(20) NULL,
  `official_credential` VARCHAR(100) NULL,
  `official_title` VARCHAR(200) NULL,
  `official_phone` VARCHAR(20) NULL,
  `mailing_address1` VARCHAR(100) NULL,
  `mailing_address2` VARCHAR(100) NULL,
  `mailing_city` VARCHAR(100) NULL,
  `mailing_state` VARCHAR(100) NULL,
  `mailing_postal_code` VARCHAR(20) NULL,
  `mailing_country_code` VARCHAR(2) NULL,
  `mailing_phone` VARCHAR(20) NULL,
  `mailing_fax` VARCHAR(20) NULL,
  `practice_fax` VARCHAR(20) NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
