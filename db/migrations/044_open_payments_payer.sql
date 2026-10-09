-- 044_open_payments_payer.sql
--
-- The three companies that paid a recipient NPI the most in the program year (CLAUDE.md §7 Stage 5.5
-- item 6).

CREATE TABLE IF NOT EXISTS `open_payments_payer` (
  `npi` CHAR(10) NOT NULL,
  `payer_rank` TINYINT NOT NULL,
  `payer` VARCHAR(150) NOT NULL,
  `amount` DOUBLE NOT NULL,
  `records` INT NOT NULL,
  PRIMARY KEY (`npi`, `payer_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
