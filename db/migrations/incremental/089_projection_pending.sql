-- 089_projection_pending.sql
--
-- Incremental search projection (owner, 2026-10-13: "optimize updates and runs"):
--   projection_pending  NPIs whose raw NPPES data changed since the projection was built: written by the weekly load (the
--                       file's NPIs), the monthly load (NPIs whose npidata row or child rows differ from the stored ones)
--                       and the deactivation flags (NPIs whose flag or date changed). The next projection rebuilds only
--                       these NPIs' rows when there are few of them, instead of all ~9.5M providers.

CREATE TABLE IF NOT EXISTS `projection_pending` (
  `npi` CHAR(10) NOT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
