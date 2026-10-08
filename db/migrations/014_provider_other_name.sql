-- 014_provider_other_name.sql
--
-- Other organization names (from other_names) for the provider detail page. Part of the projection
-- because the Azure database holds only the projection and reference tables (CLAUDE.md §6).

CREATE TABLE IF NOT EXISTS `provider_other_name` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `npi` CHAR(10) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  `type_code` VARCHAR(2) NULL,
  PRIMARY KEY (`id`),
  KEY `ix_provider_other_name_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
