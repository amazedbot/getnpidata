-- 081_cc_dac_raw_address.sql
--
-- Record quality (docs/PRODUCT_PLAN.md step 3.1): the raw Care Compare template keeps each listed practice's street address
-- and ZIP code too, so the loader can compare where Medicare lists a clinician with the NPPES practice locations. The
-- template holds no rows, so adding the columns is instant.

ALTER TABLE `cc_dac_raw` ADD COLUMN `address1` VARCHAR(150) NULL, ADD COLUMN `zip` VARCHAR(10) NULL;
