-- 051_credentials.sql
--
-- Standardized credentials (CLAUDE.md §7 Stage 5.5 item 12). provider.credential is free text (173k distinct values for
-- 4.85M providers: "M.D.", "MD", "MD, PHD" …). Built by the loader (CredentialBuilder) after each projection:
--   credential_map       each distinct raw value → its standard credentials, in order ("M.D., PH.D." → MD, PhD)
--   provider_credential  NPI → standard credentials (display, and the credential filter by exact value)
--   credential_list      each standard credential and its active provider count (the search form's dropdown)

CREATE TABLE IF NOT EXISTS `credential_map` (
  `raw` VARCHAR(100) NOT NULL,
  `ord` TINYINT NOT NULL,
  `credential` VARCHAR(60) NOT NULL,
  PRIMARY KEY (`raw`, `ord`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `provider_credential` (
  `npi` CHAR(10) NOT NULL,
  `ord` TINYINT NOT NULL,
  `credential` VARCHAR(60) NOT NULL,
  PRIMARY KEY (`npi`, `ord`),
  KEY `ix_provider_credential_credential` (`credential`, `npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `credential_list` (
  `credential` VARCHAR(60) NOT NULL,
  `providers` INT NOT NULL,
  PRIMARY KEY (`credential`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
