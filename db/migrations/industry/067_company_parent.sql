-- 067_company_parent.sql
--
-- A hand-made list of Open Payments companies that report under a subsidiary's name and their public parent company
-- (CLAUDE.md §7 Stage 5.5 item 17): MERCK SHARP & DOHME LLC → Merck & Co., Inc., JANSSEN … → Johnson & Johnson, and
-- so on, by the Open Payments company ID and the parent's SEC CIK. The list is `src/Npi.Loader/Datasets/company_parents.csv`,
-- loaded with the SEC registrants; the company page shows the parent's ticker and filings, marked as from this list.

CREATE TABLE IF NOT EXISTS `company_parent` (
  `company_id` VARCHAR(20) NOT NULL,
  `parent_cik` INT NOT NULL,
  `note` VARCHAR(100) NULL,
  PRIMARY KEY (`company_id`),
  KEY `ix_company_parent_cik` (`parent_cik`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
