# all_in_one.qbl → PowerBase — import verification report (13 apps)

Date: 2026-10-21 · Import job #1 (`all_in_one.qbl`, 42.8 MB) · Tenant database `Powerbase_25`
Companion specs: [QBL_ALL_IN_ONE_MAPPING_SPEC.md](QBL_ALL_IN_ONE_MAPPING_SPEC.md) (mappings) · [QBL_ALL_IN_ONE_MISSING_FEATURES.md](QBL_ALL_IN_ONE_MISSING_FEATURES.md) (features PowerBase lacks)

---

## 0. How this was verified

Three layers were compared, so every gap can be placed where it happened:

| Layer | What it is | Source |
|---|---|---|
| **QBL** | What the file contains | line-level scan of `all_in_one.qbl` (types, properties, nested keys) |
| **PBL** | What the converter produced from it | the converter run on the same file (`QblToPblConverter.ConvertAll`) |
| **PowerBase** | What actually exists after the import | direct counts in the tenant database (`meta.*` tables) + the stored job report (`meta.AppImportJob.ResultJson`: 7,599 skipped/flagged items with reasons) |

A gap is therefore either a **conversion loss** (QBL → PBL: the converter did not carry it) or an **import loss** (PBL → PowerBase: the importer could not create it). Reasons are quoted from the import report.

Not part of a QBL file, so not compared: **records (data)**, users, attachments, pipelines/notifications. QBL is schema + configuration only — all 326 imported tables are empty.

Status words: **100 % in sync** · **Partial** (some lost, with count) · **Gap** (target exists, not carried — a fix would be importer work) · **Missing in PowerBase** (target does not exist — product work).

---

## 1. Verdict

| | Result |
|---|---|
| Apps in file | **13** |
| Imported | **12** (job says "12 of 13 applications imported") |
| **Failed** | **CFTSS Compliance Process** — `SqlException: Execution Timeout Expired` while creating its 74 tables |
| **Left behind** | ⚠ the failed app is **still in PowerBase, half-built**: 33 of 74 tables, 3 roles, **0** forms / reports / relationships / fields beyond scalars. It must be deleted and re-imported (see §6 defect D1). The import report said the app was not left behind; that is not what happened. |
| Schema skeleton (apps, tables, scalar fields, relationships, forms, variables) | **100 %** for the 12 imported apps |
| Calculated fields | formulas **77 %**, lookups **89 %**, summaries **64 %** |
| UI | form sections 99 %, form elements 82 %, form rules 59 % |
| Reports | table reports 92 % (1,614 of 1,746), **summary reports 0 %** (0 of 24), charts / calendars / maps **0 %** |
| Security | roles 100 % created, **table permissions only 57 % intact** (defect D2), record filters 20 %, field permissions 91 % |
| Dashboards, home pages, code pages | **0 %** (28 dashboards, 14 home pages, 24 code pages) |

---

## 2. Scoreboard — QBL count / count now in PowerBase

(Formula = formula fields actually created, i.e. excluding the "Add …" link formulas PowerBase generates per relationship. Table-report totals in PowerBase include a few reports PowerBase seeds itself.)

| App | Tables | Scalar fields | Formula | Lookup | Summary | Relationships | Forms | Form sections | Form elements | Form rules | Table reports | Summary reports | Roles | Variables |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| CARES Imports | 22 / 22 | 124 / 124 | 63 / 48 | 69 / 64 | 9 / 8 | 27 / 27 | 22 / 22 | 57 / 53 | 331 / 249 | 8 / 7 | 90 / 82 | 0 / 0 | 6 / 7 | 0 / 0 |
| CARES Time Sheets | 63 / 63 | 540 / 539 | 395 / 288 | 531 / 466 | 180 / 112 | 143 / 143 | 71 / 71 | 246 / 244 | 5,244 / 4,191 | 804 / 436 | 398 / 352 | 11 / 0 | 17 / 18 | 52 / 52 |
| CARES Training Tracking | 16 / 16 | 85 / 85 | 73 / 50 | 77 / 67 | 26 / 9 | 24 / 24 | 17 / 17 | 44 / 44 | 318 / 255 | 23 / 15 | 73 / 70 | 0 / 0 | 9 / 10 | 52 / 52 |
| CCR Timesheet Storage | 17 / 17 | 79 / 79 | 71 / 45 | 48 / 46 | 19 / 16 | 22 / 22 | 17 / 17 | 42 / 42 | 326 / 230 | 13 / 4 | 129 / 127 | 5 / 0 | 7 / 8 | 52 / 52 |
| CFTSS Billing Process | 14 / 14 | 55 / 55 | 20 / 11 | 28 / 28 | 8 / 7 | 12 / 12 | 14 / 14 | 28 / 28 | 142 / 121 | 2 / 1 | 50 / 50 | 1 / 0 | 6 / 6 | 52 / 52 |
| **CFTSS Compliance Process — FAILED, half-built** | 74 / 33 | 473 / 259 | 370 / 0 | 309 / 0 | 122 / 0 | 119 / 0 | 87 / 0 | 285 / 0 | 4,125 / 0 | 648 / 0 | 414 / 0 | 11 / 0 | 20 / 3 | 52 / 0 |
| CFTSS Goals & Objectives | 8 / 8 | 34 / 34 | 22 / 17 | 28 / 22 | 21 / 12 | 8 / 8 | 8 / 8 | 13 / 13 | 133 / 98 | 0 / 0 | 31 / 31 | 0 / 0 | 3 / 3 | 0 / 0 |
| DSP Supervision | 33 / 33 | 318 / 318 | 218 / 171 | 247 / 219 | 106 / 74 | 79 / 79 | 10 / 10 | 55 / 55 | 625 / 548 | 107 / 77 | 207 / 191 | 4 / 0 | 14 / 15 | 54 / 54 |
| HR Onboarding Process | 30 / 30 | 461 / 461 | 297 / 245 | 289 / 258 | 127 / 79 | 57 / 57 | 32 / 32 | 111 / 111 | 1,602 / 1,343 | 161 / 103 | 198 / 184 | 3 / 0 | 24 / 25 | 0 / 0 |
| Ramapo Management | 19 / 19 | 96 / 96 | 93 / 69 | 75 / 74 | 11 / 11 | 28 / 28 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 81 / 79 | 0 / 0 | 5 / 5 | 3 / 3 |
| Serenity Database | 47 / 47 | 421 / 421 | 197 / 153 | 250 / 209 | 65 / 30 | 60 / 60 | 47 / 47 | 136 / 135 | 1,039 / 865 | 46 / 33 | 200 / 188 | 0 / 0 | 18 / 19 | 52 / 52 |
| VDB Database | 35 / 35 | 432 / 432 | 237 / 196 | 309 / 278 | 145 / 97 | 63 / 63 | 37 / 37 | 132 / 132 | 1,698 / 1,460 | 92 / 69 | 198 / 183 | 0 / 0 | 19 / 20 | 0 / 0 |
| VDB Financial Docs | 22 / 22 | 134 / 134 | 65 / 50 | 81 / 74 | 12 / 10 | 27 / 27 | 22 / 22 | 57 / 53 | 355 / 277 | 8 / 7 | 91 / 83 | 0 / 0 | 6 / 7 | 0 / 0 |
| **Total, 12 imported apps** | **326 / 326** | **2,779 / 2,778** | **1,751 / 1,343** | **2,032 / 1,805** | **729 / 465** | **550 / 550** | **297 / 297** | **921 / 910** | **11,813 / 9,637** | **1,264 / 752** | **1,746 / 1,620** | **24 / 0** | **134 / 143** | **317 / 317** |
| **% in sync** | 100 % | 100 % | **77 %** | **89 %** | **64 %** | 100 % | 100 % | 99 % | **82 %** | **59 %** | 92 %² | **0 %** | 107 %³ | 100 % |

² 132 table reports were dropped as same-name duplicates (§ Reports). ³ PowerBase also seeds a "Participant" role; no source role was lost.

---

## 3. Module-by-module report

### M1. Application settings (13 apps)

| Item | Result | Detail / why |
|---|---|---|
| Name, description | **100 % in sync** | 12/12 |
| Colour | **100 % in sync** | 11 of 12 (Ramapo has none in the source) |
| Currency symbol/position, number separators, date format | **100 % in sync** | `Formatting` stored on all 12 apps (`$`, comma, `MM-DD-YYYY`) |
| Security switches (copy, export, sync connections, app token) | **100 % in sync** | stored on all 12 |
| Icon | **Partial** | 11 of 12 apps got the generic `database` icon, HR `user` — Quickbase's icon names ("Application" …) map to PowerBase's default when there is no equal. Cosmetic |
| Branding (header colour) | **Gap** | `Branding` is empty on all 12 apps although 6 source apps use a custom header/footer; only the colour could be carried |
| "Searchable" / global search | **Gap (by design)** | 12 notes: Quickbase's switch is not PowerBase's "hide from public search"; left default |
| Table order, role order | table order ✔ 100 %; role order **Missing in PowerBase** (all imported roles rank equal) | |
| Fiscal year, first day of week, app time zone, performance bar, header/footer links, default view mode | **Missing in PowerBase** | one value repeated in all 13 apps |

### M2. App variables (369)

| Result | Detail |
|---|---|
| **100 % in sync** (317 / 317 for the 12 imported apps; Compliance's 52 not created) | names and values kept; 6 apps × 52, DSP 54, Ramapo 3 |

### M3. Tables and table settings (326 imported / 400)

| Item | Result | Detail / why |
|---|---|---|
| Name, singular/plural labels, description | **100 % in sync** | 326/326 |
| Icon, alias (`_DBID_…`) | **100 % in sync** | 326/326 |
| Order, "show in table bar" (false ×13), "save on exit" (Ramapo 19/19), record-title field (6/6), record picker (all) | **100 % in sync** | |
| Key field | **Partial** | 97 key fields applied; **15 not applied**: field not found (8; the field is a formula/lookup that was not created) or "type cannot be a key field" (7). TS 5, SER 3, VDB 3, others 1 each |
| Custom data rule (12 enabled in source) | **Partial** | 13 rules stored, **6 enabled**; the rest are stored *switched off* with their text because the formula needs manual review (reported as "Custom data rule needs manual review") |
| **Table default report settings** (sort, header text, grid edit, group display, row numbers, quick search, dynamic filters) — on all 400 source tables | **Gap** | the converter never produces them and the importer has no code to apply them: **0 of 326** tables have them (defect D4) |
| Table name override (1), table "searchable" (400), "index file attachments" (4), colour coding default, row height default, "action on new field add" | **Missing in PowerBase** | |
| Per-role table home page (31 tables) | **Gap** | depends on dashboards/home pages being imported (M17) |
| **Tables without a default report** | **Gap** | **70** tables have no default report (IMP 6, TT 1, HR 21, SER 18, VDB 17, FIN 7) — the table's "List All" was dropped as a same-name duplicate or its default flag was not carried (defect D6) |
| **Tables without any form** | **Gap** | **43** tables (DSP 24, Ramapo 19): these tables only have *legacy* Quickbase forms, which are skipped, and PowerBase's own default form is suppressed when a table supplies its own reports (defect D5) |
| Connected tables (64 in source) | **Missing in PowerBase** | imported as ordinary local tables, no live sync, and **no warning was emitted** |

### M4. Fields — scalar types (2,779 source → 2,778)

| Type | Result |
|---|---|
| Text · Multi-line · Rich text · Choice (single/multi) · Number · Currency · Percent · Date · DateTime · Time · Duration · Checkbox · Email · Phone · URL · File · Address · User | **100 % in sync** — counts equal per app and per type (the one field short is a label clash, noted below) |
| Source fields with a duplicate name/label on the same table | **Partial** — renamed `Name (2)` and flagged: 6 fields + 1 Reference field |
| Unsupported fields — Quickbase field whose referenced field was **deleted** (45) | **Not importable** — reported with Quickbase's own explanation; SER 13, TS 12, IMP 8, FIN 8, VDB 1 (Compliance 3 not reached). They have no definition to create |
| Record ID#, Date Created, Date Modified, Record Owner, Last Modified By | **100 % in sync** — PowerBase's seeded system fields |

### M5. Field settings and flags

| Item | Result | Detail |
|---|---|---|
| Required | **100 % in sync** | 137 / 137 |
| Default value (incl. today/now/current-user) | **100 % in sync** | 427 / 427 |
| Per-type settings (decimals, separators, units, totals, currency, date/time display, URL options, file revisions, user display, choices & order, max length, column width, bold, no-wrap) | **100 % in sync** for everything that has a PowerBase setting | every imported field carries a settings blob |
| Unique | **Partial 85 %** | 123 of 145: uniqueness on reference fields is not applicable in PowerBase |
| Description (Quickbase "comments") | **Partial 58 %** | 25 of 43 stored; the rest were on formula/lookup fields whose description is not passed through (defect D7) |
| **Help text** | **Gap 0 %** | 18 fields had help text; **none** reached PowerBase (`AppField.HelpText` is empty everywhere) — defect D3 |
| Not searchable / not reportable flags | **100 % in sync** | honoured when false |
| Allow data copy off (≥ 200 fields), add-to-all-new-reports, phone extension/country, email domain, URL button colour/iframe size, address format/map type, choice "allow users to add", user list restriction | **Missing in PowerBase** | see missing-features file |

### M6. Formula fields (1,751 source for the 12 apps → 1,343 created)

| Result | Count | Detail |
|---|---|---|
| **Created and clean** | 908 | the expression compiled unchanged |
| **Created after automatic rewrite** | 435 | e.g. `[_DBID_X]` → `Dbid("X")`, numeric field ids → names, Quickbase-only syntax (listed as "Adjusted" in the report) |
| **Not created — needs manual review** | **383 (22 %)** | reasons below |
| Formula with no expression | 25 | empty in the source |

Why the 383 failed (translator diagnostics): *text/number type mix* — `+`/`&` between text and numbers, "requires text on both sides" (≈ 90), `Unknown field [Name]` — the formula names a field that does not exist or was itself not created, e.g. `[File Attachment]`, `[CurrentUser - SPC List]`, `[LatestStatus]` (≈ 150), `If` with an even number of arguments (the multi-branch `If(c1,v1,c2,v2,…)` form), references to a Quickbase table outside the import (`_DBID_CLIENTS`, 11), unknown function. Loss by app: TS 101, DSP 46, HR 47, SER 41, VDB 35, CCR 26, RAM 23, TT 22, IMP 14, FIN 14, BIL 9, GO 5.
**Knock-on effect:** every form element, report column, role filter and rule that referenced one of these formulas was dropped too (see M9–M11, M18).

⚠ **Noise to be aware of:** the converter logged 3,279 "formula refers to field id N, which is not on its table; left as written" warnings (CCR 624, Billing 468, DSP 261, TS 209…). Almost all are `_fid_N=` parameters inside Quickbase **record URLs** (they point at the *target* table's fields). The formulas were created unchanged, so the parameters still carry Quickbase's field ids: **"Add record" links will not pre-fill** until the ids are remapped to PowerBase fids (defect D8).
Also: formulas created by PowerBase itself — one "Add …" URL formula and one ReportLink per relationship (550 each) — sit beside the source's own "Add …" fields, so many tables now show **two** add-links.

### M7. Relationships, Reference and Report-link fields

| Item | Result | Detail |
|---|---|---|
| Relationships | **100 % in sync** | 550 / 550 |
| Reference fields | **100 % in sync** | 550 / 550; 1 renamed (`Last Modified By (2)`) |
| Report links | **100 % in sync** | PowerBase creates one per relationship (550); the source has 687 `ReportLink` fields because Quickbase adds extras PowerBase does not need |
| Reference proxy (display) | **Partial** | first lookup used; 485 in source |
| **Formula-driven references** (97 in the 12-app file: key computed by formula) | **Missing in PowerBase** | relationship is created, the formula that populated the key is not |
| Reference conditional values / picker filters (81) | **Gap** | not carried |
| "Display as link to parent/children", drill-down report, "evaluate only on change" | **Missing in PowerBase** | |

### M8. Lookup fields (2,032 → 1,805; 89 %)

| Reason lost (227) | Count |
|---|---|
| Source field was not created (it was a formula/lookup that failed) | 169 |
| References a parent field "not available in this import phase" | 54 |
| The Reference behind it points at a deleted field (Quickbase's own "Unsupported") | 12 |
Loss by app: TS 65, HR 31, VDB 31, SER 41, DSP 28, TT 10, GO 6, IMP 5, FIN 7, CCR 2, RAM 1.

### M9. Summary fields (729 → 465; 64 %)

| Reason lost (264) | Count |
|---|---|
| Summary of a lookup/formula/reference that doesn't exist or is calculated ("can't be summarised; choose another field") | 51 + 16 + 23 |
| **Filter query cannot be translated** — relative dates (`IR`/`XIR`, 48+39 uses), operator `TV` against the current user (26), `DEX` (2); importing without the filter would count every child record, so the summary is **not created** | 39 + 6 |
| Target field was not created | 77 |
| Summary of text values (use Distinct Count instead) | 17 |
| Summary over a Reference field | 9 |
| Broken relationship/function reference | 6 |
Loss by app: HR 48, SER 35, VDB 48, TS 68, TT 17, DSP 32, GO 9, CCR 3, BIL 1, FIN 2, IMP 1. (Reason counts are report entries and can overlap, so they do not add up exactly.)

### M10. Forms (297 forms / 921 sections / 11,813 elements in the 12 apps)

| Item | Result | Detail |
|---|---|---|
| Forms (FormV2) | **100 % in sync** | 297 / 297 |
| Form settings (auto-add fields, built-in footer, tabs on view, save options, page style) | **100 % in sync** | |
| Pages (named) | **100 % in sync** | |
| Sections | **99 %** | 910 / 921: 11 dropped as same-name duplicates within a form |
| Columns (blocks) headings, widths, background colours | **100 % in sync** for imported sections | |
| Field elements — label mode/override, help text override, width, view/edit/add visibility, read-only, required, choice display | **100 % in sync** for imported elements | |
| **Field elements overall** | **Partial 82 %** | 9,637 / 11,813 (DSP 88 %, HR 84 %, VDB 86 %, **TS 80 %**, SER 83 %, TT 80 %, CCR 71 %, BIL 85 %, IMP 75 %, FIN 78 %, GO 74 %). **1,961 elements** were dropped because their field does not exist in PowerBase (not-created formula/lookup/summary/unsupported field); **174 embedded-report elements** dropped (report or relationship missing) |
| Embedded report elements | **Partial 81 %** | 256 of 317 converted (e.g. TS 67 of 92); the rest could not resolve their report or relationship |
| Static text elements | **100 % in sync** | 133 / 133 |
| Page colour, choice filtering, element validation, sub-form per report | **Missing in PowerBase / Gap** | |
| **Legacy Quickbase forms** (402 forms, ~9,500 elements) | **Not imported by design** | only FormV2 is live. But for **43 tables with no FormV2** (DSP 24, Ramapo 19) that leaves the table with **no form at all** (D5) |
| Role-specific form defaults (`RoleDefaults`) | **Gap** | not carried |

### M11. Form rules (1,264 → 752; 59 %)

Conversion kept 944; the importer kept 752. Across the 12 apps the rules carry 1,843 actions and 1,608 conditions after conversion; PowerBase has 1,458 and 1,207.

| Why rules/actions were lost | Count |
|---|---|
| ⚠ **`MakeReadonly` action not recognised** — the file spells it `MakeReadonly`; the converter only knows `MakeReadOnly` (defect D9). Pure converter bug — PowerBase supports it | 158 actions (228 in the file) |
| Rule has no action PowerBase can represent | 142 |
| Nested AND/OR **condition group** structure | 135 (104 groups; 6 rules) |
| Condition type with no equivalent: `DaysAgo` 11, `Relative` 4, `Range` 3, group 104 | 122 |
| Comparison with no equivalent (`Changed`, `IsDuring`…) | 25 |
| Condition field / action target field not created | 145 + 245 |
| Action target is a section/column/page scope | 67 |
| Action targets a field with no element on this form | 65 |
| Role condition that cannot be resolved | 6 |
Fully carried: Show/Hide/Require/Unrequire/Change (incl. live-value)/SetLabel/MakeEditable, DisplayMessage (modal, notification, field), AbortSave, run-triggers, field and role conditions, formula conditions, disabled rules kept disabled. Not carried: rule **tags** (39), "fire only on change" (1,710 true) — **Gap**.
Loss by app: **TS 368 of 804**, HR 58, DSP 30, VDB 23, SER 13, TT 8, CCR 9, IMP 1, BIL 1, FIN 1.

### M12. Table reports (1,746 → 1,614 imported)

| Item | Result | Detail |
|---|---|---|
| Name, description, columns (list/default), sort, grouping (incl. group mode) | **100 % in sync** for imported reports | |
| Filters (478 non-default) | **Partial** | applied except **103**: field missing (64) / untranslatable operator (`IR`/`XIR`, `TV` vs current user, `DEX`) / field-id unresolved — the report is imported **without** the filter (it then shows more rows than in Quickbase) |
| Hide totals, group default, header text, edit/view icon, bulk-delete, grid edit, dynamic filters, quick search | **100 % in sync** | |
| Role visibility (2,160 × roles) | **100 % in sync** | 5,703 report-role rows |
| **Duplicate report names** on one table | **Gap** | **132** reports dropped (only the first of equal names kept — PowerBase requires unique names per table). TS 50, DSP 16, HR 16, VDB 15 … Most are Quickbase's auto-generated "Embedded for X" reports |
| Reports referencing fields that don't exist | **Partial** | 578 reports had a column or sort on a missing field; **309** were imported *without those columns* (e.g. "Status", "Final File URL") and the rest dropped |
| Colour coding (491 reports), row height, column label/alignment/pin, "only new/changed records", summary-table/hide-main-table, XSL format | **Missing in PowerBase** | |
| Default report | **Gap** | 70 tables have none (D6) |

### M13. Summary reports (24 in the 12 apps → **0**)

| Result | Why |
|---|---|
| **Gap — 0 %** | all 24 failed with "*<setting> is required for Summary reports*" (TS 11, CCR 5, DSP 4, HR 3, BIL 1). The converter produces Summary reports without the grouping/aggregation PowerBase requires (defect D10). Summary variables (24), summary formulas (8) and crosstab are **Missing in PowerBase** |

### M14. Report-level extras

| Item | Count | Result |
|---|---|---|
| Report formula columns (`Report::Formula::*`) | 42 | **Missing in PowerBase** — not imported; formula text is only in the source |
| Report colour-code formulas | 28 | **Missing in PowerBase** |

### M15. Charts (24 → 0)

Pie 15, Gauge 6, Stacked bar 3 (CP 11 — not created —, TS 6, HR 4, DSP 2, TT 1). **Gap 0 %**: PowerBase has all three chart types (Pie, Gauge, StackedBar), but the converter does not create chart reports. Each is reported as "has no PowerBase equivalent" or silently absent (verify). Gauge colour bands, goal lines, drill-down: Missing in PowerBase.

### M16. Calendar and map reports (7 → 0)

Calendar 5 (TS 2, HR 1, RAM 1; CP 1 not reached), Map 2 (HR 1, RAM 1). **Missing in PowerBase** — no calendar or map report type. Reported: `Report type 'QB::Report::Calendar'/'Map' has no PowerBase equivalent` (6).

### M17. Dashboards, home pages, rich-text page (0 %)

| Item | In file | In PowerBase | Result |
|---|---|---|---|
| Dashboards | 28 (98 widgets: tables, summaries, charts, buttons, rich text, web page, search, spacer, **6 KPI**) | 0 | **Gap** — PowerBase pages/widgets can hold all except KPI; not converted ("Page … of type QB::Dashboard has no PowerBase equivalent", 24) |
| Home pages | 14 (+12 widgets) | 0 | **Gap** (13 reported) |
| Rich-text page | 1 | 0 | **Gap** |
| KPI widgets | 6 (all Compliance — failed app) | 0 | **Missing in PowerBase** |
| Per-role home page assignment | – | 0 | **Gap** |

### M18. Roles and permissions

| Item | QBL | In PowerBase | Result |
|---|---|---|---|
| Roles (name, description, default flag) | 134 | 143 (incl. seeded Participant) | **100 % in sync** — 27 roles named like PowerBase's built-ins (Administrator, Viewer…) were **adopted and overwritten** (reported), 22 built-in roles' permission sets left unchanged |
| Manage-users / edit-app / disable-access flags | – | applied | **100 % in sync** |
| **Table permissions** (role × table) | 4,419 | 2,976 | **Partial 57 %** — 2,505 identical, **186 deliberately restricted** (the record filter referred to a field that was not created → role given *no access* to that table), **1,728 missing** — see D2 |
| Record filters (custom rules) | 236 | 48 | **Partial 20 %** — 186 became "no access" (filter field not created), 29 "modify rule differs from view rule → modify set to none", 26 untranslatable operators (`TV`/`IR`), 20 field id unresolvable |
| Field permissions (modify/view/none) | 1,056 | 960 | **Partial 91 %** — 74 skipped because the field was not created; 34 flagged "required without default and set to View → role cannot add records" |
| Scope "user in field" (8) | 8 | 0 | **Gap** — reported "no confirmed PowerBase equivalent; defaulting to no access" |
| `CanRefreshConnectedData` (940) | – | – | **Missing in PowerBase** |
| **Role-based UI hiding** (app-level: Settings 71, Help 65, Users 59, Test-as 58, Favorites 41, Search 17; table-level: not in bar 2,920, no grid edit 576, no new record 487, no import/export 40, …) | 154 roles / 3,490 entries | none | **Missing in PowerBase** |
| Role order/rank, "restrict access to new tables" | 1 | – | rank **Missing in PowerBase**; restrict flag noted |

### M19. Code pages (24 → 0)

**Gap 0 %** — "Code Pages are not enabled for this tenant" (22 reported: RAM 16, CCR 2, DSP 2, TS 1, + HR's `closeWindow.js`). Enable the tenant flag and the `pages:code` capability and re-run just these, or upload manually. Not a conversion fault.

### M20. Connected data (64 tables, 45 record sets, 15 data sources)

**Missing in PowerBase** — no live table sync. Imported as plain tables (see M3).

---

## 4. Per-app summary

| App | Status | What is solid | What is incomplete |
|---|---|---|---|
| **CARES Imports** | Imported | all structure; 100 % relationships/forms | formulas 76 %, form elements 75 %, 6 default reports missing, 8 deleted-field fields |
| **CARES Time Sheets** (largest) | Imported | structure, relationships, 52 variables | formulas 73 %, lookups 88 %, summaries 62 %, form elements 80 %, **rules 54 %**, 11 summary reports, 2 calendars, 5 dashboards, 6 charts, code page; **13 roles lost 731 table-permission rows** |
| **CARES Training Tracking** | Imported | structure | summaries **35 %** (9 of 26), formulas 68 %, rules 65 % |
| **CCR Timesheet Storage** | Imported | structure | formulas 63 %, rules **31 %** (4 of 13), 5 summary reports, 2 code pages |
| **CFTSS Billing Process** | Imported | structure | formulas 55 % (11 of 20), 1 summary report |
| **CFTSS Compliance Process** | **FAILED — half-built (33/74 tables)** | – | **everything** — delete the stub and re-import (needs the timeout fix) |
| **CFTSS Goals & Objectives** | Imported | structure | summaries 57 %, formulas 77 %, form elements 74 % |
| **DSP Supervision** | Imported | structure, relationships | **24 tables without a form**, rules 72 %, summaries 70 %, 2 code pages |
| **HR Onboarding Process** (10 dashboards) | Imported | structure | formulas 82 %, summaries 62 %, rules 64 %, 21 tables without default report, 10 dashboards + home + map + calendar not imported; 11 roles lost 263 permission rows |
| **Ramapo Management** | Imported | structure, formulas 74 % | **no forms at all** (legacy only), 16 code pages blocked, 2 home pages, calendar, map |
| **Serenity Database** | Imported | structure | summaries **46 %**, formulas 78 %, **488 permission rows lost** (11 roles), 13 deleted-field fields, 18 tables without default report |
| **VDB Database** | Imported | structure, 94 % permissions | summaries 67 %, form elements 86 %, 17 tables without default report |
| **VDB Financial Docs** | Imported | structure | formulas 77 %, form elements 78 %, 8 deleted-field fields |

---

## 5. Totally missing functionality (needs PowerBase product work)

Detail and counts: [QBL_ALL_IN_ONE_MISSING_FEATURES.md](QBL_ALL_IN_ONE_MISSING_FEATURES.md).

| Module | Missing in PowerBase |
|---|---|
| Tables | connected (synced) tables (64); default colour coding / row height; table-level global-search switch; per-table per-role home page |
| Fields | block data copy; add-to-all-new-reports; phone extension/international/country; email default domain; URL button colour/iframe size/abbreviate; address format/map type; "allow users to add choices"; choices from another field |
| Relationships | formula-driven reference fields (97); drill-down/"link to children" displays |
| Forms / rules | form page colour; per-element validation; sub-form per report; date conditions (days-ago, range, relative, during); rule tags |
| Reports | calendar and map reports; row colour coding; report formula columns (42); summary formulas/variables (32); column label/alignment override; "only new or changed", summary-table/hide-main-table |
| Charts / dashboards | KPI widget (6); gauge colour bands, goal lines; dashboard tabs *(verify)*; button styling |
| Formula engine | `UserRoles()`; multi-branch `If`; reference fields inside formulas *(verify)*; relative-date (`IR`/`XIR`) criteria |
| Security | role-based UI hiding (app and table level); "user in field" scope; separate modify filter distinct from view filter; role rank order |
| App | fiscal year; first day of week; app time zone; custom header/footer and links; performance bar |

---

## 6. Defects found in the importer (fixable — no PowerBase product work)

| # | Defect | Evidence | Effect | Fix |
|---|---|---|---|---|
| **D1** | **A timeout while creating the app leaves a half-built app.** `CreateAppCommandHandler` is called *before* the try/rollback block, and the 74-table DDL exceeded the default SQL command timeout | Compliance: 33 of 74 tables present, 0 forms/reports; job says failed | a broken app is visible to users and blocks a re-import under the same name | put app creation inside the rollback scope; raise the command timeout for DDL (or create tables in batches) |
| **D2** | **A role with one un-storable table loses *all* its other table permissions.** `RestrictInsteadAsync` sends only the failed tables to `UpdateTablePermissions`, and `SetTablePermissionsAsync` first **deletes every row of the role** | consistent with the code path and confirmed by the row counts: 46 roles end with 1–13 rows instead of 63; **1,728 permission rows lost** (TS 731, SER 488, HR 263, DSP 150, TT 49, VDB 47). Roles affected: CC Rep, CC Admin, CC Manager, Cares Admin, DSP, LEA, LEA TS Rep, SPC, … | those roles lose their table access/add/delete settings beyond the restricted table | merge the restriction rows into the role's full permission set before saving |
| **D3** | Field **help text** never reaches PowerBase | 18 fields in the converted document, 0 stored | help text lost | trace `AppFieldSpec.HelpText` → bulk field create (stored as null) |
| **D4** | Table **default report settings** are neither produced by the converter nor applied by the importer | 0 / 326 tables | grid-edit, header-wrap, group-collapse, row-number and sort defaults lost | add converter + applier code (PBL model already exists) |
| **D5** | Tables with **only legacy forms** get **no form** (legacy skipped, default form suppressed because the table has reports) | DSP 24 tables, Ramapo 19 | users cannot open a form for those tables | seed PowerBase's default form when a table has no FormV2 |
| **D6** | **70 tables have no default report** (cause to be confirmed: the table's "List All" was dropped as a duplicate name, or the default flag was not carried) | IMP 6, TT 1, HR 21, SER 18, VDB 17, FIN 7 | the table opens with no default view | mark the first imported report (or recreate "List All") as default |
| **D7** | Field **description** lost on formula/lookup fields | 43 → 25 | notes lost | pass `Description` for formula/lookup creation |
| **D8** | `_fid_N` URL parameters inside **Add/Edit record links** are not remapped to PowerBase field ids | 3,279 warnings; CCR 624, BIL 468, DSP 261 | pre-fill links do nothing | rewrite `_fid_N=` using the target table's source-id → fid map |
| **D9** | `MakeReadonly` (lower-case *o*) not recognised as the "make read-only" rule action | 158 skipped actions | rules lose their read-only effect | accept both spellings |
| **D10** | **Summary reports** are created without required grouping/aggregation | 24 / 24 fail | all summary reports missing | map `GroupingAndCrosstabs.Rows` and `Summary::Column` to row groups and aggregations |
| **D11** | Operator **`TV` against `_curuser_`** ("current user is in this user field") treated as untranslatable | 26 role/report/summary filters | filters dropped or roles set to "no access" | map to *field = current user* |
| **D12** | Reports/forms silently lose items that depend on a **formula that failed** (cascade) | 1,961 form elements, 309 report columns, 186 role filters | large knock-on losses from 383 formulas | fix the formula causes (type mixing, field names) first |
| **D13** | **No warning for connected tables**, legacy forms, charts, dashboards (some are only counted as "has no equivalent") | 64 connected tables imported without notice | users think data will sync | add per-item warnings |

---

## 7. Recommended order of work

1. **Clean up and re-run the failed app:** delete the half-built Compliance Process; fix D1; import it again.
2. **D2** (permissions wipe) — security-relevant, affects 46 roles. Re-run roles for the 6 affected apps after the fix.
3. **D5, D6** (no form / no default report) — blocks day-to-day use of 113 tables.
4. **D9, D11, D10, D3, D7, D4** — small converter fixes, recover ≈ 450 rules/actions, 24 summary reports, filters, help text.
5. **Formula quality (D12):** review the 383 failing formulas (type mixing, missing fields, multi-branch `If`) — recovers form elements, report columns, role filters and summaries in cascade.
6. **Dashboards, home pages, charts** → pages/widgets and chart reports (28 + 14 + 24 items with direct PowerBase equivalents).
7. **Code pages:** enable the tenant capability, then import the 24.
8. Decide on the product gaps in §5 (connected tables, KPI, calendar/map, colour coding, role UI hiding, formula references).
