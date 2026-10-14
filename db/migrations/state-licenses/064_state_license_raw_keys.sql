-- 064_state_license_raw_keys.sql
--
-- More states for item 15 (CLAUDE.md §7 Stage 5.5): a second license-number key and full names.
--   license_key_last  the LAST run of digits without leading zeros, for numbers whose prefix holds a digit
--                     ("C1-0024514" → 24514, while license_key, all digits, is 10024514); a match on either key counts
--   full_name         for states that publish one name field ("JOSEPH U SINGH", "ACOSTA, GILBERTO M.D."); matched when
--                     the provider's last name is a whole word of it
--   id                lets the match pick rows without comparing their text
-- `state_license_raw` is an empty template, so this is instant.

ALTER TABLE `state_license_raw`
  ADD COLUMN `id` BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY FIRST,
  ADD COLUMN `full_name` VARCHAR(300) NULL AFTER `first_name`,
  ADD COLUMN `license_key_last` VARCHAR(60) GENERATED ALWAYS AS (
    IF(COALESCE(`license_number`, '') REGEXP '[0-9]',
       NULLIF(TRIM(LEADING '0' FROM REGEXP_REPLACE(`license_number`, '^.*?([0-9]+)[^0-9]*$', '$1')), ''), NULL)) STORED AFTER `license_key`,
  ADD KEY `ix_state_license_raw_key_last` (`state`, `license_key_last`);
