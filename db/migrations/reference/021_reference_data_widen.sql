-- 021_reference_data_widen.sql
--
-- Stage 5.5 sources record versions like "2026-08-01 OP_DTL_GNRL_PGYR2025_P06302026_06032026.csv"
-- and long catalog URLs, so widen both columns. reference_data now tracks every external source
-- (CLAUDE.md §7 Stage 5.5), not only NUCC/HUD/Census.

ALTER TABLE `reference_data`
  MODIFY `version` VARCHAR(200) NOT NULL,
  MODIFY `source_url` VARCHAR(1000) NOT NULL;
