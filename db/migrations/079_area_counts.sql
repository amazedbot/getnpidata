-- 079_area_counts.sql
--
-- Area pages (docs/PRODUCT_PLAN.md step 1.6): how many active providers practise in the US, each state and each county, in total
-- and per plain specialty category and subcategory, so a page can show counts and providers per 100,000 people at once.
--   specialty_code_category: every taxonomy code → its plain category/subcategory rows (Npi.Core SpecialtyCategories; codes the
--     list doesn't place fall under Other › Other), written by the loader so SQL can group by category.
--   area_provider_count: distinct NPIs with a practice location in the area (any of their locations, as the search counts them;
--     a county by the HUD ZIP crosswalk, so a ZIP in two counties counts in both) and a taxonomy in the category. Category ''
--     is every provider; subcategory '' is the whole category.
-- Both are rebuilt by the loader after each projection (staging + RENAME).

CREATE TABLE IF NOT EXISTS `specialty_code_category` (
  `code` VARCHAR(10) NOT NULL,
  `category` VARCHAR(80) NOT NULL,
  `subcategory` VARCHAR(80) NOT NULL,
  PRIMARY KEY (`code`, `category`, `subcategory`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `area_provider_count` (
  `area_type` VARCHAR(6) NOT NULL,
  `area_id` VARCHAR(5) NOT NULL,
  `category` VARCHAR(80) NOT NULL DEFAULT '',
  `subcategory` VARCHAR(80) NOT NULL DEFAULT '',
  `providers` INT NOT NULL,
  PRIMARY KEY (`area_type`, `area_id`, `category`, `subcategory`),
  KEY `ix_area_provider_count_specialty` (`category`, `subcategory`, `area_type`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
