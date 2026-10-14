# Migrations: Open Payments, companies and products

Industry payments per provider, the company and product pages, and their FDA, SEC, OIG, spending, price and prescribing data.

Applied in number order together with the other folders (see [../README.md](../README.md)).

| File | What |
|---|---|
| `041_open_payments_raw.sql` … `044_open_payments_payer.sql` | Open Payments: raw template, per-NPI summary, by nature of payment, top 3 payers (item 6). |
| `058_open_payments_year.sql` | Open Payments per NPI and program year: general, research, research as principal investigator, ownership (item 13). |
| `059_open_payments_company.sql` | The top 5 companies per NPI over all program years, by payment type (item 13). |
| `065_open_payments_companies.sql` | Company pages (item 17): companies, their payments per year, natures, products, specialties and top providers; company IDs on the per-NPI payer tables. |
| `066_company_records.sql` | FDA recalls, SEC registrants and OIG integrity agreements for the company pages, matched by name key (item 17 extras). |
| `067_company_parent.sql` | The hand-made list of subsidiaries and their public parent companies (SEC CIK), item 17. |
| `068_open_payments_products.sql` | Product pages (item 19, part 1): every product named in the newest year's general and research payments, its companies, kinds of payment, specialties, top providers and studies; each provider's top products. |
| `069_fda_products.sql` | What a product is (item 19, part 2): FDA's NDC directory, Drugs@FDA approvals, the labels and GUDID device records of the products named in Open Payments, 510(k)/PMA decisions; `op_product.ndc_key`. |
| `070_fda_ndc_lookups.sql` | Brand-name and application indexes on the NDC directory (product pages, item 19 part 2). |
| `071_product_public_data.sql` | Product pages, part 3 (item 19): CMS spending by drug, NADAC prices, FDA shortages, the adverse event and ClinicalTrials.gov caches; FULLTEXT on FDA recall descriptions. |
| `072_nadac_classification.sql` | NADAC classification widened for B-ANDA / B-BIO (item 19, part 3). |
| `073_product_prescribing.sql` | Product pages, part 4 (item 19): every (drug product, NPI) payment pair, and Medicare Part D prescribing per brand and NPI. |
| `074_product_prescribing_cache.sql` | The prescribing overlap precomputed per product (item 19, part 4). |
