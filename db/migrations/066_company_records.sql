-- 066_company_records.sql
--
-- Public records about the companies on the company pages (CLAUDE.md §7 Stage 5.5 item 17, extras): FDA recalls
-- (openFDA drug and device enforcement reports), SEC EDGAR registrants (public companies: CIK, ticker, exchange) and
-- HHS-OIG Corporate Integrity Agreements. None of them carries the Open Payments ID, so each is matched by a name key
-- (`Npi.Core.Search.CompanyNames.Key`: upper-case words without legal suffixes such as INC or LLC, sorted), computed
-- by the loader for both sides; `op_company_key` holds the keys of each company's name and other names. The match is
-- made when a page is read, so a reload of either side needs nothing else.

CREATE TABLE IF NOT EXISTS `op_company_key` (
  `company_id` VARCHAR(20) NOT NULL,
  `name_key` VARCHAR(255) NOT NULL,
  PRIMARY KEY (`company_id`, `name_key`),
  KEY `ix_op_company_key_key` (`name_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Every openFDA drug and device enforcement report (recalls classified I–III, since 2004).
CREATE TABLE IF NOT EXISTS `fda_enforcement` (
  `recall_number` VARCHAR(30) NOT NULL,
  `product_type` VARCHAR(20) NOT NULL,
  `firm` VARCHAR(255) NULL,
  `firm_key` VARCHAR(255) NULL,
  `classification` VARCHAR(30) NULL,
  `status` VARCHAR(30) NULL,
  `initiation_date` DATE NULL,
  `report_date` DATE NULL,
  `voluntary_mandated` VARCHAR(60) NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(40) NULL,
  `country` VARCHAR(100) NULL,
  `product_description` VARCHAR(1000) NULL,
  `reason` VARCHAR(1000) NULL,
  PRIMARY KEY (`recall_number`, `product_type`),
  KEY `ix_fda_enforcement_firm_key` (`firm_key`, `initiation_date`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- SEC EDGAR registrants with a ticker (company_tickers_exchange.json).
CREATE TABLE IF NOT EXISTS `sec_company` (
  `cik` INT NOT NULL,
  `ticker` VARCHAR(20) NOT NULL,
  `name` VARCHAR(255) NOT NULL,
  `name_key` VARCHAR(255) NULL,
  `exchange` VARCHAR(40) NULL,
  PRIMARY KEY (`cik`, `ticker`),
  KEY `ix_sec_company_key` (`name_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- HHS-OIG Corporate Integrity Agreements (and other integrity agreements), one row per agreement; the entities it
-- names (split at ";") each get a key.
CREATE TABLE IF NOT EXISTS `oig_cia` (
  `slug` VARCHAR(200) NOT NULL,
  `name` VARCHAR(2000) NOT NULL,
  `location` VARCHAR(200) NULL,
  `agreement_type` VARCHAR(100) NULL,
  `status` VARCHAR(40) NULL,
  `status_date` DATE NULL,
  `url` VARCHAR(400) NOT NULL,
  PRIMARY KEY (`slug`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `oig_cia_entity` (
  `slug` VARCHAR(200) NOT NULL,
  `name_key` VARCHAR(255) NOT NULL,
  PRIMARY KEY (`slug`, `name_key`),
  KEY `ix_oig_cia_entity_key` (`name_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
