# getnpidata to-do

The working list: what is being done now, what comes next, and what waits for a decision. The full plan, with the
reasons and the data behind each item, is [PRODUCT_PLAN.md](PRODUCT_PLAN.md); this file only tracks the work.

**How it is kept:** updated in the same commit as each decision or finished step. Finished items are ~~crossed out~~
with the date and pull request, and stay in the list. New items are added only when the owner decides on them;
proposals that wait for a yes are under "Waiting for the owner". Work goes one item at a time, and each waits for the
owner's "go" (PRODUCT_PLAN.md §5).

Last updated: 2026-10-15.

## In progress

Fixes the owner found (2026-10-14), one pull request each:
- ~~**A:** nationwide lists and exports: no filter required, category alone, `quality` / `addressType` alone, the ready-made file of every provider rebuilt by the loader, Excel over its limit refused with a note, the pager says how many pages can be browsed~~ (2026-10-14, PRs #83 and #84)
- ~~**B:** the map's filters laid out like the search page's, one "Search in this area" button, a clear message when the area is too large~~ (2026-10-15)
- ~~**C:** the map's area as a list and a CSV (a `bbox` filter in the list, the exports, the API and the client)~~ (2026-10-15)
- ~~**Listings that confirm** (owner, 2026-10-15): an open Overture listing is a sign of practice only when a confirming source backs it; a 10+ year old record with only a listing is Check~~ (2026-10-15)

## Next, in order

1. **Speed items 6, 7, 13:**
   - 6: Socrata delta pulls (TX, WA, IL, CO, CT, DE) with a periodic full pull;
   - 7: independent sources, states and the Vermont feed in parallel (2–4 at a time);
   - 13: build the map-by-credential table once; run credentials, address types and area counts side by side.
2. **Phase 3, the rest** (data quality signals and change history):
   - **New data, approved 2026-10-13 (not built yet):**
     - **Older Medicare years, 2013–2023** (Part B by provider and Part D prescribers by provider, data.cms.gov), shown as "last billed Medicare in [year]" and as a reason on the quality label.
   - **Step 3.6 (owner, 2026-10-14):** a **"Facilities & hospitals"** home page (`/facilities`, `/hospitals` opening it on hospitals: every certified facility by type, filters, map, ratings, exports) and a **"Groups & organizations"** home page (`/organizations`, `/groups`: Medicare groups and organization NPIs, exports), both in the menu, built from data already loaded. Proposed new sources for those pages and the profiles (PRODUCT_PLAN.md Phase 3; each needs a yes): CMS All Owners and Change of Ownership files, the AHRQ health systems compendium, hospital cost reports and Form 990s, Medicare inpatient/outpatient hospital services, more Care Compare files (dialysis, surgery centres, rehab and long-term care hospitals, nursing home inspections and penalties), HIFLD hospitals, HRSA health center sites, UDS and 340B, and the ACO files.
   - **Step 3.7 (owner, 2026-10-14; researched, not built):** more states' Medicaid enrollment. Florida's Provider Master List (public, weekly, by NPI) is confirmed and is added like NY and CA. The national HHS "Medicaid Provider Enrollment Segments" (every state, 2018–2024 history) and "Medicaid Provider Spending by HCPCS" (Medicaid billing, 2018–2024) are proposals that need a yes. Texas, New Jersey, Pennsylvania, Ohio and Maine publish no bulk list (PRODUCT_PLAN.md Phase 3).
   - **Step 3.3:** change pages ("Changes in [county] this month", "New practices", "Closed or moved") and monthly change counts on county pages.
   - **Step 3.4, approved 2026-10-13:** history before October 2026 from the NBER archive of past NPPES monthly files, for trends, analytics and statistics (a large download, one file per period).

## Waiting for the owner

**Decisions:**
- **Step 3.6 sources:** which of the proposed sources for the facility and organization pages to add (PRODUCT_PLAN.md Phase 3 step 3.6).
- **Step 3.7 sources:** the national T-MSIS Medicaid enrollment history and Medicaid billing files (opendata.hhs.gov), besides Florida's list.
- **Phase 8, insurance networks (Stage 5.5 item 16):** pick the pilot state and approve the sources.
- **Phase 4 sources:** SAM.gov exclusions, state Medicaid exclusion lists.

**Actions only the owner can take:**
- **New York license status:** submit the Office of the Professions' Bulk Verifications Request Form (weekly files of currently registered licensees, free). NY today has board actions only.
- **Michigan license lists:** download the remaining lists (A–L, Nursing, Pharmacy) into `StateLicenseFilesFolder\MI`.

## Not needed for now (owner, 2026-10-13)

- **VA addresses and the VA facility list** (step 3.5 found 1 of 50 Stale records at a VA medical center).
- **Phase 2, private test hosting on Azure:** still only when a feature needs it, with the design and a cost estimate first.

## Later phases (PRODUCT_PLAN.md §3)

- **Phase 4, bulk verification and compliance:** name, specialty and state match against an uploaded roster; changes since a date; the quality label; a PDF/CSV report with sources and dates; SAM.gov and state Medicaid exclusions (need a yes).
- **Phase 5, accounts, saved lists and alerts:** sign-in, saved searches, lists and rosters, weekly alerts by email and in the site, team sharing. Needs Phase 2 and the owner's choice of identity and email services.
- **Phase 6, API and export tooling:** self-serve API keys and a usage page, a weekly change feed, bulk CSV/Parquet downloads, webhooks, the .NET client on NuGet, a Python client.
- **Phase 7, area intelligence:** per-capita benchmarks against similar counties, trends, shortage overlays on the map, "where is the gap" rankings, printable area reports, specialty pages.
- **Phase 8, patient access:** find care deepened (hospital ratings in plain words, "what this specialty treats", large-text checks); the insurance network pilot.
- **Phase 9, workflow tools:** territories, private tags and notes, list de-duplication, CRM-ready exports, a "new in my territory" digest.
- **Public launch:** only after every phase; then branding, marketing and a business plan.

## Not scheduled

- Speed item 9: stream zip files instead of extracting them first, where a loader still extracts.
- Speed item 11, the rest: ClinicalTrials.gov 3–5 requests at once.
- Speed item 16: build the loader to its own output folder, so loader changes never wait on stopping the site.

## Done

**Product plan:**
- ~~Phase 1 step 1.1: combined design drawings~~ (approved 2026-10-11)
- ~~Step 1.2: site shell~~ (2026-10-11)
- ~~Step 1.3: search, typed queries, list builder, type-ahead, export panel~~ (2026-10-11)
- ~~Plain specialty categories (26 categories, 197 subcategories)~~ (2026-10-11)
- ~~Step 1.4: clinician profile and the "Deactivated on [date]" page~~ (2026-10-11)
- ~~Step 1.5: organization, group and facility profiles; Medicare enrollment data~~ (2026-10-11)
- ~~Step 1.6: area pages~~ (2026-10-11)
- ~~Step 1.7: Verify NPIs~~ (2026-10-11)
- ~~Step 1.8: Find care~~ (2026-10-11)
- ~~Step 1.9: developer page, Application Insights, privacy notice, accessibility and speed pass~~ (2026-10-11). **Phase 1 complete.**
- ~~Light color scheme only~~ (2026-10-11)
- ~~Phase 9, companies and products: main page, company and product pages, CSV and raw data~~ (2026-10-12)
- ~~Phase 3 step 3.1: record-quality label~~ (2026-10-12, PR #57)
- ~~Step 3.1, more signs of practice and closures~~ (2026-10-12, PR #58)
- ~~State licenses: RI, VT, WV~~ (2026-10-12, PR #59); ~~AL, MI, CA~~ (2026-10-13, PR #60); ~~per-state reloads~~ (2026-10-13, PR #61)
- ~~Step 3.5: spot check of 50 Stale records, and other sources tested~~ (2026-10-13; results in PRODUCT_PLAN.md Phase 3)

**Speed work:**
- ~~Small fixes: an unneeded `using` in `FdaProductSource.cs` removed; CLAUDE.md no longer listed `deploy/register-task.ps1`~~ (2026-10-14)
- ~~Phase 3: the NY and CA Medicaid enrolled-provider lists~~ (2026-10-14: 544,718 enrollments, a sign of current practice and a line on the profile; Stale −36,249, Check −23,433, Current +59,682)
- ~~Phase 3 step 3.2: the change recorder widened~~ (2026-10-14: phone, secondary locations, every taxonomy, other names; licenses, board actions, exclusions and opt-outs as they start, end or change; 3,991 license changes recorded on the first reload)
- ~~A fresh clone runs: doctor, sample, Docker MySQL, scripts, README setup, Linux check~~ (2026-10-14, owner's request; Linux job 8 min, all green)
- ~~Migrations in topic folders, `db/schema.sql`~~ (2026-10-14, owner's request)
- ~~Item 10: deactivation flags by difference~~ (2026-10-14: after a monthly under 1 s instead of 55–61 s, after a report a few seconds instead of ~57 s; the NPIs queued in the monthly's existing diff pass, which took no longer)
- ~~Item 14: relax MySQL's disk flushing~~ skipped (owner, 2026-10-14). Measured first in throwaway test databases: a commit flush costs ~0.6 ms (20,000 one-row commits 12.8 s, one commit 0.24 s), and the big loads are single statements, so `innodb_flush_log_at_trx_commit = 2` would save little; a bigger `innodb_ddl_buffer_size` (64–256 MB, 4 or 8 threads) didn't speed index builds (provider 184–217 s vs 199 s); turning off doublewrite (~560 GB extra writes in 4 days) would risk corrupt tables on a power cut, so it wasn't proposed
- ~~Item 12: staging tables filled without their plain secondary indexes, added in one statement afterwards~~ (2026-10-14: full projection 63 → ~51 min, area counts 1,163 → 634 s, credentials and address types ~1 min less each; FULLTEXT and SPATIAL kept, they cost more added afterwards)
- ~~Fix: the suite-or-floor sign of the address type label~~ (2026-10-14: broken by item 2, PR #65, which sent MySQL a backspace instead of a word boundary; found when a full relabel moved ~106k addresses out of commercial; relabelled: commercial 1,847,645, residential 1,142,330, unknown 321,614)
- ~~Item 5: Overture in `run` for new addresses only, a full pass for a new release~~ (2026-10-14, PR #73: 6,399 new addresses, 4,951 placed, in 18.7 min instead of a 50–70 min pass of every state)
- ~~Item 4: FEMA buildings for new and moved addresses only, the files read in place~~ (2026-10-14, PR #72: 5,724 new addresses in 5.6 min instead of a 17 min full load with a 21 GB download)
- ~~Item 8: downloads kept until loaded, reused when unchanged, large files in parallel parts~~ (2026-10-13, PR #71)
- ~~Item 3: dataset reloads queue only the providers whose rows changed, for the quality label~~ (2026-10-13, PR #70: 35,260 relabelled in 1:55 instead of 9.5M in 25 min)
- ~~Item 2: the builders after the projection work from the queue~~ (2026-10-13, PR #65: everything after a weekly in about 10 min instead of about 2.5 h)
- ~~Item 1: incremental projection; the monthly compared by a hash of the projected columns~~ (2026-10-13, PR #64: the same October monthly loaded twice found 9.85M then 0 changed NPIs; a load that changes nothing rebuilds nothing)
- ~~Item 11, the openFDA part: the API key's rate; ask again only after new exports~~ (2026-10-13, PR #62)
- ~~Item 15: MySQL tests in parallel on a pool of test databases (4.5 min → 35 s)~~ (2026-10-13, PR #63)
- ~~Cleanup: unused code and test warnings~~ (2026-10-13, PR #66)

Stages 0–5 and Stage 5.5 items 1–15 and 17–19 are done; their history is in CLAUDE.md §12.
| 2026-10-13 | Speed item 3 | **Dataset reloads queue changed providers** for the quality label (`NpiRowDiff` in `TableSwap`, no migration). On `workplace`: `cms_order_referring` (a new CMS release, 2,059,772 rows, 86 s) and `cc_mips` (same release, 520,011 rows, 25 s) force-reloaded, then `quality`: 35,260 providers relabelled in 1:55 (about 32,600 of them left queued by item 2's map step in its check script, the rest order-and-refer changes; MIPS queued none) instead of a full relabel of 9.5M (25 min). Labels: Current 6,018,360, Check 1,677,697, Stale 1,798,198. Tests: Loader 218 (with MySQL). |
