-- 019_provider_identifier.sql
--
-- Projection: the provider's other identifiers from npidata's 50 Other_Provider_Identifier slots
-- (Medicaid numbers, legacy Medicare IDs, …; CLAUDE.md §7 Stage 5.5 item 1). Blank slots skipped.
--   type_code  NPPES code: 01 Other, 02 Medicare UPIN, 04 Medicare ID (type unspecified),
--              05 Medicaid, 06 Medicare OSCAR/Certification, 07 Medicare NSC, 08 Medicare PIN

CREATE TABLE IF NOT EXISTS `provider_identifier` (
  `npi` CHAR(10) NOT NULL,
  `slot` TINYINT NOT NULL,
  `identifier` VARCHAR(100) NOT NULL,
  `type_code` VARCHAR(2) NULL,
  `state` VARCHAR(2) NULL,
  `issuer` VARCHAR(200) NULL,
  PRIMARY KEY (`npi`, `slot`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
