-- 072_nadac_classification.sql
--
-- NADAC's "Classification for Rate Setting" also takes the values B-ANDA and B-BIO (CLAUDE.md §7 Stage 5.5 item 19,
-- part 3), longer than the two characters 071 allowed.

ALTER TABLE `nadac_raw` MODIFY `classification` VARCHAR(10) NULL;
ALTER TABLE `nadac` MODIFY `classification` VARCHAR(10) NULL;
