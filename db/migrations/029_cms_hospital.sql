-- 029_cms_hospital.sql
--
-- Care Compare Hospital General Information, one row per hospital CCN (CLAUDE.md §7 Stage 5.5 item 4a).
--   overall_rating  CMS overall star rating 1-5; NULL when "Not Available"

CREATE TABLE IF NOT EXISTS `cms_hospital` (
  `ccn` VARCHAR(10) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  `address` VARCHAR(200) NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  `zip` VARCHAR(10) NULL,
  `phone` VARCHAR(20) NULL,
  `hospital_type` VARCHAR(100) NULL,
  `ownership` VARCHAR(100) NULL,
  `emergency_services` TINYINT NULL,
  `overall_rating` TINYINT NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
