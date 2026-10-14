-- 077_provider_deactivated.sql
--
-- Deactivated NPIs with their deactivation date (owner, 2026-10-11; docs/PRODUCT_PLAN.md step 1.4), so the site can answer
-- "Deactivated on [date]" for an NPI that is no longer active, without the raw npidata table (which stays local). NPPES
-- publishes nothing else about a deactivated NPI. Part of the projection (rebuilt with it, staging + RENAME); filled here
-- once so the status page works before the next projection.

CREATE TABLE IF NOT EXISTS `provider_deactivated` (
  `npi` CHAR(10) NOT NULL,
  `deactivation_date` DATE NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO `provider_deactivated` (`npi`, `deactivation_date`)
SELECT n.`NPI`, STR_TO_DATE(NULLIF(TRIM(n.`NPI_Deactivation_Date`), ''), '%m/%d/%Y')
FROM `npidata` n
WHERE n.`Is_Deactivated` = 1;
