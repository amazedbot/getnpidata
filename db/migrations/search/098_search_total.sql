-- 098_search_total.sql
--
-- Totals the search uses instead of counting millions of rows for a single broad filter nationwide (owner, 2026-10-14: every
-- list may be nationwide): name = 'building:<commercial|residential|unknown>', the providers with a practice address of
-- that building type (counting it from building_search took 28 s). Written by the address-type builder.

CREATE TABLE IF NOT EXISTS `search_total` (
  `name` VARCHAR(40) NOT NULL,
  `total` BIGINT NOT NULL,
  `counted_at` DATETIME NOT NULL,
  PRIMARY KEY (`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
