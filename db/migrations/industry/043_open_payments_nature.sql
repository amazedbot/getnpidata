-- 043_open_payments_nature.sql
--
-- Open Payments general payments per recipient NPI and nature of payment (Food and Beverage, Consulting
-- Fee, …) in the program year (CLAUDE.md §7 Stage 5.5 item 6).

CREATE TABLE IF NOT EXISTS `open_payments_nature` (
  `npi` CHAR(10) NOT NULL,
  `nature` VARCHAR(200) NOT NULL,
  `amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  PRIMARY KEY (`npi`, `nature`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
