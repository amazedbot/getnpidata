-- 056_provider_phonetic.sql
--
-- "Similar" name search (CLAUDE.md §7 Stage 5.5 item 9): the standard 4-character Soundex of each provider's
-- accent-folded last and first name, as indexed VIRTUAL columns, so typos and spelling variants ("Smiht", "Jonhson",
-- "Nunez") find SMITH, JOHNSON, NUÑEZ. The expression is NameSearch.PhoneticSql in Npi.Core (a test compares them).
-- Virtual columns add no row data (instant); the indexes are built online in a second statement, because MySQL
-- can't do both online in one (the site keeps reading). provider_staging is
-- created LIKE provider, so the projection keeps them.

ALTER TABLE `provider`
  ADD COLUMN `last_phonetic` VARCHAR(10) GENERATED ALWAYS AS (LEFT(SOUNDEX(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(`last_name`), 'À', 'A'), 'Á', 'A'), 'Â', 'A'), 'Ã', 'A'), 'Ä', 'A'), 'Å', 'A'), 'Ç', 'C'), 'È', 'E'), 'É', 'E'), 'Ê', 'E'), 'Ë', 'E'), 'Ì', 'I'), 'Í', 'I'), 'Î', 'I'), 'Ï', 'I'), 'Ñ', 'N'), 'Ò', 'O'), 'Ó', 'O'), 'Ô', 'O'), 'Õ', 'O'), 'Ö', 'O'), 'Ø', 'O'), 'Ù', 'U'), 'Ú', 'U'), 'Û', 'U'), 'Ü', 'U'), 'Ý', 'Y')), 4)) VIRTUAL,
  ADD COLUMN `first_phonetic` VARCHAR(10) GENERATED ALWAYS AS (LEFT(SOUNDEX(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(`first_name`), 'À', 'A'), 'Á', 'A'), 'Â', 'A'), 'Ã', 'A'), 'Ä', 'A'), 'Å', 'A'), 'Ç', 'C'), 'È', 'E'), 'É', 'E'), 'Ê', 'E'), 'Ë', 'E'), 'Ì', 'I'), 'Í', 'I'), 'Î', 'I'), 'Ï', 'I'), 'Ñ', 'N'), 'Ò', 'O'), 'Ó', 'O'), 'Ô', 'O'), 'Õ', 'O'), 'Ö', 'O'), 'Ø', 'O'), 'Ù', 'U'), 'Ú', 'U'), 'Û', 'U'), 'Ü', 'U'), 'Ý', 'Y')), 4)) VIRTUAL,
  ALGORITHM=INSTANT;

ALTER TABLE `provider`
  ADD INDEX `ix_provider_last_phonetic` (`last_phonetic`, `first_phonetic`),
  ADD INDEX `ix_provider_first_phonetic` (`first_phonetic`),
  ALGORITHM=INPLACE, LOCK=NONE;
