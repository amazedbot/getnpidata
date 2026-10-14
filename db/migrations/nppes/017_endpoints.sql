-- 017_endpoints.sql
--
-- Raw NPPES endpoints (endpoint_pfile in every data zip): Direct messaging addresses, FHIR and other
-- URLs a provider publishes (CLAUDE.md §7 Stage 5.5 item 1). Loaded like other_names: the whole table
-- is replaced by each monthly file, and a weekly file replaces the rows of the NPIs it contains.
-- Column names follow the header rule in CLAUDE.md §4; all text, empty → NULL.

CREATE TABLE IF NOT EXISTS `endpoints` (
  `ID` INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `NPI` CHAR(10) NOT NULL,
  `Endpoint_Type` TEXT NULL,
  `Endpoint_Type_Description` TEXT NULL,
  `Endpoint` TEXT NULL,
  `Affiliation` TEXT NULL,
  `Endpoint_Description` TEXT NULL,
  `Affiliation_Legal_Business_Name` TEXT NULL,
  `Use_Code` TEXT NULL,
  `Use_Description` TEXT NULL,
  `Other_Use_Description` TEXT NULL,
  `Content_Type` TEXT NULL,
  `Content_Description` TEXT NULL,
  `Other_Content_Description` TEXT NULL,
  `Affiliation_Address_Line_One` TEXT NULL,
  `Affiliation_Address_Line_Two` TEXT NULL,
  `Affiliation_Address_City` TEXT NULL,
  `Affiliation_Address_State` TEXT NULL,
  `Affiliation_Address_Country` TEXT NULL,
  `Affiliation_Address_Postal_Code` TEXT NULL,
  PRIMARY KEY (`ID`),
  KEY `ix_endpoints_npi` (`NPI`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
