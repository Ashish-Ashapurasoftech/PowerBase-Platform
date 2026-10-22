# Features in all_in_one.qbl that PowerBase does not have — by module

Date: 2026-10-21 · Source: `all_in_one.qbl` (13 applications, 400 tables) · Companion: [QBL_ALL_IN_ONE_MAPPING_SPEC.md](QBL_ALL_IN_ONE_MAPPING_SPEC.md) (what *can* be kept).

**Scope of this list.** Only things the file actually uses and PowerBase cannot hold. Quickbase features the file does not use (KPI-less dashboards, Kanban, Timeline, WorkDate, vCard, cross-app relationships, CAPTCHA, barcode scanning …) are listed once at the end, not by module. "Missing" is based on a search of PowerBase's domain, application and Angular code; items marked *(verify)* need a quick confirmation with the owning team before being scheduled.

**Seen in** gives the apps and counts so impact can be judged. **Interim handling** is what the importer should do until the feature exists, so nothing is lost silently.

App short names: **TS** Time Sheets · **CP** Compliance Process · **HR** HR Onboarding · **DSP** DSP Supervision · **VDB** VDB Database · **SER** Serenity · **CCR** CCR Timesheet Storage · **TT** Training Tracking · **IMP** CARES Imports · **FIN** VDB Financial Docs · **BIL** CFTSS Billing · **GO** CFTSS Goals & Objectives · **RAM** Ramapo.

### Corrections to the earlier cares-only list
This file disproves several items in [POWERBASE_MISSING_FEATURES.md](POWERBASE_MISSING_FEATURES.md): **edit/append history is not used** (`EditHistory.Enable` is false on all 1,463 text fields), **CAPTCHA / bot-check is not used** (`BotCheckModes` empty on all 384 forms), **`ToTimeOfDay` is registered** in the formula engine, and the formula engine resolves every function the file calls except `UserRoles`. App-level items (fiscal year, first day of week, time zone, performance bar) are real but are one value repeated across apps.

---

## 1. App

| Missing in PowerBase | Seen in | Interim handling |
|---|---|---|
| Fiscal-year settings (`FirstMonthOfFiscalYear` Jan, `FiscalYearDesignation` Last) | all 13 (identical) | report once |
| First day of week (`FirstDayOfWeek` Sun) | all 13 | report once |
| App-level time zone (`Timezone`: Eastern ×7, Default ×6) *(verify — per-user/field zones exist)* | all 13 | report; keep Eastern as a note |
| Custom header/footer text and links, `HideQuickbaseStandardMenu` | 6 apps use custom header/footer; 13 hide the standard menu | map colour to branding; report text/links |
| Performance bar / analyzer (`ShowPerformanceBar` ×2, `ShowPerformanceAnalyzer` ×3) | 5 apps | drop (Quickbase-only) |
| `HideNewUpdatedIcons`, `GlobalSearchExactMatch`, `DisableAllUsersSelection`, `DisplayChartShading`, `DefaultViewMode` (Auto), `TableV3State`, `Utf8DataEncoding` | all 13 (all default/false) | drop silently |
| `CrossApplicationAccess` (None) | all 13 | nothing to carry |
| Role order (`RoleOrder`) *(verify)* | all 13 | imported roles rank equally |

## 2. Table

| Missing | Seen in | Interim handling |
|---|---|---|
| **Connected tables** — live sync from another Quickbase app (`ConnectedData`: data source, field map, key fields, schedule, update mode, filter) | **64 tables**: TS 19, DSP 10, CP 8, CCR 7, HR 5, BIL 4, VDB 4, TT 3, GO 3, SER 1 (+ 45 record sets, ~8,000 connected columns, 15 data sources) | import as ordinary local table **and warn per table** (data will not refresh) |
| Per-table, per-role **home page** (`TableHomePageSettings.RoleViews`, `BasedOnRole` ×31) — PowerBase has one home page per *role* | 31 tables | use role-level home page only |
| Default **colour coding** for reports (`DefaultColorCoding`) | 400 tables | drop |
| Default **row height** (Short ×400) | 400 | drop |
| `ActionOnNewFieldAdd` (MakeDefault — new fields auto-added to reports) | 400 | drop (PowerBase auto-adds only to forms) |
| Table "Searchable" (exclude from global search) and "index file attachments" (true ×4) | 400 / 4 | report the 4 |
| `TableNameOverride` (separate display name) | 1 | use as label |
| Report groups (`QB::ReportGroup`, folders of reports) | 415 | informational |

## 3. Field

| Missing | Seen in | Interim handling |
|---|---|---|
| **Add to all new reports** (`AddToAllNewReports`, present on every field) | all fields | drop |
| **Block data copy** (`AllowDataCopy: false`) | ≥ 200 fields (Text 68, Checkbox 40, Number 24, Choice 25, Date 20, DateTime 10 …) | report per app |
| Phone: has-extension (true ×65), international flag, default country (US) | 81 fields | drop |
| Email: default domain, sort by domain, "email all" (all default) | 105 fields | nothing set |
| URL: **button colour** (626 URL formulas), "abbreviate" (11), iframe width/height (4) | 641 fields | drop colour (cosmetic) |
| Address: format (US ×19 / International ×5), default map type, input width | 24 fields | drop |
| File attachment: "open in new window" (false ×2), "open access" (true ×1) | 3 fields | drop |
| Choice: **"allow users to create choices"** (true ×35), choices **from another field** (1) | 36 fields | drop; report the 1 |
| Rich text `@mentions` (false), text multi-line `Height` (rows) | 112 fields | drop |
| Per-field sharing (`SharedWith`) | 1 field | map to field permission if possible |
| `OverrideSubFieldAccess`, `SnapshotSourceLookupField` (Quickbase-internal; snapshot fields 15) | 15 | report |
| Unsupported fields (the field they pointed at was **deleted** in Quickbase) | 45: SER 13, TS 12, IMP 8, FIN 8, CP 3, VDB 1 | skip + report (done); the import cannot recreate data that no longer exists |

## 4. Relationships, Lookup, Summary, Reference

| Missing | Seen in | Interim handling |
|---|---|---|
| **Reference field populated by a formula** (`Reference.Formula`: relationship key computed, e.g. current-user record id) | **97**: CP 29, TS 25, DSP 12, HR 8, CCR 6, SER 5, VDB 5, RAM 2, TT/IMP/BIL/GO/FIN 1 each | import the relationship; keep the formula text in the report; optionally add a formula field beside it |
| **Reference conditional values** (`ConditionalValues` — filter the picker) *(verify — PowerBase has reference filter conditions)* | 81 | translate where possible, else report |
| Summary "display as link to children" (true ×820), **drill-down report**, "evaluate only on change" | 851 summaries | drop |
| Lookup/summary **chain depth** limit (PowerBase caps chains; Quickbase does not) | ~47 lookups in earlier sample — count per app on first full run | report which ones |
| `CombinedText` summary over a Reference/calculated field (280 CombinedText summaries; PowerBase refuses some sources) | up to 280 | skip + report |
| Reference "display as link to parent" (true ×582), "use record picker for grid edit" (false) | 669 | drop |

## 5. Forms

| Missing | Seen in | Interim handling |
|---|---|---|
| Form **page colour** (`Page.Color`) | 566 pages | drop |
| Section visibility **per mode** (`ModesToShowIn` on sections) *(verify — PowerBase has per-element only)* | 1,206 sections | apply to the section's elements |
| Element `ShouldValidate` (10), `HideMap` (39), address `EntryMode` (1) | 50 | drop |
| **Choice-filtering by another field** (`FilterChoicesBy` ≠ Default) | 119 of 5,120 | drop; report |
| Embedded report **sub-form** (`SubForm` ×202) | 202 | embed report only |
| Form `AllowedSaveActions` values with no PowerBase option *(verify)* | 199 forms set it | map the known ones, report the rest |
| **Legacy forms** (`QB::Form`, ~9,500 elements) | 402 forms (every table) | skipped; one informational line per table (FormV2 is the live form) |
| Legacy-form settings (`NewFieldAction`, `HorizontalRuleBetweenSections`, `WrapFieldLabels`, `ShowSaveAndCancelButtons`, `WaitForEmbeddedReports`) | 402 | none |
| Per-role form defaults (`Forms.Properties.RoleDefaults`) | tables using roles *(count on first full run)* | report |

## 6. Form rules

| Missing | Seen in | Interim handling |
|---|---|---|
| Date conditions **Days ago / Range / Relative (Today…) / IsDuring / IsNotDuring** | 12 / 6 / 13 / 5 / 1 | convert to formula condition where possible, else report |
| **Mixed nested AND/OR groups** inside one rule (1,977 condition groups) | rules in TS/CP (count per rule on first run) | flatten one level; split or report deeper |
| **Tags** on rules (39 with tags) | 39 | drop |
| "Fire only on change" toggle (true ×1,710) *(verify)* | 1,912 rules | map to `AnyChange` |
| Condition `Changed` (field-change trigger) *(verify)* | 2 | report |
| Legacy dynamic form rules (`QB::FormRule`, 50) | legacy forms | skipped with them |

## 7. Reports

| Missing | Seen in | Interim handling |
|---|---|---|
| **Calendar reports** (start/end date, colour coding, create-from-calendar) | 5: TS 2, CP 1, HR 1, RAM 1 | warn per report; (could become a table report) |
| **Map reports** (address field, map type, icons) | 2: HR 1, RAM 1 | warn |
| **Row/cell colour coding** (`ColorCoding.ColorBy`: Default 463, Formula 23, RowValue 5; `ColorFormula` ×28) | 491 reports | drop; keep formula text in report |
| **Default row height** | 2,160 | drop |
| **Report formula columns** (`Report::Formula::*`, 42: Text 17, Numeric 9, Date 6, Checkbox 6, Percent 3, TimeOfDay 1) | TS 15, CCR 15, CP 9, BIL 1, GO 1, HR 1 | report formula text; optionally create a table formula field |
| **Summary formulas / summary variables** (`SummaryFormula::*` 8, `SummaryVariable` 24) | CP (all 32) | report |
| Column **label override / alignment / pinning** (`ColumnsUI`) *(verify)* | 2,152 | drop |
| "Display only new or changed records" (true ×402), "show summary table" (11), "hide main table" (7) | 420 | drop; report the 18 |
| Table report `Format` (XSL stylesheet; Normal ×2,160) | 2,160 | nothing set |
| Per-user personal report settings | – | no equivalent |

## 8. Charts

| Missing | Seen in | Interim handling |
|---|---|---|
| Goal lines on stacked bars (`GoalLine`) *(verify)* | 3 | drop |
| Gauge **limits/colour bands** (`GaugeLimits`, `Colors`) — PowerBase gauge has fixed/data-value goal only *(verify)* | 6 (all CP) | drop colours |
| Pie "legend colours", per-series colours | 15 | drop |
| Chart **drill-down report** (`DrilldownReport`) | 24 | drop |

All 24 charts are Pie, Stacked bar and Gauge, which PowerBase has (Bar, StackedBar, HorizontalBar, HorizontalStackedBar, Line, LineBarCombo, Pie, Donut, Gauge, Waterfall, Radial). The gap is the importer, not PowerBase — see the mapping spec §8.

## 9. Dashboards and home pages

| Missing | Seen in | Interim handling |
|---|---|---|
| **KPI widget** (`Dashboard::Widget::Kpi`: text KPI, delta, target, scale, decimals, dynamic colour) | **6**, all CP | report; replace with a Report widget on a summary report if acceptable |
| **Per-widget button styling** (colour, icon, alignment) | 28 buttons | map to ButtonBar; drop styling |
| Dashboard **tabs** (`Dashboard::Tab`, 49) *(verify — PowerBase pages have no tab container)* | 49 across 28 dashboards | one page per tab, or flatten |
| Widget grid (`X/Y/Width/Height`, 12-column canvas versions 1 & 2) | 98 widgets | needs a scaling rule to PowerBase's grid |
| Home-page **button widget** (`Button`, `ButtonBar` with record links) | RAM 6 | ButtonBar covers part; report rest |
| **Dashboard filters** (not used here) | 0 | – |

PowerBase dashboard widget types: Report, Text, ButtonBar, WebPage, Search, Spacer, LinkBar. Table/Summary/Chart widgets become Report widgets; RichText → Text.

## 10. Code pages

PowerBase can hold them (24 pages; RAM 16). Constraints to document: needs the tenant flag `CodePagesEnabled` and `pages:code`; PowerBase stores HTML/CSS/JS in separate columns (Quickbase has one blob); Quickbase-specific DBID tokens inside the code are not rewritten; 14 of the 24 have no stable name in the QBL (`Name` is a parameter).

## 11. Formula engine

| Missing | Seen in | Interim handling |
|---|---|---|
| **`UserRoles(…)`** function | 5 calls | report formula |
| **Reference fields usable inside formulas / lookups of reference fields** *(verify engine limit)* | most "needs manual review" formulas | flag; engine change needed |
| **Multi-branch `If(c1,v1,c2,v2,…,else)`** | found in earlier sample; count on first run | rewrite to nested `If` or flag |
| Typed variable declarations (`var datetime`) | earlier sample | add alias or rewrite |
| Quickbase **URL conventions** (`?a=API_GenAddRecordForm`, `Rurl()` semantics, `URLRoot`) in 2,100+ calls (URLRoot 780, Rurl 582, URLEncode 642) | URL::Formula fields: TS ~, all apps | functions exist; link targets need review |
| Query syntax `{13.EX.'x'}` inside `GetRecords`/`GetFieldValues` (56 + 44 calls) *(verify supported query strings)* | CP/TS/HR | verify at run time |

Function coverage is otherwise complete: the file calls 186 distinct names; the commonest (`SearchAndReplace` 3,784, `If` 1,918, `ToText` 1,905, `URLRoot` 780, `URLEncode` 642, `Rurl` 582, `Contains` 430, `Today` 247, `Part` 243, `ToDate` 215, `Base64Encode` 200, `Dbid` 159, `Case` 153) all resolve.

## 12. Roles and permissions

| Missing | Seen in | Interim handling |
|---|---|---|
| **Role-based UI hiding — app level** (`HideSettings` 71, `HideHelp` 65, `HideUsers` 59, `HideTestAs` 58, `HideFavorites` 41, `HideSearch` 17, `HidePersonalSettings`, `HideCreateCustomizeReport`) | 154 roles in all 13 apps | report per role |
| **Role-based UI hiding — table level** (`HideInBar` 2,920, `HideGridEdit` 576, `HideNewRecord` 487, `HideCreateCustomizeReport` 42, `HideImportExport` 40, `HidePersonalSettings` 32, `HideEmail` 25, `HidePrint` 17, `HideSaveSpreadsheet` 14) | 3,490 role×table entries | map `HideNewRecord` → no add, `HideGridEdit` → report grid edit, `HideInBar` → hidden for role *(verify)*; report the rest |
| Scope **"UserInField"** (record visible when the current user is in a user field) | 8 (modify) | translate to a record filter `field = current user`; else report |
| `CanRefreshConnectedData` | 940 | no connected tables |
| Separate **modify** filter distinct from the **view** filter *(verify — one filter per role+table today)* | 418 `CustomRule` entries (view and modify) | apply to `ModifyScope`, else report |
| Role rank / hierarchy | – | all imported roles rank equally |

## 13. Record filters

| Missing | Seen in | Interim handling |
|---|---|---|
| **In-range / relative date operators** (`IR` 36, `XIR` 38; "today", "last N days") *(verify `FilterCondition` operator list)* | criteria in reports/roles | report; a few can become `gte/lte` against a formula |
| Operator `DEX` (meaning unconfirmed) | 2 | report |
| Compare to **ask at run time** (`_ask1_`, 107) | reports | `ValueMode=ask` exists ✔ |

---

## Unused in this file (not missing for this migration)

Kanban, Timeline, Chart types beyond Pie/StackedBar/Gauge, Funnel/Heatmap/Scatter, dashboard filters, Create-record / Open-page dashboard buttons, WorkDate / Predecessor / vCard / iCalendar fields, cross-application relationships, Master/Child app propagation, CAPTCHA bot check, barcode scanner, edit/append history, rating fields, Duration beyond 1 field, application guides.

## Summary — truly missing vs. just not imported yet

| Category | Items |
|---|---|
| **Truly missing in PowerBase (needs product work)** | connected tables (64) · KPI widget (6) · calendar (5) and map (2) reports · colour coding (491 reports) · report formula columns (42) and summary formulas/variables (32) · role-based UI hiding (154 roles / 3,490 table entries) · formula-driven reference fields (97) · `UserRoles()` · in-range/relative date filter operators · "add to all new reports", "block data copy", phone/address/email/URL extras · fiscal year, first day of week, app time zone · per-table role home pages · dashboard tabs (verify) |
| **PowerBase has it, importer does not carry it yet (no product work)** | dashboards (28) and home pages (14) → pages/widgets · charts (24) → chart reports · rich-text page (1) · summary-report aggregation detail (35) · role `UserInField` · table `RoleViews` → role home page · form-rule tags / fire-only-on-change · page colour |
| **Already imported (for reference)** | app settings/branding/formatting · tables and all table settings · field settings and flags · all 2,121 formula fields · relationships, lookups, summaries with query filters · FormV2 forms, pages, sections, elements, embedded reports · 1,912 form rules (field/role/formula conditions, message/abort/live-value actions) · table reports with filters, grouping, options, role visibility · roles, table/field permissions, record filters · variables (369) · code pages (24) |
