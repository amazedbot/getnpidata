-- 095_deactivation_pending.sql
--
-- Deactivation flags by difference (docs/PRODUCT_PLAN.md §7 speed item 10). A monthly or deactivation report load writes
-- here, before its swap, the NPIs whose flag may change: a monthly the NPIs whose row differs from the stored one (by
-- Source_Hash) or whose deactivation or reactivation date does, a report the NPIs it adds, drops or redates. After the
-- swap only they are recomputed (was all ~9.8M npidata rows, ~1 min), and the rows are deleted once applied, so a load
-- stopped between its swap and the flags is caught up by the next one.

CREATE TABLE IF NOT EXISTS `deactivation_pending` (
  `NPI` CHAR(10) NOT NULL,
  PRIMARY KEY (`NPI`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
