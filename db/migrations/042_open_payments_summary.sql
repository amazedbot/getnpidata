-- 042_open_payments_summary.sql
--
-- Open Payments (Sunshine Act) general payments from drug and device makers to one recipient NPI in
-- program_year (CLAUDE.md §7 Stage 5.5 item 6): totals, plus amounts by nature of payment and the top
-- payers in the two tables below. Research payments and ownership interests are not included.

CREATE TABLE IF NOT EXISTS `open_payments_summary` (
  `npi` CHAR(10) NOT NULL,
  `program_year` SMALLINT NOT NULL,
  `total_amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  `payers` INT NOT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
