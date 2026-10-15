-- 108_directory_facility_ownership.sql
--
-- Two more filters on the Facilities & hospitals list (docs/PRODUCT_PLAN.md step 3.6 part 2): "Changed ownership recently" and
-- "Has penalties". DirectoryBuilder fills the columns from the change-of-ownership and nursing home penalty tables (migrations
-- 106-107) when it rebuilds the list, so the site filters its in-memory copy as before.
--   ownership_changed  the newest change of ownership with this CCN as the buyer or the seller (hospitals and nursing homes)
--   penalties          a nursing home's fines and payment denials in Care Compare's penalty file (the last three years)
--   fines              the total of those fines, in dollars (NULL: no fine)
--   last_penalty       the newest of those penalties

ALTER TABLE `directory_facility`
  ADD COLUMN `ownership_changed` DATE NULL,
  ADD COLUMN `penalties` INT NOT NULL DEFAULT 0,
  ADD COLUMN `fines` DOUBLE NULL,
  ADD COLUMN `last_penalty` DATE NULL;
