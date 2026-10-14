-- 005_deactivated_report_utf8mb4.sql
--
-- utf8mb4 everywhere (CLAUDE.md §6). The table is replaced in full on each deactivation report
-- load (the report lists every currently deactivated NPI), so its charset must match the
-- staging copy created LIKE it.

ALTER TABLE `nppes_deactivated_npi_report`
  CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
