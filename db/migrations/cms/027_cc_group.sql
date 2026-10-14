-- 027_cc_group.sql
--
-- Care Compare group practices a clinician reassigns benefits to (CLAUDE.md §7 Stage 5.5 item 3a),
-- one row per clinician NPI and group (org PAC ID).

CREATE TABLE IF NOT EXISTS `cc_group` (
  `npi` CHAR(10) NOT NULL,
  `org_pac_id` VARCHAR(10) NOT NULL,
  `group_name` VARCHAR(200) NULL,
  `members` INT NULL,
  `accepts_assignment` TINYINT NOT NULL,
  `city` VARCHAR(100) NULL,
  `state` VARCHAR(2) NULL,
  PRIMARY KEY (`npi`, `org_pac_id`),
  KEY `ix_cc_group_org` (`org_pac_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
