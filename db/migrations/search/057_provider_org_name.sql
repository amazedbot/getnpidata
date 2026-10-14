-- 057_provider_org_name.sql
--
-- "Similar" organization name search (CLAUDE.md §7 Stage 5.5 item 9): every organization's legal business name and
-- other (DBA, former) names, one row each, with a FULLTEXT index, so the words of a query can appear anywhere and in
-- any order. Part of the projection (rebuilt and swapped with it); filled once here from the current projection so
-- the search works before the next rebuild.

CREATE TABLE IF NOT EXISTS `provider_org_name` (
  `id` BIGINT NOT NULL AUTO_INCREMENT,
  `npi` CHAR(10) NOT NULL,
  `name` VARCHAR(200) NOT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_provider_org_name_npi` (`npi`),
  FULLTEXT KEY `ft_provider_org_name` (`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO `provider_org_name` (`npi`, `name`)
SELECT x.`npi`, x.`name`
FROM (
  SELECT p.`npi`, p.`org_name` AS `name` FROM `provider` p WHERE p.`org_name` IS NOT NULL
  UNION
  SELECT o.`npi`, o.`name` FROM `provider_other_name` o
) x
WHERE NOT EXISTS (SELECT 1 FROM `provider_org_name`)
ORDER BY x.`npi`;
