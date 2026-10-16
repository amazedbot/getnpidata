-- 117_org_group_member.sql
--
-- Combined organization profiles (docs/PRODUCT_PLAN.md step 3.8): active organization NPIs that belong to one organization,
-- grouped by the loader (OrgGroupBuilder, after the projection): the same legal name at the same street address, or tied by CMS
-- or NPPES in one ZIP code (a shared CCN, the same PAC ID, the same parent organization). One row per NPI of a group of two or
-- more; NPIs alone in their organization have none. Replaced in full (staging + RENAME).
--   main_npi       the group's main NPI (a hospital's CCN, else any CCN, else Medicare enrollment or billing, else the newest update)
--   members        NPIs in the group
--   is_main        1 for the main NPI
--   ccn            the CCN CMS ties the NPI to (its hospital's own first), with the kind of facility
--   joined_by      the ties that joined it (Npi.Core OrganizationTie bits: 1 name and address, 2 CCN, 4 PAC ID, 8 parent)
-- The search shows one row per organization by default: a member is left out when its main NPI matches the same filters.

CREATE TABLE IF NOT EXISTS `org_group_member` (
  `npi` CHAR(10) NOT NULL,
  `main_npi` CHAR(10) NOT NULL,
  `members` INT NOT NULL,
  `is_main` TINYINT NOT NULL,
  `ccn` VARCHAR(10) NULL,
  `facility_kind` VARCHAR(20) NULL,
  `joined_by` SMALLINT NOT NULL,
  PRIMARY KEY (`npi`),
  KEY `ix_org_group_member_main` (`main_npi`, `npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
