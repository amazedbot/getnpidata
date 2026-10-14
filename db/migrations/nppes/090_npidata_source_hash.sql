-- 090_npidata_source_hash.sql
--
-- Faster monthly diff (owner, 2026-10-13: "optimize updates and runs"):
--   npidata.Source_Hash  MD5 of the row's values, as in the NPPES file, of the columns the search projection reads,
--                        computed by LOAD DATA while the file streams in (weekly and monthly). A monthly then finds the
--                        NPIs that changed by comparing 16 bytes per row instead of hashing ~300 columns of both the
--                        stored table and the new file (21 min on the October 2026 file). NULL until a load writes it,
--                        so the first monthly after this migration queues every NPI (a full projection, as before).
--
-- npidata's worst-case row size exceeds InnoDB's strict-mode check (see 002), so strict mode is off for this statement,
-- and MySQL refuses ALGORITHM=INSTANT for the same reason: the table is rebuilt online once (reads and writes continue).

SET SESSION innodb_strict_mode = OFF;
ALTER TABLE `npidata` ADD COLUMN `Source_Hash` BINARY(16) NULL, ALGORITHM=INPLACE, LOCK=NONE;
