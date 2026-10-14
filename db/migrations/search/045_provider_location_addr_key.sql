-- 045_provider_location_addr_key.sql
--
-- A key per practice street address (CLAUDE.md §7 Stage 5.5 item 10, map search): MD5 of the street with
-- punctuation collapsed, the city, state and ZIP, upper-cased. Locations at the same street address share
-- it, so each address is geocoded once and providers there share a map pin. NULL when there is no US ZIP.
-- VIRTUAL: adding it changes metadata only (no rebuild of the 10M-row table), and `project` needs no
-- change because CREATE TABLE … LIKE copies the expression.

ALTER TABLE `provider_location`
  ADD COLUMN `addr_key` CHAR(32) CHARACTER SET ascii COLLATE ascii_bin GENERATED ALWAYS AS (
    IF(`zip5` IS NULL OR `address1` IS NULL OR `state` IS NULL, NULL,
       MD5(CONCAT_WS('|', UPPER(TRIM(REGEXP_REPLACE(`address1`, '[^[:alnum:]]+', ' '))), UPPER(TRIM(COALESCE(`city`, ''))), UPPER(`state`), `zip5`)))
  ) VIRTUAL;
