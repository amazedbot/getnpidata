-- 088_state_license_by_state.sql
--
-- Faster state license loads (owner, 2026-10-13):
--   state_license_version  the version of each state's files when its licenses were last loaded, so a load reloads only the
--                          states whose files changed (one state's rows in provider_state_license are replaced at a time)
--   quality_pending        NPIs whose state license rows changed since the record-quality labels were built; the next
--                          `Npi.Loader quality` (or `run`) relabels only them when nothing else changed
--   provider_state_license gains an index on the state, for replacing one state's rows

CREATE TABLE IF NOT EXISTS `state_license_version` (
  `state` CHAR(2) NOT NULL,
  `version` VARCHAR(500) NOT NULL,
  `rows_loaded` BIGINT NOT NULL,
  `loaded_at` DATETIME NOT NULL,
  PRIMARY KEY (`state`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `quality_pending` (
  `npi` CHAR(10) NOT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

ALTER TABLE `provider_state_license` ADD KEY `ix_provider_state_license_state` (`state`, `npi`);
