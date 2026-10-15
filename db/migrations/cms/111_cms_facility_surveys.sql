-- 111_cms_facility_surveys.sql
--
-- Surgery centers' patient survey and dialysis facilities' quality incentive scores for the facility pages (docs/PRODUCT_PLAN.md
-- step 3.6 part 2; owner, 2026-10-15). Each is replaced in full (staging + RENAME) by its source.
--   cms_asc_survey     Care Compare OAS CAHPS for ambulatory surgical centers (cc_oas_cahps), the "- Facility" and "- National" files:
--                      per CCN the top-box share of each question (staff definitely gave care in a professional way and the
--                      facility was clean; staff definitely communicated about the procedure; would definitely recommend; rated it
--                      9 or 10), the completed surveys, the response rate and the period; the national shares as ccn 'NATION'
--   cms_dialysis_qip   ESRD Quality Incentive Program total performance scores, newest payment year (cc_esrd_qip): per CCN the
--                      total performance score (NULL for CMS's "No Score"), the state and national averages and the payment
--                      reduction in percent

CREATE TABLE IF NOT EXISTS `cms_asc_survey` (
  `ccn` VARCHAR(10) NOT NULL,
  `professional` SMALLINT NULL,
  `communication` SMALLINT NULL,
  `recommend` SMALLINT NULL,
  `rating` SMALLINT NULL,
  `surveys` INT NULL,
  `response_rate` SMALLINT NULL,
  `start_date` DATE NULL,
  `end_date` DATE NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `cms_dialysis_qip` (
  `ccn` VARCHAR(10) NOT NULL,
  `payment_year` SMALLINT NOT NULL,
  `total_score` SMALLINT NULL,
  `state_average` SMALLINT NULL,
  `national_average` SMALLINT NULL,
  `payment_reduction` DOUBLE NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
