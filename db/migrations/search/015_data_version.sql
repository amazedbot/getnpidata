-- 015_data_version.sql
--
-- What the current projection was built from (CLAUDE.md §6.2), for the site's "Data as of …"
-- footer and /api/v1/meta. A single row (id = 1), rewritten by every `project`; published_at is set
-- by `publish` (Stage 6).

CREATE TABLE IF NOT EXISTS `data_version` (
  `id` TINYINT NOT NULL,
  `as_of_date` DATE NULL,
  `monthly_file` VARCHAR(255) NULL,
  `weekly_file` VARCHAR(255) NULL,
  `deactivation_file` VARCHAR(255) NULL,
  `nucc_version` VARCHAR(40) NULL,
  `hud_version` VARCHAR(40) NULL,
  `census_version` VARCHAR(40) NULL,
  `provider_count` INT NOT NULL,
  `projected_at` DATETIME NOT NULL,
  `published_at` DATETIME NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
