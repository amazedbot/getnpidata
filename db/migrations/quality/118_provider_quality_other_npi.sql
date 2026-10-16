-- 118_provider_quality_other_npi.sql
--
-- Combined organization profiles (docs/PRODUCT_PLAN.md step 3.8): a member of an organization with no sign of practice, when
-- another NPI of the organization is current, is labelled Stale with the reason "Another NPI of this organization is current"
-- (reason bit 65536); other_npi is that current NPI (the main NPI when it is current), for the link. NULL otherwise.
-- Added at the end and nullable, so the change is instant (no table rebuild).

ALTER TABLE `provider_quality` ADD COLUMN `other_npi` CHAR(10) NULL, ALGORITHM=INSTANT;
