-- 092_builder_old_location_label.sql
--
-- Address type labels after an incremental projection relabel only the addresses whose inputs changed (the live check of
-- 2026-10-13: a weekly's 32,641 re-saved providers shared addresses with 1.85M practice locations, all relabelled). An
-- address's label depends on who practises there and, per location, on a suite or floor in the address and on whether the
-- provider is an organization. label_input keeps those two per old location (bit 1 = suite or floor, bit 2 = organization),
-- so the projection queues in address_pending only the addresses where an (NPI, address, label_input) row appeared or went.

ALTER TABLE `builder_old_location`
  ADD COLUMN `label_input` TINYINT NULL,
  ALGORITHM=INSTANT;
