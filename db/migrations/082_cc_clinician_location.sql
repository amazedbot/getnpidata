-- 082_cc_clinician_location.sql
--
-- Record quality (docs/PRODUCT_PLAN.md step 3.1): the practice locations Medicare's Care Compare lists for each clinician,
-- one row per NPI and ZIP code (with a street address, city and state of that ZIP), summarized from cc_dac_raw by the
-- cc_clinicians load (staging + RENAME). Compared by ZIP code with provider_location: when Care Compare lists practices and
-- none shares a ZIP code with the NPPES record, the record is labelled "check".

CREATE TABLE IF NOT EXISTS `cc_clinician_location` (
  `npi` CHAR(10) NOT NULL,
  `zip5` CHAR(5) NOT NULL,
  `address1` VARCHAR(150) NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  PRIMARY KEY (`npi`, `zip5`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
