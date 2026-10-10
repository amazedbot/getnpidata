-- 070_fda_ndc_lookups.sql
--
-- Lookups for the product pages' FDA section (CLAUDE.md §7 Stage 5.5 item 19, part 2): a product whose reported NDC
-- isn't listed is matched by brand name, and a listing without a label or drug class borrows them from another listing
-- under the same FDA application (e.g. a repackager's).

ALTER TABLE `fda_ndc_product`
  ADD KEY `ix_fda_ndc_product_brand` (`brand_name`(100)),
  ADD KEY `ix_fda_ndc_product_application` (`application_number`);
