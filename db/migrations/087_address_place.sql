-- 087_address_place.sql
--
-- The businesses Overture Maps lists at each practice street address (docs/PRODUCT_PLAN.md step 3.1): name, operating
-- status (open, permanently_closed, temporarily_closed or NULL) and when a source last updated the listing. Written by
-- `Npi.Loader overture` with the address points, area by area (each area's rows replaced). The record-quality label
-- counts an open listing with the organization's name as a sign of current operation, and a permanently closed one
-- (with no open one) as a reason to check.

CREATE TABLE IF NOT EXISTS `address_place` (
  `addr_key` CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `name` VARCHAR(255) NOT NULL,
  `operating_status` VARCHAR(20) NULL,
  `updated` DATE NULL,
  `release` VARCHAR(40) NOT NULL,
  PRIMARY KEY (`addr_key`, `name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
