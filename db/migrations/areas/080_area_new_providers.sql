-- 080_area_new_providers.sql
--
-- Area pages (docs/PRODUCT_PLAN.md step 1.6): of each area's providers, those whose NPI was issued in the 90 days before the
-- data's as-of date, counted with the rest when the loader rebuilds area_provider_count (counting them when a page is read
-- took up to 12 s for a whole state and a large specialty).

ALTER TABLE `area_provider_count` ADD COLUMN `new_providers` INT NOT NULL DEFAULT 0;
