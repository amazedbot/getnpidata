-- 120_provider_quality_activity_int.sql
--
-- More states' Medicaid (docs/PRODUCT_PLAN.md step 3.7): billing Medicaid is a sign of current practice of its own, bit 32768 of
-- provider_quality.activity (Npi.Core QualityActivity.MedicaidBilling). The column was a SMALLINT, whose last bit (16384) is the
-- Medicaid enrollment sign, so it is widened to INT. A table rebuild (the label table only, ~9.5M short rows).

ALTER TABLE `provider_quality` MODIFY COLUMN `activity` INT NOT NULL;
