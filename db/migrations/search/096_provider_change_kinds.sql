-- 096_provider_change_kinds.sql
--
-- The change log widened (docs/PRODUCT_PLAN.md Phase 3 step 3.2): besides name, credential, primary specialty and primary
-- address, the projection now records phone, secondary practice locations, every taxonomy and other names added or
-- dropped, and the dataset reloads record exclusions, opt-outs and state licenses as they start, end or change
-- (license_added, opt_out_renewed, other_name_removed …): change_type grows to 20 characters. An online change (the column
-- only widens within the same length byte).

ALTER TABLE `provider_change` MODIFY COLUMN `change_type` VARCHAR(20) NOT NULL, ALGORITHM=INPLACE, LOCK=NONE;
