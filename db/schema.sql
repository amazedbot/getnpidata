-- db/schema.sql: every table as migrations 001 to the newest leave it, generated from a freshly migrated database.
-- For reading only: the database is built and upgraded by `Npi.Loader migrate` from db/migrations. Regenerate it
-- after adding a migration: set NPI_UPDATE_SCHEMA=1 and run the MySQL tests.

CREATE TABLE `address_building` (
  `addr_key` char(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `matched` varchar(8) NOT NULL,
  `distance_m` smallint DEFAULT NULL,
  `occ_cls` varchar(20) DEFAULT NULL,
  `prim_occ` varchar(40) DEFAULT NULL,
  `sqft` int DEFAULT NULL,
  `lat` double DEFAULT NULL,
  `lon` double DEFAULT NULL,
  PRIMARY KEY (`addr_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `address_geocode` (
  `addr_key` char(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `status` varchar(10) NOT NULL,
  `match_type` varchar(10) DEFAULT NULL,
  `matched_address` varchar(255) DEFAULT NULL,
  `lat` decimal(9,6) DEFAULT NULL,
  `lon` decimal(10,6) DEFAULT NULL,
  `benchmark` varchar(40) NOT NULL,
  `geocoded_at` datetime NOT NULL,
  PRIMARY KEY (`addr_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `address_pending` (
  `addr_key` char(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `zip5` char(5) NOT NULL,
  PRIMARY KEY (`addr_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `address_place` (
  `addr_key` char(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `name` varchar(255) NOT NULL,
  `operating_status` varchar(20) DEFAULT NULL,
  `sources` varchar(255) DEFAULT NULL,
  `updated` date DEFAULT NULL,
  `release` varchar(40) NOT NULL,
  PRIMARY KEY (`addr_key`,`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `address_point` (
  `addr_key` char(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `source` varchar(12) NOT NULL,
  `lat` decimal(9,6) NOT NULL,
  `lon` decimal(10,6) NOT NULL,
  `matched` varchar(255) DEFAULT NULL,
  `release` varchar(40) NOT NULL,
  `matched_at` datetime NOT NULL,
  PRIMARY KEY (`addr_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `address_type` (
  `addr_key` char(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `building_type` varchar(12) NOT NULL,
  `basis` varchar(255) NOT NULL,
  PRIMARY KEY (`addr_key`),
  KEY `ix_address_type_type` (`building_type`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `animals` (
  `id` mediumint NOT NULL AUTO_INCREMENT,
  `name` char(30) NOT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `area_history_location` (
  `npi` char(10) NOT NULL,
  `state` varchar(5) NOT NULL,
  `zip5` char(5) NOT NULL DEFAULT '',
  PRIMARY KEY (`npi`,`state`,`zip5`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `area_history_meta` (
  `id` tinyint NOT NULL,
  `derived_from` date DEFAULT NULL,
  `tracked_from` date DEFAULT NULL,
  `changes_from` date DEFAULT NULL,
  `as_of_month` date DEFAULT NULL,
  `last_change_id` bigint NOT NULL DEFAULT '0',
  `archive_from` date DEFAULT NULL,
  `archive_to` date DEFAULT NULL,
  `any_location_from` date DEFAULT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `area_month_event` (
  `area_type` varchar(6) NOT NULL,
  `area_id` varchar(5) NOT NULL,
  `month` date NOT NULL,
  `kind` varchar(12) NOT NULL,
  `npi` char(10) NOT NULL,
  PRIMARY KEY (`area_type`,`area_id`,`month`,`kind`,`npi`),
  KEY `ix_area_month_event_month` (`month`,`kind`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `area_month_stat` (
  `area_type` varchar(6) NOT NULL,
  `area_id` varchar(5) NOT NULL,
  `category` varchar(80) NOT NULL DEFAULT '',
  `subcategory` varchar(80) NOT NULL DEFAULT '',
  `month` date NOT NULL,
  `basis` tinyint NOT NULL,
  `providers` int DEFAULT NULL,
  `new_providers` int DEFAULT NULL,
  `deactivated` int DEFAULT NULL,
  `moved_in` int DEFAULT NULL,
  `moved_out` int DEFAULT NULL,
  `specialty_changes` int DEFAULT NULL,
  `license_changes` int DEFAULT NULL,
  `exclusion_changes` int DEFAULT NULL,
  `quality_current` int DEFAULT NULL,
  `quality_check` int DEFAULT NULL,
  `quality_stale` int DEFAULT NULL,
  PRIMARY KEY (`area_type`,`area_id`,`category`,`subcategory`,`month`),
  KEY `ix_area_month_stat_month` (`month`,`area_type`,`category`,`subcategory`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `area_provider_count` (
  `area_type` varchar(6) NOT NULL,
  `area_id` varchar(5) NOT NULL,
  `category` varchar(80) NOT NULL DEFAULT '',
  `subcategory` varchar(80) NOT NULL DEFAULT '',
  `providers` int NOT NULL,
  `new_providers` int NOT NULL DEFAULT '0',
  PRIMARY KEY (`area_type`,`area_id`,`category`,`subcategory`),
  KEY `ix_area_provider_count_specialty` (`category`,`subcategory`,`area_type`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `builder_old_location` (
  `id` int unsigned NOT NULL AUTO_INCREMENT,
  `npi` char(10) NOT NULL,
  `addr_key` char(32) CHARACTER SET ascii COLLATE ascii_bin DEFAULT NULL,
  `state` varchar(40) NOT NULL DEFAULT '',
  `city` varchar(60) NOT NULL DEFAULT '',
  `zip5` char(5) NOT NULL DEFAULT '',
  `is_primary` tinyint NOT NULL,
  `label_input` tinyint DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_builder_old_location_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `builder_old_taxonomy` (
  `npi` char(10) NOT NULL,
  `taxonomy_code` varchar(10) NOT NULL,
  PRIMARY KEY (`npi`,`taxonomy_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `builder_pending` (
  `builder` varchar(16) NOT NULL,
  `npi` char(10) NOT NULL,
  PRIMARY KEY (`builder`,`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `building_search` (
  `building_type` varchar(12) NOT NULL,
  `state` varchar(40) NOT NULL DEFAULT '',
  `city` varchar(60) NOT NULL DEFAULT '',
  `zip5` char(5) NOT NULL DEFAULT '',
  `npi` char(10) NOT NULL,
  PRIMARY KEY (`building_type`,`state`,`city`,`zip5`,`npi`),
  KEY `ix_building_search_zip` (`building_type`,`zip5`,`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cc_clinician` (
  `npi` char(10) NOT NULL,
  `ind_pac_id` varchar(10) DEFAULT NULL,
  `medical_school` varchar(150) DEFAULT NULL,
  `graduation_year` smallint DEFAULT NULL,
  `primary_specialty` varchar(100) DEFAULT NULL,
  `secondary_specialties` varchar(300) DEFAULT NULL,
  `accepts_assignment` tinyint NOT NULL,
  `telehealth` tinyint NOT NULL,
  PRIMARY KEY (`npi`),
  KEY `ix_cc_clinician_grad` (`graduation_year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cc_clinician_location` (
  `npi` char(10) NOT NULL,
  `zip5` char(5) NOT NULL,
  `address1` varchar(150) DEFAULT NULL,
  `city` varchar(100) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  PRIMARY KEY (`npi`,`zip5`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cc_dac_raw` (
  `npi` char(10) NOT NULL,
  `ind_pac_id` varchar(10) DEFAULT NULL,
  `medical_school` varchar(150) DEFAULT NULL,
  `graduation_year` smallint DEFAULT NULL,
  `primary_specialty` varchar(100) DEFAULT NULL,
  `secondary_specialties` varchar(300) DEFAULT NULL,
  `telehealth` varchar(1) DEFAULT NULL,
  `group_name` varchar(200) DEFAULT NULL,
  `org_pac_id` varchar(10) DEFAULT NULL,
  `group_members` int DEFAULT NULL,
  `city` varchar(100) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  `ind_assignment` varchar(1) DEFAULT NULL,
  `grp_assignment` varchar(1) DEFAULT NULL,
  `address1` varchar(150) DEFAULT NULL,
  `zip` varchar(10) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cc_facility_affiliation` (
  `id` int unsigned NOT NULL AUTO_INCREMENT,
  `npi` char(10) NOT NULL,
  `facility_type` varchar(50) NOT NULL,
  `ccn` varchar(10) NOT NULL,
  `parent_ccn` varchar(10) DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_cc_facility_affiliation_npi` (`npi`),
  KEY `ix_cc_facility_affiliation_ccn` (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cc_group` (
  `npi` char(10) NOT NULL,
  `org_pac_id` varchar(10) NOT NULL,
  `group_name` varchar(200) DEFAULT NULL,
  `members` int DEFAULT NULL,
  `accepts_assignment` tinyint NOT NULL,
  `city` varchar(100) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  PRIMARY KEY (`npi`,`org_pac_id`),
  KEY `ix_cc_group_org` (`org_pac_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cc_mips` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `npi` char(10) DEFAULT NULL,
  `program_year` smallint NOT NULL,
  `source` varchar(20) DEFAULT NULL,
  `org_pac_id` varchar(20) DEFAULT NULL,
  `facility_name` varchar(200) DEFAULT NULL,
  `quality_score` double DEFAULT NULL,
  `pi_score` double DEFAULT NULL,
  `ia_score` double DEFAULT NULL,
  `cost_score` double DEFAULT NULL,
  `final_score` double DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_cc_mips_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_asc` (
  `ccn` varchar(10) NOT NULL,
  `npi` char(10) NOT NULL,
  `name` varchar(200) DEFAULT NULL,
  `city` varchar(100) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  `zip` varchar(10) DEFAULT NULL,
  `data_year` smallint DEFAULT NULL,
  `asc9_rate` double DEFAULT NULL,
  `asc13_rate` double DEFAULT NULL,
  `asc12_cases` int DEFAULT NULL,
  `asc12_category` varchar(60) DEFAULT NULL,
  `asc12_rate` double DEFAULT NULL,
  `asc17_cases` int DEFAULT NULL,
  `asc17_category` varchar(60) DEFAULT NULL,
  `asc17_rate` double DEFAULT NULL,
  `asc18_cases` int DEFAULT NULL,
  `asc18_category` varchar(60) DEFAULT NULL,
  `asc18_rate` double DEFAULT NULL,
  `asc19_cases` int DEFAULT NULL,
  `asc19_category` varchar(60) DEFAULT NULL,
  `asc19_rate` double DEFAULT NULL,
  PRIMARY KEY (`ccn`,`npi`),
  KEY `idx_cms_asc_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_asc_survey` (
  `ccn` varchar(10) NOT NULL,
  `professional` smallint DEFAULT NULL,
  `communication` smallint DEFAULT NULL,
  `recommend` smallint DEFAULT NULL,
  `rating` smallint DEFAULT NULL,
  `surveys` int DEFAULT NULL,
  `response_rate` smallint DEFAULT NULL,
  `start_date` date DEFAULT NULL,
  `end_date` date DEFAULT NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_dialysis` (
  `ccn` varchar(10) NOT NULL,
  `name` varchar(200) NOT NULL,
  `address` varchar(200) DEFAULT NULL,
  `city` varchar(100) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  `zip` varchar(10) DEFAULT NULL,
  `phone` varchar(20) DEFAULT NULL,
  `ownership` varchar(40) DEFAULT NULL,
  `chain` varchar(200) DEFAULT NULL,
  `stations` smallint DEFAULT NULL,
  `in_center` tinyint DEFAULT NULL,
  `peritoneal` tinyint DEFAULT NULL,
  `home_hemodialysis` tinyint DEFAULT NULL,
  `five_star` double DEFAULT NULL,
  `five_star_period` varchar(40) DEFAULT NULL,
  `survey_rating` tinyint DEFAULT NULL,
  `survival` varchar(60) DEFAULT NULL,
  `survival_period` varchar(40) DEFAULT NULL,
  `hospitalization` varchar(60) DEFAULT NULL,
  `hospitalization_period` varchar(40) DEFAULT NULL,
  `readmission` varchar(60) DEFAULT NULL,
  `readmission_period` varchar(40) DEFAULT NULL,
  `ed_visits` varchar(60) DEFAULT NULL,
  `ed_visits_period` varchar(40) DEFAULT NULL,
  `infection` varchar(60) DEFAULT NULL,
  `infection_period` varchar(40) DEFAULT NULL,
  `transfusion` varchar(60) DEFAULT NULL,
  `transfusion_period` varchar(40) DEFAULT NULL,
  `waitlist` varchar(60) DEFAULT NULL,
  `waitlist_period` varchar(40) DEFAULT NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_dialysis_npi` (
  `ccn` varchar(10) NOT NULL,
  `npi` char(10) NOT NULL,
  PRIMARY KEY (`ccn`,`npi`),
  KEY `idx_cms_dialysis_npi_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_dialysis_qip` (
  `ccn` varchar(10) NOT NULL,
  `payment_year` smallint NOT NULL,
  `total_score` smallint DEFAULT NULL,
  `state_average` smallint DEFAULT NULL,
  `national_average` smallint DEFAULT NULL,
  `payment_reduction` double DEFAULT NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_facility_enrollment` (
  `enrollment_id` varchar(20) NOT NULL,
  `kind` varchar(20) NOT NULL,
  `ccn` varchar(10) DEFAULT NULL,
  `npi` char(10) DEFAULT NULL,
  `associate_id` char(10) DEFAULT NULL,
  `org_name` varchar(200) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  PRIMARY KEY (`enrollment_id`),
  KEY `ix_cms_facility_enrollment_ccn` (`ccn`),
  KEY `ix_cms_facility_enrollment_associate` (`associate_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_facility_npi` (
  `ccn` varchar(10) NOT NULL,
  `npi` char(10) NOT NULL,
  `kind` varchar(20) NOT NULL,
  PRIMARY KEY (`ccn`,`npi`),
  KEY `ix_cms_facility_npi_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_hcahps_raw` (
  `ccn` varchar(10) DEFAULT NULL,
  `measure_id` varchar(40) DEFAULT NULL,
  `star_rating` tinyint DEFAULT NULL,
  `surveys` int DEFAULT NULL,
  `response_rate` double DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_home_health` (
  `ccn` varchar(10) NOT NULL,
  `name` varchar(200) NOT NULL,
  `address` varchar(200) DEFAULT NULL,
  `city` varchar(100) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  `zip` varchar(10) DEFAULT NULL,
  `phone` varchar(20) DEFAULT NULL,
  `ownership` varchar(100) DEFAULT NULL,
  `quality_rating` double DEFAULT NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_hospice` (
  `ccn` varchar(10) NOT NULL,
  `name` varchar(200) NOT NULL,
  `address` varchar(200) DEFAULT NULL,
  `city` varchar(100) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  `zip` varchar(10) DEFAULT NULL,
  `phone` varchar(20) DEFAULT NULL,
  `ownership` varchar(100) DEFAULT NULL,
  `family_rating` tinyint DEFAULT NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_hospice_cahps_raw` (
  `ccn` varchar(10) DEFAULT NULL,
  `measure_code` varchar(60) DEFAULT NULL,
  `star_rating` tinyint DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_hospital` (
  `ccn` varchar(10) NOT NULL,
  `name` varchar(200) NOT NULL,
  `address` varchar(200) DEFAULT NULL,
  `city` varchar(100) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  `zip` varchar(10) DEFAULT NULL,
  `phone` varchar(20) DEFAULT NULL,
  `hospital_type` varchar(100) DEFAULT NULL,
  `ownership` varchar(100) DEFAULT NULL,
  `emergency_services` tinyint DEFAULT NULL,
  `overall_rating` tinyint DEFAULT NULL,
  `mort_measures` smallint DEFAULT NULL,
  `mort_better` smallint DEFAULT NULL,
  `mort_worse` smallint DEFAULT NULL,
  `safety_measures` smallint DEFAULT NULL,
  `safety_better` smallint DEFAULT NULL,
  `safety_worse` smallint DEFAULT NULL,
  `readm_measures` smallint DEFAULT NULL,
  `readm_better` smallint DEFAULT NULL,
  `readm_worse` smallint DEFAULT NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_hospital_measure` (
  `ccn` varchar(10) NOT NULL,
  `measure_id` varchar(30) NOT NULL,
  `score` double DEFAULT NULL,
  `compared` varchar(60) DEFAULT NULL,
  `cases` int DEFAULT NULL,
  `start_date` date DEFAULT NULL,
  `end_date` date DEFAULT NULL,
  PRIMARY KEY (`ccn`,`measure_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_hospital_service` (
  `ccn` varchar(10) NOT NULL,
  `setting` char(1) NOT NULL,
  `service_rank` tinyint NOT NULL,
  `code` varchar(20) NOT NULL,
  `description` varchar(255) DEFAULT NULL,
  `cases` int NOT NULL,
  `beneficiaries` int DEFAULT NULL,
  `avg_medicare_payment` double DEFAULT NULL,
  PRIMARY KEY (`ccn`,`setting`,`service_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_hospital_survey` (
  `ccn` varchar(10) NOT NULL,
  `star_rating` tinyint DEFAULT NULL,
  `surveys` int DEFAULT NULL,
  `response_rate` double DEFAULT NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_hospital_volume` (
  `ccn` varchar(10) NOT NULL,
  `inpatient_year` smallint DEFAULT NULL,
  `discharges` int DEFAULT NULL,
  `beneficiaries` int DEFAULT NULL,
  `covered_days` int DEFAULT NULL,
  `medicare_payments` double DEFAULT NULL,
  `total_payments` double DEFAULT NULL,
  `outpatient_year` smallint DEFAULT NULL,
  `outpatient_services` int DEFAULT NULL,
  `outpatient_payments` double DEFAULT NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_nursing_home` (
  `ccn` varchar(10) NOT NULL,
  `name` varchar(200) NOT NULL,
  `address` varchar(200) DEFAULT NULL,
  `city` varchar(100) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  `zip` varchar(10) DEFAULT NULL,
  `phone` varchar(20) DEFAULT NULL,
  `provider_type` varchar(50) DEFAULT NULL,
  `ownership` varchar(100) DEFAULT NULL,
  `certified_beds` int DEFAULT NULL,
  `overall_rating` tinyint DEFAULT NULL,
  `inspection_rating` tinyint DEFAULT NULL,
  `staffing_rating` tinyint DEFAULT NULL,
  `quality_rating` tinyint DEFAULT NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_otp_provider` (
  `npi` char(10) NOT NULL,
  `name` varchar(150) DEFAULT NULL,
  `effective_date` date DEFAULT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_pos_facility` (
  `ccn` varchar(10) NOT NULL,
  `name` varchar(150) DEFAULT NULL,
  `category` varchar(10) DEFAULT NULL,
  `address` varchar(150) DEFAULT NULL,
  `city` varchar(60) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  `zip5` char(5) DEFAULT NULL,
  `active` tinyint NOT NULL,
  `termination_code` varchar(4) DEFAULT NULL,
  `termination_date` date DEFAULT NULL,
  PRIMARY KEY (`ccn`),
  KEY `ix_cms_pos_facility_zip` (`zip5`,`active`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_post_acute` (
  `ccn` varchar(10) NOT NULL,
  `kind` varchar(10) NOT NULL,
  `name` varchar(200) NOT NULL,
  `address` varchar(200) DEFAULT NULL,
  `city` varchar(100) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  `zip` varchar(10) DEFAULT NULL,
  `phone` varchar(20) DEFAULT NULL,
  `ownership` varchar(100) DEFAULT NULL,
  `certified_since` date DEFAULT NULL,
  `beds` int DEFAULT NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cms_post_acute_measure` (
  `ccn` varchar(10) NOT NULL,
  `measure_id` varchar(20) NOT NULL,
  `score` double DEFAULT NULL,
  `compared` varchar(60) DEFAULT NULL,
  `cases` int DEFAULT NULL,
  `start_date` date DEFAULT NULL,
  `end_date` date DEFAULT NULL,
  PRIMARY KEY (`ccn`,`measure_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `company_parent` (
  `company_id` varchar(20) NOT NULL,
  `parent_cik` int NOT NULL,
  `note` varchar(100) DEFAULT NULL,
  PRIMARY KEY (`company_id`),
  KEY `ix_company_parent_cik` (`parent_cik`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `country_codes` (
  `country_code` char(2) NOT NULL,
  `country_name` varchar(75) NOT NULL,
  PRIMARY KEY (`country_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `county` (
  `county_fips` char(5) NOT NULL,
  `state` char(2) NOT NULL,
  `county_name` varchar(100) NOT NULL,
  `source` varchar(40) NOT NULL,
  PRIMARY KEY (`county_fips`),
  KEY `ix_county_state_name` (`state`,`county_name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `county_population` (
  `county_fips` varchar(5) NOT NULL,
  `state_fips` varchar(2) NOT NULL,
  `county_code` varchar(3) NOT NULL,
  `population` int NOT NULL,
  `year` smallint NOT NULL,
  PRIMARY KEY (`county_fips`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `county_population_raw` (
  `sumlev` varchar(3) NOT NULL,
  `state_fips` varchar(2) NOT NULL,
  `county_code` varchar(3) NOT NULL,
  `population` int DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `county_shortage` (
  `county_fips` varchar(5) NOT NULL,
  `discipline` varchar(2) NOT NULL,
  `whole_county` tinyint NOT NULL,
  `hpsa_count` int NOT NULL,
  `max_score` int DEFAULT NULL,
  PRIMARY KEY (`county_fips`,`discipline`),
  KEY `ix_county_shortage_discipline` (`discipline`,`county_fips`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `credential_list` (
  `credential` varchar(60) NOT NULL,
  `providers` int NOT NULL,
  PRIMARY KEY (`credential`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `credential_map` (
  `raw` varchar(100) NOT NULL,
  `ord` tinyint NOT NULL,
  `credential` varchar(60) NOT NULL,
  PRIMARY KEY (`raw`,`ord`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `credential_search` (
  `credential` varchar(60) NOT NULL,
  `state` varchar(40) NOT NULL DEFAULT '',
  `city` varchar(60) NOT NULL DEFAULT '',
  `zip5` char(5) NOT NULL DEFAULT '',
  `npi` char(10) NOT NULL,
  PRIMARY KEY (`credential`,`state`,`city`,`zip5`,`npi`),
  KEY `ix_credential_search_zip` (`credential`,`zip5`,`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `data_version` (
  `id` tinyint NOT NULL,
  `as_of_date` date DEFAULT NULL,
  `monthly_file` varchar(255) DEFAULT NULL,
  `weekly_file` varchar(255) DEFAULT NULL,
  `deactivation_file` varchar(255) DEFAULT NULL,
  `nucc_version` varchar(40) DEFAULT NULL,
  `hud_version` varchar(40) DEFAULT NULL,
  `census_version` varchar(40) DEFAULT NULL,
  `provider_count` int NOT NULL,
  `projected_at` datetime NOT NULL,
  `published_at` datetime DEFAULT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `deactivation_pending` (
  `NPI` char(10) NOT NULL,
  PRIMARY KEY (`NPI`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `dicomhosts` (
  `ID` int NOT NULL AUTO_INCREMENT,
  `DicomAEName` varchar(100) DEFAULT NULL,
  `DicomHostName` varchar(100) DEFAULT NULL,
  `DicomPort` int DEFAULT NULL,
  PRIMARY KEY (`ID`)
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

CREATE TABLE `directory_facility` (
  `ccn` varchar(10) NOT NULL,
  `type` varchar(20) NOT NULL,
  `kind` varchar(80) DEFAULT NULL,
  `name` varchar(200) NOT NULL,
  `address` varchar(200) DEFAULT NULL,
  `city` varchar(100) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  `zip5` char(5) DEFAULT NULL,
  `phone` varchar(20) DEFAULT NULL,
  `ownership` varchar(100) DEFAULT NULL,
  `beds` int DEFAULT NULL,
  `rating` double DEFAULT NULL,
  `active` tinyint NOT NULL,
  `termination_date` date DEFAULT NULL,
  `termination_reason` varchar(80) DEFAULT NULL,
  `billing_npis` int NOT NULL,
  `lat` double DEFAULT NULL,
  `lon` double DEFAULT NULL,
  `ownership_changed` date DEFAULT NULL,
  `penalties` int NOT NULL DEFAULT '0',
  `fines` double DEFAULT NULL,
  `last_penalty` date DEFAULT NULL,
  PRIMARY KEY (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `directory_group` (
  `pac_id` varchar(10) NOT NULL,
  `name` varchar(200) NOT NULL,
  `state` varchar(2) DEFAULT NULL,
  `clinicians` int NOT NULL,
  `organization_npis` int NOT NULL,
  `accepts_assignment` tinyint DEFAULT NULL,
  `mips_score` double DEFAULT NULL,
  `mips_year` smallint DEFAULT NULL,
  PRIMARY KEY (`pac_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `directory_org_billing` (
  `npi` char(10) NOT NULL,
  `data_year` smallint NOT NULL,
  `payments` double NOT NULL,
  `beneficiaries` int DEFAULT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `directory_org_count` (
  `taxonomy_code` varchar(10) NOT NULL,
  `state` varchar(2) NOT NULL,
  `label` tinyint NOT NULL,
  `organizations` int NOT NULL,
  PRIMARY KEY (`taxonomy_code`,`state`,`label`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `downlog` (
  `id` int NOT NULL AUTO_INCREMENT,
  `filename` varchar(500) NOT NULL,
  `kind` varchar(20) DEFAULT NULL,
  `file_date` date DEFAULT NULL,
  `status` varchar(20) NOT NULL DEFAULT 'Legacy',
  `received_date` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `started_at` datetime DEFAULT NULL,
  `completed_at` datetime DEFAULT NULL,
  `rows_loaded` bigint DEFAULT NULL,
  `error` text,
  PRIMARY KEY (`id`),
  UNIQUE KEY `ux_downlog_filename` (`filename`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `drug_shortage` (
  `id` int NOT NULL,
  `generic_name` varchar(500) DEFAULT NULL,
  `substance` varchar(500) DEFAULT NULL,
  `company` varchar(255) DEFAULT NULL,
  `presentation` varchar(1000) DEFAULT NULL,
  `status` varchar(40) DEFAULT NULL,
  `availability` varchar(100) DEFAULT NULL,
  `reason` varchar(500) DEFAULT NULL,
  `related_info` varchar(2000) DEFAULT NULL,
  `initial_date` date DEFAULT NULL,
  `update_date` date DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_drug_shortage_substance` (`substance`(100))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `drug_shortage_ndc` (
  `ndc_key` char(9) NOT NULL,
  `shortage_id` int NOT NULL,
  PRIMARY KEY (`ndc_key`,`shortage_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `drug_spending` (
  `program` varchar(10) NOT NULL,
  `brand_name` varchar(255) NOT NULL,
  `generic_name` varchar(255) NOT NULL,
  `year` smallint NOT NULL,
  `spending` double NOT NULL,
  `units` double DEFAULT NULL,
  `claims` double DEFAULT NULL,
  `beneficiaries` double DEFAULT NULL,
  PRIMARY KEY (`program`,`brand_name`,`generic_name`,`year`),
  KEY `ix_drug_spending_brand` (`brand_name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `drug_spending_raw` (
  `hcpcs` varchar(20) DEFAULT NULL,
  `brand_name` varchar(255) DEFAULT NULL,
  `generic_name` varchar(500) DEFAULT NULL,
  `manufacturer` varchar(255) DEFAULT NULL,
  `spending1` double DEFAULT NULL,
  `units1` double DEFAULT NULL,
  `claims1` double DEFAULT NULL,
  `benes1` double DEFAULT NULL,
  `spending2` double DEFAULT NULL,
  `units2` double DEFAULT NULL,
  `claims2` double DEFAULT NULL,
  `benes2` double DEFAULT NULL,
  `spending3` double DEFAULT NULL,
  `units3` double DEFAULT NULL,
  `claims3` double DEFAULT NULL,
  `benes3` double DEFAULT NULL,
  `spending4` double DEFAULT NULL,
  `units4` double DEFAULT NULL,
  `claims4` double DEFAULT NULL,
  `benes4` double DEFAULT NULL,
  `spending5` double DEFAULT NULL,
  `units5` double DEFAULT NULL,
  `claims5` double DEFAULT NULL,
  `benes5` double DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `endpoints` (
  `ID` int unsigned NOT NULL AUTO_INCREMENT,
  `NPI` char(10) NOT NULL,
  `Endpoint_Type` text,
  `Endpoint_Type_Description` text,
  `Endpoint` text,
  `Affiliation` text,
  `Endpoint_Description` text,
  `Affiliation_Legal_Business_Name` text,
  `Use_Code` text,
  `Use_Description` text,
  `Other_Use_Description` text,
  `Content_Type` text,
  `Content_Description` text,
  `Other_Content_Description` text,
  `Affiliation_Address_Line_One` text,
  `Affiliation_Address_Line_Two` text,
  `Affiliation_Address_City` text,
  `Affiliation_Address_State` text,
  `Affiliation_Address_Country` text,
  `Affiliation_Address_Postal_Code` text,
  PRIMARY KEY (`ID`),
  KEY `ix_endpoints_npi` (`NPI`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `entity_types` (
  `entity_type_code` smallint NOT NULL,
  `entity_type_description` varchar(255) NOT NULL,
  PRIMARY KEY (`entity_type_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `extractlog` (
  `id` mediumint unsigned NOT NULL AUTO_INCREMENT,
  `ZipFileName` varchar(256) NOT NULL,
  `ExtractFileName` varchar(256) NOT NULL,
  `extract_date` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `rows_loaded` bigint DEFAULT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `facility_owner` (
  `enrollment_id` varchar(20) NOT NULL,
  `owner_id` char(10) NOT NULL,
  `role_code` varchar(4) NOT NULL,
  `kind` varchar(20) NOT NULL,
  `facility_associate_id` char(10) DEFAULT NULL,
  `facility_name` varchar(200) DEFAULT NULL,
  `owner_type` char(1) NOT NULL,
  `role_text` varchar(100) DEFAULT NULL,
  `association_date` date DEFAULT NULL,
  `name` varchar(200) DEFAULT NULL,
  `title` varchar(100) DEFAULT NULL,
  `dba_name` varchar(200) DEFAULT NULL,
  `city` varchar(60) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  `percentage` double DEFAULT NULL,
  `owner_kinds` varchar(400) DEFAULT NULL,
  `period_end` date DEFAULT NULL,
  PRIMARY KEY (`enrollment_id`,`owner_id`,`role_code`),
  KEY `ix_facility_owner_owner` (`owner_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `facility_owner_entity` (
  `owner_id` char(10) NOT NULL,
  `owner_type` char(1) NOT NULL,
  `name` varchar(200) NOT NULL,
  `dba_name` varchar(200) DEFAULT NULL,
  `city` varchar(60) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  `owner_kinds` varchar(400) DEFAULT NULL,
  `facilities` int NOT NULL,
  `owned` int NOT NULL,
  PRIMARY KEY (`owner_id`),
  KEY `ix_facility_owner_entity_size` (`owned`,`facilities`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `facility_ownership_change` (
  `id` int unsigned NOT NULL AUTO_INCREMENT,
  `kind` varchar(20) NOT NULL,
  `chow_type_code` varchar(4) DEFAULT NULL,
  `chow_type` varchar(60) DEFAULT NULL,
  `effective_date` date DEFAULT NULL,
  `buyer_enrollment_id` varchar(20) DEFAULT NULL,
  `buyer_ccn` varchar(10) DEFAULT NULL,
  `buyer_npi` char(10) DEFAULT NULL,
  `buyer_associate_id` char(10) DEFAULT NULL,
  `buyer_name` varchar(200) DEFAULT NULL,
  `buyer_dba_name` varchar(200) DEFAULT NULL,
  `seller_enrollment_id` varchar(20) DEFAULT NULL,
  `seller_ccn` varchar(10) DEFAULT NULL,
  `seller_npi` char(10) DEFAULT NULL,
  `seller_associate_id` char(10) DEFAULT NULL,
  `seller_name` varchar(200) DEFAULT NULL,
  `seller_dba_name` varchar(200) DEFAULT NULL,
  `period_end` date DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_facility_ownership_change_buyer_ccn` (`buyer_ccn`),
  KEY `ix_facility_ownership_change_seller_ccn` (`seller_ccn`),
  KEY `ix_facility_ownership_change_buyer` (`buyer_associate_id`),
  KEY `ix_facility_ownership_change_seller` (`seller_associate_id`),
  KEY `ix_facility_ownership_change_seller_enrollment` (`seller_enrollment_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `facility_ownership_change_owner` (
  `enrollment_id` varchar(20) NOT NULL,
  `owner_id` char(10) NOT NULL,
  `role_code` varchar(4) NOT NULL,
  `owner_type` char(1) NOT NULL,
  `name` varchar(200) DEFAULT NULL,
  `role_text` varchar(100) DEFAULT NULL,
  `percentage` double DEFAULT NULL,
  PRIMARY KEY (`enrollment_id`,`owner_id`,`role_code`),
  KEY `ix_facility_ownership_change_owner_owner` (`owner_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `fda_application` (
  `application_number` varchar(40) NOT NULL,
  `sponsor` varchar(255) DEFAULT NULL,
  `approval_date` date DEFAULT NULL,
  PRIMARY KEY (`application_number`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `fda_device` (
  `device_id` varchar(100) NOT NULL,
  `brand_name` varchar(500) DEFAULT NULL,
  `company_name` varchar(500) DEFAULT NULL,
  `description` text,
  `model` varchar(500) DEFAULT NULL,
  `gmdn_term` varchar(500) DEFAULT NULL,
  `gmdn_definition` text,
  `product_code` varchar(20) DEFAULT NULL,
  `product_code_name` varchar(500) DEFAULT NULL,
  `device_class` varchar(10) DEFAULT NULL,
  `medical_specialty` varchar(200) DEFAULT NULL,
  `is_rx` tinyint DEFAULT NULL,
  `is_otc` tinyint DEFAULT NULL,
  `implantable` tinyint DEFAULT NULL,
  `distribution_status` varchar(200) DEFAULT NULL,
  `submissions` varchar(2000) DEFAULT NULL,
  PRIMARY KEY (`device_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `fda_drug_label` (
  `set_id` varchar(64) NOT NULL,
  `effective_date` date DEFAULT NULL,
  `brand_name` varchar(500) DEFAULT NULL,
  `indications` mediumtext,
  `boxed_warning` mediumtext,
  PRIMARY KEY (`set_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `fda_drug_label_ndc` (
  `ndc_key` char(9) NOT NULL,
  `set_id` varchar(64) NOT NULL,
  PRIMARY KEY (`ndc_key`,`set_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `fda_enforcement` (
  `recall_number` varchar(30) NOT NULL,
  `product_type` varchar(20) NOT NULL,
  `firm` varchar(255) DEFAULT NULL,
  `firm_key` varchar(255) DEFAULT NULL,
  `classification` varchar(30) DEFAULT NULL,
  `status` varchar(30) DEFAULT NULL,
  `initiation_date` date DEFAULT NULL,
  `report_date` date DEFAULT NULL,
  `voluntary_mandated` varchar(60) DEFAULT NULL,
  `city` varchar(100) DEFAULT NULL,
  `state` varchar(40) DEFAULT NULL,
  `country` varchar(100) DEFAULT NULL,
  `product_description` varchar(1000) DEFAULT NULL,
  `reason` varchar(1000) DEFAULT NULL,
  PRIMARY KEY (`recall_number`,`product_type`),
  KEY `ix_fda_enforcement_firm_key` (`firm_key`,`initiation_date`),
  FULLTEXT KEY `ft_fda_enforcement_product` (`product_description`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `fda_ndc_product` (
  `product_ndc` varchar(20) NOT NULL,
  `ndc_key` char(9) GENERATED ALWAYS AS (if(((length(`product_ndc`) - length(replace(`product_ndc`,_utf8mb4'-',_utf8mb4''))) in (1,2)),concat(lpad(substring_index(`product_ndc`,_utf8mb4'-',1),5,_utf8mb4'0'),lpad(substring_index(substring_index(`product_ndc`,_utf8mb4'-',2),_utf8mb4'-',-(1)),4,_utf8mb4'0')),NULL)) STORED,
  `brand_name` varchar(500) DEFAULT NULL,
  `generic_name` varchar(1000) DEFAULT NULL,
  `active_ingredients` varchar(2000) DEFAULT NULL,
  `dosage_form` varchar(200) DEFAULT NULL,
  `route` varchar(200) DEFAULT NULL,
  `labeler` varchar(255) DEFAULT NULL,
  `marketing_category` varchar(100) DEFAULT NULL,
  `application_number` varchar(40) DEFAULT NULL,
  `marketing_start` date DEFAULT NULL,
  `pharm_classes` varchar(1000) DEFAULT NULL,
  `product_type` varchar(100) DEFAULT NULL,
  `spl_set_id` varchar(64) DEFAULT NULL,
  PRIMARY KEY (`product_ndc`),
  KEY `ix_fda_ndc_product_key` (`ndc_key`),
  KEY `ix_fda_ndc_product_generic` (`generic_name`(100)),
  KEY `ix_fda_ndc_product_brand` (`brand_name`(100)),
  KEY `ix_fda_ndc_product_application` (`application_number`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `fda_premarket` (
  `number` varchar(20) NOT NULL,
  `kind` varchar(10) NOT NULL,
  `applicant` varchar(500) DEFAULT NULL,
  `device_name` varchar(1000) DEFAULT NULL,
  `decision_date` date DEFAULT NULL,
  `decision` varchar(200) DEFAULT NULL,
  PRIMARY KEY (`number`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `gender_codes` (
  `gender_code` char(1) NOT NULL,
  `description` varchar(50) NOT NULL,
  PRIMARY KEY (`gender_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `history_area_count` (
  `area_type` varchar(6) NOT NULL,
  `area_id` varchar(5) NOT NULL,
  `category` varchar(80) NOT NULL,
  `providers` int NOT NULL,
  PRIMARY KEY (`area_type`,`area_id`,`category`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `history_period` (
  `period` date NOT NULL,
  `status` varchar(10) NOT NULL,
  `source` varchar(8) DEFAULT NULL,
  `file_name` varchar(200) DEFAULT NULL,
  `source_url` varchar(400) DEFAULT NULL,
  `file_date` date DEFAULT NULL,
  `bytes` bigint DEFAULT NULL,
  `columns_count` smallint DEFAULT NULL,
  `rows_read` int DEFAULT NULL,
  `deactivated_rows` int DEFAULT NULL,
  `active` int DEFAULT NULL,
  `versions` int DEFAULT NULL,
  `new_npis` int DEFAULT NULL,
  `gone` int DEFAULT NULL,
  `zip_unmatched` int DEFAULT NULL,
  `zip_unmatched_providers` int DEFAULT NULL,
  `stats_done` tinyint NOT NULL DEFAULT '0',
  `locations` tinyint NOT NULL DEFAULT '0',
  `secondary_rows` int DEFAULT NULL,
  `started_at` datetime DEFAULT NULL,
  `completed_at` datetime DEFAULT NULL,
  `seconds` int DEFAULT NULL,
  `error` text,
  PRIMARY KEY (`period`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `hrsa_shortage_raw` (
  `discipline` varchar(2) NOT NULL,
  `hpsa_id` varchar(20) DEFAULT NULL,
  `designation_type` varchar(100) DEFAULT NULL,
  `status` varchar(50) DEFAULT NULL,
  `score` int DEFAULT NULL,
  `component_type` varchar(50) DEFAULT NULL,
  `county_fips` varchar(5) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `medicare_activity_year` (
  `data_year` smallint NOT NULL,
  `npi` char(10) NOT NULL,
  `parts` tinyint NOT NULL,
  `part_b_beneficiaries` int DEFAULT NULL,
  `part_d_claims` int DEFAULT NULL,
  PRIMARY KEY (`data_year`,`npi`),
  KEY `ix_medicare_activity_year_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `medicare_dme_supplier` (
  `npi` char(10) NOT NULL,
  `data_year` smallint NOT NULL,
  `beneficiaries` int DEFAULT NULL,
  `claims` int DEFAULT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `medicare_enrollment` (
  `npi` char(10) NOT NULL,
  `enrollment_id` varchar(20) NOT NULL,
  `pac_id` varchar(10) DEFAULT NULL,
  `provider_type` varchar(120) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  `org_name` varchar(200) DEFAULT NULL,
  PRIMARY KEY (`npi`,`enrollment_id`),
  KEY `ix_medicare_enrollment_pac` (`pac_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `medicare_history_version` (
  `part` char(1) NOT NULL,
  `data_year` smallint NOT NULL,
  `version` varchar(500) NOT NULL,
  `rows_loaded` bigint NOT NULL,
  `loaded_at` datetime NOT NULL,
  PRIMARY KEY (`part`,`data_year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `medicare_opt_out` (
  `id` int unsigned NOT NULL AUTO_INCREMENT,
  `npi` char(10) NOT NULL,
  `specialty` varchar(100) DEFAULT NULL,
  `effective_date` date DEFAULT NULL,
  `end_date` date DEFAULT NULL,
  `can_order_refer` tinyint DEFAULT NULL,
  `last_updated` date DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_medicare_opt_out_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `medicare_order_referring` (
  `id` int unsigned NOT NULL AUTO_INCREMENT,
  `npi` char(10) NOT NULL,
  `part_b` tinyint DEFAULT NULL,
  `dme` tinyint DEFAULT NULL,
  `hha` tinyint DEFAULT NULL,
  `pmd` tinyint DEFAULT NULL,
  `hospice` tinyint DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_medicare_order_referring_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `medicare_part_d` (
  `npi` char(10) NOT NULL,
  `data_year` smallint NOT NULL,
  `prescriber_type` varchar(100) DEFAULT NULL,
  `claims` int DEFAULT NULL,
  `fills_30day` double DEFAULT NULL,
  `drug_cost` double DEFAULT NULL,
  `day_supply` bigint DEFAULT NULL,
  `beneficiaries` int DEFAULT NULL,
  `brand_claims` int DEFAULT NULL,
  `generic_claims` int DEFAULT NULL,
  `opioid_claims` int DEFAULT NULL,
  `opioid_rate` double DEFAULT NULL,
  `antibiotic_claims` int DEFAULT NULL,
  `avg_beneficiary_age` double DEFAULT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `medicare_reassignment` (
  `id` int unsigned NOT NULL AUTO_INCREMENT,
  `group_pac_id` varchar(10) NOT NULL,
  `group_enrollment_id` varchar(20) DEFAULT NULL,
  `group_name` varchar(200) DEFAULT NULL,
  `group_state` varchar(2) DEFAULT NULL,
  `record_type` varchar(30) DEFAULT NULL,
  `npi` char(10) DEFAULT NULL,
  `specialty` varchar(120) DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_medicare_reassignment_group` (`group_pac_id`,`npi`),
  KEY `ix_medicare_reassignment_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `medicare_revalidation` (
  `npi` char(10) NOT NULL,
  `due_date` date DEFAULT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `medicare_service_raw` (
  `npi` char(10) NOT NULL,
  `hcpcs` varchar(10) NOT NULL,
  `description` varchar(300) DEFAULT NULL,
  `is_drug` tinyint DEFAULT NULL,
  `place_of_service` varchar(1) DEFAULT NULL,
  `beneficiaries` int DEFAULT NULL,
  `services` double DEFAULT NULL,
  `avg_payment` double DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `medicare_top_service` (
  `npi` char(10) NOT NULL,
  `service_rank` tinyint NOT NULL,
  `data_year` smallint NOT NULL,
  `hcpcs` varchar(10) NOT NULL,
  `description` varchar(300) DEFAULT NULL,
  `is_drug` tinyint DEFAULT NULL,
  `place_of_service` varchar(1) DEFAULT NULL,
  `beneficiaries` int DEFAULT NULL,
  `services` double DEFAULT NULL,
  `avg_payment` double DEFAULT NULL,
  PRIMARY KEY (`npi`,`service_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `medicare_utilization` (
  `npi` char(10) NOT NULL,
  `data_year` smallint NOT NULL,
  `provider_type` varchar(100) DEFAULT NULL,
  `participating` tinyint DEFAULT NULL,
  `distinct_services` int DEFAULT NULL,
  `beneficiaries` int DEFAULT NULL,
  `services` double DEFAULT NULL,
  `submitted_charges` double DEFAULT NULL,
  `allowed_amount` double DEFAULT NULL,
  `payment_amount` double DEFAULT NULL,
  `avg_beneficiary_age` double DEFAULT NULL,
  `avg_risk_score` double DEFAULT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `nadac` (
  `ndc` char(11) NOT NULL,
  `ndc_key` char(9) GENERATED ALWAYS AS (left(`ndc`,9)) STORED,
  `description` varchar(255) DEFAULT NULL,
  `per_unit` double NOT NULL,
  `pricing_unit` varchar(10) DEFAULT NULL,
  `effective_date` date DEFAULT NULL,
  `classification` varchar(10) DEFAULT NULL,
  `otc` varchar(2) DEFAULT NULL,
  `as_of` date DEFAULT NULL,
  PRIMARY KEY (`ndc`),
  KEY `ix_nadac_key` (`ndc_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `nadac_raw` (
  `description` varchar(255) DEFAULT NULL,
  `ndc` varchar(20) DEFAULT NULL,
  `per_unit` double DEFAULT NULL,
  `effective_date` date DEFAULT NULL,
  `pricing_unit` varchar(10) DEFAULT NULL,
  `otc` varchar(2) DEFAULT NULL,
  `classification` varchar(10) DEFAULT NULL,
  `as_of` date DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `nh_citation` (
  `ccn` varchar(10) NOT NULL,
  `kind` varchar(6) NOT NULL,
  `survey_date` date NOT NULL,
  `prefix` varchar(2) NOT NULL,
  `tag` varchar(6) NOT NULL,
  `cycle` int NOT NULL,
  `scope_severity` char(1) NOT NULL,
  `corrected` varchar(80) DEFAULT NULL,
  `correction_date` date DEFAULT NULL,
  `complaint` tinyint NOT NULL,
  `infection_control` tinyint NOT NULL,
  PRIMARY KEY (`ccn`,`kind`,`survey_date`,`prefix`,`tag`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `nh_deficiency_count` (
  `ccn` varchar(10) NOT NULL,
  `kind` varchar(6) NOT NULL,
  `cycle` int NOT NULL,
  `scope_severity` char(1) NOT NULL,
  `deficiencies` int NOT NULL,
  `complaint` int NOT NULL,
  PRIMARY KEY (`ccn`,`kind`,`cycle`,`scope_severity`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `nh_deficiency_tag` (
  `prefix` varchar(2) NOT NULL,
  `tag` varchar(6) NOT NULL,
  `category` varchar(120) DEFAULT NULL,
  `description` varchar(600) DEFAULT NULL,
  PRIMARY KEY (`prefix`,`tag`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `nh_penalty` (
  `id` int unsigned NOT NULL AUTO_INCREMENT,
  `ccn` varchar(10) NOT NULL,
  `penalty_date` date DEFAULT NULL,
  `penalty_type` varchar(40) DEFAULT NULL,
  `fine_amount` double DEFAULT NULL,
  `denial_start` date DEFAULT NULL,
  `denial_days` int DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_nh_penalty_ccn` (`ccn`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `nh_survey` (
  `ccn` varchar(10) NOT NULL,
  `kind` varchar(6) NOT NULL,
  `survey_date` date NOT NULL,
  `cycle` int NOT NULL,
  `deficiencies` int NOT NULL,
  `complaint` int NOT NULL,
  `most_severe` char(1) DEFAULT NULL,
  PRIMARY KEY (`ccn`,`kind`,`survey_date`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `npi_history` (
  `npi` int unsigned NOT NULL,
  `from_period` date NOT NULL,
  `to_period` date DEFAULT NULL,
  `status` tinyint NOT NULL,
  `entity_type` tinyint DEFAULT NULL,
  `state` varchar(40) DEFAULT NULL,
  `zip5` char(5) DEFAULT NULL,
  `country` char(2) DEFAULT NULL,
  `primary_taxonomy` varchar(10) DEFAULT NULL,
  `taxonomy_set` int unsigned DEFAULT NULL,
  `location_set` int unsigned DEFAULT NULL,
  `enumeration_date` date DEFAULT NULL,
  `last_update_date` date DEFAULT NULL,
  `deactivation_date` date DEFAULT NULL,
  `reactivation_date` date DEFAULT NULL,
  PRIMARY KEY (`npi`,`from_period`),
  KEY `ix_npi_history_from` (`from_period`),
  KEY `ix_npi_history_enumeration` (`enumeration_date`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `npi_history_location_set` (
  `set_id` int unsigned NOT NULL,
  `state` varchar(5) NOT NULL,
  `zip5` char(5) NOT NULL DEFAULT '',
  PRIMARY KEY (`set_id`,`state`,`zip5`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `npi_history_set_category` (
  `set_id` int unsigned NOT NULL,
  `category` varchar(80) NOT NULL,
  PRIMARY KEY (`set_id`,`category`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `npi_history_taxonomy_set` (
  `id` int unsigned NOT NULL,
  `codes` varchar(200) NOT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `npidata` (
  `NPI` char(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `Entity_Type_Code` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Replacement_NPI` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Employer_Identification_Number_EIN` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Organization_Name_Legal_Business_Name` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Last_Name_Legal_Name` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_First_Name` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Middle_Name` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Name_Prefix_Text` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Name_Suffix_Text` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Credential_Text` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Other_Organization_Name` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Other_Organization_Name_Type_Code` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Other_Last_Name` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Other_First_Name` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Other_Middle_Name` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Other_Name_Prefix_Text` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Other_Name_Suffix_Text` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Other_Credential_Text` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Other_Last_Name_Type_Code` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_First_Line_Business_Mailing_Address` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Second_Line_Business_Mailing_Address` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Business_Mailing_Address_City_Name` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Business_Mailing_Address_State_Name` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Business_Mailing_Address_Postal_Code` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Business_Mailing_Address_Country_Code` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Business_Mailing_Address_Telephone_Number` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Business_Mailing_Address_Fax_Number` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_First_Line_Business_Practice_Location_Address` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Second_Line_Business_Practice_Location_Address` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Business_Practice_Location_Address_City_Name` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Business_Practice_Location_Address_State_Name` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Business_Practice_Location_Address_Postal_Code` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Business_Practice_Location_Address_Country_Code` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Business_Practice_Location_Address_Telephone_Number` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Business_Practice_Location_Address_Fax_Number` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Enumeration_Date` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Last_Update_Date` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `NPI_Deactivation_Reason_Code` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `NPI_Deactivation_Date` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `NPI_Reactivation_Date` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_Gender_Code` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Authorized_Official_Last_Name` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Authorized_Official_First_Name` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Authorized_Official_Middle_Name` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Authorized_Official_Title_or_Position` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Authorized_Official_Telephone_Number` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Code_1` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_1` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_State_Code_1` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Primary_Taxonomy_Switch_1` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Code_2` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_2` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_State_Code_2` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Primary_Taxonomy_Switch_2` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Code_3` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_3` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_State_Code_3` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Primary_Taxonomy_Switch_3` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Code_4` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_4` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_State_Code_4` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Primary_Taxonomy_Switch_4` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Code_5` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_5` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_State_Code_5` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Primary_Taxonomy_Switch_5` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Code_6` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_6` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_State_Code_6` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Primary_Taxonomy_Switch_6` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Code_7` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_7` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_State_Code_7` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Primary_Taxonomy_Switch_7` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Code_8` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_8` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_State_Code_8` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Primary_Taxonomy_Switch_8` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Code_9` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_9` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_State_Code_9` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Primary_Taxonomy_Switch_9` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Code_10` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_10` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_State_Code_10` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Primary_Taxonomy_Switch_10` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Code_11` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_11` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_State_Code_11` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Primary_Taxonomy_Switch_11` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Code_12` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_12` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_State_Code_12` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Primary_Taxonomy_Switch_12` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Code_13` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_13` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_State_Code_13` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Primary_Taxonomy_Switch_13` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Code_14` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_14` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_State_Code_14` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Primary_Taxonomy_Switch_14` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Code_15` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_15` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Provider_License_Number_State_Code_15` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Primary_Taxonomy_Switch_15` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_1` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_1` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_1` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_1` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_2` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_2` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_2` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_2` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_3` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_3` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_3` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_3` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_4` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_4` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_4` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_4` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_5` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_5` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_5` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_5` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_6` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_6` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_6` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_6` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_7` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_7` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_7` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_7` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_8` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_8` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_8` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_8` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_9` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_9` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_9` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_9` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_10` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_10` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_10` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_10` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_11` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_11` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_11` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_11` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_12` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_12` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_12` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_12` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_13` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_13` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_13` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_13` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_14` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_14` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_14` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_14` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_15` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_15` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_15` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_15` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_16` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_16` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_16` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_16` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_17` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_17` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_17` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_17` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_18` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_18` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_18` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_18` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_19` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_19` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_19` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_19` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_20` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_20` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_20` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_20` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_21` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_21` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_21` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_21` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_22` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_22` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_22` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_22` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_23` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_23` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_23` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_23` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_24` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_24` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_24` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_24` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_25` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_25` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_25` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_25` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_26` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_26` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_26` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_26` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_27` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_27` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_27` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_27` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_28` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_28` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_28` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_28` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_29` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_29` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_29` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_29` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_30` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_30` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_30` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_30` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_31` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_31` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_31` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_31` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_32` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_32` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_32` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_32` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_33` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_33` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_33` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_33` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_34` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_34` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_34` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_34` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_35` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_35` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_35` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_35` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_36` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_36` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_36` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_36` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_37` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_37` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_37` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_37` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_38` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_38` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_38` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_38` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_39` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_39` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_39` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_39` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_40` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_40` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_40` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_40` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_41` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_41` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_41` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_41` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_42` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_42` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_42` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_42` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_43` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_43` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_43` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_43` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_44` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_44` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_44` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_44` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_45` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_45` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_45` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_45` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_46` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_46` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_46` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_46` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_47` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_47` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_47` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_47` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_48` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_48` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_48` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_48` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_49` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_49` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_49` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_49` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_50` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Type_Code_50` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_State_50` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Other_Provider_Identifier_Issuer_50` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Is_Sole_Proprietor` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Is_Organization_Subpart` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Parent_Organization_LBN` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Parent_Organization_TIN` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Authorized_Official_Name_Prefix_Text` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Authorized_Official_Name_Suffix_Text` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Authorized_Official_Credential_Text` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Group_1` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Group_2` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Group_3` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Group_4` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Group_5` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Group_6` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Group_7` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Group_8` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Group_9` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Group_10` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Group_11` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Group_12` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Group_13` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Group_14` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Healthcare_Provider_Taxonomy_Group_15` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Certification_Date` text CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `Is_Deactivated` tinyint NOT NULL DEFAULT '0',
  `Loaded_From` varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `Source_Hash` binary(16) DEFAULT NULL,
  PRIMARY KEY (`NPI`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `nppes_deactivated_npi_report` (
  `NPI` char(10) NOT NULL,
  `NPPES_Deactivation_Date` date DEFAULT NULL,
  PRIMARY KEY (`NPI`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `oig_cia` (
  `slug` varchar(200) NOT NULL,
  `name` varchar(2000) NOT NULL,
  `location` varchar(200) DEFAULT NULL,
  `agreement_type` varchar(100) DEFAULT NULL,
  `status` varchar(40) DEFAULT NULL,
  `status_date` date DEFAULT NULL,
  `url` varchar(400) NOT NULL,
  PRIMARY KEY (`slug`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `oig_cia_entity` (
  `slug` varchar(200) NOT NULL,
  `name_key` varchar(255) NOT NULL,
  PRIMARY KEY (`slug`,`name_key`),
  KEY `ix_oig_cia_entity_key` (`name_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `oig_exclusion` (
  `id` int unsigned NOT NULL AUTO_INCREMENT,
  `npi` char(10) DEFAULT NULL,
  `last_name` varchar(100) DEFAULT NULL,
  `first_name` varchar(100) DEFAULT NULL,
  `middle_name` varchar(100) DEFAULT NULL,
  `business_name` varchar(200) DEFAULT NULL,
  `general_category` varchar(100) DEFAULT NULL,
  `specialty` varchar(100) DEFAULT NULL,
  `address` varchar(200) DEFAULT NULL,
  `city` varchar(100) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  `zip` varchar(10) DEFAULT NULL,
  `exclusion_type` varchar(20) DEFAULT NULL,
  `exclusion_date` date DEFAULT NULL,
  `reinstatement_date` date DEFAULT NULL,
  `waiver_date` date DEFAULT NULL,
  `waiver_state` varchar(2) DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_oig_exclusion_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_company` (
  `company_id` varchar(20) NOT NULL,
  `name` varchar(255) NOT NULL,
  `other_names` varchar(1300) DEFAULT NULL,
  `state` varchar(40) DEFAULT NULL,
  `country` varchar(100) DEFAULT NULL,
  `general_amount` double NOT NULL,
  `research_amount` double NOT NULL,
  `invested_amount` double NOT NULL,
  `interest_value` double NOT NULL,
  `first_year` smallint DEFAULT NULL,
  `last_year` smallint DEFAULT NULL,
  PRIMARY KEY (`company_id`),
  KEY `ix_op_company_name` (`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_company_key` (
  `company_id` varchar(20) NOT NULL,
  `name_key` varchar(255) NOT NULL,
  PRIMARY KEY (`company_id`,`name_key`),
  KEY `ix_op_company_key_key` (`name_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_company_nature` (
  `company_id` varchar(20) NOT NULL,
  `program_year` smallint NOT NULL,
  `nature` varchar(200) NOT NULL,
  `amount` double NOT NULL,
  `records` int NOT NULL,
  PRIMARY KEY (`company_id`,`nature`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_company_product` (
  `company_id` varchar(20) NOT NULL,
  `product_rank` smallint NOT NULL,
  `program_year` smallint NOT NULL,
  `product` varchar(500) NOT NULL,
  `kind` varchar(40) DEFAULT NULL,
  `category` varchar(500) DEFAULT NULL,
  `amount` double NOT NULL,
  `records` int NOT NULL,
  `slug` varchar(200) DEFAULT NULL,
  PRIMARY KEY (`company_id`,`product_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_company_profile_raw` (
  `company_id` varchar(20) DEFAULT NULL,
  `name` varchar(255) DEFAULT NULL,
  `state` varchar(40) DEFAULT NULL,
  `country` varchar(100) DEFAULT NULL,
  `alt_name1` varchar(255) DEFAULT NULL,
  `alt_name2` varchar(255) DEFAULT NULL,
  `alt_name3` varchar(255) DEFAULT NULL,
  `alt_name4` varchar(255) DEFAULT NULL,
  `alt_name5` varchar(255) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_company_reach` (
  `company_id` varchar(20) NOT NULL,
  `providers` int NOT NULL,
  `amount` double NOT NULL,
  PRIMARY KEY (`company_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_company_recipient` (
  `company_id` varchar(20) NOT NULL,
  `recipient_rank` smallint NOT NULL,
  `npi` char(10) NOT NULL,
  `total_amount` double NOT NULL,
  `general_amount` double NOT NULL,
  `research_amount` double NOT NULL,
  `associated_research_amount` double NOT NULL,
  `ownership_amount` double NOT NULL,
  `records` int NOT NULL,
  PRIMARY KEY (`company_id`,`recipient_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_company_specialty` (
  `company_id` varchar(20) NOT NULL,
  `specialty_rank` tinyint NOT NULL,
  `specialty` varchar(255) NOT NULL,
  `providers` int NOT NULL,
  `amount` double NOT NULL,
  PRIMARY KEY (`company_id`,`specialty_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_company_year` (
  `company_id` varchar(20) NOT NULL,
  `program_year` smallint NOT NULL,
  `general_amount` double NOT NULL,
  `research_amount` double NOT NULL,
  `invested_amount` double NOT NULL,
  `interest_value` double NOT NULL,
  `general_records` int NOT NULL,
  `research_records` int NOT NULL,
  `ownership_records` int NOT NULL,
  PRIMARY KEY (`company_id`,`program_year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_company_year_raw` (
  `company_id` varchar(20) DEFAULT NULL,
  `name` varchar(255) DEFAULT NULL,
  `state` varchar(40) DEFAULT NULL,
  `country` varchar(100) DEFAULT NULL,
  `general_amount` double DEFAULT NULL,
  `research_amount` double DEFAULT NULL,
  `invested_amount` double DEFAULT NULL,
  `interest_value` double DEFAULT NULL,
  `general_records` int DEFAULT NULL,
  `research_records` int DEFAULT NULL,
  `ownership_records` int DEFAULT NULL,
  `program_year` varchar(10) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_product` (
  `slug` varchar(200) NOT NULL,
  `name` varchar(500) NOT NULL,
  `kind` varchar(40) DEFAULT NULL,
  `category` varchar(500) DEFAULT NULL,
  `ndc` varchar(100) DEFAULT NULL,
  `device_id` varchar(100) DEFAULT NULL,
  `program_year` smallint NOT NULL,
  `amount` double NOT NULL,
  `records` int NOT NULL,
  `companies` int NOT NULL,
  `providers` int NOT NULL,
  `ndc_key` char(9) GENERATED ALWAYS AS (if(((length(`ndc`) - length(replace(`ndc`,_utf8mb4'-',_utf8mb4''))) in (1,2)),concat(lpad(substring_index(`ndc`,_utf8mb4'-',1),5,_utf8mb4'0'),lpad(substring_index(substring_index(`ndc`,_utf8mb4'-',2),_utf8mb4'-',-(1)),4,_utf8mb4'0')),NULL)) VIRTUAL,
  PRIMARY KEY (`slug`),
  KEY `ix_op_product_name` (`name`(100)),
  KEY `ix_op_product_device_id` (`device_id`),
  KEY `ix_op_product_ndc_key` (`ndc_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_product_company` (
  `slug` varchar(200) NOT NULL,
  `company_id` varchar(20) NOT NULL,
  `amount` double NOT NULL,
  `records` int NOT NULL,
  PRIMARY KEY (`slug`,`company_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_product_nature` (
  `slug` varchar(200) NOT NULL,
  `nature` varchar(200) NOT NULL,
  `amount` double NOT NULL,
  `records` int NOT NULL,
  PRIMARY KEY (`slug`,`nature`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_product_npi` (
  `slug` varchar(200) NOT NULL,
  `npi` char(10) NOT NULL,
  `amount` double NOT NULL,
  `records` int NOT NULL,
  PRIMARY KEY (`slug`,`npi`),
  KEY `ix_op_product_npi_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_product_recipient` (
  `slug` varchar(200) NOT NULL,
  `recipient_rank` smallint NOT NULL,
  `npi` char(10) NOT NULL,
  `amount` double NOT NULL,
  `records` int NOT NULL,
  PRIMARY KEY (`slug`,`recipient_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_product_research` (
  `slug` varchar(200) NOT NULL,
  `program_year` smallint NOT NULL,
  `amount` double NOT NULL,
  `records` int NOT NULL,
  `studies` int NOT NULL,
  PRIMARY KEY (`slug`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_product_specialty` (
  `slug` varchar(200) NOT NULL,
  `specialty_rank` tinyint NOT NULL,
  `specialty` varchar(255) NOT NULL,
  `providers` int NOT NULL,
  `amount` double NOT NULL,
  PRIMARY KEY (`slug`,`specialty_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_product_study` (
  `slug` varchar(200) NOT NULL,
  `study_rank` tinyint NOT NULL,
  `study` text,
  `nct_id` varchar(200) DEFAULT NULL,
  `company_id` varchar(20) DEFAULT NULL,
  `amount` double NOT NULL,
  `records` int NOT NULL,
  PRIMARY KEY (`slug`,`study_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_provider_product` (
  `npi` char(10) NOT NULL,
  `product_rank` tinyint NOT NULL,
  `slug` varchar(200) NOT NULL,
  `amount` double NOT NULL,
  `records` int NOT NULL,
  PRIMARY KEY (`npi`,`product_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `op_research_raw` (
  `company_id` varchar(20) DEFAULT NULL,
  `amount` double DEFAULT NULL,
  `study` text,
  `nct_id` varchar(200) DEFAULT NULL,
  `product` varchar(500) DEFAULT NULL,
  `product2` varchar(500) DEFAULT NULL,
  `product3` varchar(500) DEFAULT NULL,
  `product4` varchar(500) DEFAULT NULL,
  `product5` varchar(500) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `open_payments_company` (
  `npi` char(10) NOT NULL,
  `company_rank` tinyint NOT NULL,
  `company` varchar(255) NOT NULL,
  `total_amount` double NOT NULL,
  `general_amount` double NOT NULL,
  `research_amount` double NOT NULL,
  `associated_research_amount` double NOT NULL,
  `ownership_amount` double NOT NULL,
  `records` int NOT NULL,
  `company_id` varchar(20) DEFAULT NULL,
  PRIMARY KEY (`npi`,`company_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `open_payments_company_raw` (
  `npi` char(10) DEFAULT NULL,
  `payment_type` varchar(40) DEFAULT NULL,
  `company` varchar(255) DEFAULT NULL,
  `records` int DEFAULT NULL,
  `amount` double DEFAULT NULL,
  `company_id` varchar(20) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `open_payments_nature` (
  `npi` char(10) NOT NULL,
  `nature` varchar(200) NOT NULL,
  `amount` double NOT NULL,
  `records` int NOT NULL,
  PRIMARY KEY (`npi`,`nature`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `open_payments_payer` (
  `npi` char(10) NOT NULL,
  `payer_rank` tinyint NOT NULL,
  `payer` varchar(150) NOT NULL,
  `amount` double NOT NULL,
  `records` int NOT NULL,
  `company_id` varchar(20) DEFAULT NULL,
  PRIMARY KEY (`npi`,`payer_rank`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `open_payments_raw` (
  `npi` char(10) DEFAULT NULL,
  `payer` varchar(150) DEFAULT NULL,
  `amount` double DEFAULT NULL,
  `payments` int DEFAULT NULL,
  `nature` varchar(200) DEFAULT NULL,
  `company_id` varchar(20) DEFAULT NULL,
  `product` varchar(500) DEFAULT NULL,
  `product_kind` varchar(40) DEFAULT NULL,
  `product_category` varchar(500) DEFAULT NULL,
  `ndc` varchar(100) DEFAULT NULL,
  `device_id` varchar(100) DEFAULT NULL,
  `product2` varchar(500) DEFAULT NULL,
  `product_kind2` varchar(40) DEFAULT NULL,
  `product_category2` varchar(500) DEFAULT NULL,
  `ndc2` varchar(100) DEFAULT NULL,
  `device_id2` varchar(100) DEFAULT NULL,
  `product3` varchar(500) DEFAULT NULL,
  `product_kind3` varchar(40) DEFAULT NULL,
  `product_category3` varchar(500) DEFAULT NULL,
  `ndc3` varchar(100) DEFAULT NULL,
  `device_id3` varchar(100) DEFAULT NULL,
  `product4` varchar(500) DEFAULT NULL,
  `product_kind4` varchar(40) DEFAULT NULL,
  `product_category4` varchar(500) DEFAULT NULL,
  `ndc4` varchar(100) DEFAULT NULL,
  `device_id4` varchar(100) DEFAULT NULL,
  `product5` varchar(500) DEFAULT NULL,
  `product_kind5` varchar(40) DEFAULT NULL,
  `product_category5` varchar(500) DEFAULT NULL,
  `ndc5` varchar(100) DEFAULT NULL,
  `device_id5` varchar(100) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `open_payments_summary` (
  `npi` char(10) NOT NULL,
  `program_year` smallint NOT NULL,
  `total_amount` double NOT NULL,
  `records` int NOT NULL,
  `payers` int NOT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `open_payments_year` (
  `npi` char(10) NOT NULL,
  `program_year` smallint NOT NULL,
  `general_amount` double NOT NULL,
  `general_records` int NOT NULL,
  `research_amount` double NOT NULL,
  `research_records` int NOT NULL,
  `associated_research_amount` double NOT NULL,
  `associated_research_records` int NOT NULL,
  `invested_amount` double NOT NULL,
  `interest_value` double NOT NULL,
  `ownership_records` int NOT NULL,
  PRIMARY KEY (`npi`,`program_year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `open_payments_year_raw` (
  `npi` char(10) DEFAULT NULL,
  `program_year` varchar(10) DEFAULT NULL,
  `general_amount` double DEFAULT NULL,
  `general_records` int DEFAULT NULL,
  `research_amount` double DEFAULT NULL,
  `research_records` int DEFAULT NULL,
  `associated_research_amount` double DEFAULT NULL,
  `associated_research_records` int DEFAULT NULL,
  `invested_amount` double DEFAULT NULL,
  `interest_value` double DEFAULT NULL,
  `ownership_records` int DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `other_names` (
  `ID` int unsigned NOT NULL AUTO_INCREMENT,
  `NPI` char(10) DEFAULT NULL,
  `Provider_Other_Organization_Name` varchar(100) DEFAULT NULL,
  `Provider_Other_Organization_Name_Type_Code` char(1) DEFAULT NULL,
  `Created_Date` date DEFAULT NULL,
  PRIMARY KEY (`ID`),
  KEY `idx_other_names_NPI` (`NPI`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Temporary holding table for other names';

CREATE TABLE `other_names_temp` (
  `NPI` char(10) DEFAULT NULL,
  `Provider_Other_Organization_Name` varchar(100) DEFAULT NULL,
  `Provider_Other_Organization_Name_Type_Code` char(1) DEFAULT NULL,
  `Created_Date` date DEFAULT NULL,
  KEY `idx_other_names_NPI` (`NPI`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='Temporary holding table for other names';

CREATE TABLE `other_provider_identifier_issuer_codes` (
  `other_provider_type_code` char(2) NOT NULL,
  `other_provider_type_description` varchar(10) NOT NULL,
  PRIMARY KEY (`other_provider_type_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `other_provider_name_type_codes` (
  `name_type_code` smallint NOT NULL,
  `name_type_description` varchar(50) NOT NULL,
  `entity_name_type_code` varchar(25) NOT NULL,
  PRIMARY KEY (`name_type_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `overture_checked` (
  `addr_key` char(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `release` varchar(20) NOT NULL,
  `checked_at` datetime NOT NULL,
  PRIMARY KEY (`addr_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `part_d_brand` (
  `brand` varchar(255) NOT NULL,
  `generic` varchar(255) DEFAULT NULL,
  `data_year` smallint NOT NULL,
  `prescribers` int NOT NULL,
  `claims` bigint NOT NULL,
  `drug_cost` double DEFAULT NULL,
  `beneficiaries` bigint DEFAULT NULL,
  PRIMARY KEY (`brand`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `part_d_brand_prescriber` (
  `brand` varchar(255) NOT NULL,
  `npi` char(10) NOT NULL,
  `claims` int NOT NULL,
  `fills` double DEFAULT NULL,
  `drug_cost` double DEFAULT NULL,
  `beneficiaries` int DEFAULT NULL,
  PRIMARY KEY (`brand`,`npi`),
  KEY `ix_part_d_brand_prescriber_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `part_d_drug_raw` (
  `npi` char(10) DEFAULT NULL,
  `brand` varchar(255) DEFAULT NULL,
  `generic` varchar(255) DEFAULT NULL,
  `claims` int DEFAULT NULL,
  `fills` double DEFAULT NULL,
  `drug_cost` double DEFAULT NULL,
  `beneficiaries` int DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `practice_locations` (
  `ID` int unsigned NOT NULL AUTO_INCREMENT,
  `NPI` char(10) NOT NULL,
  `Provider_Secondary_Practice_Location_Address_Line_1` varchar(55) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Line_2` varchar(55) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_City_Name` varchar(40) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_State_Name` varchar(40) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Postal_Code` varchar(20) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Country_Code` varchar(2) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Telephone_Number` varchar(20) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Telephone_Extension` varchar(5) DEFAULT NULL,
  `Provider_Practice_Location_Address_Fax_Number` varchar(20) DEFAULT NULL,
  PRIMARY KEY (`ID`),
  KEY `idx_NPI` (`NPI`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `practice_locations_temp` (
  `NPI` char(10) NOT NULL,
  `Provider_Secondary_Practice_Location_Address_Line_1` varchar(55) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Line_2` varchar(55) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_City_Name` varchar(40) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_State_Name` varchar(40) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Postal_Code` varchar(20) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Country_Code` varchar(2) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Telephone_Number` varchar(20) DEFAULT NULL,
  `Provider_Secondary_Practice_Location_Address_Telephone_Extension` varchar(5) DEFAULT NULL,
  `Provider_Practice_Location_Address_Fax_Number` varchar(20) DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `product_adverse_events` (
  `slug` varchar(200) NOT NULL,
  `kind` varchar(10) NOT NULL,
  `query_name` varchar(500) NOT NULL,
  `reports` int DEFAULT NULL,
  `serious` int DEFAULT NULL,
  `deaths` int DEFAULT NULL,
  `injuries` int DEFAULT NULL,
  `malfunctions` int DEFAULT NULL,
  `fetched_at` datetime NOT NULL,
  PRIMARY KEY (`slug`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `product_part_d_brand` (
  `slug` varchar(200) NOT NULL,
  `brand` varchar(255) NOT NULL,
  PRIMARY KEY (`slug`,`brand`),
  KEY `ix_product_part_d_brand_brand` (`brand`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `product_prescriber` (
  `slug` varchar(200) NOT NULL,
  `npi` char(10) NOT NULL,
  `amount` double NOT NULL,
  `records` int NOT NULL,
  `claims` int NOT NULL,
  `drug_cost` double DEFAULT NULL,
  `beneficiaries` int DEFAULT NULL,
  PRIMARY KEY (`slug`,`npi`),
  KEY `ix_product_prescriber_amount` (`slug`,`amount`),
  KEY `ix_product_prescriber_claims` (`slug`,`claims`),
  KEY `ix_product_prescriber_cost` (`slug`,`drug_cost`),
  KEY `ix_product_prescriber_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `product_prescribing` (
  `slug` varchar(200) NOT NULL,
  `data_year` smallint NOT NULL,
  `payment_year` smallint NOT NULL,
  `brands` varchar(2000) NOT NULL,
  `prescribers` int NOT NULL,
  `claims` bigint NOT NULL,
  `drug_cost` double DEFAULT NULL,
  `paid_providers` int NOT NULL,
  `paid_prescribers` int NOT NULL,
  `paid_claims` bigint NOT NULL,
  `paid_drug_cost` double DEFAULT NULL,
  PRIMARY KEY (`slug`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `product_trials` (
  `slug` varchar(200) NOT NULL,
  `query_name` varchar(500) NOT NULL,
  `studies` int DEFAULT NULL,
  `recruiting` int DEFAULT NULL,
  `fetched_at` datetime NOT NULL,
  PRIMARY KEY (`slug`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `projection_pending` (
  `npi` char(10) NOT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider` (
  `npi` char(10) NOT NULL,
  `entity_type` tinyint NOT NULL,
  `last_name` varchar(100) DEFAULT NULL,
  `first_name` varchar(100) DEFAULT NULL,
  `middle_name` varchar(100) DEFAULT NULL,
  `name_prefix` varchar(20) DEFAULT NULL,
  `name_suffix` varchar(20) DEFAULT NULL,
  `credential` varchar(100) DEFAULT NULL,
  `credential_key` varchar(100) DEFAULT NULL,
  `org_name` varchar(200) DEFAULT NULL,
  `sort_name` varchar(255) NOT NULL,
  `gender` char(1) DEFAULT NULL,
  `primary_taxonomy_code` varchar(10) DEFAULT NULL,
  `phone` varchar(20) DEFAULT NULL,
  `enumeration_date` date DEFAULT NULL,
  `last_update_date` date DEFAULT NULL,
  `row_hash` binary(16) DEFAULT NULL,
  `last_phonetic` varchar(10) GENERATED ALWAYS AS (left(soundex(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(upper(`last_name`),_utf8mb4'À',_utf8mb4'A'),_utf8mb4'Á',_utf8mb4'A'),_utf8mb4'Â',_utf8mb4'A'),_utf8mb4'Ã',_utf8mb4'A'),_utf8mb4'Ä',_utf8mb4'A'),_utf8mb4'Å',_utf8mb4'A'),_utf8mb4'Ç',_utf8mb4'C'),_utf8mb4'È',_utf8mb4'E'),_utf8mb4'É',_utf8mb4'E'),_utf8mb4'Ê',_utf8mb4'E'),_utf8mb4'Ë',_utf8mb4'E'),_utf8mb4'Ì',_utf8mb4'I'),_utf8mb4'Í',_utf8mb4'I'),_utf8mb4'Î',_utf8mb4'I'),_utf8mb4'Ï',_utf8mb4'I'),_utf8mb4'Ñ',_utf8mb4'N'),_utf8mb4'Ò',_utf8mb4'O'),_utf8mb4'Ó',_utf8mb4'O'),_utf8mb4'Ô',_utf8mb4'O'),_utf8mb4'Õ',_utf8mb4'O'),_utf8mb4'Ö',_utf8mb4'O'),_utf8mb4'Ø',_utf8mb4'O'),_utf8mb4'Ù',_utf8mb4'U'),_utf8mb4'Ú',_utf8mb4'U'),_utf8mb4'Û',_utf8mb4'U'),_utf8mb4'Ü',_utf8mb4'U'),_utf8mb4'Ý',_utf8mb4'Y')),4)) VIRTUAL,
  `first_phonetic` varchar(10) GENERATED ALWAYS AS (left(soundex(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(upper(`first_name`),_utf8mb4'À',_utf8mb4'A'),_utf8mb4'Á',_utf8mb4'A'),_utf8mb4'Â',_utf8mb4'A'),_utf8mb4'Ã',_utf8mb4'A'),_utf8mb4'Ä',_utf8mb4'A'),_utf8mb4'Å',_utf8mb4'A'),_utf8mb4'Ç',_utf8mb4'C'),_utf8mb4'È',_utf8mb4'E'),_utf8mb4'É',_utf8mb4'E'),_utf8mb4'Ê',_utf8mb4'E'),_utf8mb4'Ë',_utf8mb4'E'),_utf8mb4'Ì',_utf8mb4'I'),_utf8mb4'Í',_utf8mb4'I'),_utf8mb4'Î',_utf8mb4'I'),_utf8mb4'Ï',_utf8mb4'I'),_utf8mb4'Ñ',_utf8mb4'N'),_utf8mb4'Ò',_utf8mb4'O'),_utf8mb4'Ó',_utf8mb4'O'),_utf8mb4'Ô',_utf8mb4'O'),_utf8mb4'Õ',_utf8mb4'O'),_utf8mb4'Ö',_utf8mb4'O'),_utf8mb4'Ø',_utf8mb4'O'),_utf8mb4'Ù',_utf8mb4'U'),_utf8mb4'Ú',_utf8mb4'U'),_utf8mb4'Û',_utf8mb4'U'),_utf8mb4'Ü',_utf8mb4'U'),_utf8mb4'Ý',_utf8mb4'Y')),4)) VIRTUAL,
  PRIMARY KEY (`npi`),
  KEY `ix_provider_name` (`last_name`,`first_name`),
  KEY `ix_provider_org` (`org_name`),
  KEY `ix_provider_credential` (`credential_key`),
  KEY `ix_provider_sort` (`sort_name`),
  KEY `ix_provider_enumeration` (`enumeration_date`),
  KEY `ix_provider_last_update` (`last_update_date`),
  KEY `ix_provider_last_phonetic` (`last_phonetic`,`first_phonetic`),
  KEY `ix_provider_first_phonetic` (`first_phonetic`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_change` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `npi` char(10) NOT NULL,
  `detected_at` datetime NOT NULL,
  `change_type` varchar(20) NOT NULL,
  `old_value` varchar(300) DEFAULT NULL,
  `new_value` varchar(300) DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_provider_change_npi` (`npi`,`detected_at`),
  KEY `ix_provider_change_detected` (`detected_at`,`change_type`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_credential` (
  `npi` char(10) NOT NULL,
  `ord` tinyint NOT NULL,
  `credential` varchar(60) NOT NULL,
  PRIMARY KEY (`npi`,`ord`),
  KEY `ix_provider_credential_credential` (`credential`,`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_deactivated` (
  `npi` char(10) NOT NULL,
  `deactivation_date` date DEFAULT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_endpoint` (
  `id` int unsigned NOT NULL AUTO_INCREMENT,
  `npi` char(10) NOT NULL,
  `endpoint_type` varchar(50) DEFAULT NULL,
  `endpoint_type_description` varchar(200) DEFAULT NULL,
  `endpoint` text NOT NULL,
  `endpoint_description` text,
  `use_description` varchar(200) DEFAULT NULL,
  `content_description` varchar(200) DEFAULT NULL,
  `affiliation_name` varchar(200) DEFAULT NULL,
  `affiliation_city` varchar(100) DEFAULT NULL,
  `affiliation_state` varchar(100) DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_provider_endpoint_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_identifier` (
  `npi` char(10) NOT NULL,
  `slot` tinyint NOT NULL,
  `identifier` varchar(100) NOT NULL,
  `type_code` varchar(2) DEFAULT NULL,
  `state` varchar(2) DEFAULT NULL,
  `issuer` varchar(200) DEFAULT NULL,
  PRIMARY KEY (`npi`,`slot`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_location` (
  `id` int unsigned NOT NULL AUTO_INCREMENT,
  `npi` char(10) NOT NULL,
  `is_primary` tinyint NOT NULL,
  `address1` varchar(100) DEFAULT NULL,
  `address2` varchar(100) DEFAULT NULL,
  `city` varchar(60) DEFAULT NULL,
  `state` varchar(40) DEFAULT NULL,
  `zip5` char(5) DEFAULT NULL,
  `zip4` char(4) DEFAULT NULL,
  `postal_code` varchar(20) DEFAULT NULL,
  `country_code` varchar(2) DEFAULT NULL,
  `phone` varchar(20) DEFAULT NULL,
  `addr_key` char(32) CHARACTER SET ascii COLLATE ascii_bin GENERATED ALWAYS AS (if(((`zip5` is null) or (`address1` is null) or (`state` is null)),NULL,md5(concat_ws(_utf8mb4'|',upper(trim(regexp_replace(`address1`,_utf8mb4'[^[:alnum:]]+',_utf8mb4' '))),upper(trim(coalesce(`city`,_utf8mb4''))),upper(`state`),`zip5`)))) VIRTUAL,
  PRIMARY KEY (`id`),
  KEY `ix_provider_location_state_city` (`state`,`city`,`npi`),
  KEY `ix_provider_location_zip` (`zip5`,`npi`),
  KEY `ix_provider_location_npi` (`npi`,`state`,`zip5`,`city`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_map` (
  `npi` char(10) NOT NULL,
  `addr_key` char(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `lat` double NOT NULL,
  `lon` double NOT NULL,
  `approximate` tinyint NOT NULL,
  `pt` point NOT NULL /*!80003 SRID 0 */,
  `source` varchar(12) NOT NULL DEFAULT 'zip',
  PRIMARY KEY (`npi`,`addr_key`),
  SPATIAL KEY `sx_provider_map_pt` (`pt`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_map_credential` (
  `credential` varchar(60) NOT NULL,
  `cell` int NOT NULL,
  `npi` char(10) NOT NULL,
  PRIMARY KEY (`credential`,`cell`,`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_map_specialty` (
  `taxonomy_code` varchar(10) NOT NULL,
  `cell` int NOT NULL,
  `npi` char(10) NOT NULL,
  PRIMARY KEY (`taxonomy_code`,`cell`,`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_org_name` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `npi` char(10) NOT NULL,
  `name` varchar(200) NOT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_provider_org_name_npi` (`npi`),
  FULLTEXT KEY `ft_provider_org_name` (`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_other_name` (
  `id` int unsigned NOT NULL AUTO_INCREMENT,
  `npi` char(10) NOT NULL,
  `name` varchar(200) NOT NULL,
  `type_code` varchar(2) DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_provider_other_name_npi` (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_profile` (
  `npi` char(10) NOT NULL,
  `is_sole_proprietor` tinyint DEFAULT NULL,
  `is_subpart` tinyint DEFAULT NULL,
  `parent_org_name` varchar(200) DEFAULT NULL,
  `official_prefix` varchar(20) DEFAULT NULL,
  `official_first_name` varchar(100) DEFAULT NULL,
  `official_middle_name` varchar(100) DEFAULT NULL,
  `official_last_name` varchar(100) DEFAULT NULL,
  `official_suffix` varchar(20) DEFAULT NULL,
  `official_credential` varchar(100) DEFAULT NULL,
  `official_title` varchar(200) DEFAULT NULL,
  `official_phone` varchar(20) DEFAULT NULL,
  `mailing_address1` varchar(100) DEFAULT NULL,
  `mailing_address2` varchar(100) DEFAULT NULL,
  `mailing_city` varchar(100) DEFAULT NULL,
  `mailing_state` varchar(100) DEFAULT NULL,
  `mailing_postal_code` varchar(20) DEFAULT NULL,
  `mailing_country_code` varchar(2) DEFAULT NULL,
  `mailing_phone` varchar(20) DEFAULT NULL,
  `mailing_fax` varchar(20) DEFAULT NULL,
  `practice_fax` varchar(20) DEFAULT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_quality` (
  `npi` char(10) NOT NULL,
  `label` tinyint NOT NULL,
  `reasons` int NOT NULL,
  `activity` smallint NOT NULL,
  PRIMARY KEY (`npi`),
  KEY `ix_provider_quality_label` (`label`,`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_search` (
  `taxonomy_code` varchar(10) NOT NULL,
  `state` varchar(40) NOT NULL DEFAULT '',
  `city` varchar(60) NOT NULL DEFAULT '',
  `zip5` char(5) NOT NULL DEFAULT '',
  `npi` char(10) NOT NULL,
  PRIMARY KEY (`taxonomy_code`,`state`,`city`,`zip5`,`npi`),
  KEY `ix_provider_search_zip` (`taxonomy_code`,`zip5`,`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_state_license` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `npi` char(10) NOT NULL,
  `state` char(2) NOT NULL,
  `kind` varchar(10) NOT NULL,
  `source` varchar(20) NOT NULL,
  `license_number` varchar(60) DEFAULT NULL,
  `license_type` varchar(300) DEFAULT NULL,
  `status` varchar(300) DEFAULT NULL,
  `expiration_date` date DEFAULT NULL,
  `discipline` varchar(300) DEFAULT NULL,
  `action_date` date DEFAULT NULL,
  `action` text,
  `description` text,
  `verify_url` varchar(1000) DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_provider_state_license_npi` (`npi`),
  KEY `ix_provider_state_license_state` (`state`,`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `provider_taxonomy` (
  `npi` char(10) NOT NULL,
  `slot` tinyint NOT NULL,
  `taxonomy_code` varchar(10) NOT NULL,
  `is_primary` tinyint NOT NULL,
  `license_no` varchar(50) DEFAULT NULL,
  `license_state` varchar(2) DEFAULT NULL,
  PRIMARY KEY (`npi`,`slot`),
  KEY `ix_provider_taxonomy_code` (`taxonomy_code`,`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `quality_pending` (
  `npi` char(10) NOT NULL,
  PRIMARY KEY (`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `quality_search` (
  `label` tinyint NOT NULL,
  `state` varchar(40) NOT NULL DEFAULT '',
  `city` varchar(60) NOT NULL DEFAULT '',
  `zip5` char(5) NOT NULL DEFAULT '',
  `npi` char(10) NOT NULL,
  PRIMARY KEY (`label`,`state`,`city`,`zip5`,`npi`),
  KEY `ix_quality_search_zip` (`label`,`zip5`,`npi`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `reference_data` (
  `source` varchar(30) NOT NULL,
  `version` varchar(500) NOT NULL,
  `source_url` varchar(1000) NOT NULL,
  `rows_loaded` int NOT NULL,
  `loaded_at` datetime NOT NULL,
  `checked_at` datetime NOT NULL,
  PRIMARY KEY (`source`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `schema_migrations` (
  `version` int NOT NULL,
  `name` varchar(255) NOT NULL,
  `sha256` char(64) NOT NULL,
  `applied_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`version`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `search_total` (
  `name` varchar(40) NOT NULL,
  `total` bigint NOT NULL,
  `counted_at` datetime NOT NULL,
  PRIMARY KEY (`name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `sec_company` (
  `cik` int NOT NULL,
  `ticker` varchar(20) NOT NULL,
  `name` varchar(255) NOT NULL,
  `name_key` varchar(255) DEFAULT NULL,
  `exchange` varchar(40) DEFAULT NULL,
  PRIMARY KEY (`cik`,`ticker`),
  KEY `ix_sec_company_key` (`name_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `sole_proprietor_codes` (
  `sole_proprietor_code` char(1) NOT NULL,
  `sole_proprietor_description` varchar(100) DEFAULT NULL,
  PRIMARY KEY (`sole_proprietor_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `specialty_code_category` (
  `code` varchar(10) NOT NULL,
  `category` varchar(80) NOT NULL,
  `subcategory` varchar(80) NOT NULL,
  PRIMARY KEY (`code`,`category`,`subcategory`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `state_codes` (
  `state_reference_code` char(2) NOT NULL,
  `state_name` varchar(50) NOT NULL,
  `state_type_code` char(1) NOT NULL,
  PRIMARY KEY (`state_reference_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `state_license_raw` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `state` char(2) NOT NULL,
  `kind` varchar(10) NOT NULL,
  `source` varchar(20) NOT NULL,
  `license_number` varchar(60) DEFAULT NULL,
  `license_key` varchar(60) GENERATED ALWAYS AS (nullif(trim(leading _utf8mb4'0' from regexp_replace(coalesce(`license_number`,_utf8mb4''),_utf8mb4'[^0-9]',_utf8mb4'')),_utf8mb4'')) STORED,
  `license_key_last` varchar(60) GENERATED ALWAYS AS (if(regexp_like(coalesce(`license_number`,_utf8mb4''),_utf8mb4'[0-9]'),nullif(trim(leading _utf8mb4'0' from regexp_replace(`license_number`,_utf8mb4'^.*?([0-9]+)[^0-9]*$',_utf8mb4'$1')),_utf8mb4''),NULL)) STORED,
  `last_name` varchar(200) DEFAULT NULL,
  `first_name` varchar(200) DEFAULT NULL,
  `full_name` varchar(300) DEFAULT NULL,
  `license_type` varchar(300) DEFAULT NULL,
  `status` varchar(300) DEFAULT NULL,
  `expiration_date` date DEFAULT NULL,
  `discipline` varchar(300) DEFAULT NULL,
  `action_date` date DEFAULT NULL,
  `action` text,
  `description` text,
  `verify_url` varchar(1000) DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_state_license_raw_key` (`state`,`license_key`),
  KEY `ix_state_license_raw_key_last` (`state`,`license_key_last`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `state_license_version` (
  `state` char(2) NOT NULL,
  `version` varchar(500) NOT NULL,
  `rows_loaded` bigint NOT NULL,
  `loaded_at` datetime NOT NULL,
  PRIMARY KEY (`state`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `state_medicaid` (
  `npi` char(10) NOT NULL,
  `state` char(2) NOT NULL,
  `enrolled_since` date DEFAULT NULL,
  `provider_type` varchar(100) DEFAULT NULL,
  PRIMARY KEY (`npi`,`state`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `statelookup` (
  `State` char(2) NOT NULL DEFAULT '',
  `StateName` varchar(32) DEFAULT NULL,
  PRIMARY KEY (`State`) USING BTREE
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

CREATE TABLE `subpart_codes` (
  `subpart_code` char(1) NOT NULL,
  `subpart_description` varchar(100) DEFAULT NULL,
  PRIMARY KEY (`subpart_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `taxonomy_codes` (
  `Taxonomy_Code` varchar(10) NOT NULL,
  `Grouping` varchar(100) DEFAULT NULL,
  `Classification` varchar(150) DEFAULT NULL,
  `Specialization` varchar(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `Definition` varchar(4000) DEFAULT NULL,
  `Notes` varchar(1600) DEFAULT NULL,
  `Display_Name` varchar(150) DEFAULT NULL,
  `Section` varchar(20) DEFAULT NULL,
  `Nucc_Version` varchar(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  PRIMARY KEY (`Taxonomy_Code`),
  KEY `ix_taxonomy_codes_classification` (`Classification`,`Specialization`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `taxonomy_codes_old` (
  `Taxonomy_Code` varchar(10) NOT NULL,
  `Grouping` varchar(100) DEFAULT NULL,
  `Classification` varchar(150) DEFAULT NULL,
  `Specialization` varchar(75) DEFAULT NULL,
  `Definition` varchar(4000) DEFAULT NULL,
  `Effective_Date` date DEFAULT NULL,
  `Deactivation_Date` date DEFAULT NULL,
  `Last_Modified_Date` date DEFAULT NULL,
  `Notes` varchar(1600) DEFAULT NULL,
  `Display_Name` varchar(150) DEFAULT NULL,
  PRIMARY KEY (`Taxonomy_Code`)
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

CREATE TABLE `us_city_populations` (
  `State` varchar(50) NOT NULL DEFAULT '',
  `City` varchar(50) NOT NULL DEFAULT '',
  `population` int DEFAULT NULL,
  PRIMARY KEY (`State`,`City`) USING BTREE
) ENGINE=InnoDB DEFAULT CHARSET=latin1;

CREATE TABLE `zip_centroid` (
  `zip5` char(5) NOT NULL,
  `lat` decimal(9,6) NOT NULL,
  `lon` decimal(9,6) NOT NULL,
  PRIMARY KEY (`zip5`),
  KEY `ix_zip_centroid_lat_lon` (`lat`,`lon`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `zip_county` (
  `zip5` char(5) NOT NULL,
  `county_fips` char(5) NOT NULL,
  `res_ratio` decimal(12,10) NOT NULL,
  `bus_ratio` decimal(12,10) NOT NULL,
  `oth_ratio` decimal(12,10) NOT NULL,
  `tot_ratio` decimal(12,10) NOT NULL,
  `usps_city` varchar(64) DEFAULT NULL,
  `usps_state` char(2) DEFAULT NULL,
  `year` smallint NOT NULL,
  `quarter` tinyint NOT NULL,
  PRIMARY KEY (`zip5`,`county_fips`),
  KEY `ix_zip_county_county` (`county_fips`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
