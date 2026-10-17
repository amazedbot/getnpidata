-- 119_medicaid_tmsis.sql
--
-- Medicaid in every state from T-MSIS, the data states send CMS (docs/PRODUCT_PLAN.md step 3.7; owner's OK 2026-10-15, go
-- 2026-10-17), both from HHS's open-data platform (opendata.hhs.gov), summarized per NPI by the loader and replaced in full
-- (staging + RENAME):
--   medicaid_enrollment_period  "Medicaid Provider Enrollment Segments" (one row per provider, state and enrollment segment,
--                               January 2018 to December 2024): the NPI's enrollment periods in each state, its active segments
--                               joined where they touch or a month or less apart (period 1 = the first); enrolled_from NULL = the
--                               state gave no real start date (1900-01-01 and the like), enrolled_until NULL = still enrolled at the
--                               end of the data; ended_status = the termination that ended the period, in T-MSIS's words
--                               ("TERM - LICENSE EXPIRED"), when one follows it
--   medicaid_billing_year       "Medicaid Provider Spending by HCPCS" (billing NPI x servicing NPI x procedure x month, 2018-2024;
--                               cells under 12 claim lines or 12 patients left out by HHS): per NPI and calendar year, the claim
--                               lines and the amount Medicaid paid, as billing and as servicing provider (a cell with the NPI as
--                               both counts once in the totals)
--   medicaid_top_procedure      each NPI's five procedures Medicaid paid most for in the newest year, with the description
--                               Medicare's files give the code, where they have one
--   medicaid_data_period        the months each of the two files covers, for the pages ("through December 2024") and the
--                               two-year rule of the record-quality signs

CREATE TABLE IF NOT EXISTS `medicaid_enrollment_period` (
  `npi` CHAR(10) NOT NULL,
  `state` CHAR(2) NOT NULL,
  `period` SMALLINT NOT NULL,
  `enrolled_from` DATE NULL,
  `enrolled_until` DATE NULL,
  `ended_status` VARCHAR(80) NULL,
  PRIMARY KEY (`npi`, `state`, `period`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `medicaid_billing_year` (
  `npi` CHAR(10) NOT NULL,
  `data_year` SMALLINT NOT NULL,
  `claim_lines` BIGINT NOT NULL,
  `paid` DECIMAL(15,2) NOT NULL,
  `billing_lines` BIGINT NOT NULL,
  `billing_paid` DECIMAL(15,2) NOT NULL,
  `servicing_lines` BIGINT NOT NULL,
  `servicing_paid` DECIMAL(15,2) NOT NULL,
  PRIMARY KEY (`npi`, `data_year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `medicaid_top_procedure` (
  `npi` CHAR(10) NOT NULL,
  `procedure_rank` TINYINT NOT NULL,
  `data_year` SMALLINT NOT NULL,
  `hcpcs` VARCHAR(12) NOT NULL,
  `description` VARCHAR(300) NULL,
  `claim_lines` BIGINT NOT NULL,
  `paid` DECIMAL(15,2) NOT NULL,
  PRIMARY KEY (`npi`, `procedure_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `medicaid_data_period` (
  `source` VARCHAR(40) NOT NULL,
  `first_month` DATE NOT NULL,
  `last_month` DATE NOT NULL,
  PRIMARY KEY (`source`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
