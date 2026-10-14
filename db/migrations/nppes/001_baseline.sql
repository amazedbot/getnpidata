-- 001_baseline.sql
--
-- Schema of the owner's `workplace` database as of 2026-10-07 (MySQL 8.0.37), taken from the
-- per-table mysqldump backup (workplace.zip). Faithful copy: column types, keys, engines and
-- character sets are unchanged (including the V1 field widths and latin1 tables). Changes go in
-- later migrations; never edit this file.
--
-- Idempotent: CREATE TABLE IF NOT EXISTS, and AUTO_INCREMENT counters are omitted.
-- Local database only. The Azure database gets only the projection/reference tables (CLAUDE.md §6).

SET NAMES utf8mb4;

-- `npidata` (330 short V1 varchar columns, latin1) exceeds InnoDB's worst-case row size of 8126
-- bytes, so MySQL 8.4 with its default innodb_strict_mode=ON rejects the CREATE TABLE
-- (ERROR 1118). The owner's 8.0.37 server evidently ran with strict mode off, which makes it a
-- warning. Actual rows are far smaller, so they fit. Needs SESSION_VARIABLES_ADMIN. Stage 1
-- replaces this table design, so later migrations must not rely on this.
SET SESSION innodb_strict_mode = OFF;

CREATE TABLE IF NOT EXISTS `animals` (
  `id` mediumint NOT NULL AUTO_INCREMENT,
  `name` char(30) NOT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `country_codes` (
  `country_code` char(2) NOT NULL,
  `country_name` varchar(75) NOT NULL,
  PRIMARY KEY (`country_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `dicomhosts` (
  `ID` int NOT NULL AUTO_INCREMENT,
  `DicomAEName` varchar(100) DEFAULT NULL,
  `DicomHostName` varchar(100) DEFAULT NULL,
  `DicomPort` int DEFAULT NULL,
  PRIMARY KEY (`ID`)
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

CREATE TABLE IF NOT EXISTS `downlog` (
  `id` int NOT NULL AUTO_INCREMENT,
  `filename` varchar(500) NOT NULL,
  `received_date` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

CREATE TABLE IF NOT EXISTS `entity_types` (
  `entity_type_code` smallint NOT NULL,
  `entity_type_description` varchar(255) NOT NULL,
  PRIMARY KEY (`entity_type_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `extractlog` (
  `id` mediumint unsigned NOT NULL AUTO_INCREMENT,
  `ZipFileName` varchar(256) NOT NULL,
  `ExtractFileName` varchar(256) NOT NULL,
  `extract_date` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `gender_codes` (
  `gender_code` char(1) NOT NULL,
  `description` varchar(50) NOT NULL,
  PRIMARY KEY (`gender_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `npidata` (
  `NPI` char(10) NOT NULL,
  `Entity_Type_Code` varchar(1) DEFAULT NULL,
  `Replacement_NPI` varchar(10) DEFAULT NULL,
  `Employer_Identification_Number_EIN` varchar(9) DEFAULT NULL,
  `Provider_Organization_Name_Legal_Business_Name` varchar(70) DEFAULT NULL,
  `Provider_Last_Name_Legal_Name` varchar(35) DEFAULT NULL,
  `Provider_First_Name` varchar(20) DEFAULT NULL,
  `Provider_Middle_Name` varchar(20) DEFAULT NULL,
  `Provider_Name_Prefix_Text` varchar(5) DEFAULT NULL,
  `Provider_Name_Suffix_Text` varchar(5) DEFAULT NULL,
  `Provider_Credential_Text` varchar(20) DEFAULT NULL,
  `Provider_Other_Organization_Name` varchar(70) DEFAULT NULL,
  `Provider_Other_Organization_Name_Type_Code` varchar(1) DEFAULT NULL,
  `Provider_Other_Last_Name` varchar(35) DEFAULT NULL,
  `Provider_Other_First_Name` varchar(20) DEFAULT NULL,
  `Provider_Other_Middle_Name` varchar(20) DEFAULT NULL,
  `Provider_Other_Name_Prefix_Text` varchar(5) DEFAULT NULL,
  `Provider_Other_Name_Suffix_Text` varchar(5) DEFAULT NULL,
  `Provider_Other_Credential_Text` varchar(20) DEFAULT NULL,
  `Provider_Other_Last_Name_Type_Code` varchar(1) DEFAULT NULL,
  `Provider_First_Line_Business_Mailing_Address` varchar(55) DEFAULT NULL,
  `Provider_Second_Line_Business_Mailing_Address` varchar(55) DEFAULT NULL,
  `Provider_Business_Mailing_Address_City_Name` varchar(40) DEFAULT NULL,
  `Provider_Business_Mailing_Address_State_Name` varchar(40) DEFAULT NULL,
  `Provider_Business_Mailing_Address_Postal_Code` varchar(20) DEFAULT NULL,
  `Provider_Business_Mailing_Address_Country_Code` varchar(2) DEFAULT NULL,
  `Provider_Business_Mailing_Address_Telephone_Number` varchar(20) DEFAULT NULL,
  `Provider_Business_Mailing_Address_Fax_Number` varchar(20) DEFAULT NULL,
  `Provider_First_Line_Business_Practice_Location_Address` varchar(55) DEFAULT NULL,
  `Provider_Second_Line_Business_Practice_Location_Address` varchar(55) DEFAULT NULL,
  `Provider_Business_Practice_Location_Address_City_Name` varchar(40) DEFAULT NULL,
  `Provider_Business_Practice_Location_Address_State_Name` varchar(40) DEFAULT NULL,
  `Provider_Business_Practice_Location_Address_Postal_Code` varchar(20) DEFAULT NULL,
  `Provider_Business_Practice_Location_Address_Country_Code` varchar(50) DEFAULT NULL,
  `Provider_Business_Practice_Location_Address_Telephone_Number` varchar(20) DEFAULT NULL,
  `Provider_Business_Practice_Location_Address_Fax_Number` varchar(20) DEFAULT NULL,
  `Provider_Enumeration_Date` varchar(10) DEFAULT NULL,
  `Last_Update_Date` varchar(10) DEFAULT NULL,
  `NPI_Deactivation_Reason_Code` varchar(2) DEFAULT NULL,
  `NPI_Deactivation_Date` varchar(10) DEFAULT NULL,
  `NPI_Reactivation_Date` varchar(10) DEFAULT NULL,
  `Provider_Gender_Code` varchar(10) DEFAULT NULL,
  `Authorized_Official_Last_Name` varchar(35) DEFAULT NULL,
  `Authorized_Official_First_Name` varchar(20) DEFAULT NULL,
  `Authorized_Official_Middle_Name` varchar(20) DEFAULT NULL,
  `Authorized_Official_Title_or_Position` varchar(35) DEFAULT NULL,
  `Authorized_Official_Telephone_Number` varchar(20) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Code_1` varchar(10) DEFAULT NULL,
  `Provider_License_Number_1` varchar(20) DEFAULT NULL,
  `Provider_License_Number_State_Code_1` varchar(2) DEFAULT NULL,
  `Healthcare_Provider_Primary_Taxonomy_Switch_1` varchar(10) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Code_2` varchar(10) DEFAULT NULL,
  `Provider_License_Number_2` varchar(20) DEFAULT NULL,
  `Provider_License_Number_State_Code_2` varchar(2) DEFAULT NULL,
  `Healthcare_Provider_Primary_Taxonomy_Switch_2` varchar(10) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Code_3` varchar(10) DEFAULT NULL,
  `Provider_License_Number_3` varchar(20) DEFAULT NULL,
  `Provider_License_Number_State_Code_3` varchar(2) DEFAULT NULL,
  `Healthcare_Provider_Primary_Taxonomy_Switch_3` varchar(10) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Code_4` varchar(10) DEFAULT NULL,
  `Provider_License_Number_4` varchar(20) DEFAULT NULL,
  `Provider_License_Number_State_Code_4` varchar(2) DEFAULT NULL,
  `Healthcare_Provider_Primary_Taxonomy_Switch_4` varchar(10) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Code_5` varchar(10) DEFAULT NULL,
  `Provider_License_Number_5` varchar(20) DEFAULT NULL,
  `Provider_License_Number_State_Code_5` varchar(2) DEFAULT NULL,
  `Healthcare_Provider_Primary_Taxonomy_Switch_5` varchar(10) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Code_6` varchar(10) DEFAULT NULL,
  `Provider_License_Number_6` varchar(20) DEFAULT NULL,
  `Provider_License_Number_State_Code_6` varchar(2) DEFAULT NULL,
  `Healthcare_Provider_Primary_Taxonomy_Switch_6` varchar(10) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Code_7` varchar(10) DEFAULT NULL,
  `Provider_License_Number_7` varchar(20) DEFAULT NULL,
  `Provider_License_Number_State_Code_7` varchar(2) DEFAULT NULL,
  `Healthcare_Provider_Primary_Taxonomy_Switch_7` varchar(10) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Code_8` varchar(10) DEFAULT NULL,
  `Provider_License_Number_8` varchar(20) DEFAULT NULL,
  `Provider_License_Number_State_Code_8` varchar(2) DEFAULT NULL,
  `Healthcare_Provider_Primary_Taxonomy_Switch_8` varchar(10) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Code_9` varchar(10) DEFAULT NULL,
  `Provider_License_Number_9` varchar(20) DEFAULT NULL,
  `Provider_License_Number_State_Code_9` varchar(2) DEFAULT NULL,
  `Healthcare_Provider_Primary_Taxonomy_Switch_9` varchar(10) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Code_10` varchar(10) DEFAULT NULL,
  `Provider_License_Number_10` varchar(20) DEFAULT NULL,
  `Provider_License_Number_State_Code_10` varchar(2) DEFAULT NULL,
  `Healthcare_Provider_Primary_Taxonomy_Switch_10` varchar(10) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Code_11` varchar(10) DEFAULT NULL,
  `Provider_License_Number_11` varchar(20) DEFAULT NULL,
  `Provider_License_Number_State_Code_11` varchar(2) DEFAULT NULL,
  `Healthcare_Provider_Primary_Taxonomy_Switch_11` varchar(10) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Code_12` varchar(10) DEFAULT NULL,
  `Provider_License_Number_12` varchar(20) DEFAULT NULL,
  `Provider_License_Number_State_Code_12` varchar(2) DEFAULT NULL,
  `Healthcare_Provider_Primary_Taxonomy_Switch_12` varchar(10) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Code_13` varchar(10) DEFAULT NULL,
  `Provider_License_Number_13` varchar(20) DEFAULT NULL,
  `Provider_License_Number_State_Code_13` varchar(2) DEFAULT NULL,
  `Healthcare_Provider_Primary_Taxonomy_Switch_13` varchar(10) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Code_14` varchar(10) DEFAULT NULL,
  `Provider_License_Number_14` varchar(20) DEFAULT NULL,
  `Provider_License_Number_State_Code_14` varchar(2) DEFAULT NULL,
  `Healthcare_Provider_Primary_Taxonomy_Switch_14` varchar(10) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Code_15` varchar(10) DEFAULT NULL,
  `Provider_License_Number_15` varchar(20) DEFAULT NULL,
  `Provider_License_Number_State_Code_15` varchar(2) DEFAULT NULL,
  `Healthcare_Provider_Primary_Taxonomy_Switch_15` varchar(10) DEFAULT NULL,
  `Other_Provider_Identifier_1` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_1` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_1` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_1` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_2` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_2` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_2` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_2` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_3` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_3` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_3` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_3` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_4` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_4` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_4` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_4` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_5` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_5` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_5` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_5` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_6` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_6` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_6` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_6` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_7` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_7` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_7` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_7` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_8` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_8` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_8` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_8` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_9` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_9` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_9` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_9` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_10` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_10` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_10` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_10` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_11` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_11` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_11` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_11` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_12` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_12` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_12` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_12` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_13` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_13` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_13` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_13` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_14` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_14` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_14` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_14` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_15` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_15` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_15` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_15` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_16` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_16` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_16` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_16` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_17` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_17` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_17` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_17` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_18` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_18` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_18` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_18` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_19` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_19` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_19` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_19` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_20` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_20` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_20` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_20` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_21` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_21` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_21` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_21` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_22` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_22` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_22` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_22` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_23` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_23` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_23` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_23` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_24` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_24` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_24` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_24` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_25` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_25` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_25` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_25` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_26` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_26` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_26` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_26` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_27` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_27` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_27` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_27` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_28` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_28` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_28` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_28` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_29` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_29` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_29` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_29` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_30` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_30` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_30` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_30` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_31` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_31` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_31` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_31` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_32` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_32` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_32` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_32` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_33` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_33` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_33` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_33` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_34` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_34` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_34` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_34` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_35` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_35` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_35` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_35` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_36` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_36` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_36` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_36` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_37` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_37` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_37` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_37` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_38` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_38` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_38` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_38` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_39` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_39` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_39` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_39` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_40` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_40` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_40` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_40` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_41` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_41` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_41` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_41` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_42` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_42` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_42` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_42` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_43` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_43` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_43` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_43` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_44` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_44` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_44` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_44` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_45` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_45` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_45` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_45` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_46` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_46` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_46` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_46` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_47` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_47` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_47` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_47` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_48` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_48` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_48` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_48` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_49` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_49` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_49` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_49` varchar(80) DEFAULT NULL,
  `Other_Provider_Identifier_50` varchar(20) DEFAULT NULL,
  `Other_Provider_Identifier_Type_Code_50` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_State_50` varchar(2) DEFAULT NULL,
  `Other_Provider_Identifier_Issuer_50` varchar(80) DEFAULT NULL,
  `Is_Sole_Proprietor` varchar(1) DEFAULT NULL,
  `Is_Organization_Subpart` varchar(1) DEFAULT NULL,
  `Parent_Organization_LBN` varchar(70) DEFAULT NULL,
  `Parent_Organization_TIN` varchar(10) DEFAULT NULL,
  `Authorized_Official_Name_Prefix_Text` varchar(5) DEFAULT NULL,
  `Authorized_Official_Name_Suffix_Text` varchar(5) DEFAULT NULL,
  `Authorized_Official_Credential_Text` varchar(255) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Group_1` varchar(60) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Group_2` varchar(60) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Group_3` varchar(60) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Group_4` varchar(60) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Group_5` varchar(60) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Group_6` varchar(60) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Group_7` varchar(60) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Group_8` varchar(60) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Group_9` varchar(60) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Group_10` varchar(60) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Group_11` varchar(60) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Group_12` varchar(60) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Group_13` varchar(60) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Group_14` varchar(60) DEFAULT NULL,
  `Healthcare_Provider_Taxonomy_Group_15` varchar(60) DEFAULT NULL,
  `Certification_Date` varchar(10) DEFAULT NULL,
  PRIMARY KEY (`NPI`),
  KEY `IdxEntityTypeCode` (`Entity_Type_Code`),
  KEY `npidata_taxonomy_code_1` (`Healthcare_Provider_Taxonomy_Code_1`),
  KEY `npidata_Provider_Last_Name_Legal_Name` (`Provider_Last_Name_Legal_Name`),
  KEY `npidata_Provider_First_Name` (`Provider_First_Name`),
  KEY `npidata_Provider_Business_Mailing_Address_State_Name` (`Provider_Business_Mailing_Address_State_Name`),
  KEY `npidata_Healthcare_Provider_Taxonomy_Code_2` (`Healthcare_Provider_Taxonomy_Code_2`),
  KEY `npidata_Provider_Business_Mailing_Address_Country_Code` (`Provider_Business_Mailing_Address_Country_Code`)
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

CREATE TABLE IF NOT EXISTS `nppes_deactivated_npi_report` (
  `NPI` char(10) NOT NULL,
  `NPPES_Deactivation_Date` date DEFAULT NULL,
  PRIMARY KEY (`NPI`)
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

CREATE TABLE IF NOT EXISTS `other_names` (
  `ID` int unsigned NOT NULL AUTO_INCREMENT,
  `NPI` char(10) DEFAULT NULL,
  `Provider_Other_Organization_Name` varchar(100) DEFAULT NULL,
  `Provider_Other_Organization_Name_Type_Code` char(1) DEFAULT NULL,
  `Created_Date` date DEFAULT NULL,
  PRIMARY KEY (`ID`),
  KEY `idx_other_names_NPI` (`NPI`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Temporary holding table for other names';

CREATE TABLE IF NOT EXISTS `other_names_temp` (
  `NPI` char(10) DEFAULT NULL,
  `Provider_Other_Organization_Name` varchar(100) DEFAULT NULL,
  `Provider_Other_Organization_Name_Type_Code` char(1) DEFAULT NULL,
  `Created_Date` date DEFAULT NULL,
  KEY `idx_other_names_NPI` (`NPI`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Temporary holding table for other names';

CREATE TABLE IF NOT EXISTS `other_provider_identifier_issuer_codes` (
  `other_provider_type_code` char(2) NOT NULL,
  `other_provider_type_description` varchar(10) NOT NULL,
  PRIMARY KEY (`other_provider_type_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `other_provider_name_type_codes` (
  `name_type_code` smallint NOT NULL,
  `name_type_description` varchar(50) NOT NULL,
  `entity_name_type_code` varchar(25) NOT NULL,
  PRIMARY KEY (`name_type_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `practice_locations` (
  `ID` int unsigned NOT NULL AUTO_INCREMENT,
  `NPI` char(10) NOT NULL,
  `Provider_Secondary_Practice_Location_Address_Line_1` varchar(55) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Line_2` varchar(55) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_City_Name` varchar(40) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_State_Name` varchar(40) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Postal_Code` varchar(20) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Country_Code` varchar(2) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Telephone_Number` varchar(20) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Telephone_Extension` varchar(5) DEFAULT NULL,
  `Provider_Practice_Location_Address_Fax_Number` varchar(20) DEFAULT NULL,
  PRIMARY KEY (`ID`),
  KEY `idx_NPI` (`NPI`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `practice_locations_temp` (
  `NPI` char(10) NOT NULL,
  `Provider_Secondary_Practice_Location_Address_Line_1` varchar(55) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Line_2` varchar(55) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_City_Name` varchar(40) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_State_Name` varchar(40) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Postal_Code` varchar(20) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Country_Code` varchar(2) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Telephone_Number` varchar(20) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Telephone_Extension` varchar(5) DEFAULT NULL,
  `Provider_Practice_Location_Address_Fax_Number` varchar(20) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `sole_proprietor_codes` (
  `sole_proprietor_code` char(1) NOT NULL,
  `sole_proprietor_description` varchar(100) DEFAULT NULL,
  PRIMARY KEY (`sole_proprietor_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `state_codes` (
  `state_reference_code` char(2) NOT NULL,
  `state_name` varchar(50) NOT NULL,
  `state_type_code` char(1) NOT NULL,
  PRIMARY KEY (`state_reference_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `statelookup` (
  `State` char(2) NOT NULL DEFAULT '',
  `StateName` varchar(32) DEFAULT NULL,
  PRIMARY KEY (`State`) USING BTREE
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

CREATE TABLE IF NOT EXISTS `subpart_codes` (
  `subpart_code` char(1) NOT NULL,
  `subpart_description` varchar(100) DEFAULT NULL,
  PRIMARY KEY (`subpart_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `taxonomy_codes` (
  `Taxonomy_Code` varchar(10) NOT NULL,
  `Grouping` varchar(100) DEFAULT NULL,
  `Classification` varchar(150) DEFAULT NULL,
  `Specialization` varchar(75) DEFAULT NULL,
  `Definition` varchar(4000) DEFAULT NULL,
  `Notes` varchar(1600) DEFAULT NULL,
  `Display_Name` varchar(150) DEFAULT NULL,
  `Section` varchar(20) DEFAULT NULL,
  PRIMARY KEY (`Taxonomy_Code`)
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

CREATE TABLE IF NOT EXISTS `taxonomy_codes_old` (
  `Taxonomy_Code` varchar(10) NOT NULL,
  `Grouping` varchar(100) DEFAULT NULL,
  `Classification` varchar(150) DEFAULT NULL,
  `Specialization` varchar(75) DEFAULT NULL,
  `Definition` varchar(4000) DEFAULT NULL,
  `Effective_Date` date DEFAULT NULL,
  `Deactivation_Date` date DEFAULT NULL,
  `Last_Modified_Date` date DEFAULT NULL,
  `Notes` varchar(1600) DEFAULT NULL,
  `Display_Name` varchar(150) DEFAULT NULL,
  PRIMARY KEY (`Taxonomy_Code`)
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

CREATE TABLE IF NOT EXISTS `us_city_populations` (
  `State` varchar(50) NOT NULL DEFAULT '',
  `City` varchar(50) NOT NULL DEFAULT '',
  `population` int DEFAULT NULL,
  PRIMARY KEY (`State`,`City`) USING BTREE
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

