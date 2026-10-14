-- 083_provider_quality.sql
--
-- Record quality (docs/PRODUCT_PLAN.md step 3.1): one label per active provider, how current and trustworthy its NPPES record
-- looks, written by the loader after each projection (QualityBuilder; staging + RENAME).
--   label     1 = current, 2 = check, 3 = stale (Npi.Core RecordQuality.Label)
--   reasons   bit flags of what was found (RecordQuality.Reason): an old record, no sign of current practice, an address that
--             couldn't be mapped or is a PO box, no phone, Care Compare listing another address or profession, a lapsed state
--             license, a reactivated NPI, a home address
--   activity  bit flags of the signs of current practice (RecordQuality.Activity): Medicare billing or prescribing, a Care
--             Compare listing, Medicare enrollment, order and refer eligibility, industry payments, a certified facility, a
--             current state license

CREATE TABLE IF NOT EXISTS `provider_quality` (
  `npi` CHAR(10) NOT NULL,
  `label` TINYINT NOT NULL,
  `reasons` INT NOT NULL,
  `activity` SMALLINT NOT NULL,
  PRIMARY KEY (`npi`),
  KEY `ix_provider_quality_label` (`label`, `npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
