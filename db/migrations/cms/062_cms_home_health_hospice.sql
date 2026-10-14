-- 062_cms_home_health_hospice.sql
--
-- Home health agencies and hospices (CLAUDE.md §7 Stage 5.5 item 14), one row per CCN, linked to organization NPIs
-- through cms_facility_npi (kinds home_health, hospice, from CMS's enrollment files). Home health: Care Compare's
-- quality of patient care star rating (1-5 in half stars). Hospice: the CAHPS Hospice family caregiver survey
-- summary star rating. `cms_hospice_cahps_raw` is the empty template for the survey load.

CREATE TABLE IF NOT EXISTS `cms_home_health` (
  `ccn` VARCHAR(10) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  `address` VARCHAR(200) NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  `zip` VARCHAR(10) NULL,
  `phone` VARCHAR(20) NULL,
  `ownership` VARCHAR(100) NULL,
  `quality_rating` DOUBLE NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `cms_hospice` (
  `ccn` VARCHAR(10) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  `address` VARCHAR(200) NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  `zip` VARCHAR(10) NULL,
  `phone` VARCHAR(20) NULL,
  `ownership` VARCHAR(100) NULL,
  `family_rating` TINYINT NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `cms_hospice_cahps_raw` (
  `ccn` VARCHAR(10) NULL,
  `measure_code` VARCHAR(60) NULL,
  `star_rating` TINYINT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
