-- 084_quality_search.sql
--
-- Record quality × practice location (docs/PRODUCT_PLAN.md step 3.1), like building_search for the practice building:
-- "stale records in CA" is one index range here, instead of checking the label of each of California's ~1.2M located
-- providers (30 s). Rebuilt with provider_quality.

CREATE TABLE IF NOT EXISTS `quality_search` (
  `label` TINYINT NOT NULL,
  `state` VARCHAR(40) NOT NULL DEFAULT '',
  `city` VARCHAR(60) NOT NULL DEFAULT '',
  `zip5` CHAR(5) NOT NULL DEFAULT '',
  `npi` CHAR(10) NOT NULL,
  PRIMARY KEY (`label`, `state`, `city`, `zip5`, `npi`),
  KEY `ix_quality_search_zip` (`label`, `zip5`, `npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
