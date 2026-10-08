-- 004_extractlog_rows.sql
--
-- `extractlog` records each CSV/XLSX entry the loader read from a zip (CLAUDE.md §6.1).
-- The C# loader streams entries straight from the zip, so "extract" now means "loaded from";
-- rows_loaded is the number of rows that entry put into its staging table.

ALTER TABLE `extractlog`
  ADD COLUMN `rows_loaded` BIGINT NULL;
