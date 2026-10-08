-- 003_downlog_status.sql
--
-- Extend `downlog` into the loader's bookkeeping table (CLAUDE.md §6.1, §7 Stage 1.2).
-- A file counts as done only when status = 'Completed'. Rows written by the legacy VB loader
-- get status 'Legacy': the VB loader logged files before (and often without) loading them, so
-- they do not count as done and the C# loader may reload them.
-- received_date loses ON UPDATE CURRENT_TIMESTAMP so status updates keep the original time.

ALTER TABLE `downlog`
  CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  MODIFY COLUMN `received_date` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  ADD COLUMN `kind` VARCHAR(20) NULL AFTER `filename`,
  ADD COLUMN `file_date` DATE NULL AFTER `kind`,
  ADD COLUMN `status` VARCHAR(20) NOT NULL DEFAULT 'Legacy' AFTER `file_date`,
  ADD COLUMN `started_at` DATETIME NULL,
  ADD COLUMN `completed_at` DATETIME NULL,
  ADD COLUMN `rows_loaded` BIGINT NULL,
  ADD COLUMN `error` TEXT NULL,
  ADD UNIQUE KEY `ux_downlog_filename` (`filename`);
