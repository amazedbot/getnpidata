# getnpidata to-do

The working list: what is being done now, what comes next, and what waits for a decision. The full plan, with the
reasons and the data behind each item, is [PRODUCT_PLAN.md](PRODUCT_PLAN.md); this file only tracks the work.

**How it is kept:** updated in the same commit as each decision or finished step. Finished items are ~~crossed out~~
with the date and pull request, and stay in the list. New items are added only when the owner decides on them;
proposals that wait for a yes are under "Waiting for the owner". Work goes one item at a time, and each waits for the
owner's "go" (PRODUCT_PLAN.md §5).

Last updated: 2026-10-13.

## In progress

Loader speed work (PRODUCT_PLAN.md §7). Each item is coded and tested, then proven by a live run on `workplace`, then
merged. The live runs go one at a time.

| # | Item | State |
|---|---|---|
| 1 | Incremental projection: weekly files rebuild only the providers they touch; the monthly file is compared with the stored data | PR #64, CI green. Live check running: the first October load and full rebuild are done; the second load (the real test) is next. Also fix: the log line says "0 recorded" although the changes are recorded |
| 2 | The steps after the projection (credentials, address types, area counts, map, quality) work from a queue of changed providers | PR #65 (on top of #64), CI green. Live check after item 1's |
| 3 | A dataset reload queues only the providers whose rows changed, for the quality label | Branch `incremental-quality-inputs` (on top of #65). Live check after item 2's, then its PR |
| 8 | Downloads kept until loaded and reused when unchanged; large files in 4 parallel parts | Branch `download-reuse`. Live check after item 3's, then its PR |

## Next, in order

1. **Speed items 4–5:** FEMA buildings matched only for new addresses, reading only their areas; Overture in `run` for new addresses only, a full pass only for a new Overture release.
2. **Speed items 12 and 14:** bulk-load into tables without their indexes, add the indexes afterwards; relax MySQL's disk-flush settings during big loads (approved 2026-10-13; a crash loses the last second of writes, which a reload repeats). If a setting needs changing by hand (the loader doesn't change server settings itself, CLAUDE.md §7 Stage 1.8), the exact steps go to the owner first.
3. **Speed items 6, 7, 10, 13:**
   - 6: Socrata delta pulls (TX, WA, IL, CO, CT, DE) with a periodic full pull;
   - 7: independent sources, states and the Vermont feed in parallel (2–4 at a time);
   - 10: deactivation flags by difference instead of recomputing all 9.8M;
   - 13: build the map-by-credential table once; run credentials, address types and area counts side by side.
4. **Phase 3, the rest** (data quality signals and change history):
   - **New data, approved 2026-10-13 (not built yet):**
     - **State Medicaid enrolled-provider lists**, New York (health.data.ny.gov `keti-qx5t`) and California (data.chhs.ca.gov, Medi-Cal fee-for-service providers), as a sign of current practice. Measured: 34,797 Stale and 25,850 Check records in these two states are enrolled with no other sign. Other states as they are found to publish a list.
     - **Older Medicare years, 2013–2023** (Part B by provider and Part D prescribers by provider, data.cms.gov), shown as "last billed Medicare in [year]" and as a reason on the quality label.
   - **Step 3.2:** the change recorder widened: secondary locations, phone, every taxonomy, organization name, licenses, exclusions and opt-outs as they start and end.
   - **Step 3.3:** change pages ("Changes in [county] this month", "New practices", "Closed or moved") and monthly change counts on county pages.
   - **Step 3.4, approved 2026-10-13:** history before October 2026 from the NBER archive of past NPPES monthly files, for trends, analytics and statistics (a large download, one file per period).
5. **Small fixes, after the open branches merge:**
   - remove the unneeded `using` in `CredentialBuilder.cs`;
   - CLAUDE.md §5 lists `deploy/register-task.ps1`, which doesn't exist;
   - the item 1 log count above.

## Waiting for the owner

**Decisions:**
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
- ~~Item 11, the openFDA part: the API key's rate; ask again only after new exports~~ (2026-10-13, PR #62)
- ~~Item 15: MySQL tests in parallel on a pool of test databases (4.5 min → 35 s)~~ (2026-10-13, PR #63)
- ~~Cleanup: unused code and test warnings~~ (2026-10-13, PR #66)

Stages 0–5 and Stage 5.5 items 1–15 and 17–19 are done; their history is in CLAUDE.md §12.
