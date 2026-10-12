-- 085_practice_signals.sql
--
-- Signs of current practice for the record-quality label (docs/PRODUCT_PLAN.md step 3.1, owner's OK 2026-10-12), from
-- data.cms.gov, each replaced in full by the loader (staging + RENAME):
--   cms_otp_provider        "Opioid Treatment Program Providers": certified opioid treatment programs, by NPI
--   medicare_revalidation   "Revalidation Due Date List": NPIs with a current Medicare enrollment and their next revalidation
--   medicare_dme_supplier   "Medicare Durable Medical Equipment, Devices & Supplies - by Supplier": suppliers that billed
--                           Medicare for equipment and supplies in the data year (the supplier directory is no longer published)

CREATE TABLE IF NOT EXISTS `cms_otp_provider` (
  `npi` CHAR(10) NOT NULL,
  `name` VARCHAR(150) NULL,
  `effective_date` DATE NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `medicare_revalidation` (
  `npi` CHAR(10) NOT NULL,
  `due_date` DATE NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `medicare_dme_supplier` (
  `npi` CHAR(10) NOT NULL,
  `data_year` SMALLINT NOT NULL,
  `beneficiaries` INT NULL,
  `claims` INT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The facility enrollments now combine six file names in their version (FQHC and RHC added).
ALTER TABLE `reference_data` MODIFY `version` VARCHAR(500) NOT NULL;
