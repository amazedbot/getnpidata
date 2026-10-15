-- 110_cms_hospital_measures.sql
--
-- Hospital performance and Medicare volume for the facility pages (docs/PRODUCT_PLAN.md step 3.6 part 2). Each is replaced in full
-- (staging + RENAME) by its source.
--   cms_hospital_measure   the measures of Npi.Core FacilityMeasures.Hospital (cc_hospital_measures), from Care Compare's "Complications
--                          and Deaths", "Healthcare Associated Infections", "Unplanned Hospital Visits", "Timely and Effective Care" and
--                          "Medicare Spending Per Beneficiary" hospital files: one row per CCN and measure (CMS's ID, "-" written "_")
--                          with the value, CMS's comparison with the nation, the patients (denominator or sample) and the period; the
--                          national files' values as ccn 'NATION'
--   cms_hospital_volume    Medicare fee-for-service volume per CCN (cms_hospital_volume): data.cms.gov "Medicare Inpatient Hospitals -
--                          by Provider" (discharges, patients, covered days, Medicare and total payments) and "Medicare Outpatient
--                          Hospitals - by Provider and Service" summed per CCN (services, Medicare payments), each with its data year
--   cms_hospital_service   per CCN the ten DRGs ('I', "Medicare Inpatient Hospitals - by Provider and Service": discharges, average
--                          Medicare payment) and APCs ('O': services, patients, average Medicare payment) with the most cases

CREATE TABLE IF NOT EXISTS `cms_hospital_measure` (
  `ccn` VARCHAR(10) NOT NULL,
  `measure_id` VARCHAR(30) NOT NULL,
  `score` DOUBLE NULL,
  `compared` VARCHAR(60) NULL,
  `cases` INT NULL,
  `start_date` DATE NULL,
  `end_date` DATE NULL,
  PRIMARY KEY (`ccn`, `measure_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `cms_hospital_volume` (
  `ccn` VARCHAR(10) NOT NULL,
  `inpatient_year` SMALLINT NULL,
  `discharges` INT NULL,
  `beneficiaries` INT NULL,
  `covered_days` INT NULL,
  `medicare_payments` DOUBLE NULL,
  `total_payments` DOUBLE NULL,
  `outpatient_year` SMALLINT NULL,
  `outpatient_services` INT NULL,
  `outpatient_payments` DOUBLE NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS `cms_hospital_service` (
  `ccn` VARCHAR(10) NOT NULL,
  `setting` CHAR(1) NOT NULL,
  `service_rank` TINYINT NOT NULL,
  `code` VARCHAR(20) NOT NULL,
  `description` VARCHAR(255) NULL,
  `cases` INT NOT NULL,
  `beneficiaries` INT NULL,
  `avg_medicare_payment` DOUBLE NULL,
  PRIMARY KEY (`ccn`, `setting`, `service_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
