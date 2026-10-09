-- 055_provider_change.sql
--
-- Change log (CLAUDE.md §7 Stage 5.5 item 11): each projection compares the new provider rows with the
-- current ones and appends what changed (name, credential, primary specialty, primary practice address) and
-- which NPIs were added or dropped. NPPES publishes no history, so the log starts with the first projection
-- after this migration. Rows are only added; not part of the swapped projection tables.

CREATE TABLE IF NOT EXISTS `provider_change` (
  `id` BIGINT NOT NULL AUTO_INCREMENT,
  `npi` CHAR(10) NOT NULL,
  `detected_at` DATETIME NOT NULL,
  `change_type` VARCHAR(12) NOT NULL,
  `old_value` VARCHAR(300) NULL,
  `new_value` VARCHAR(300) NULL,
  PRIMARY KEY (`id`),
  KEY `ix_provider_change_npi` (`npi`, `detected_at`),
  KEY `ix_provider_change_detected` (`detected_at`, `change_type`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
