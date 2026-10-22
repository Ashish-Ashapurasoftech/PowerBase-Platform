# Features Quickbase (QBL) has that PowerBase does not — by module

Date: 2026-10-19 · Companion to [QBL_TO_PBL_MAPPING_SPEC.md](QBL_TO_PBL_MAPPING_SPEC.md) (which lists what *can* be kept).

How this list was built: every property/node type in `cares.qbl.txt` (plus the Quickbase QBL reference for features cares doesn't use) was searched for in PowerBase's domain, application and Angular code. **"Not found" means no equivalent was located in the code**, not that it is impossible. Items marked *(verify)* need a quick confirmation with the owning team.

Legend — **Seen in cares** gives the number of occurrences, so you can judge impact. **Interim handling** is what the importer should do until the feature exists (so nothing is silently lost).

---

## 1. App

| Missing in PowerBase | Seen in cares | Interim handling |
|---|---|---|
| Fiscal year settings (`FirstMonthOfFiscalYear`, `FiscalYearDesignation`) | 1 app | report as "not applicable" |
| First day of week (`FirstDayOfWeek`) | 1 | report |
| App-level time zone (`Timezone`) *(verify — time zones exist per user/field, not in app settings)* | 1 | report |
| Custom app header/footer text and links, `HideQuickbaseStandardMenu` | 1 | map colours to branding, report text |
| Performance bar / performance analyzer | 1 | drop silently (Quickbase-only) |
| "Hide new/updated icons", `GlobalSearchExactMatch`, `DisableAllUsersSelection`, `DisplayChartShading` | 1 each | report |
| Default view mode (`Auto/Desktop/Mobile`) | 1 | report |
| Cross-application access (`CrossApplicationAccess`) | 1 | report |
| Application guides (`ApplicationGuides`) | 0 | n/a |
| UTF-8 data-encoding option | 1 | drop |
| Rich-text pages, home pages (non-code, non-dashboard) | 1 homepage | warn |
| Quickbase connection parameters (`ConnectionId`) | 5 | warn |

## 2. App Settings

| Missing | Seen | Note |
|---|---|---|
| Per-app "approved users only / approved IP" exist in PowerBase, but not the Quickbase "private app" and "load all embedded reports on form load" switches | – | n/a |
| Role-based UI visibility settings (see §16) | 16 roles | – |

## 3. Table

| Missing | Seen | Interim handling |
|---|---|---|
| **Connected tables** (live sync of a table from another Quickbase app / Admin Console / sample source, schedule, update mode, field map) | 5 tables, 244 connected columns | import as plain local table **and warn** (today it is silent) |
| Per-table, per-role **home page** (`TableHomePageSettings.RoleViews`) — PowerBase has one home page per *role*, not per table | 95 tables × 16 roles | keep role-level homepage only |
| Default colour coding for reports (`DefaultColorCoding`) | 95 | drop |
| Default row height / row-height setting | 95 | drop |
| `ActionOnNewFieldAdd` (make default / ask / none for *reports*) — PowerBase only has auto-add for forms | 95 | drop |
| Table-level "Searchable" (exclude from global search) and "Index file attachments" | 95 | report |
| `TableNameOverride` (separate display name) | 95 | use Name |
| Report groups (`QB::ReportGroup`) — folders of reports | 95 | informational |

## 4. Table Settings

| Missing | Note |
|---|---|
| Table-level "Save on exit" semantics identical to Quickbase — PowerBase's `IsAutoSaveOnLeave` is the closest *(verify)* | map 1:1 |
| Quickbase **table bar** ordering per role | PowerBase has one sidebar order |

## 5. Field

| Missing | Seen | Interim handling |
|---|---|---|
| **Append / edit history** on text fields (`EditHistory`: prepend, include time/user) | 467 fields | report as "history not kept" |
| `WorkDate`, `Predecessor` field types | 0 | warn |
| vCard / iCalendar fields (`QB::Field::Unsupported`) | 0 | warn with Quickbase's explanation |
| **Add to all new reports** (`AddToAllNewReports`) | 2,825 | drop |
| **Allow data copy** off (`AllowDataCopy`) — block copy of values | 15 types | report |
| "Show time zone" on date/time (`ShowTimeZone`) | – | drop |
| Date default "today / now" as a *dynamic* default (`DefaultIsToday/DefaultIsNow`) *(verify — only static `DefaultValue` seen)* | few | store nothing, report |
| Phone: default country, extension flag, international flag | 25 fields | drop |
| Email: default domain, sort by domain, "email all" | 30 fields | drop |
| URL: button colour, iframe width/height, "abbreviate" | 7 | drop |
| Address: format (US/International), default map type, input width | 2 | drop |
| File attachment: "open access", "open in new window" | 6 | drop |
| User fields: "allow inviting external users" | 1 | drop |
| Choice fields: **"allow users to create choices"** | 38 | drop |
| Rich text: `@mentions` (`AllowMentions`) | 2 | drop |
| Text: multi-line `Height` (rows) | 10 | drop |
| Rating, Currency exist — nothing missing | – | – |

## 6. Field Settings

| Missing | Note |
|---|---|
| Per-field "sharing" (`SharedWith`) — field visible to selected users | 1 field; map to field permissions where possible |
| `Format` (display format string for some types) | 1 |
| `OverrideSubFieldAccess` | 13 types; Quickbase-internal |

## 7. Relationships, Lookup, Summary, Reference

| Missing | Seen | Interim handling |
|---|---|---|
| **Reference field populated by a formula** (relationship key computed from a formula, e.g. `CurrentUserRecordID`) | 26 | import the relationship; **keep the formula text in the report**; (optional) create a formula field beside it |
| **Cross-application relationships** (`CrossAppParent/Child`) | 0 | warn |
| Summary functions **StdDeviation**, **CombinedUser**, Median *(verify)* | 0 | warn |
| Summary "Display as link to children" / **drill-down report** / "evaluate only on change" | 328 each | drop |
| Lookup/summary chain **limit of 3** (Quickbase has no such limit) | ~47 lookups | report which ones |
| `CombinedText` summary over a **Reference** or calculated field (PowerBase refuses) | ≥38 | create as Distinct Count? or skip + report |
| Reference "use record picker for grid edit" | 161 | drop |

## 8. Forms

| Missing | Seen | Interim handling |
|---|---|---|
| **Form page colour** (`Page.Color`) | 112 | drop |
| **CAPTCHA / bot check** (`BotCheckModes`, v0.15) | 96 forms | drop |
| **Barcode scanner** on fields (`ShowBarcodeScanner`, v0.15) | 0 | – |
| Section visibility *per mode* (`ModesToShowIn` on sections) *(verify — PowerBase has per-element only)* | 260 | apply to its elements |
| Element `ShouldValidate`, address `EntryMode: Coordinates`, `HideMap` | 120 / 1 / 1 | drop |
| **Embedded report with its own sub-form** (`SubForm`) | 16 | embed report only |
| Visual **element group** container (PowerBase has `GroupId`, but flattened on import) *(verify)* | 40 | flatten + report |
| Legacy forms (`QB::Form`) | 95 forms, ~2,500 elements | skip (FormV2 is live) — report one line per table |
| Form-level theming parity (PowerBase has `ThemeJson`; Quickbase has colours only) | – | – |

## 9. Forms Settings

Nothing missing structurally; the items above plus `NewFieldAction`, `HorizontalRuleBetweenSections`, `WrapFieldLabels`, `ShowSaveAndCancelButtons`, `WaitForEmbeddedReports` exist **only on legacy forms** (95) and have no PowerBase counterpart.

## 10. Form Rules

| Missing | Seen | Interim handling |
|---|---|---|
| Conditions **Days ago / Range / Relative date** (Today, Tomorrow, Yesterday) | 0 | warn |
| **Mixed nested AND/OR groups** inside one rule | 6 rules | warn / split into rules |
| **Tags** on rules | 40 | drop |
| "Fire only on change" toggle *(verify)* | 40 | map to `AnyChange` when true |
| Action "Set color" target = column (works) — nothing missing | – | – |
| Legacy dynamic form rules | legacy forms | skip |
| `UnsafeRecordId` warning concept (record ids in rule values) | 0 | – |

## 11. Reports

| Missing | Seen | Interim handling |
|---|---|---|
| **Calendar, Timeline, Kanban, Map** report types | 0 in cares | warn (types exist in other apps) |
| **Colour coding** of rows/cells (`ColorCoding`, `ColorFormula`) | 1 | drop |
| **Default row height** | 532 | drop |
| **Report formula columns** (`QB::Report::Formula::*`) | 11 | report the formula text |
| **Column label override, alignment and pinning** (`ColumnsUI`) *(verify — PowerBase has pinned columns in the UI, no stored label override)* | 532 | drop |
| "Display only new or changed records" | 532 | drop |
| "Show summary table / hide main table" on table reports | 532 | drop |
| Table report `Format` (XSL stylesheet) | 532 | drop |
| Chart types not found: Funnel, Heatmap, Scatter, Streamgraph, Variwide, 3-D variants, Radial bar | 0 | warn per chart |

## 12. Report Settings

Same as §11 where flagged; additionally **per-user personal report settings** and "XML stylesheet" have no equivalent.

## 13. Dashboard Pages

| Missing (from the QBL v0.15 spec; none in cares) | Note |
|---|---|
| **KPI** widget (gauge, delta, target, scale) | PowerBase widgets: Report, Text, ButtonBar, WebPage, Search, Spacer, LinkBar |
| **Kanban, Calendar, Timeline, Map, Chart (standalone), Summary, Table** widgets | PowerBase embeds a *report* instead |
| Button variants **Create record / Open page / Open link / Open report** | partly covered by ButtonBar (`ActionType`) |
| Dashboard **filters bound to several reports** (`Targets`) | PowerBase has filter slots/bindings — closest match |
| Per-dashboard **homepage** assignment per role | via `AppRole.HomePageId` |

## 14. Code Pages

Nothing missing in PowerBase itself. Constraints to document: needs tenant flag `CodePagesEnabled` and `pages:code`; PowerBase stores HTML/CSS/JS in separate columns (Quickbase has one blob); Quickbase-specific DBID tokens inside the code are not rewritten.

## 15. Formula Engine

| Missing | Seen in cares | Interim handling |
|---|---|---|
| **Reference fields usable in formulas** (and lookups of reference fields) | 82 + part of 168 refs → most of the 149 manual-review formulas | flag; engine change needed |
| **Multi-branch `If(c1,v1,c2,v2,…,else)`** | 1+ (`Age`) | rewrite to nested `If` or flag |
| **`Left/Right/NotLeft/NotRight` with a text delimiter** | ~29 | rewrite via `Part`/`Find`, or flag |
| **`ToTimeOfDay`** | 6 | add function |
| Typed variable `var datetime` | 2 | add alias or rewrite to `date` |
| Quickbase **query syntax** `{13.EX.'x'}` inside `GetRecords` *(verify supported strings)* | 20 | verify at runtime |
| Numeric field IDs `[1226]` / `_fid_N` | 2 + URL formulas | translate to `[fid_N]` (needs FID map) |
| Quickbase URL conventions (`?a=API_GenAddRecordForm`, `Rurl()` semantics) | ~160 | link needs manual rewrite |
| Report-column / data-rule / form-rule formulas translated through the same engine | 18 | translate with the field-formula path |

Everything else cares uses (116 registered functions covering `URLRoot, URLEncode, Rurl, If, Case, ToText, ToNumber, Today, Part, Contains, Trim, SearchAndReplace, GetRecords, GetFieldValues, Days…`, `var` declarations and `$` variables) is supported.

## 16. Roles & Permissions

| Missing | Seen | Interim handling |
|---|---|---|
| **Role-based UI hiding — app level**: hide Settings, Users, Favorites, Search, Help, "Test as" | 16 roles | report per role |
| **Role-based UI hiding — table level**: hide in table bar, New record, Grid edit, Email, Print, Import/Export, Save as spreadsheet, Create/customize report, Personal settings | 16 roles × tables | map what has a permission code (`records:create`, `reports:create`); report the rest |
| `DisableAccess` as a role flag *(map to empty permission set)* | 16 | – |
| `RestrictAccessToNewTablesForThisRole` | 16 | apply as default `ViewScope=None` for future tables |
| Role **rank / hierarchy by Quickbase manager** | – | all imported roles rank 3 |

## 17. Table-Level Permissions

| Missing | Seen | Note |
|---|---|---|
| `CustomRule` view/modify expressed as a **query criteria** | 34 | implement through record filters (§19), not a new feature |
| `CanRefreshConnectedData` | 5 | no connected tables in PowerBase |

## 18. Field-Level Permissions

Nothing missing in PowerBase (`AppRoleFieldPermission`: Modify / View / None). Gap is on the importer side only.

## 19. Table-Level Record Filters

| Missing | Note |
|---|---|
| **Relative-date / "in range" operators** (`IR`, `XIR`, "today", "last 30 days") *(verify the operator list of `FilterCondition`)* | 0 in cares |
| Filter conditions **comparing to another field / user's field** | partly via `ValueFieldId`; Quickbase `_curuser_` maps to `UseCurrentUser` |
| Separate **modify** filter distinct from the **view** filter (cares has 1,232 modify criteria) *(verify — `AppRoleRecordFilter` has one filter per role+table)* | 1,232 | decide: apply modify criteria to `ModifyScope`, or keep view only and report |

---

## Summary — what is genuinely missing vs. what is just not imported

| Category | Examples |
|---|---|
| **Truly missing in PowerBase** (needs product work) | connected tables; append/edit history; formula Reference fields; multi-branch `If`; delimiter `Left/Right`; `ToTimeOfDay`; per-role UI hiding; calendar/timeline/kanban/map reports; colour coding; row height; report formula columns; KPI/Kanban/Calendar/Map dashboard widgets; fiscal year / first-day-of-week / app time zone; email/phone/address/URL extras |
| **PowerBase can hold it, importer doesn't carry it** (no product work) | field settings, required/default/unique, description; record filters; field permissions; report filters/grouping/options/role visibility; summary criteria; form settings & pages; role conditions; message / abort-save / live-value rule actions; embedded report elements; icons; app formatting & security settings; table show-in-bar, key/title fields, default report settings, custom data rule |
