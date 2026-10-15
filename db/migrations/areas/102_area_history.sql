-- 102_area_history.sql
--
-- Areas over time (docs/PRODUCT_PLAN.md Phase 3 step 3.3): monthly figures per area (the US, each state, each county by the
-- HUD ZIP crosswalk), written by the loader after each load (AreaHistoryBuilder). Past months are frozen: a build recomputes
-- only the data's as-of month and the month before it.
--   area_month_stat: per area, plain specialty category ('' = every provider; subcategory is always '' for now) and month:
--     providers           active providers with a practice location there at the month's last build (area_provider_count)
--     new_providers       NPIs issued in the month whose holders practise there (by their location when counted)
--     deactivated         NPIs deactivated in the month: the US by CMS's deactivation date, states and counties by where the
--                         provider practised before (known only for deactivations seen by the loader)
--     moved_in/moved_out  providers who added or dropped their last practice location there (states and counties)
--     specialty_changes, license_changes, exclusion_changes  providers with such a change recorded in provider_change
--     quality_current/check/stale  the record-quality mix at the month's last build
--     basis               1 = derived from the data as it is today (NPIs issued per month, US deactivations), 2 = recorded as the
--                         loads arrived, 3 = from archived monthly NPPES files (step 3.4, not built)
--   Per category only providers and new_providers are filled; the other columns are for every provider (category '').
--   NULL = not known for that month; a missing row inside a series' months (area_history_meta) is 0.
--   area_month_event: the providers behind the moved, deactivated and change counts, so each count lists who it counts.
--   area_history_location: where each active provider practised when the history was last built (state, ZIP), compared
--     with the projection to find moves and the places of deactivated providers.
--   area_history_meta: the first month of each series, the as-of month of the last build, and the change log rows counted.

CREATE TABLE IF NOT EXISTS `area_month_stat` (
  `area_type` VARCHAR(6) NOT NULL,
  `area_id` VARCHAR(5) NOT NULL,
  `category` VARCHAR(80) NOT NULL DEFAULT '',
  `subcategory` VARCHAR(80) NOT NULL DEFAULT '',
  `month` DATE NOT NULL,
  `basis` TINYINT NOT NULL,
  `providers` INT NULL,
  `new_providers` INT NULL,
  `deactivated` INT NULL,
  `moved_in` INT NULL,
  `moved_out` INT NULL,
  `specialty_changes` INT NULL,
  `license_changes` INT NULL,
  `exclusion_changes` INT NULL,
  `quality_current` INT NULL,
  `quality_check` INT NULL,
  `quality_stale` INT NULL,
  PRIMARY KEY (`area_type`, `area_id`, `category`, `subcategory`, `month`),
  KEY `ix_area_month_stat_month` (`month`, `area_type`, `category`, `subcategory`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `area_month_event` (
  `area_type` VARCHAR(6) NOT NULL,
  `area_id` VARCHAR(5) NOT NULL,
  `month` DATE NOT NULL,
  `kind` VARCHAR(12) NOT NULL,
  `npi` CHAR(10) NOT NULL,
  PRIMARY KEY (`area_type`, `area_id`, `month`, `kind`, `npi`),
  KEY `ix_area_month_event_month` (`month`, `kind`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `area_history_location` (
  `npi` CHAR(10) NOT NULL,
  `state` VARCHAR(5) NOT NULL,
  `zip5` CHAR(5) NOT NULL DEFAULT '',
  PRIMARY KEY (`npi`, `state`, `zip5`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `area_history_meta` (
  `id` TINYINT NOT NULL,
  `derived_from` DATE NULL,
  `tracked_from` DATE NULL,
  `changes_from` DATE NULL,
  `as_of_month` DATE NULL,
  `last_change_id` BIGINT NOT NULL DEFAULT 0,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
