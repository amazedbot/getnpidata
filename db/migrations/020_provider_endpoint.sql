-- 020_provider_endpoint.sql
--
-- Projection: endpoints published in NPPES (from `endpoints`), for the detail page and the API
-- (CLAUDE.md §7 Stage 5.5 item 1).

CREATE TABLE IF NOT EXISTS `provider_endpoint` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `npi` CHAR(10) NOT NULL,
  `endpoint_type` VARCHAR(50) NULL,
  `endpoint_type_description` VARCHAR(200) NULL,
  `endpoint` TEXT NOT NULL,
  `endpoint_description` TEXT NULL,
  `use_description` VARCHAR(200) NULL,
  `content_description` VARCHAR(200) NULL,
  `affiliation_name` VARCHAR(200) NULL,
  `affiliation_city` VARCHAR(100) NULL,
  `affiliation_state` VARCHAR(100) NULL,
  PRIMARY KEY (`id`),
  KEY `ix_provider_endpoint_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
