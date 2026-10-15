# Migrations: CMS and OIG datasets

Exclusions, Medicare opt-out and order-and-refer, Care Compare, facilities, Medicare billing and prescribing, MIPS and facility quality, Medicare enrollment, and the CMS files used as signs of practice.

Applied in number order together with the other folders (see [../README.md](../README.md)).

| File | What |
|---|---|
| `022_oig_exclusion.sql` … `024_medicare_order_referring.sql` | Compliance: OIG LEIE, Medicare opt-out, order & referring (item 2). |
| `025_cc_dac_raw.sql` … `028_cc_facility_affiliation.sql` | Care Compare: raw DAC template, clinicians, group practices, facility affiliations (item 3). |
| `029_cms_hospital.sql` … `031_cms_facility_npi.sql` | Facilities: hospitals, nursing homes, CCN ↔ NPI from the enrollments (item 4). |
| `033_medicare_utilization.sql` … `036_medicare_part_d.sql` | Medicare Part B totals, raw by-service template, top 5 services, Part D (item 5). |
| `060_cc_mips.sql` | Clinician MIPS scores (individual, group, APM entity), item 14. |
| `061_cms_hospital_quality.sql` | Hospital outcome measure counts (better/worse than national) and the HCAHPS summary star rating, item 14. |
| `062_cms_home_health_hospice.sql` | Home health agencies and hospices with their star ratings, item 14. |
| `078_medicare_enrollment.sql` | Medicare enrollment (PPEF) and group reassignments (redesign step 1.5). |
| `081_cc_dac_raw_address.sql`, `082_cc_clinician_location.sql` | Care Compare practice addresses per clinician and ZIP code, for the record-quality check (Phase 3 step 3.1). |
| `085_practice_signals.sql` | Signs of current practice: opioid treatment programs, Medicare revalidation list, equipment suppliers that billed Medicare (Phase 3 step 3.1). |
| `086_cms_pos_facility.sql` | CMS Provider of Services: every certified facility, active or terminated, for closures (Phase 3 step 3.1). |
| `100_medicare_history.sql` | Older Medicare years (2013 on): Part B billing and Part D prescribing per NPI and data year, and the file each year was loaded from (Phase 3). |
| `104_cms_facility_enrollment.sql` | One row per facility Medicare enrollment (ID, CCN, NPI, associate ID), filled with cms_facility_npi, to join owners to facilities (Phase 3 step 3.6 part 2). |
| `105_facility_owner.sql` | Facility owners from the CMS All Owners files: one row per enrollment, owner and role, and one per owner (step 3.6 part 2). |
| `106_facility_ownership_change.sql` | Hospital and nursing home changes of ownership since 2016, and the sellers' owners before (step 3.6 part 2). |
| `107_nursing_home_inspections.sql` | Nursing home health and fire safety deficiencies summarized per CCN (inspections, counts by cycle and scope and severity, the most serious and newest citations, tag wording) and penalties (step 3.6 part 2). |
