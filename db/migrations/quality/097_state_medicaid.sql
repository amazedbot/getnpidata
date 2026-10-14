-- 097_state_medicaid.sql
--
-- State Medicaid enrolled-provider lists, a sign of current practice for the record-quality label (docs/PRODUCT_PLAN.md
-- Phase 3; owner's OK 2026-10-13): New York's Medicaid Enrolled Provider Listing (health.data.ny.gov keti-qx5t) and
-- California's Enrolled Medi-Cal Fee-for-Service Providers (data.chhs.ca.gov). One row per NPI and state: the earliest
-- enrollment date listed and the kind of provider (the state's wording). Replaced in full by the loader (staging + RENAME).

CREATE TABLE IF NOT EXISTS `state_medicaid` (
  `npi` CHAR(10) NOT NULL,
  `state` CHAR(2) NOT NULL,
  `enrolled_since` DATE NULL,
  `provider_type` VARCHAR(100) NULL,
  PRIMARY KEY (`npi`, `state`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
