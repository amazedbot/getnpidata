-- 041_open_payments_raw.sql
--
-- Template for the raw Open Payments General Payment Data file (~16M rows per program year; CLAUDE.md §7
-- Stage 5.5 item 6). Holds no data: each load fills `open_payments_raw_staging`, summarizes it per NPI,
-- and drops it.

CREATE TABLE IF NOT EXISTS `open_payments_raw` (
  `npi` CHAR(10) NULL,
  `payer` VARCHAR(150) NULL,
  `amount` DOUBLE NULL,
  `payments` INT NULL,
  `nature` VARCHAR(200) NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
