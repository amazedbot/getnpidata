-- 030_cms_nursing_home.sql
--
-- Care Compare Nursing Home Provider Information, one row per CCN (CLAUDE.md §7 Stage 5.5 item 4a).
-- Star ratings 1-5 (NULL when not rated).

CREATE TABLE IF NOT EXISTS `cms_nursing_home` (
  `ccn` VARCHAR(10) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  `address` VARCHAR(200) NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  `zip` VARCHAR(10) NULL,
  `phone` VARCHAR(20) NULL,
  `provider_type` VARCHAR(50) NULL,
  `ownership` VARCHAR(100) NULL,
  `certified_beds` INT NULL,
  `overall_rating` TINYINT NULL,
  `inspection_rating` TINYINT NULL,
  `staffing_rating` TINYINT NULL,
  `quality_rating` TINYINT NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
