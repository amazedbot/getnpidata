-- 116_hrsa_340b.sql
--
-- HRSA 340B Drug Pricing Program covered entities (docs/PRODUCT_PLAN.md step 3.6 part 2), from the OPAIS "Covered Entity Daily
-- Export" (JSON, downloaded as a zip) or "Covered Entity Daily Report" (Excel), which the operator downloads into the 340B folder: OPAIS has no
-- bulk download URL or API. Replaced together (staging + RENAME). Contacts, billing addresses, shipping addresses and contract
-- pharmacies are not loaded.
--   hrsa_340b_entity   one row per 340B ID (e.g. DSH050001, CH12345): entity type code, participating now, start and
--                      termination dates, name and sub-division, grant number, HRSA site ID (health center sites), the
--                      Medicare provider number as a CCN (hospitals), the street address, and the registration a child
--                      site is registered under (parent_id_340b: a hospital's outpatient site, a grantee's clinic)
--   hrsa_340b_npi      the NPIs the export lists for each 340B ID

CREATE TABLE IF NOT EXISTS `hrsa_340b_entity` (
  `id_340b` VARCHAR(20) NOT NULL,
  `ce_id` INT NULL,
  `entity_type` VARCHAR(10) NOT NULL,
  `participating` TINYINT NOT NULL,
  `start_date` DATE NULL,
  `termination_date` DATE NULL,
  `termination_reason` VARCHAR(200) NULL,
  `name` VARCHAR(200) NOT NULL,
  `sub_name` VARCHAR(255) NULL,
  `grant_number` VARCHAR(30) NULL,
  `site_id` VARCHAR(20) NULL,
  `ccn` VARCHAR(10) NULL,
  `address` VARCHAR(255) NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  `zip5` CHAR(5) NULL,
  `rural` TINYINT NULL,
  `parent_id_340b` VARCHAR(20) NULL,
  PRIMARY KEY (`id_340b`),
  KEY `ix_340b_ccn` (`ccn`),
  KEY `ix_340b_site` (`site_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `hrsa_340b_npi` (
  `id_340b` VARCHAR(20) NOT NULL,
  `npi` CHAR(10) NOT NULL,
  PRIMARY KEY (`id_340b`, `npi`),
  KEY `ix_340b_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
