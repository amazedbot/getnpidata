-- 114_hrsa_health_center_site.sql
--
-- HRSA Health Center Service Delivery and Look-Alike Sites (data.hrsa.gov data downloads; docs/PRODUCT_PLAN.md step 3.6
-- part 2): every active site of a federally funded health center (FQHC) or look-alike, replaced in full (staging + RENAME).
-- Administrative-only sites are left out.
--   site_id        HRSA's BPHC assigned number (BPS-H80-…)
--   grant_number   the health center's grant (H80…) or look-alike designation number: its sites share it
--   ccn            the site's Medicare billing number as a CMS Certification Number (6 characters; HRSA's column drops
--                  leading zeros, so 5 digits are padded), when given
--   npi            the site's NPI, when given
--   lat, lon       HRSA's geocoded point of the site

CREATE TABLE IF NOT EXISTS `hrsa_health_center_site` (
  `site_id` VARCHAR(20) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  `address` VARCHAR(200) NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  `zip5` CHAR(5) NULL,
  `phone` VARCHAR(20) NULL,
  `website` VARCHAR(255) NULL,
  `hours_per_week` DOUBLE NULL,
  `setting` VARCHAR(60) NULL,
  `location_type` VARCHAR(30) NULL,
  `site_type` VARCHAR(60) NULL,
  `look_alike` TINYINT NOT NULL,
  `grant_number` VARCHAR(20) NULL,
  `health_center` VARCHAR(200) NULL,
  `health_center_city` VARCHAR(100) NULL,
  `health_center_state` VARCHAR(2) NULL,
  `ccn` VARCHAR(10) NULL,
  `npi` CHAR(10) NULL,
  `added_date` DATE NULL,
  `county_fips` CHAR(5) NULL,
  `lat` DOUBLE NULL,
  `lon` DOUBLE NULL,
  PRIMARY KEY (`site_id`),
  KEY `ix_hrsa_site_ccn` (`ccn`),
  KEY `ix_hrsa_site_npi` (`npi`),
  KEY `ix_hrsa_site_grant` (`grant_number`),
  KEY `ix_hrsa_site_point` (`lat`, `lon`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
