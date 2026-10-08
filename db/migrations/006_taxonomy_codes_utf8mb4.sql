-- 006_taxonomy_codes_utf8mb4.sql
--
-- NUCC taxonomy (CLAUDE.md §6.2, §6.3). The NUCC CSV is UTF-8 and contains characters latin1 can't
-- hold. Specialization widened (NUCC 26.1 already uses 71 of the old 75). Nucc_Version records
-- the last NUCC release that listed the code; codes NUCC drops are kept (NPIs may still use them)
-- and keep their older version. Index on Classification for the search dropdown and filter.

ALTER TABLE `taxonomy_codes`
  CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  MODIFY COLUMN `Specialization` VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
  ADD COLUMN `Nucc_Version` VARCHAR(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NULL,
  ADD INDEX `ix_taxonomy_codes_classification` (`Classification`, `Specialization`);
