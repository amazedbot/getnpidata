-- 115_directory_facility_networks.sql
--
-- The Facilities & hospitals list (directory_facility, migration 101) with the health system a facility belongs to (AHRQ
-- Compendium, by CCN), the HRSA health center it is a site of, and HRSA's health center sites that no CMS row stands for
-- (source = 'hrsa'; their key is HRSA's site number, up to 20 characters), and whether it is a 340B covered entity (HRSA
-- OPAIS: the entity type code, e.g. DSH or CH, and the start of its participation). Rebuilt by the loader (DirectoryBuilder).
ALTER TABLE `directory_facility`
  MODIFY `ccn` VARCHAR(20) NOT NULL,
  ADD COLUMN `source` VARCHAR(10) NOT NULL DEFAULT 'cms',
  ADD COLUMN `system_id` VARCHAR(20) NULL,
  ADD COLUMN `system_name` VARCHAR(200) NULL,
  ADD COLUMN `health_center` VARCHAR(200) NULL,
  ADD COLUMN `website` VARCHAR(255) NULL,
  ADD COLUMN `program_340b` VARCHAR(10) NULL,
  ADD COLUMN `since_340b` DATE NULL;
