# getnpidata product plan

Status: **approved by the owner on 2026-10-11**, with the decisions recorded in §6. CLAUDE.md carries the new direction.
The work goes one phase (and one step) at a time; each step is committed, summarized, and waits for the owner's "go".
Done: 1.1 (drawings approved 2026-10-11), 1.2 (site shell), 1.3 (search), 1.4 (clinician profile), 1.5 (organization, facility and group profiles), 1.6 (area pages), 1.7 (verify NPIs), 1.8 (find care), 1.9 (developer page, Application Insights, privacy notice, accessibility and speed). **Phase 1 is complete.** From Phase 9, the company and product redesign is done (pulled forward, 2026-10-12). Now: **Phase 3, data quality signals and change history** (owner, 2026-10-12): 3.1 (record-quality label, then more signs of practice and closures), 3.2 (wider change recorder), 3.3 (areas over time), 3.5 (spot check), 3.6 (facility and group pages), the NY and CA Medicaid lists and older Medicare years done; 3.4 (history before October 2026, built 2026-10-15, with secondary practice locations from September 2018 and per-NPI history on profiles and in Verify NPIs; its full run of the archive is part of the combined run), 3.6 part 2 and 3.7 are next. Alongside it, the loader speed work (§7). Phase 2 waits until a feature needs hosting.

**The working to-do list** (what is in progress, what is next, what waits for a decision, what is done) is [TODO.md](TODO.md). This document stays the full plan: the reasons, the scope and the data for each phase.

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

## 1. Audit of the site before the redesign (2026-10-11)

Kept as the record of why Phase 1 changed what it did. The pages described here have been replaced (§2.5).

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

**Visual direction (owner, 2026-10-11): all three options combined.**
- From A: the calm, summary-first layout.
- From B: the deep green header and serif headings.
- From C: the sidebar navigation and dense tables for professional work.

The 12 page drawings are on the design canvas [getnpidata redesign options](https://claude.ai/artifact/4wa9icARu23jZwAChzwoN4), page "Combined direction": home, results and list builder, clinician, organization, hospital, area, verify, find care, company, product, developers, and the clinician profile on a phone. The first three options stay on the page "Options A–C".

**Clinician profiles feel personal, not like a record (owner):**
- An avatar: a silhouette tinted by gender (blue for male, plum for female, neutral gray when gender isn't listed), or the initials.
- A map preview of the practice locations on OpenStreetMap; a paid map only if the owner approves one.
- A one-sentence plain-English summary at the top ("Chiropractor in Babylon, NY since 2007, part of a 6-clinician group").
- The specialty explained in plain words, with practice details and affiliations as clear cards.

Every profile keeps a "Download raw data" button.

### 2.1 Information architecture

Navigation by task, the same on every page:

| Menu | Goes to | Task |
|---|---|---|
| **Search & lists** (plus the search box on every page) | Results and the list builder: specialty + area + key filters → table → export; an exact NPI goes straight to its profile; a name finds people and organizations | T1, T2, T5 |
| **Map** | Pins per practice address with the same filters | T2 |
| **Verify NPIs** | Bulk check with an on-screen summary (`/verify`; `/lookup` still works) | T3 |
| **Areas** | State → county pages | T4 |
| **Companies & products** | The Open Payments companies and products (`/companies-and-products`) | Sales, journalists |
| **Developers & API** | A developer page with examples, then Swagger | Developers |
| **Find care** (a separate, plainer entry, linked from the home page and the footer) | The patient path | Patients |

The menu is a sidebar on desktop (a menu button on phones), as built in step 1.2. Organizations have no menu entry of their own: the search box finds them by name, and organization, group and facility profiles link to each other (step 1.5). Companies and products got the new layout in Phase 1 and their full redesign on 2026-10-12 (pulled forward from Phase 9); their URLs stay.

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
| Companies & products | `/companies-and-products` (**new** main page); `/companies`, `/products`, `/company/{id}`, `/product/{slug}` unchanged | redesigned 2026-10-12 |

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
| 1.1 | Combined direction drawn for 12 pages (home, results/list builder, clinician, organization/group, hospital, area, verify, find care, company, product, developers, clinician on a phone) | The owner approves the drawings |
| 1.2 | Site shell: header with the search box, task navigation, footer, design tokens, page-speed budget. **Done 2026-10-11:** deep green sidebar (a menu button on phones), header search box (`/go`: an NPI opens its profile, other text searches names until 1.3), tokens in `site.css` (light only since 2026-10-11, owner), system fonts until the brand fonts are self-hosted | Every existing page renders in the new shell; no URL changes |
| 1.3 | Search: intent detection, typeahead, the results page with three key filters and the drawer, the export panel. **Done 2026-10-11:** brand fonts self-hosted; home page (hero search, tasks, this week in the data); `/search?q=` read by `QueryInterpreter` (NPI, specialty incl. everyday words and plurals, state, county, USPS city, ZIP, "within N miles", name) with removable chips; a name alone shows people and organizations; `/search` list builder (specialty and location filters, "More filters" drawer, compact rows that become cards on phones, readable titles); type-ahead (`/suggest`); export panel (summary, contact or compliance columns; CSV or Excel up to 1,048,575 rows) | The T1 and T2 test scripts pass (below) |
| 1.4 | Clinician profile: avatar, one-sentence summary, map preview, specialty in plain words, cards, sections in question order, raw-data download; the minimal "Deactivated on [date]" status page. **Done 2026-10-11:** summary card with avatar, summary sentence, status chips, Copy NPI, raw data (JSON/CSV), Call/Directions; static OSM map preview; plain-language specialty descriptions for every category and subcategory; sections in question order with sources; `provider_deactivated` (migration 077) and the status page | T1 under 10 s on desktop and phone |
| 1.5 | Organization, facility and group profiles. **Done 2026-10-11:** Medicare enrollment data added (owner's OK: organization NPI ↔ group PAC ID ↔ clinicians); `/group/{pacId}`, `/facility/{ccn}`, organization NPIs list their clinicians; clinician pages link groups and facilities; CSV of every clinician | T5 test script passes |
| 1.6 | Area pages (state, county). **Done 2026-10-11:** `/area`, `/area/{state}`, `/area/{state}/{county}` with a specialty picker; counts and per 100,000 vs state and US precomputed after each projection (`area_provider_count`); new in 90 days, top cities, shortage areas, hospitals, changes; every number links to its list; "Areas" in the menu | T4 test script passes |
| 1.7 | Verify NPIs: on-screen summary, filterable results ("Deactivated on [date]" included), download. **Done 2026-10-11:** `/verify` (and `/lookup`): upload CSV, text or Excel, or paste; summary tiles; tabs (needs attention, each status); results with deactivation, exclusion and opt-out dates; sources with load dates; CSV and Excel of every row. Name/specialty/state matching, changes since a date and the PDF report stay in Phase 4 | T3 test script passes |
| 1.8 | Find care (patients). **Done 2026-10-11:** `/find-care` with its own plain layout: ten everyday kinds of care plus "something else", ZIP or "use my location", Medicare / video visits / woman or man; cards nearest first (widening 2 → 50 miles) with phone, address, directions, tags and a hospital it works with, a numbered map, and the new-patients/insurance note | A first-time user finds a nearby specialist in under 60 s |
| 1.9 | API developer page; Azure Application Insights across the site and API, with a privacy notice; accessibility and mobile pass; README and CLAUDE.md. **Done 2026-10-11:** `/developers`; Application Insights wired (server-side, query strings and client details removed; on once a connection string is set); `/privacy`; Lighthouse mobile: accessibility 100 on 18 pages, performance 90–99 except the map page (84–90); all old URLs answer | Lighthouse ≥ 90 for accessibility and performance on the main pages; all old URLs resolve |

### 2.5.1 Page-speed budget (set in step 1.2)

- **CSS:** one stylesheet, at most 40 KB (39 KB on 2026-10-13, close to the budget).
- **Scripts:** only small page scripts, deferred; third-party code only where a page needs it (Leaflet on the map page).
- **Fonts:** no third-party font requests; brand fonts self-hosted (woff2, at most ~300 KB together) or system fonts.
- **Server time:** pages answer in under 1 s warm (p95); checked with every step.
- **Phones:** no sideways scrolling at 375 px.

### 2.6 Data for Phase 1

| Task | Already have | Missing (would be a new source; proposed, not added without the owner's OK) |
|---|---|---|
| T1 provider | NPPES (names, taxonomies, locations, phones, dates); standardized credentials; Care Compare (accepts Medicare, telehealth, school, graduation year, groups, hospital affiliations); OIG LEIE; opt-out; order/refer; state licenses (9 states then, 15 since 2026-10-13); MIPS; Medicare and Part D activity; Open Payments; change log (since 2026-10-09); FEMA building type | **Deactivation status:** shown since step 1.4 (`provider_deactivated`). Plain-language specialty descriptions are written for this site (no new data). Languages spoken, board certification and "accepting new patients" have no free, reliable source |
| T2 list | Search with every filter, streamed CSV, map | Excel export and column sets (done in step 1.3); CRM column maps are Phase 9 |
| T3 verify | Bulk lookup, Luhn check, flags (exclusion, opt-out, order/refer, Medicare billing), last update date | Deactivation dates: done in step 1.7. "Changed since" per NPI: done in step 3.4 (the archived monthly files since 2007, then the changes recorded since October 2026) |
| T4 area | County population (Census), HPSA shortage areas, provider counts from search, enumeration dates (new providers), change log | Per-capita benchmarks are computed from what we have (no new data). Population by ZIP or city (Census ACS by ZCTA) would be new. Trends before October 2026 would need historical NPPES files (Phase 3) |
| T5 organization | NPPES organizations (LBN, other names, authorized official, parent/subparts, locations); Care Compare group membership (clinician ↔ group PAC ID); hospital affiliations (clinician ↔ CCN); hospital, nursing home, home health and hospice data with CCN ↔ NPI links | **Organization NPI ↔ group PAC ID** (to show "clinicians in this organization" from an organization's NPI page): the CMS *Medicare Fee-For-Service Public Provider Enrollment* file has it (**added in step 1.5**, owner's OK). **Health-system ownership** (which hospitals belong to which system): AHRQ Compendium of U.S. Health Systems, CMS hospital ownership files (proposal). **Bed counts and facility details**: CMS Provider of Services file (**loaded in step 3.1** for closed facilities; bed counts and details aren't shown yet) |

### 2.7 How we'll know Phase 1 worked

- **Task tests.** A written script per task is run by the owner, or by a test user who hasn't seen the site. The time to answer is recorded before and after the redesign. Targets: T1 ≤ 10 s, T2 ≤ 60 s to a downloaded CSV, T3 ≤ 60 s to a summary, T4 ≤ 30 s, T5 ≤ 30 s.
- **Speed:** profile and results pages answer in under 1 s (p95, warm) and pass Lighthouse ≥ 90 on a phone.
- **Once there are users (after the public launch, which follows every phase):**
  - searches that end in a profile view or an export;
  - the share of searches typed in the single box rather than the drawer;
  - bounce rate on the home page.

  All are measured with Azure Application Insights (owner, 2026-10-11), covered by the privacy notice.

---

## 3. Later phases (recommended order)

Ordering rule: first what makes the product credible, then what professionals use most, then breadth. Everything is free
(owner, 2026-10-11): no paywalls, payment accounts or Stripe, but the code is structured so limits or tiers could be added
later. There is **no public launch until every phase is complete**; branding, marketing and a business plan come after.
Each phase lists its goals, scope, data, and how we'll know it worked.

### Phase 2: Private test hosting (only when a feature needs it)

- **Goal:** a private, password-protected test copy on Azure for features that can't be tested locally (alerts, email, accounts). No public launch until every phase is complete (owner, 2026-10-11).
- **Scope:** the site on Azure behind a password, deployed by GitHub Actions, and **the nightly load running in Azure on a schedule, not on the owner's PC** (owner, 2026-10-12): Azure downloads the source files, loads them, builds the projection and refreshes the datasets itself. The design (where the loader runs, a database large enough for the raw data, the DuckDB-based Overture and FEMA steps) and its cost estimate come first. The PC stays the development and test environment.
- **When:** only when a feature needs it; the owner is asked first, with a cost estimate. Not needed yet (owner, 2026-10-13).
- **Data:** nothing new.
- **Admin console (owner, 2026-10-15; before release, built with this phase):** a web-based console for a few named admins, running everything in Docker. Whether production moves to containers (a Docker host in Azure that Portainer manages) or stays on App Service + Azure Database for MySQL is decided here, with the cost estimate.
  - **Containers:** Docker images and a compose stack for the site, the loader and MySQL, managed with **Portainer Community Edition** (free): start, stop, restart, logs, resource use, image updates. Every Portainer user is a full admin; finer roles (Business Edition) are paid and not planned.
  - **Database:** **CloudBeaver** (free community edition) next to it: browse tables, run SQL, export; read and write, one account per admin, each with their own MySQL login (not the site's read-only login nor the loader's).
  - **Loader:** see the loader's runs (command, start, end, result, timings per step, the log) and start one (`run`, `project`, `datasets <name>` …) from the console; loader runs as containers Portainer starts, or an admin page in our own site, whichever the design picks.
  - **Site settings:** an admin page in our own site for the API key switch (`Api:RequireKey`), the keys themselves and the rate limit, instead of editing environment variables.
  - **Access:** a few admins with their own accounts and HTTPS; never open without a login. How sign-in works (Portainer's and CloudBeaver's own logins, or one sign-in in front of all of them) is part of the design.
  - **Audit log:** who did what and when: console sign-ins, loader runs started, settings and key changes, and the admin logins' database changes (MySQL audit), kept for review.
- **Worked when:** the test copy serves every page within the Phase 1 speed targets, the nightly load in Azure succeeds 7 days in a row, the monthly cost matches the estimate, and the admins can manage the containers, query the database, start and follow a loader run and change the API settings from the console, with each action in the audit log.

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

    Shown as Current / Check / Stale with the reasons. **Done 2026-10-12 (step 3.1):** `provider_quality`, rebuilt after each load; on profiles, result badges, Verify NPIs (tabs and pills), CSV/Excel and the API, and a `quality` filter. Rules in CLAUDE.md §7 Stage 5.5 "Record quality"; the spot check of 50 stale records is step 3.5. **More signs (owner, 2026-10-12):** Open Payments in either of the last two years, Medicare reassignments, MIPS, facility affiliations, open business listings at the address (Overture), FQHC and rural clinic enrollments, opioid treatment programs, the Medicare revalidation list, equipment suppliers' Medicare billing; closures from Overture (permanently closed) and the Provider of Services file (terminated facilities). **Listings that confirm (owner, 2026-10-15):** an open Overture listing counts only when a source that keeps up with the business backs it (not business-registration data alone), and a 10+ year old record whose only sign is a listing is Check.
  - **The change recorder widened (step 3.2). Done 2026-10-14:** secondary locations, phone, every taxonomy (not just the primary), other names (an organization's name change was already recorded), licenses (where a state publishes them: listed, dropped, status, renewal, board actions), exclusions and opt-outs as they start and end (opt-out renewals too). The first state license reload recorded 3,991 changes in six states.
  - **Areas with history (step 3.3; scope clarified by the owner 2026-10-15):** the Areas section becomes analytical and statistics-based, at the country, state and county level for now: changes over time easy to see, a JavaScript map with statistics down to the county level, shortage areas shown, and the statistics going back in time once the NBER history (step 3.4) is loaded, not only since recording started on 2026-10-09. **Done 2026-10-15:** a D3 map of states or counties coloured by a statistic, shortage areas hatched, and the figures month by month (providers, new and deactivated NPIs, moves, specialty, license and exclusion changes, record-quality mix), each count listing its providers; derived back 60 months for NPIs issued, recorded from now on for the rest. (First outline: "Changes in [county] this month", "New practices", "Closed or moved", monthly change counts on county pages.)
  - **History before October 2026 (step 3.4). Approved 2026-10-13:** the NBER archive of past NPPES monthly files, for trends, analytics and statistics (see "Missing" below). **Researched and decided 2026-10-15:** 197 of the 227 months from 2007-11 exist (30 missing, mostly 2007-12 to 2011-02 and 2013-05); CMS still serves every monthly from 2015-06 at its usual (unlisted) URLs, NBER the earlier ones. Every month is processed (~137 GB downloaded, each zip deleted after use) into a per-NPI history (`npi_history`) and the area figures (basis 3), counted by primary practice location (any location from 2018-09), with today's county boundaries; credited "CMS NPPES; historical copies via NBER". **Built 2026-10-15** (migration 103, `Npi.Loader history`): the archive's 198 files (61 from NBER, 137 from CMS including October 2026) are found from NBER's listings and CMS's name patterns, read straight from each zip (a 6 GB CSV in ~15 s), compared in memory and written as versions (about 1% of NPIs a month: 50,942 from December 2016 to January 2017); the area figures from November 2007 replace the derived months and stop where the recorded series starts; the area pages, lists, map and API show them with the source credited. **Owner's answers 2026-10-15, built the same day:** secondary practice locations count from the first file that lists them (September 2018), so from then the archive counts every practice location as the recorded series does and the join in October 2026 is smooth; the counts change definition once, at September 2018, which the area pages and the API coverage (`anyLocationFrom`) say. Per-NPI history: the profile's "Over the years" (each earlier version in plain words, the months without a file, the source), `history` in the API record, a deactivated NPI's last known record without its name, and Verify NPIs' "Changed since [date]" (a pill, the lines of what changed, a tab, CSV and Excel columns, `changedSince` in the lookup API). Measured numbers and the full-run estimate are in CLAUDE.md §12; the full run is part of the combined run on `workplace`, and each monthly `run` loads adds its month.
  - **Spot check (step 3.5). Done 2026-10-13.** 50 random Stale records (seeded, so the same sample can be drawn again; 39 individuals, 11 organizations, of 1,798,241 Stale), each checked against the live NPPES registry, web searches, older Medicare years, the NY and CA Medicaid lists and our state license data:
    - **Organizations: 10 of 11 had a real problem:** 5 closed or not found anywhere, 3 taken over or moved, 2 still open but at a wrong NPPES address; 1 couldn't be told.
    - **Individuals: 6 real problems** (4 expired licenses, a practice moved to another state, a 2008 military box address), 3 consistent with Stale (billed Medicare until 2013, 2016, 2020), 1 possible false alarm (a dietitian at a VA medical center: VA clinicians don't bill Medicare), **29 not verifiable** with free sources: mostly massage therapists, LPNs, aides, doulas, pharmacists and counselors, who never bill Medicare, so the old record is the only evidence. Web search won't look up private individuals, and state license lookup sites can't be scripted (New York blocks it).
    - **So:** the label works for organizations; for individuals in professions that don't bill Medicare, only state license or Medicaid data can confirm or clear it. The live registry matched our copy for all 50.
  - **More states' Medicaid enrollment (step 3.7, owner 2026-10-14; researched, not built):** the NY and CA lists cover two states. Checked (2026-10-14) for the same kind of data, a public bulk file of enrolled providers by NPI: every Socrata open-data portal (data.texas.gov, data.pa.gov, data.nj.gov, health.data.ny.gov …), the state Medicaid sites, and HHS's open-data platform.
    - **Florida: confirmed.** AHCA's **Provider Master List** (portal.flmmis.com, Managed Care › Registration › Provider Information Reports): every enrolled Florida Medicaid provider with NPI, Medicaid ID, enrollment type and contract dates, published for health plans to check enrollment; a pipe-delimited zip (~17 MB, `prw19002.zip`) or a spreadsheet, no login, updated weekly or so (last 2026-10-14). Added like NY and CA: `state_medicaid` rows for FL, a sign of current practice; enrollment types "Enrollment" and "Limited Enrollment" count, "Registered" (no Medicaid ID) does not.
    - **Every state, as history: HHS "Medicaid Provider Enrollment Segments"** (opendata.hhs.gov, from T-MSIS; one 293 MB zip, published 2026-07-24): one row per provider, state and enrollment period, January 2018 to December 2024, with NPI, state, start and end date, status and Medicaid/CHIP plan. Not a current list (it ends December 2024, and 2024 is preliminary), so it would count as "enrolled in [state] Medicaid through [date]": a sign of practice when the period is open or ended within the last two years, and history on the profile either way. The NPI isn't validated (blank, 0000000000 or other values are skipped).
    - **Possible addition, same platform: "Medicaid Provider Spending by HCPCS"** (3.5 GB, 2018–2024, billing and servicing NPI × procedure × month, cells under 12 claims or patients suppressed): Medicaid billing like Medicare Part B, "billed Medicaid in [year]". A separate yes.
    - **Not found as a public bulk file:** Texas (TMHP has a lookup only), New Jersey (data.nj.gov has the ineligible-provider list, which is exclusions), Pennsylvania (PROMISe portal, no list), Ohio (the Provider Master File goes to managed-care plans under contract; a records request is the route), Maine (only the MaineCare exclusion list). Exclusion lists are Phase 4.
  - **"Facilities & hospitals" and "Groups & organizations" home pages (step 3.6, owner 2026-10-14; pages from data already loaded built 2026-10-15, migration 101; the new sources are next).** Two menu entries, each a starting page like Companies & products (search, totals, the largest, lists, exports), leading to the profiles that exist today (`/facility/{ccn}`, `/group/{pacId}`, organization NPIs at `/provider/{npi}`).
    - **Facilities & hospitals** (`/facilities`; `/hospitals` opens it on hospitals): every certified facility by type (hospitals, nursing homes, home health agencies, hospices, FQHCs, rural health clinics, and dialysis centres, surgery centres, rehabilitation and long-term care hospitals if their sources are added), with type, state and rating filters, a map, totals per type, the best and worst rated, and CSV exports. Search by name, city or CCN.
    - **Groups & organizations** (`/organizations`): Medicare groups by size (`/groups`), organization NPIs by plain category and state with the record-quality label, health systems (if the Compendium below is added), totals and CSV exports. Search by name, NPI or PAC ID.
    - **Already loaded but not shown** (no new source): Provider of Services file (bed counts, ownership type, certification and termination dates, every facility type, terminated ones too), MIPS scores of groups and APM entities, FQHC and rural clinic enrollments (no facility page yet), Medicare billing totals of organization NPIs, Open Payments to teaching hospitals.
    - **Proposed new sources** (free, public; each needs the owner's yes, and its current file and fields are checked before it is built):
      - **Ownership:** CMS **"All Owners"** files on data.cms.gov (hospitals, nursing homes, home health, hospice, FQHC, RHC; each owner with its role, share and kind, e.g. private equity or REIT) and the **Change of Ownership** files, for "Owned by" and ownership history on facility pages, and owners as links between facilities. **Built 2026-10-15** (group 2a; first load in the next combined run): "Who owns it" and "Ownership changes" on facility pages, owner pages `/owner/{id}` and the owner list `/owners` (organizations only: people are named on the facility pages, without a page, ID or search of their own; owner, 2026-10-15), and the `/facilities` filters "Changed ownership" (within 1–10 years) and "Has penalties" (nursing homes, last three years); CMS publishes change-of-ownership files for hospitals and SNFs only.
      - **Health systems:** AHRQ **Compendium of U.S. Health Systems** (systems with the hospitals and physician groups they own; already named as a proposal in T5), for system pages and "part of [system]" on hospital and group profiles.
      - **Hospital finances and size:** CMS **Hospital Cost Reports** (HCRIS, yearly: beds, discharges, staff, revenue, uncompensated care), and IRS **Form 990** filings of nonprofit hospitals (IRS e-file bulk data; Schedule H community benefit).
      - **What a hospital does:** **Medicare Inpatient Hospitals by Provider and Service** and **Outpatient Hospitals by Provider and Service** (data.cms.gov, by CCN: discharges and payments per DRG or procedure); more Care Compare hospital files (timely and effective care, complications and deaths, infections, unplanned visits, emergency department times).
      - **More facility types for the Facilities page:** Care Compare **dialysis facilities** (listing and quality incentive scores, by CCN; no NPI link needed for a facility page), **ambulatory surgical centers**, **inpatient rehabilitation** and **long-term care hospitals**.
      - **Nursing home inspections:** Care Compare **health deficiencies, fire safety deficiencies and penalties** (fines, payment denials), for the nursing home profile. **Built 2026-10-15** (group 2a): summarized per CCN (~620,000 citations → inspections, counts by cycle and scope and severity, the ten most serious and ten newest citations).
      - **Hospital location and capabilities:** DHS **HIFLD Hospitals** (public domain: coordinates, beds, trauma level, helipad), for the map and a trauma-level filter.
      - **Built (group 2b, 2026-10-15; first loaded in the next combined run):** the dialysis facility listing and ICH CAHPS stars (NPIs from the Medicare Dialysis Facility Report's data API, two requests instead of its 2.7 GB file), ambulatory surgical center measures, inpatient rehabilitation and long-term care hospital listings and measures, the hospital Complications and Deaths, Infections, Unplanned Visits, Timely and Effective Care and Spending files (a compact per-CCN set with the national values: "How it compares"), and Medicare Inpatient by Provider, Inpatient and Outpatient by Provider and Service ("Medicare volume", top 10 DRGs and APCs; CMS publishes no outpatient by-provider file, so outpatient totals add up the listed APCs). Then (owner, 2026-10-15): the dialysis report's and surgery center file's NPIs count as holding a certified facility for the record quality; OAS CAHPS for surgery centers (the top box of all four questions: facilities and staff, communication about the procedure, rated 9–10, would recommend; with the national values) and ESRD QIP total performance scores and payment reductions (newest payment year) added; surgery center measures in CMS's units (ASC-12 per 1,000 colonoscopies, ASC-17 and 18 percent, ASC-19 a ratio).
      - **Clinics:** HRSA **Health Center Service Delivery Sites** (every FQHC site) and **UDS** health-center data (patients served, quality measures), and HRSA **340B** covered entities (hospitals and clinics in the drug discount program).
      - **Groups:** CMS **Medicare Shared Savings Program ACO** files (ACOs, their participant organizations and results), for "part of [ACO]" on group and organization profiles.
  - **New signs of practice, approved 2026-10-13:**
    - **State Medicaid enrolled-provider lists. Done 2026-10-14** (544,718 enrollments; Stale 1,798,199 → 1,761,950, Check 1,677,377 → 1,653,944; 112,942 providers have no other sign of practice): New York (health.data.ny.gov `keti-qx5t`, 236k NPIs) and California (data.chhs.ca.gov, Medi-Cal fee-for-service providers, 308k NPIs), free open data updated weekly or so. Measured: 34,797 Stale and 25,850 Check records in the two states are enrolled with no other sign of practice (12.1% of NY's and 6.5% of CA's Stale individuals). Other states seem to release their lists only on records requests; each one found is a proposal.
    - **Older Medicare years, 2013–2023 (built 2026-10-15: migration 100, reason MedicareStopped; first load in the combined run):** Part B by provider and Part D prescribers by provider (data.cms.gov, same catalog as today's newest year), shown as "last billed Medicare in [year]" and given as a reason on the label. It explains a Stale label more often than it changes one.
- **Data:**
  - **Have:** weekly NPPES files, `provider_change` (since 2026-10-09), geocodes and building types, Care Compare with its addresses (`cc_clinician_location`, added in step 3.1), LEIE, opt-out, state licenses (15 states), Medicare enrollment, reassignment and revalidation, Overture business listings, the Provider of Services file.
  - **Missing:**
    - **History before October 2026.** CMS publishes only the current monthly file. The NBER archive of past NPPES monthly files is free and would let us show trends back many years. It's a large download (one monthly file per period). **Approved by the owner 2026-10-13** (step 3.4).
    - **New York license status:** NY publishes board actions only. Its Office of the Professions sends weekly bulk files of currently registered licensees on request (a form the owner submits, free) and says an API is being developed.
    - **VA facility locations:** would explain clinicians at VA addresses, who show no Medicare activity. **Not needed for now** (owner, 2026-10-13).
  - **Tested and not usable as sources (step 3.5):** web search and provider directories (their terms forbid reuse; the search refuses private individuals), state license lookup sites without bulk data (can't be scripted).
- **Worked when:**
  - every profile shows a quality label with reasons (**met**, step 3.1);
  - county pages show monthly change counts (**met**, step 3.3);
  - spot checks of 50 "Stale" records find real problems (wrong address, closed practice) in most of them (**met for organizations**, 10 of 11; most individuals couldn't be verified with free sources, step 3.5).

### Phase 4: Bulk verification and compliance checks

- **Goal:** credentialing, billing and staffing teams can verify a roster in one step and get a report they can file.
- **Already on Verify NPIs (steps 1.7 and 3.1):** status with the deactivation date, OIG exclusion and Medicare opt-out dates, order/refer and Medicare billing, the record-quality label, CSV and Excel.
- **Scope:**
  - **Verify NPIs, extended.** For each NPI:
    - status (active, deactivated with date, invalid);
    - name match against the uploaded name;
    - specialty and state match;
    - OIG exclusion, Medicare opt-out, order/refer eligibility;
    - state license status and actions (15 states);
    - changes since a given date;
    - the record-quality label from Phase 3.
  - **Output:** a PDF/CSV report with a summary, the sources and the as-of dates.
  - **Saved monitoring:** a roster re-checked weekly. This arrives with accounts in Phase 5.
- **Data:**
  - **Have:** everything listed above.
  - **Missing (all free; SAM.gov and the state Medicaid exclusion lists approved by the owner 2026-10-15):**
    - **SAM.gov exclusions**: the federal debarment list, a public extract plus an API key;
    - **state Medicaid exclusion lists**: about 40 states, each in its own format, so pilot the largest first;
    - **more state license boards**: from the survey in CLAUDE.md; RI, VT, WV, AL, MI and CA were added 2026-10-12/13, and New York's bulk files can be requested (Phase 3);
    - ~~**CMS public enrollment**: shows whether a provider is enrolled in Medicare (PPEF)~~ added in step 1.5.
  - **Not available free:** the DEA registration list, the NPDB, the CMS Preclusion List, and ABMS board certification.
- **Worked when:**
  - a 1,000-NPI roster is verified in under a minute with a downloadable report;
  - three real credentialing users say the report replaces a manual step.

### Phase 5: Accounts, saved lists and alerts

- **Goal:** professionals come back weekly because the site tells them what changed in their lists, territories and rosters.
- **Scope:**
  - **Accounts:** sign-in with email, Microsoft or Google, free; they hold saved items and alerts.
  - **Saved items:** searches, lists and rosters.
  - **Weekly alerts:** new providers in a territory, moves, deactivations, new exclusions, license actions. Sent by email and shown in an in-app feed. Built from the weekly NPPES files we already process.
  - **Team sharing.**
- **Data:** nothing new.
  - **New infrastructure (the owner chooses and approves):**
    - an identity provider (ASP.NET Core Identity, or Microsoft Entra External ID);
    - an email service (Azure Communication Services or SendGrid);
    - a small user database;
    - the private test copy (Phase 2) to test sign-in and email.
  - **Privacy:** a privacy policy and terms of service are needed before launch.
- **Worked when:** 30% of signed-up users open an alert within a week, and saved lists are re-exported.

### Phase 6: API and export tooling

- **Goal:** developers and data teams can build on the data reliably, for free.
- **Scope:**
  - **API keys:** a self-serve key page and a usage dashboard, with per-key quotas available but off (exports and the API stay unlimited, owner 2026-10-11). Key checking is already built (`Api:RequireKey`, off).
  - **Weekly change feed:** an endpoint plus a downloadable file.
  - **Bulk downloads:** the full summary dataset as CSV/Parquet.
  - **Webhooks** for alerts.
  - **Client libraries:** the .NET client published on NuGet; a Python client.
- **Data:** nothing new. **Check licenses before publishing bulk files:**
  - Illinois license data is under the Open Database License (share-alike): bulk files that include it must stay under the same license.
  - Overture's licenses are reviewed for map data.
  - CMS, FDA, HRSA, Census and OIG data are public domain.
- **Worked when:** keys are issued and used weekly, and the change feed and bulk files are downloaded.

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
  - **Insurance network pilot in New York** (owner, 2026-10-15; the open Stage 5.5 item 16, folded into this phase): "listed in [plan]'s directory" with its source and date, never "accepting new patients".
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
- **Company and product profiles redesign:** **Done 2026-10-12 (pulled forward by the owner):** a Companies & products main page (`/companies-and-products`: totals, payments by year, top companies and products, search across both, CSV of every company and every product); company and product pages in the P09/P10 layout with a raw-data download each.
  - **Goal:** the same summary-first layout for the Open Payments companies and the products.
  - **Scope:**
    - a summary card ("Paid $X to Y providers in 2025; top products; recalls; integrity agreement");
    - sections by question;
    - raw-data download.
  - **Data:** have it all.
  - **Worked when:** company and product pages hold readers (time on page, clicks into providers) better than today.

---

## 4. Business model

Not part of this plan (owner, 2026-10-11). Every feature is built free: no paywalls, no accounts-for-payment, no
Stripe. Pricing is planned separately, after the product is complete.

**Design rule for the code:** limits or tiers must be addable later without rework. Usage-sensitive features (exports,
bulk verification, API calls, saved lists, alerts) go through one place that can apply a limit per feature and per
account or key, configured, and off by default.

**Parked for the separate pricing plan** (ideas from the draft, not commitments): free core with Pro, Team and Data/API
tiers; pay-as-you-go verification; data licensing; custom area reports. Sponsored listings are not recommended.

## 5. Working rules for this plan (owner, 2026-10-11)

- **Pacing:** one phase at a time; within a phase, one step at a time. Each step is committed, summarized briefly, and waits for the owner's "go".
- **New ideas:** new features and data sources are proposed, never added without asking. Every "Missing" item above is a proposal.
- **Constraints:** existing URLs and API endpoints keep working, and pages stay fast and mobile-friendly.
- **Documents:** CLAUDE.md, this plan and [TODO.md](TODO.md) are kept current in the same commit as each decision or step.

## 6. Decisions (owner, 2026-10-11)

1. **Visual direction:** all three combined (A's summary-first layout, B's deep green and serif headings, C's sidebar and dense tables). Clinician profiles get an avatar, an OpenStreetMap preview, a one-sentence summary and the specialty in plain words (§2).
2. **Deactivated NPIs:** shown as "Deactivated on [date]" on a minimal status page and in bulk verification, for credentialing users. Never listed as active or returned by search.
3. **Exports and the API:** both stay unlimited for now.
4. **No paid features in this plan** (§4). Code is structured so limits or tiers could be added later.
5. **Data sources:** Claude may propose any free source that helps; each is added only after the owner says yes. The candidates named in this plan are proposals until then.
6. **Going live:** no public launch until every phase is complete; then branding, marketing and a business plan. A private, password-protected test copy on Azure may be set up when a feature needs real hosting (alerts, email, accounts); ask first, with a cost estimate.
7. **Analytics:** Azure Application Insights across the site and the API, with a privacy notice.

**Later decisions:**
- 2026-10-12: Phase 3 comes next; Phase 2 waits until a feature needs hosting. When Azure comes, the nightly load runs in Azure, not on the owner's PC (Phase 2).
- 2026-10-12: the company and product redesign is pulled forward from Phase 9 (done).
- 2026-10-12: more signs of practice for the quality label (Open Payments in either of the last two years, reassignments, MIPS, facility affiliations, open business listings, FQHC and rural clinic enrollments, opioid treatment programs, the revalidation list, equipment suppliers) and closures (Overture, Provider of Services).
- 2026-10-13: every load and rebuild is designed to be incremental; the loader speed work (§7) runs alongside Phase 3.
- 2026-10-13: yes to the NY and CA Medicaid enrolled-provider lists and to the older Medicare years (Phase 3), to be built after the open speed items.
- 2026-10-13: yes to speed item 14 (relaxed disk flushing during big loads; the owner is told of any setting to change by hand) and to the NBER archive of past NPPES files (step 3.4: trends, analytics, statistics). Not needed for now: the VA facility list and Phase 2 (private test hosting).
- 2026-10-14: more states' Medicaid enrollment is Phase 3 step 3.7 (Florida's Provider Master List confirmed; the national T-MSIS enrollment history and Medicaid billing files proposed); not built yet.
- 2026-10-14: every list and export works without a filter (the whole country); an export of every provider comes from a ready-made file the loader rebuilds after each load. The map's filters match the search page, and the map's area opens as a list or a CSV. Organization and facility home pages are Phase 3 step 3.6.
- 2026-10-15: step 3.4: keep the per-category county rows; count secondary practice locations from September 2018 (the archive then counts every location, as today); build per-NPI history pages (profile, API, deactivated NPI's last record, Verify's "changed since").
- 2026-10-15: an open business listing counts as a sign of practice only when a confirming source backs it (Meta, store locators and listing services, or a Bing/Foursquare record seen within 3 years); a 10+ year old record with only a listing is Check.
- 2026-10-15: the owner approved the step 3.6 sources Claude judges best: All Owners and Change of Ownership, more Care Compare facility types and hospital measures, nursing home deficiencies and penalties, Medicare inpatient/outpatient by provider, HIFLD hospitals, HRSA health center sites and 340B, the MSSP ACO files and the AHRQ compendium (built after the pages); HCRIS cost reports, Form 990s and UDS later.
- 2026-10-15: step 3.3 becomes part of Areas: an analytical, statistics-based Areas section (country, state, county for now) with changes over time, a JavaScript map with statistics to the county level and shortage areas, going back in time once step 3.4's NBER history is in. Step 3.7's sources (Florida's Provider Master List, the national T-MSIS enrollment segments and Medicaid spending files) are approved; not to be added yet.
- 2026-10-15: Phase 4's SAM.gov exclusions and state Medicaid exclusion lists approved; New York is the Phase 8 insurance network pilot state; step 3.7 stays on hold. Step 3.6 part 2: people listed as facility owners are shown by name and role on the facility page only (no person pages or search); ACO participants are matched to organizations by exact legal business name, marked as a name match; dialysis and surgery center NPIs count as a sign of practice; OAS CAHPS and ESRD QIP added; HRSA health center sites in Find care; 340B from the owner's OPAIS download; AHRQ's Compendium from the owner's downloads, credited to AHRQ.
- 2026-10-15: before release, a web-based admin console with everything in Docker: Portainer Community Edition for the containers, CloudBeaver for the database (read and write, an account per admin), loader runs and site settings (API keys, rate limit) from the console, a few admins, an audit log; built with Phase 2, which decides whether production runs in containers.
- 2026-10-13: [TODO.md](TODO.md) is the working to-do list, kept up to date with each decision; this document stays the full plan.

## 7. Loader speed work (owner, 2026-10-13)

Not a product phase, but it decides how long the nightly load runs and so what Azure will cost (Phase 2). A weekly
NPPES file changes about 50,000 of 9.5 million providers, yet it rebuilt every table after it: about 2–2.5 hours
(projection 35–57 min, credentials ~7, address types 4–11, area counts ~17, map ~23, quality labels 15–25). The audit
of 2026-10-13 proposed these items; the owner chose the order below. The state of each is in [TODO.md](TODO.md).

| # | Item | Expected gain |
|---|---|---|
| 15 | MySQL tests in parallel on a pool of test databases (**done**, PR #63) | test run 4.5 min → 35 s |
| 1 | Incremental projection: weeklies rebuild only the providers they touch; the monthly file is compared with the stored data and only the differences are applied | ~50 min → 1–3 min |
| 2 | The steps after the projection work from a queue of changed providers | ~75 min → a few minutes; with item 1, a weekly run of ~10 min |
| 3 | A dataset reload queues only the providers whose rows changed (the quality label's inputs) | a full relabel → only the changed providers |
| 8 | Downloads kept until a load succeeds, reused when the server says unchanged, large files in parallel parts | no repeated 9 GB / 4 GB downloads after a failure |
| 4 | FEMA buildings: match only new and moved addresses, reading the files in place (**done** 2026-10-14) | ~17 min + 21 GB download → ~6 min, no download (measured: 5,724 addresses) |
| 5 | Overture in `run`, new addresses only; a full pass only for a new release (**done** 2026-10-14) | new addresses placed at the building without a manual 50–70 min run (measured: 6,399 new addresses, 77% placed, 18.7 min the first time; ~10 min expected with a box per region) |
| 12 | Bulk-load into tables without indexes, add them afterwards (**done** 2026-10-14; full-text and spatial indexes kept, they cost more added afterwards) | big loads 1.5–3× faster (measured: full projection 63 → ~51 min, area counts 19 → 11 min; credentials, address types ~1 min less; quality, Part D, map unchanged) |
| 6 | ~~Socrata delta pulls (TX, WA, IL, CO, CT, DE), a full pull every few weeks~~ **dropped** (owner, 2026-10-15): only CT publishes real deltas; the other portals rewrite every row at each update | ~~changed rows only~~ 1–2 min a week |
| 7 | Independent sources, states and the Vermont feed in parallel (2–4 at a time) (**done** 2026-10-15; timings to measure) | downloads overlap loads |
| 10 | Deactivation flags by difference (**done** 2026-10-14) | 9.8M rows → a few thousand (measured: after a monthly under 1 s instead of 55–61 s; after a report a few seconds instead of ~57 s) |
| 13 | The map-by-credential table built once; independent builders side by side (**done** 2026-10-15; timings to measure) | less after item 2 |
| 11 | API caches: the openFDA key (**done**, PR #62); ClinicalTrials.gov 4 products at once, one request started every 200 ms (**done** 2026-10-15; timing to measure) | the first fill in one run instead of ~10 days; trials ~7 → ~4 min for 600 products (the pace, not the parallelism, sets it: an answer takes ~0.1 s) |
| 9 | Stream zips instead of extracting them, where a loader still extracts (not scheduled) | disk and time |
| 14 | Relax MySQL's disk flushing during big loads (**skipped** 2026-10-14 after measuring: a commit flush ~0.6 ms and the big loads are single statements; a bigger index-build buffer didn't help; approved 2026-10-13: a crash loses the last second of writes, which a reload repeats; any setting changed by hand is explained to the owner first) | faster big loads |
| 16 | Build the loader to its own folder so loader work never waits on stopping the site (not scheduled) | development only |
