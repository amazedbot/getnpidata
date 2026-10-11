# getnpidata product plan

Status: **draft for the owner's approval** (2026-10-11). Nothing in this plan has been built. Once approved, CLAUDE.md
gets the new direction, and the work goes one phase (and one step) at a time. Each step is committed, summarized, and
waits for the owner's "go".

**Vision.** getnpidata is the affordable answer layer on top of free government data. It goes deeper than free lookup
tools and costs a small fraction of what enterprise vendors charge. It answers questions instead of dumping data.

**Primary audience for the redesign:** professionals (medical sales reps, recruiters and staffing firms, credentialing
and billing staff). Patients and caregivers get a separate, simple path. Health-tech developers, researchers,
journalists, public health planners and practice owners are served by the same pages, the exports and the API.

**The five tasks the redesign must make easy:**

| # | Task | Who needs it most |
|---|---|---|
| T1 | Look up one provider and understand them in 10 seconds: who, what specialty, where, active or not, affiliations | Everyone |
| T2 | Build a list (all [specialty] in [county/area]) and export it | Sales reps, recruiters, researchers |
| T3 | Check an uploaded list of NPIs: active, changed, flags | Credentialing, billing, staffing |
| T4 | Understand an area: how many providers, per capita, shortages, recent changes | Planners, journalists, practice owners, sales |
| T5 | Look up an organization or hospital and see its clinicians and locations | Recruiters, sales, credentialing |

---

## 1. Audit of the current site (October 2026)

### 1.1 Page by page

| Page | URL | What it shows | What's confusing |
|---|---|---|---|
| Search (home) | `/` | **26 filters** in four boxes: Location (5), Specialty (2), Provider (11), Medicare & compliance (8). The results are an 11-column grid with badges, plus Download CSV and View on map. Choosing a county adds a box with its population and shortage areas | **No single search box.** You must know which of five boxes a name or number goes in (last name, first name, organization name, NPI, credential). Every filter shows at once on the first visit, with no explanation of what the site is or who it's for. Jargon: "Classification" vs "Specialization", "Type" (individual/organization), "Can order/refer", "Accepts Medicare assignment", "Enumerated". The grid scrolls sideways on laptops and phones. Badges aren't explained. The county box is easy to miss |
| Provider | `/provider/{npi}` | Header: name, credential, NPI, type, gender, enumeration date, last update. Then up to **25 sections in data-source order**: Medicare, state licenses, Medicare-certified facilities, MIPS, Care Compare, group practices, facility affiliations, Medicare services, Part D, industry payments (five tables), and only then specialties, **practice locations**, registration details, endpoints, other identifiers, other names, recorded changes | **The basics are at the bottom:** specialty and address come after payments and Medicare billing. There's no one-line answer to "is this provider active and in good standing?". Sections are named after the source ("MIPS", "Care Compare", "Part D") rather than the question they answer. Organizations use the same page, so the authorized official, parent organization and subparts are buried in "Registration details". It's very long on a phone. "Back to results" uses browser history, so it breaks when you arrive from a link |
| Map | `/map` | Pins per practice address with the same 26 filters; "Search in this area", near me, a table of pins in view; at most 1,000 pins | Useful, but it's a second copy of the search page; the filters are as heavy as the home page's |
| Bulk lookup | `/lookup` | Paste or upload up to 50,000 NPIs and get a CSV back (Found / Not found or deactivated / Invalid) | **No on-screen answer.** The result is only a download. It doesn't say how many are inactive, excluded or changed. "Not found or deactivated" merges two very different outcomes. Nothing explains what's checked |
| Companies | `/companies` | A list of companies that pay providers (Open Payments), with name search | Fine as a list, but it's a top-level menu item that few users of the core tasks need |
| Company | `/company/{id}` | 9 sections: payments by year, kinds of payment, products, specialties, top providers, FDA recalls, SEC listing, OIG integrity agreements, similar names | Tables without a summary; the reader has to work out what matters |
| Products | `/products` | Drugs, devices and supplies named in payments, with search and a type filter | As for Companies |
| Product | `/product/{slug}` | 16 sections: FDA facts and label, spending, NADAC, shortages, recalls, adverse events, trials, prescribing overlap, companies, payments, specialties, top providers, research | The longest page on the site; valuable data with no summary at the top |
| Paid prescribers | `/product/{slug}/prescribers` | A sortable list of paid providers who prescribe the drug, with a CSV | Fine; reached only from the product page |
| API docs | `/swagger` | Swagger UI | No getting-started guide, no examples by task, and no mention of the .NET client |
| Footer | every page | Data as of, the active provider count, the CMS source | Fine, but it's the only place that says how current the data is |

### 1.2 Across the site

- **No landing page.** The first thing a visitor sees is a 26-field form. Nothing says what the site does better than the free NPPES registry.
- **Navigation is by dataset, not by task:** Search · Map · Bulk lookup · Companies · Products · API. The five tasks aren't named anywhere.
- **Missing page types:**
  - Hospitals and clinics: they appear only as rows inside clinician pages, though we have their CMS data.
  - Group practices: they appear only as rows too.
  - Areas: a county exists only as a box above the search results.
  - Organizations: they have no layout of their own.
- **No summaries.** Every detail page is a sequence of tables. Nothing says what matters first: status, red flags, where to reach the provider.
- **Status is hidden.** Deactivated NPIs return "not found" (a decision in CLAUDE.md §2). Credentialing users need "deactivated on [date]" instead (see the decisions in §6).
- **Sources and freshness per fact are inconsistent.** Some sections name their source and year and others don't. There's no "how current is this record" signal.
- **Patients see professional screens.** There's no plain-language path, such as "find a dermatologist near me who takes Medicare".
- **No accounts, saved lists or alerts.** Every visit starts from scratch.
- **What works well and must be kept:**
  - fast queries (most searches are under 1 s);
  - shareable URLs, so every search is a link;
  - unlimited streamed CSV;
  - the public API, the .NET client and OpenAPI docs;
  - mobile layouts without sideways scrolling, except the wide grids;
  - accessible markup.

---

## 2. Phase 1: the redesign

**Goal:** a professional can do each of the five tasks in under a minute without help, and the first screen explains
the site. Patients get a separate, simple path.

**Visual direction:** three options are drafted on the design canvas
[getnpidata redesign options](https://claude.ai/artifact/4wa9icARu23jZwAChzwoN4). Each shows the home page and a
clinician profile in one direction. The owner picks one (or a mix); the other page types are then drawn in that
direction before any code is written.

### 2.1 Information architecture

Navigation by task, the same on every page:

| Menu | Goes to | Task |
|---|---|---|
| **Search** (the box in the header, on every page) | Results, or straight to a profile for an exact NPI | T1, T2 |
| **Lists** | List builder: specialty + area + key filters → table → export | T2 |
| **Verify NPIs** | Bulk check with an on-screen summary (today's `/lookup`) | T3 |
| **Areas** | State → county pages | T4 |
| **Organizations** | Search limited to organizations, hospitals and groups | T5 |
| **API** | A developer page with examples, then Swagger | Developers |
| **Find care** (a separate, plainer entry, also linked from the home page) | The patient path | Patients |

Companies and products move under a "More data" menu until their redesign (Phase 9). Their URLs stay.

**URLs:** every current URL and API endpoint keeps working (CLAUDE.md constraint).

| Page | URL | Status |
|---|---|---|
| Home (landing + search box) | `/` | redesigned; a query string still shows results as today, so old search links still work |
| Search results | `/?…` (unchanged) and `/search?…` (alias) | redesigned |
| Clinician profile | `/provider/{npi}` | redesigned |
| Organization profile | `/provider/{npi}` for an organization NPI (layout chosen by entity type) | new layout, same URL |
| Hospital / facility profile | `/facility/{ccn}` | **new** |
| Group practice profile | `/group/{pacId}` | **new** |
| Area pages | `/area/{state}` and `/area/{state}/{countyFips}` | **new** |
| Verify NPIs | `/verify`; `/lookup` keeps working (same page) | redesigned |
| Find care (patients) | `/find-care` | **new** |
| Map | `/map` | restyled, simpler filters |
| Developers | `/developers` → `/swagger` | **new** guide page |
| Companies, products | unchanged | restyled only (Phase 9 redesigns them) |

### 2.2 Search experience

- **One box with intent detection.** It reads what the user typed:
  - 10 digits → that NPI's profile.
  - A specialty word plus a place ("chiropractor suffolk county ny", "dentists 11701") → a list.
  - A person's or organization's name → people and organizations, ranked, with "similar names" on by default.
  - A hospital name → facilities.

  Matching uses the dictionaries we already have: NUCC classifications, standardized credentials, counties, USPS cities, states and ZIPs. The interpretation is shown and can be edited ("Chiropractors in Suffolk County, NY · change").
- **Typeahead** for providers, organizations, specialties and places.
- **Results page:**
  - **Three key filters** are always visible: specialty, location (state → county → city, ZIP + radius), and type (people / organizations).
  - **"More filters"** opens a drawer with the rest, grouped and explained in plain language: Medicare & compliance, practice details, record dates, practice building.
  - **Rows:** compact two-line rows instead of an 11-column grid. Each row shows the name and credential, specialty, city and phone, plus at most three badges with tooltips. On a phone, the rows become cards.
  - **Export panel:** choose the columns (summary, all, compliance), CSV or Excel. The count and the data date are shown.

### 2.3 Profile layouts (the same skeleton for every type)

1. **Summary card** (the 10-second answer):
   - the name and credential (or the legal name);
   - type, primary specialty, and the primary address with phone and a map thumbnail;
   - a **status line**:
     - active, or deactivated with its date;
     - the last NPPES update ("updated 12 days ago");
     - any red flags: OIG exclusion, Medicare opt-out, a license action;
   - **key affiliations**: hospitals and groups, as links.
2. **Actions:** copy NPI, export this record (CSV/JSON: **"Download raw data"**), view on map, add to list (once accounts exist, Phase 4).
3. **Sections in question order**, each answering one question with a one-line summary before its table, and each naming its source and as-of date:
   - **Clinician:** Where they practise → Specialties & licenses → Affiliations (hospitals, groups) → Compliance & standing → Medicare activity → Quality (MIPS) → Industry payments → Changes over time → Identifiers & endpoints.
   - **Organization (NPI):** Locations → Authorized official → Parent organization & subparts → Clinicians (through the group link, where known) → Facilities it operates (CCN links) → Compliance → Payments received → Changes.
   - **Hospital / facility (CCN):**
     - CMS ratings (overall stars, HCAHPS stars, outcome counts) → Affiliated clinicians (from Care Compare affiliations, with a specialty breakdown and a link to the full list) → NPIs it bills under → Location → Other facilities at the address.
     - Nursing homes, home health agencies and hospices show their own ratings.
   - **Group practice (PAC ID):** Clinicians (by specialty, with a list and export) → Locations → Accepts Medicare.
   - **Area (county):**
     - Population → providers per 100,000 by specialty, compared with the state and the US → shortage areas → new providers in the last 90 days → recent changes (moves, closures) → hospitals.
     - Each number links to the list behind it.
4. Jargon is explained in place (a "what's this?" tooltip for MIPS, HCAHPS, Part D, order/refer, enumeration).

### 2.4 Patient path (`/find-care`)

- **The form:** three questions in plain language: "What kind of care?" (a short list of common needs mapped to specialties), "Where?" (ZIP or "near me"), and optional preferences ("takes Medicare", "telehealth", "female/male clinician").
- **Results:** simple cards with a map, the phone and the address, and "hospital it works with". No compliance, payments or billing details are on the first screen; a link goes to the full profile.
- **Wording:** a plain-language note says that a listing doesn't mean the provider is taking new patients or in a given insurance network.

### 2.5 Steps (each one committed and approved before the next)

| Step | What | Done when |
|---|---|---|
| 1.1 | Pick the visual direction on the canvas; draw the remaining page types in it (results, organization, facility, area, verify, find care, phone views) | The owner approves the drawings |
| 1.2 | Site shell: header with the search box, task navigation, footer, design tokens, page-speed budget | Every existing page renders in the new shell; no URL changes |
| 1.3 | Search: intent detection, typeahead, the results page with three key filters and the drawer, the export panel | The T1 and T2 test scripts pass (below) |
| 1.4 | Clinician profile: summary card, sections in question order, raw-data download | T1 under 10 s on desktop and phone |
| 1.5 | Organization, facility and group profiles | T5 test script passes |
| 1.6 | Area pages (state, county) | T4 test script passes |
| 1.7 | Verify NPIs: on-screen summary, filterable results, download | T3 test script passes |
| 1.8 | Find care (patients) | A first-time user finds a nearby specialist in under 60 s |
| 1.9 | API developer page; accessibility and mobile pass; README and CLAUDE.md | Lighthouse ≥ 90 for accessibility and performance on the main pages; all old URLs resolve |

### 2.6 Data for Phase 1

| Task | Already have | Missing (would be a new source; proposed, not added without the owner's OK) |
|---|---|---|
| T1 provider | NPPES (names, taxonomies, locations, phones, dates); standardized credentials; Care Compare (accepts Medicare, telehealth, school, graduation year, groups, hospital affiliations); OIG LEIE; opt-out; order/refer; state licenses (9 states); MIPS; Medicare and Part D activity; Open Payments; change log (since 2026-10-09); FEMA building type | **Deactivation status for display** (we have it locally but drop it from the projection; this is an owner decision, §6). Languages spoken, board certification and "accepting new patients" have no free, reliable source |
| T2 list | Search with every filter, streamed CSV, map | Excel export (no new data). Ready-made column sets for CRM imports |
| T3 verify | Bulk lookup, Luhn check, flags (exclusion, opt-out, order/refer, Medicare billing), last update date | The same deactivation-status decision. "Changed since" per NPI needs Phase 3's history (we only have changes since 2026-10-09) |
| T4 area | County population (Census), HPSA shortage areas, provider counts from search, enumeration dates (new providers), change log | Per-capita benchmarks are computed from what we have (no new data). Population by ZIP or city (Census ACS by ZCTA) would be new. Trends before October 2026 would need historical NPPES files (Phase 3) |
| T5 organization | NPPES organizations (LBN, other names, authorized official, parent/subparts, locations); Care Compare group membership (clinician ↔ group PAC ID); hospital affiliations (clinician ↔ CCN); hospital, nursing home, home health and hospice data with CCN ↔ NPI links | **Organization NPI ↔ group PAC ID** (to show "clinicians in this organization" from an organization's NPI page): the CMS *Medicare Fee-For-Service Public Provider Enrollment* file has it. **Health-system ownership** (which hospitals belong to which system): AHRQ Compendium of U.S. Health Systems, CMS hospital ownership files. **Bed counts and facility details**: CMS Provider of Services file. All three are free; each needs the owner's OK |

### 2.7 How we'll know Phase 1 worked

- **Task tests.** A written script per task is run by the owner, or by a test user who hasn't seen the site. The time to answer is recorded before and after the redesign. Targets: T1 ≤ 10 s, T2 ≤ 60 s to a downloaded CSV, T3 ≤ 60 s to a summary, T4 ≤ 30 s, T5 ≤ 30 s.
- **Speed:** profile and results pages answer in under 1 s (p95, warm) and pass Lighthouse ≥ 90 on a phone.
- **After launch (needs Phase 2 hosting):**
  - searches that end in a profile view or an export;
  - the share of searches typed in the single box rather than the drawer;
  - bounce rate on the home page.

  All are measured from server logs. A privacy-friendly analytics tool would be a new service and needs the owner's OK.

---

## 3. Later phases (recommended order)

Ordering rule: first what makes the product credible and launchable, then what people will pay for, then breadth.
Each phase lists its goals, scope, data, and how we'll know it worked.

### Phase 2: Go live (the planned Stage 6)

- **Goal:** the redesigned site and the API run publicly on Azure, refreshed daily.
- **Scope:** CLAUDE.md §7 Stage 6 as planned (publisher, deploy workflow, Task Scheduler). Before anything is created, the database size is measured and a cost estimate is given.
- **Why now:** every later phase needs real users, and the paid features need hosting, accounts and email.
- **Data:** nothing new.
- **Worked when:** the public URL serves every page within the Phase 1 speed targets, the nightly publish succeeds 7 days in a row, and the monthly cost matches the estimate.

### Phase 3: Data quality signals and change history

- **Goals:**
  - every record shows how current and trustworthy it is;
  - every profile and area shows what changed over time (moves, new practices, closures, specialty changes).
- **Scope:**
  - **Record-quality score:**
    - age of the last NPPES update;
    - whether the address placed on a building (geocode quality) and the building type;
    - whether NPPES agrees with Care Compare (address, specialty);
    - a phone on file;
    - deactivation or reactivation history.

    Shown as Current / Check / Stale with the reasons.
  - **The change recorder widened:** secondary locations, phone, every taxonomy (not just the primary), organization name, licenses (where a state publishes them), exclusions and opt-outs as they start and end.
  - **Change pages:** "Changes in [county] this month", "New practices", "Closed or moved".
- **Data:**
  - **Have:** weekly NPPES files, `provider_change` (since 2026-10-09), geocodes and building types, Care Compare, LEIE, opt-out.
  - **Missing:**
    - **History before October 2026.** CMS publishes only the current monthly file. The NBER archive of past NPPES monthly files is free and would let us show trends back many years. It's a large download (one monthly file per period), so it needs the owner's OK.
    - Care Compare addresses aren't kept today (no new source, just more columns).
- **Worked when:**
  - every profile shows a quality label with reasons;
  - county pages show monthly change counts;
  - spot checks of 50 "Stale" records find real problems (wrong address, closed practice) in most of them.

### Phase 4: Bulk verification and compliance checks (first paid feature)

- **Goal:** credentialing, billing and staffing teams can verify a roster in one step and get a report they can file.
- **Scope:**
  - **Verify NPIs, extended.** For each NPI:
    - status (active, deactivated with date, invalid);
    - name match against the uploaded name;
    - specialty and state match;
    - OIG exclusion, Medicare opt-out, order/refer eligibility;
    - state license status and actions (9 states);
    - changes since a given date;
    - the record-quality label from Phase 3.
  - **Output:** a PDF/CSV report with a summary, the sources and the as-of dates.
  - **Saved monitoring:** a roster re-checked weekly. This arrives with accounts in Phase 5.
- **Data:**
  - **Have:** everything listed above.
  - **Missing (all free, all need the owner's OK):**
    - **SAM.gov exclusions**: the federal debarment list, a public extract plus an API key;
    - **state Medicaid exclusion lists**: about 40 states, each in its own format, so pilot the largest first;
    - **more state license boards**: from the survey in CLAUDE.md, MI, RI, VT, WV and AL (owner downloads), and CA (requested);
    - **CMS public enrollment**: shows whether a provider is enrolled in Medicare (PPEF).
  - **Not available free:** the DEA registration list, the NPDB, the CMS Preclusion List, and ABMS board certification.
- **Worked when:**
  - a 1,000-NPI roster is verified in under a minute with a downloadable report;
  - three real credentialing users say the report replaces a manual step;
  - the first paying customers sign up.

### Phase 5: Accounts, saved lists and alerts

- **Goal:** professionals come back weekly because the site tells them what changed in their lists, territories and rosters.
- **Scope:**
  - **Accounts:** sign-in with email, Microsoft or Google; the "Pro/Team" features (§4) unlock here.
  - **Saved items:** searches, lists and rosters.
  - **Weekly alerts:** new providers in a territory, moves, deactivations, new exclusions, license actions. Sent by email and shown in an in-app feed. Built from the weekly NPPES files we already process.
  - **Team sharing.**
- **Data:** nothing new.
  - **New infrastructure (the owner chooses and approves):**
    - an identity provider (ASP.NET Core Identity, or Microsoft Entra External ID);
    - an email service (Azure Communication Services or SendGrid);
    - a payments provider (Stripe);
    - a small user database.
  - **Privacy:** a privacy policy and terms of service are needed before launch.
- **Worked when:** 30% of signed-up users open an alert within a week, and saved lists are re-exported.

### Phase 6: API and export plans

- **Goal:** developers and data teams pay for volume and reliability.
- **Scope:**
  - **API keys:** a self-serve key page, per-key quotas and a usage dashboard. Key checking is already built (`Api:RequireKey`, off).
  - **Weekly change feed:** an endpoint plus a downloadable file.
  - **Bulk downloads:** the full summary dataset as CSV/Parquet.
  - **Webhooks** for alerts.
  - **Client libraries:** the .NET client published on NuGet; a Python client.
- **Data:** nothing new. **Check licenses before selling bulk data:**
  - Illinois license data is under the Open Database License (share-alike), so it's excluded from paid bulk files or kept open.
  - Overture's licenses are reviewed for map data.
  - CMS, FDA, HRSA, Census and OIG data are public domain.
- **Worked when:** keys are issued and used weekly, paid API plans are sold, and the API is never down for longer than the agreed limits.

### Phase 7: Area intelligence

- **Goal:** planners, journalists, researchers and practice owners get answers about places, not just lists.
- **Scope:**
  - **Per-capita benchmarks:** providers per 100,000 by specialty, compared with the state, the US and similar counties.
  - **Trends:** from Phase 3's history.
  - **Shortage overlays** on the map.
  - **"Where is the gap?"** rankings: e.g., counties with the fewest psychiatrists per person.
  - **Printable area reports** and embeddable charts.
  - **Specialty pages:** "Chiropractors in the US".
- **Data:**
  - **Have:** county population, HPSA, provider counts, history (from Phase 3).
  - **Missing:**
    - Census ACS population by age and by ZCTA, for per-capita figures for children or seniors (free; needs the owner's OK);
    - CDC PLACES county health measures, optional (free; needs the owner's OK).
- **Worked when:** area pages are found from search engines and cited or linked by outside sites, and the reports are downloaded.

### Phase 8: Patient access tools

- **Goal:** patients and caregivers find appropriate care nearby, in plain language.
- **Scope:**
  - **Find care, deepened:**
    - map and distance;
    - telehealth;
    - hospital ratings in plain language;
    - "what this specialty treats" explanations;
    - large-text and screen-reader checks.
  - **Insurance network pilot in one state** (the open Stage 5.5 item 16): "listed in [plan]'s directory" with its source and date, never "accepting new patients".
- **Data:**
  - **Have:** Care Compare, hospital ratings, HCAHPS, the map.
  - **Missing:**
    - insurer and plan directories: Medicare Advantage and Medicaid managed-care directory APIs, and Transparency in Coverage in-network files. This is item 16, and it needs the owner's OK and a state choice.
    - languages spoken: no reliable free national source; some plan directories have it.
- **Worked when:** patient sessions end on a provider's phone or address, and a sample of results are confirmed reachable.

### Phase 9: Workflow tools for professionals, then companies and products

- **Workflow tools:**
  - **Goal:** sales and recruiting teams work inside the site rather than in spreadsheets.
  - **Scope:**
    - territories (saved sets of counties and ZIPs);
    - tags and notes on providers, private to the account;
    - de-duplicating an uploaded list against the registry;
    - CRM-ready exports (Salesforce and HubSpot column maps);
    - a "new in my territory" digest.
  - **Data:** nothing new.
  - **Worked when:** teams keep territories, and alerts and exports go into their CRM.
- **Company and product profiles redesign:**
  - **Goal:** the same summary-first layout for the Open Payments companies and the products.
  - **Scope:**
    - a summary card ("Paid $X to Y providers in 2025; top products; recalls; integrity agreement");
    - sections by question;
    - raw-data download.
  - **Data:** have it all.
  - **Worked when:** company and product pages hold readers (time on page, clicks into providers) better than today.

---

## 4. Business model and pricing ideas

**Positioning.**
- Free tools (the NPPES registry and the ad-supported NPI lookup sites) show one record at a time, with no context and no bulk tools.
- Enterprise healthcare-data vendors sell annual contracts that are commonly reported in the tens of thousands of dollars or more.
- getnpidata sells the useful middle: answers, lists, verification, change alerts and an API, at self-serve prices.

**What stays free** (it drives search-engine traffic, trust and the patient mission):
- every profile page and area page, the map, and search;
- the patient path, always free and without an account;
- exports of reasonable size (see the decision in §6);
- the public API at today's rate limit;
- a small bulk verification (e.g., up to 500 NPIs at a time).

**Paid tiers.** The prices are placeholders to validate with real users before launch.

| Tier | For | Includes | Price idea |
|---|---|---|---|
| Free | Everyone | As above | $0 |
| Pro | One professional (rep, recruiter, credentialing specialist) | Unlimited exports with every column; bulk verification up to 25,000 NPIs a month with reports; 10 saved lists or territories with weekly alerts; API key with 10,000 requests a day | [PRICE] a month (e.g., $29–49) |
| Team | 5–20 seats | Everything in Pro, shared lists and territories, roster monitoring (weekly re-checks of up to 25,000 NPIs), CRM exports, invoice billing | [PRICE] a month (e.g., $149–299 for 5 seats) |
| Data / API | Developers and data teams | High API quotas, the weekly change feed, full bulk downloads (CSV/Parquet), webhooks, an uptime commitment | [PRICE] a month (e.g., from $499) |
| Pay as you go | Occasional verifiers | Bulk verification reports per 1,000 NPIs | [PRICE] (e.g., $5 per 1,000) |

**Other revenue ideas (each needs the owner's decision):**
- **Sponsored "featured" listings for practices.** Not recommended: it would undercut the trust that is the product.
- **Data licensing** of the cleaned, joined dataset, with the license review in Phase 6.
- **Area reports for planners and journalists:** free to read, paid for custom or white-label versions.

**Costs to cover:**
- Azure hosting: about $30 a month at the start (CLAUDE.md Stage 6), more as usage grows.
- Email and payment fees: Stripe charges about 2.9% plus 30 cents per card payment.
- Optional analytics.
- The owner's time for support.

---

## 5. Working rules for this plan (owner, 2026-10-11)

- **Pacing:** one phase at a time; within a phase, one step at a time. Each step is committed, summarized briefly, and waits for the owner's "go".
- **New ideas:** new features and data sources are proposed, never added without asking. Every "Missing" item above is a proposal.
- **Constraints:** existing URLs and API endpoints keep working, and pages stay fast and mobile-friendly.
- **CLAUDE.md:** it's updated with the new direction once this plan is approved, in the same commit as the first step.

## 6. Decisions the owner needs to make

1. **Visual direction:** one of the canvas options, or a mix.
2. **Deactivated NPIs.**
   - **Today:** they're never shown (CLAUDE.md §2).
   - **Proposal:** show a minimal status page and a bulk-check status ("NPI 1234567893 was deactivated on 2025-03-01"), with no other details. Credentialing (T3) and "active or not" (T1) need it.
3. **Exports and the API.**
   - **Today:** CSV has no row cap and the API is public (CLAUDE.md §2).
   - **Choice:** keep both unlimited for everyone, or cap the free tier (e.g., 5,000 rows per export) so that unlimited exports are a paid feature.
4. **New free data sources** to approve one by one:
   - CMS Medicare public enrollment (PPEF): links organization NPIs to groups;
   - AHRQ health-system compendium and CMS hospital ownership;
   - CMS Provider of Services;
   - NBER historical NPPES files;
   - SAM.gov exclusions;
   - state Medicaid exclusion lists;
   - Census ACS by ZCTA;
   - CDC PLACES.
5. **Order of the later phases** (§3), and whether Phase 2 (Azure) starts right after Phase 1.
6. **Analytics:** server logs only, or a privacy-friendly analytics service.
