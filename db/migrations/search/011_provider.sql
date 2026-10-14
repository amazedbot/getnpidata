-- 011_provider.sql
--
-- Search projection: one row per active NPI (CLAUDE.md §6.2). Built by `Npi.Loader project` from
-- npidata (deactivated NPIs excluded) and published to Azure. utf8mb4_0900_ai_ci makes name
-- matching case- and accent-insensitive.
--   sort_name      "LAST, FIRST MIDDLE" for individuals, the organization name for organizations
--   credential_key credential upper-cased with everything but letters/digits removed ("M.D." → "MD")
--   row_hash       MD5 over the provider and all its child rows; the publisher diffs on it

CREATE TABLE IF NOT EXISTS `provider` (
  `npi` CHAR(10) NOT NULL,
  `entity_type` TINYINT NOT NULL,
  `last_name` VARCHAR(100) NULL,
  `first_name` VARCHAR(100) NULL,
  `middle_name` VARCHAR(100) NULL,
  `name_prefix` VARCHAR(20) NULL,
  `name_suffix` VARCHAR(20) NULL,
  `credential` VARCHAR(100) NULL,
  `credential_key` VARCHAR(100) NULL,
  `org_name` VARCHAR(200) NULL,
  `sort_name` VARCHAR(255) NOT NULL,
  `gender` CHAR(1) NULL,
  `primary_taxonomy_code` VARCHAR(10) NULL,
  `phone` VARCHAR(20) NULL,
  `enumeration_date` DATE NULL,
  `last_update_date` DATE NULL,
  `row_hash` BINARY(16) NULL,
  PRIMARY KEY (`npi`),
  KEY `ix_provider_name` (`last_name`, `first_name`),
  KEY `ix_provider_org` (`org_name`),
  KEY `ix_provider_credential` (`credential_key`),
  KEY `ix_provider_sort` (`sort_name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
