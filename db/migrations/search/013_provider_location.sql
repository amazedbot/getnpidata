-- 013_provider_location.sql
--
-- Practice locations searched by the site (CLAUDE.md §2): the primary practice address from npidata
-- (is_primary = 1) plus every secondary location from practice_locations. Not the mailing address.
-- zip5/zip4 are parsed from the raw postal code for US addresses; postal_code keeps the raw value.

CREATE TABLE IF NOT EXISTS `provider_location` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `npi` CHAR(10) NOT NULL,
  `is_primary` TINYINT NOT NULL,
  `address1` VARCHAR(100) NULL,
  `address2` VARCHAR(100) NULL,
  `city` VARCHAR(60) NULL,
  `state` VARCHAR(40) NULL,
  `zip5` CHAR(5) NULL,
  `zip4` CHAR(4) NULL,
  `postal_code` VARCHAR(20) NULL,
  `country_code` VARCHAR(2) NULL,
  `phone` VARCHAR(20) NULL,
  PRIMARY KEY (`id`),
  KEY `ix_provider_location_npi` (`npi`, `is_primary`),
  KEY `ix_provider_location_state_city` (`state`, `city`, `npi`),
  KEY `ix_provider_location_zip` (`zip5`, `npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
