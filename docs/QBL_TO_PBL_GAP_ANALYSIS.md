# QBL → PBL Gap Analysis (cares.qbl vs current implementation)

Date: 2026-10-19 · Mode: **investigation only — no code, data or output was changed.**

Sources compared:
- **QBL**: `Downloads\cares.qbl.txt` (QBL v0.12, 227,867 lines). Every node type and property was inventoried by walking the parsed AST (106 node types), then value-level counts were taken for the properties that matter.
- **Converter / importer**: `Import/Qbl/QblToPblConverter.cs`, `QblFieldTypeMap.cs`, `Pbl/*`, `ImportAppFromPblCommandHandler.cs` and the new Update/Diff code.
- **PowerBase capability**: Domain entities, field-settings models, command records, formula engine registry, Angular app.
- **Excel**: `Downloads\CARES_Formula_Fields.xlsx` (465 formula-bearing fields).
- **Last real import** of this file into a scratch tenant (95 tables, ~1,000 fields, 502 reports, 161 relationships, 96 forms, 14 roles, 52 variables).

How to read: **"Converter reads"** is a heuristic — a property counts as read only if its name appears in the converter code; "not read" is reliable, "read" may still be partially used. Severity: **H** = data/behaviour lost that users will notice, **M** = fidelity loss, **L** = cosmetic or rare.

---

## 0. The three gaps you named — verified

| Your point | Verdict | Detail |
|---|---|---|
| "Can't use summary and reference fields in formulas" | **Half right.** | **Reference fields: yes, unsupported.** `FormulaTypeMap.FieldType()` returns null for `Reference` (and for a lookup *of* a Reference), so `[Related DSP]` is "Unknown field" — this alone blocks most of the 149 formulas flagged for review (82 reference-field references and 168 lookup references appear in cares formulas). **Summary fields: the engine does support them** (`Summary → SummaryResultType`). Formulas referencing a summary fail only because the summary itself was not created (see §7), not because the engine rejects it. |
| "Formula-rich text becomes formula-text" | **Confirmed, wider than rich text.** | `QblFieldTypeMap.FormulaResultTypes` maps `URL`, `RichText`, `Email`, `PhoneNumber` `::Formula` all to result type `Text` → `Formula_Text`. PowerBase has `Formula_Url`, `Formula_RichText`, `Formula_Email`, `Formula_Phone`. Affected in cares: 165 URL, 5 RichText, 3 Email, 1 Phone formulas. |
| "Table / app icons not synced" | **Confirmed (UI-visible bug).** | See §1 and §2: QBL icon names ("Personal Record", "Application") are stored verbatim; the UI renders `pi pi-<value>`, which is not a valid class for those names. |

---

## 1. App

| QBL (cares) | Converter | PowerBase has | Gap |
|---|---|---|---|
| `Name`, `Description` | read | `App.Name/Description` | none |
| `AppColor` (`!Var` → `#72509a`) | read (resolved via `!Var`) | `App.Color` | none |
| `AppIcon` (`!Var` → `Application`) | read, stored raw | `App.Icon` (icon-catalog id, e.g. `building`) | **H – UI.** Value `Application` is not a catalog id → no/blank icon. Needs a QB-name → catalog-id map with a default. |
| `TableOrder`, `RoleOrder` | not read | `AppTable.DisplayOrder` | M – sidebar/table order and role order not carried. |
| `Manager` (`YSL18`) | not read | app owner = importing user | L – by design; report it. |
| `CurrencySymbol`, `CurrencySymbolPosition`, `NumberFormat.*`, `DateFormat`, `FirstDayOfWeek`, `FirstMonthOfFiscalYear`, `FiscalYearDesignation`, `Timezone` | not read | `AppFormattingSettings` (currency symbol/position, decimals, thousand separator, date format string) | M – formatting settings exist but are never filled; fiscal-year, first-day-of-week and timezone have **no** PowerBase equivalent. |
| `Branding.AppHeader.*` (colours, left text), `UseCustomHeaderAndFooter`, `HideQuickbaseStandardMenu` | not read | `AppBrandingSettings` (theme, accent, `BrandColorHex`, density…) | L/M – header background/text colour could map to `BrandColorHex`; header/footer text has no equivalent. |
| `CanCopyApp`, `CanExportData`, `RequireAppToken`, `Searchable`, `DisableNonAdminSyncConnections` | not read | `AppSecurityOptionsSettings` (AllowNonAdminsToCopy/Export/Connect, RequireAppTokens, HideFromPublicSearch…) | M – 1:1 mappings exist but are not applied (importer creates the app with defaults). |
| `DefaultViewMode`, `TableV3State`, `HideNewUpdatedIcons`, `DisableAllUsersSelection`, `ShowPerformanceBar/Analyzer`, `GlobalSearchExactMatch`, `Utf8DataEncoding`, `CrossApplicationAccess`, `DisplayChartShading` | not read | none | L – Quickbase-only. Should be listed in the report as "not applicable". |
| `Variables` (52) | **now imported** | `AppVariable` | none (names with `[ ]` or `var_N` skipped + reported) |
| `Pages` → `QB::CodePage` (3) | parser drops them (corrupt `RawCode`, see §14) | `Page` type Code | **M** – 0 of 3 imported. |
| `Pages` → homepage / rich-text page | warned `QBL_UNSUPPORTED_PAGE_TYPE` | no rich-text page type | L |
| ParameterDefinitions `ConnectionId` ×5 | n/a | none | L |

## 2. App Settings

PowerBase app settings screens: Properties (name/description/icon/colour), Formatting, Security options, Branding/Layout, Variables, Roles, Users, Tokens. Apart from Variables and Roles, **none of the Quickbase settings above are populated by the import** — every imported app opens with PowerBase defaults. Highest-value additions: icon mapping, currency/date/number formatting, the three security flags.

## 3. Table

| QBL (cares, 95 tables) | Converter | PowerBase has | Gap |
|---|---|---|---|
| `Name`, `Description`, `RecordNameSingular/Plural` | read | same fields on `AppTable` | none |
| `TableIconName` (95 tables, 43 distinct names: Folder ×29, Personal Record ×5, Spreadsheet ×4, …) | read, stored raw | `AppTable.Icon` (catalog id) | **H – UI.** Quickbase display names → no catalog match → broken/blank icon on every table. |
| `IncludeInTableBar: false` (5 tables) | not read | `IsShowInBar` | M – hidden tables become visible. |
| `AliasHint` | not read | `Alias` (`_DBID_*`) generated on create | M – a formula using `[_DBID_X]` is rewritten to `Dbid("Table")`; the alias itself is not preserved. |
| `KeyField`, `RecordTitleField` (2), `UseCustomRecordTitleField` (2), `DefaultRecordPicker` | not read | `KeyFieldId`, `DisplayFieldId`, `DefaultRecordPickerField1-3Id` | M – record title / picker fields lost; all tables use Record ID#. |
| `CustomDataRules` (1 table ON, 1 formula) | not read | `CustomDataRule` + `IsCustomDataRuleEnabled` | M – supported by PowerBase, never imported (formula would need translation). |
| `ConnectedData` (5 tables, 244 connected columns) | not read | none | M – tables imported as plain local tables, silently; **no warning is emitted** for the connection. |
| `TableHomePageSettings` (role→report/page homepage) | not read | `AppRole.HomePageId` | L/M |
| `SaveOnExit`, `Searchable`, `IndexFileAttachments` | not read | `IsAutoSaveOnLeave` is the closest | L |
| `DefaultReportSettings` (sort, row height, colour coding, header text, inline editing, group display, quick search) | not read | `AppTable.DefaultReportSettings` (JSON) + Table Default Report Settings page | M – imported tables use PowerBase defaults. |
| `ReportGroups` (95) | skipped (informational) | none | L |

## 4. Table Settings

Importer touches: name, labels, description, icon. Not set: show-in-bar, key/title/picker fields, default report settings, custom data rule, advanced settings (auto-save). Table Settings → Advanced / Default Report Settings pages therefore show defaults.

## 5. Field (2,825 field nodes)

| Area | QBL evidence | Converter | PowerBase | Gap |
|---|---|---|---|---|
| Label / name | all | read; names deduped (case-insensitive), labels now numbered on clash | label unique per table | none |
| **Field settings (`PblField.Settings`)** | per-type props (see §6) | **never set** — no code assigns `Settings` | `FieldSettingsModels` (Text, Select options, Numeric decimals/separator/symbol/position, Date formats, Url, File, Address, User…) | **H** – every imported field carries empty settings. |
| Description (`Comments`) | 55 fields | not read (PBL has `Description`, importer ignores it) | `AppField.Description` | M |
| `IsRequired` | 55 fields | read into PBL; **importer drops it** (`AppFieldSpec` has no required flag) | `IsRequired` | **H** – required rules not enforced. |
| `DefaultValue` | 90 fields | not read | `DefaultValue` | **H** |
| `IsUnique` | 45 fields | not read | `IsUnique` | **H** |
| `HelpText` | 2 | read (labels) but not stored | `HelpText` | L |
| `IsAuditable`, `IsSearchable`, `IsBold`, `AllowedInReports`, `AddToAllNewReports`, `DisplayWithoutWrapping`, `AllowDataCopy`, `OverrideSubFieldAccess` | all fields | not read | `IsAuditable/IsSearchable/IsReportable`, bold/no-wrap settings | M |
| `EditHistory` (467 fields) | text-history append feature | not read | no equivalent | M – Quickbase "append edit history" has no PowerBase feature. |
| `RolePermissions` on fields (**22 fields**) | role → View/Modify/None | not read (matrix wrongly says none exist) | `AppRoleFieldPermission` | **H** – field-level permissions exist in the file and in PowerBase (`PblFieldPermission` model exists but is never populated). |
| System fields (Record ID#, Date Created/Modified, Last Modified By, Record Owner) ×95 | skipped by design | auto-seeded with names like `S_recordId` | – | none (label matching now handles references). |
| `QB::Field::Unsupported`, vCard/iCal | warned | none | L |

### Field type coverage

| QBL type | Count | PowerBase type | Status |
|---|---|---|---|
| Text, TextMultiline, RichText, Email, PhoneNumber, URL, FileAttachment, Number, Percent, Date, DateTime, TimeOfDay, Duration, Checkbox, User | ~740 | matching | ✔ (settings lost, see above) |
| TextMultipleChoice | 38 | SingleSelect | ✔ type; **choices lost** (`InputOptions.ListChoices` not read) |
| Address + 6 sub-fields | 2 | Address | ✔ |
| Reference / Lookup / Summary / ReportLink | 161 / 526 / 328 / 165 | relationship fields | ✔ structure, see §7 |
| `Currency`, `Rating`, `ListUser`, `MultiselectText`, `WorkDate`, `Predecessor` | 0 in cares | Currency/Rating/MultiUser/MultiSelect exist; WorkDate/Predecessor don't | not exercised |

## 6. Field Settings (per type) — all lost

QBL properties seen in cares and the PowerBase setting they correspond to (none is currently imported):

| QBL property | Seen on | PowerBase setting |
|---|---|---|
| `InputOptions.ListChoices` / `Choices`, `AllowCreateChoices`, `ChoiceOrder` | 38 choice fields | `SelectSettingsBase.Options` |
| `DecimalPlaces`, `NumberFormat.SeparatorStyle/Places`, `Units`, `DoesTotal/DoesAverage`, `TreatBlankAsZero`, `Width` | numeric fields | `NumericSettings` (Decimals, Separator, Symbol, Position, DisplayAs, Validation) |
| `ShowMonthAsAlpha`, `HideCurrentYear`, `ShowDayOfWeek`, `DefaultIsToday`, `ShowTime`, `ShowTimeZone`, `Use24HourClock`, `DefaultIsNow` | date/time fields | `DateSettings`, `TimeSettings` |
| `MaxLength`, `Height`, `AllowMentions` | text/rich text | `TextSettings`, `RichTextSettings` |
| `DefaultDomain`, `SortByDomain`, `EnableEmailAll`, `DisplayAs` | email | `EmailSettings` |
| `HasExtension`, `IsInternational`, `DefaultCountry` | phone | `PhoneSettings` |
| `LinkText`, `Abbreviate`, `DisplayAsButton`, `ButtonColor`, `EmbedAsIframe`, `Target` | URL / URL formula | `UrlSettings`, `FormulaUrlSettings` (open target, button, link text) |
| `RevisionsToKeep`, `AllowOpenAccess`, `ShowAsImages` | file attachment | `FileSettings` |
| `UserSet`, `AllowInviteExternalUser`, `DefaultValue: CurrentUser` | user fields | `UserFieldSettings` |
| `ShowAsCheckmark` | checkbox | `BooleanSettings` |

## 7. Relationships, Lookup, Summary and Reference fields

| Item | QBL | Converter | PowerBase | Gap |
|---|---|---|---|---|
| Relationships | 161 parent/child pairs | converted | `CreateRelationship` | ✔ |
| **Auto "Add {child}" + "{child} records" fields** | the file also has its own "Add X" URL formulas | – | PowerBase creates both for every relationship | **H – duplicates.** 17 exact label clashes (renamed "(2)") and 128 plural/singular near-duplicates ("Add Program" vs "Add Programs") → ~145 of 161 parents end up with two "Add child" buttons. |
| Reference field **with a formula** | 26 fields (e.g. `CurrentUserRecordID`) | formula ignored, **no warning** | n/a | **H** – silent loss; also blocks dependent formulas. |
| Reference props `Proxy`(112), `UseRecordPickerForGridEdit`(161), `FilterField/MatchField/ConditionalValues` (1) | | not read | `ReferenceSettings` (filter conditions, display key) | M/L |
| Lookups | 526 | most created after the dependency-round retry; **47 were still skipped** in the last import | `AddLookupFields` | M – remaining: source is a Reference field, chained >3 deep, or missing. |
| Lookup of a lookup (>3 deep) | present | skipped + reported | hard limit `LookupChain.MaxLength = 3` | M |
| Summaries | 328 (CombinedText 111, Maximum 94, AnyRelatedRecords 62, Total 29, Count 19, Min 13) | 105 skipped before the retry rounds, still ≥38 skipped after | `AddSummaryField` | **M/H** – `CombinedText` over a Reference field is refused by PowerBase ("can't show a Reference field's values as text"); summaries over a lookup/calculated target also refused. |
| **Summary matching criteria (`Query`)** | 257 summaries carry one | **not read** — summary created without criteria | `AddSummaryFieldCommand.MatchingCriteria` (FilterGroup) | **H** – a "count where status=Active" becomes "count all". |
| `DrillDownReport`, `DisplayAsLinkToChildren`, `EvaluateOnlyOnChange` | 328 each | not read | partly (`SummarySettings`) | L |
| Lookup/summary on Address sub-field | present | handled (sub-field key) | ✔ | none |
| Cross-app relationships | 0 | warned | none | n/a |
| ReportLink | 165 | folded into relationship (PowerBase auto-creates its own) | | L – labels differ ("See related X"). |

## 8. Forms (FormV2: 96 forms · 112 pages · 260 sections · 267 columns · 2,455 field elements · 153 report elements · 40 groups)

| Item | Converter | PowerBase | Gap |
|---|---|---|---|
| Sections, columns→blocks, field elements | converted | `FormSection/Block/Element` | ✔ |
| Pages / tabs (`FormV2::Page`: Name, Color) | **page tier dropped** (only ordering kept) | `FormPage` (Heading) | M – multi-page forms flatten into one long form (112 pages across 96 forms). |
| Element `ModesToShowIn`, `LabelMode/Override`, `Width`, `ForceReadOnly/Require` | read | `ShowOnAdd/Edit/View`, label mode, width, `IsReadOnly/IsRequired` | ✔ |
| Element `FilterChoicesBy` (665), `MultipleChoiceDisplayAs` (43), `ShouldValidate` (120), `EntryMode`, `HideMap` | not read | `FormElement.DisplayAs` partly | M – choice display as radio/dropdown lost. |
| **Embedded report elements** | 153 | **skipped** (`QBL_UNSUPPORTED_FORM_ELEMENT`) | `FormElement` type `Report` exists | **H** – every "related records" grid on a parent form disappears. |
| Element groups | 40 | flattened (warning) | no group element… (`GroupId` columns exist) | L/M |
| Rich-text element | 1 | → static text | ✔ | none |
| Section `IsCollapsible`, `ShowColumnDividers` | not read | `ShowDividers`, `IsCollapsed` | L |
| Form props `ShouldAutoAddFields`, `ShouldShowBuiltInFieldsFooter`, `AlwaysTabsForViewRecord`, `AllowedSaveActions`, `BotCheckModes`, `DisplayPagesAs` | not read | `Form.AutoAddNewFields`, `ShowBuiltInFields`, `SaveOptions`, `AlwaysTabsOnView`, `PageNavMode` | M – every form keeps defaults (e.g. save buttons). |
| Legacy `QB::Form` (95 forms, ~2,500 legacy elements) | intentionally skipped | – | L – FormV2 is the live one; but add **one** informational line per table instead of silence. |
| Missing-field elements | 330 form elements dropped because their field was not created | – | M – consequence of §5/§7/formula gaps (e.g. DSP In SPC form loses 4 fields). |
| Role form overrides (`RoleDefaults`, `ReportOverrides`) | not read | `AppRoleTableFormOverride` | M |

## 9. Forms Settings

Imported forms: name, default flag (first form wins), layout. Everything in the "Form settings" screen (auto-add fields, save actions, built-in fields footer, tabs mode, quick-peek) stays default.

## 10. Form Rules (40 rules, 17 forms)

| QBL | Converter | PowerBase | Gap |
|---|---|---|---|
| `Name`, `Description`, `Disabled`, `RunOn` (4 modes) | read | `FormRule` | ✔ |
| `FireOnlyOnChange`, `Tags` | not read | `RunTrigger`, tags | L |
| Field conditions (34) incl. group (34) | converted; mixed AND/OR nested groups flagged | field conditions | ✔ / M for nested |
| **Role conditions** (14) `IsInRole` | **skipped** | PowerBase **does** have `ConditionKind = "role"` | **H** – the compatibility matrix is outdated; these 14 can be imported. |
| `FormulaIsTrue` (6) | expression mode | `IsExpressionMode` | ✔ (6 formulas still need translation) |
| Actions Show/Hide/Require/Unrequire/MakeReadonly/Change | mapped | action types | ✔ |
| **DisplayMessage / AbortSave** (5 + 2) | **dropped** — mapper demands a `Target`, these actions have none; the `Message` text is never read | `FormRuleAction.MessageType`, `MessageSeverity`, `ActionValue` | **H** – validation messages and "block save" rules are lost. |
| **Change with `Value: !Ref{Field}`** (6) | skipped as "static value can't represent" | `FormRuleAction.ValueSource`, `SourceFieldId`, `IsExpressionValue` support live values | **H** – PowerBase *can* represent it; PBL schema has no field for it. |
| Condition/Action targets missing on the form | skipped (10) | – | M |
| `Comparison` operators | 12 mapped | | ✔ |

## 11. Reports (532 table reports + 11 report-formula columns)

| QBL | Converter | PowerBase | Gap |
|---|---|---|---|
| Name, columns | read (`Columns: Default` → empty = all) | `CreateReportCommand.Columns` | ✔ |
| Sort | read | `SortFields` | ✔ |
| **Group-by** (`SortAndGroup` group flags, 532) | only sort used | `GroupByFieldId`, `SortGroupLevelCommand` | **H** – grouped reports become flat. |
| **Filters** (`Filters.Criteria`, 121 non-default) | **not read** — `FilterTree` is always null | `FilterTree` (`FilterGroup`) | **H** – 121 reports show unfiltered data. |
| `ColumnsUI` (labels, alignment, pinned — 532) | not read | none (labels), partial | M |
| `HideTotals`, `HideMainTable`, `ShowSummaryTable`, `DisplayOnlyNewOrChangedRecords` | not read | `HideTotals` | M |
| `Dynamic filters` (`Filters.DynamicFields`, `AllowQuickSearch`) | not read | `DynamicFilterType`, `AllowQuickSearch` | L/M |
| `ColorCoding` (1 non-default) | not read | none | L |
| Summary reports (grouping + aggregates) | converter supports type, columns only | `Summary` report type + aggregations | M – no summary report appears in cares, untested. |
| **Report formula columns** (`QB::Report::Formula::*`, 11) | not read | none | M |
| **Duplicate report names** | 30 skipped | names unique per table | L – "Embedded for X" duplicates. |
| Missing columns | 249 reports lose columns whose field was never created | – | M |
| Calendar/Timeline/Kanban/Map/Chart | 0 in cares | Table/Summary/Chart only | n/a here |

## 12. Report Settings

`AllowEditingRecords/ViewingRecords`, `DefaultRowHeight`, `InlineEditing`, `GroupDisplay`, `DisableDeleteCheckbox`, `RowNumberVisibility`, `Format` → PowerBase `ReportOptionsCommand` (show edit/view/quick-peek icons, disable bulk delete/update/grid edit, header text) — **none imported**; every report uses defaults. Report-level `RolePermissions` (532 × roles) is not mapped to `AppRoleReport` / `VisibleToRoleIds`, so all imported reports are visible to everyone with table access (**H – security**).

## 13. Dashboard Pages

0 dashboards in cares (`QB::Dashboard` absent; 1 `QB::HomePage` warned). PowerBase dashboards support Report, Text, ButtonBar, WebPage, Search, Spacer, LinkBar widgets. **No converter exists** for `QB::Dashboard` (widgets, tabs, filters), homepages or rich-text pages — a gap for other files, none for cares.

## 14. Code Pages

3 in cares. The YAML parser fails on corrupt `RawCode` blocks and the **fallback strips all `Pages`** (`QblRawTextSanitizer`) — so even though the converter maps `QB::CodePage → Code page`, **0 are produced**. Also creation needs the tenant flag `CodePagesEnabled` + `pages:code` permission, and the whole `RawCode` goes into `CodeHtml` (no HTML/CSS/JS split, no `[_DBID_]` rewriting inside page code).

## 15. Formula engine — what is and isn't supported

**Functions.** 116 registered; cares uses 72 distinct tokens, of which only **`ToTimeOfDay` (6 fields)** is a real missing function (the other "unsupported" tokens in the Excel are words inside strings). Heavily used and supported: `URLRoot, URLEncode, Rurl, If, ToText, Contains, Case, ToNumber, Today, Part, Left, Right, GetRecords, GetFieldValues, SearchAndReplace, Trim, Days`.

| Construct | In cares | Status | Effect |
|---|---|---|---|
| `var type name = …;` / `$name` | 60 formulas | supported | ✔ |
| `[_DBID_X]` | 170 | rewritten to `Dbid("Table")` (unknown tables flagged: 16) | ✔ / 16 manual |
| `Rurl()`, `URLRoot()` | 160 | supported (output is Quickbase-shaped URL) | links need manual rewrite |
| **Multi-branch `If(c1,v1,c2,v2,…,else)`** | 1+ (`Age`) | **not supported** (If takes 2–3 args) | formula skipped |
| **`Left/Right(text, "delimiter")`** | ~22 | numeric count only | skipped (`DuplicateChecker`) |
| `NotLeft/NotRight` with text | 7 | numeric only | skipped |
| **Reference fields `[Related X]`** | 82 refs | **unsupported** | most manual-review formulas |
| Lookup of a Reference | within the 168 lookup refs | unsupported | same |
| Summary / Lookup (plain) | 53 / 168 | supported | ✔ if the field exists |
| Formula referencing another formula | common | supported — now created in retry rounds | ✔ |
| `GetRecords`/`GetFieldValues` with query `{fid.OP.value}` | 20 | functions exist; `{13.EX.'…'}` Quickbase query syntax and numeric FIDs `[1226]` (2) are not translated | partial |
| `ToTimeOfDay` | 6 | missing | skipped |
| `Month(...)`-style typed `var datetime` | 2 | engine rejects `datetime` type | skipped |
| `&` with non-text | 3 | auto-wrapped in `ToText` | adjusted |
| Formula result type | | `URL/Email/Phone/RichText` flattened to Text | §0 |
| Report formulas (11), data rule (1), form-rule formulas (6) | 18 | not translated | M |

Totals on the last import: 432 formulas → 211 clean, 72 adjusted, 149 manual review; 7 had empty expressions.

## 16. Roles & Permissions

| QBL (16 roles) | Converter | PowerBase | Gap |
|---|---|---|---|
| Name, Description, Default | read | `AppRole` | ✔ (roles named Administrator/Viewer/Participant adopt the seeded ones and **overwrite their permissions**, reported) |
| `ManageUsers`, `EditApp`, `DisableAccess`, `RestrictAccessToNewTablesForThisRole` | not read | `AppRolePermission` codes (`users:manage`, `apps:update`, `roles:manage`…) | **H** – imported roles get a default permission set; "admin-like" roles are not admin. |
| `AppUI.*` (hide settings/users/search/help…) | not read | none | L |
| `TableUI.*` (hide new record, grid edit, email, print, import/export…) | not read | none (partly via permission codes) | M |
| Role `HomePage` | not read | `AppRole.HomePageId` | L |
| Role hierarchy / `Rank` | n/a | all imported roles rank 3 | L |

## 17. Table-Level Permissions

`CanViewRecords`/`CanModifyRecords` (`Always`/`Never`/`CustomRule`), `CanAddRecords`, `CanDeleteRecords`, `CanSaveCommonReports`, `CanEditFieldProperties` → `AppRoleTablePermission` — **converted for all 95 tables × 16 roles**. Gaps: `CustomRule` (31 view + 3 modify) defaults to **no access** (flagged 34×); `OwnRecords` scope has no QBL source; `CanRefreshConnectedData` (5) dropped.

## 18. Field-Level Permissions

22 fields carry `RolePermissions` in cares; `AppRoleFieldPermission` (View/Modify/None) and `PblFieldPermission` exist but **nothing reads field `RolePermissions`** → all field-level security is lost; roles get `FullAccess`.

## 19. Table-Level Record Filters

`CustomAccessCriteria` appears **2,560 times** (1,328 on view, 1,232 on modify). They are almost all simple single conditions — `({'8'.CT.'Test'})` ×1,582, `({'200'.EX.'1'})` ×955, a handful of others (`'33'.EX.'1'`, `'29'.CT.'Fidelis letters'`). Today every one is **flagged and dropped** (2,506 warnings in the report), so the role sees **all** records. PowerBase has `AppRoleRecordFilter` (conjunction + `FilterJson` conditions, optional current-user) — a direct target. Translation only needs `FID → field` (QBL fields carry `Id`) and `CT → contains`, `EX → eq`.

---

## 20. UI-side gaps (Angular import wizard and imported-app experience)

| # | Gap | Impact |
|---|---|---|
| U1 | **Icons**: table and app icons imported as Quickbase display names; UI builds `pi pi-<value>` | blank/garbled icons on every table and the app |
| U2 | Branding/formatting/layout not applied (§1–2) | imported app looks like a fresh default app |
| U3 | Hidden tables (`IncludeInTableBar=false`) shown in the sidebar; Quickbase table order not preserved | navigation differs from source |
| U4 | **Skipped list is a flat dump** (up to ~5,000 rows in the last import) — no grouping by reason/module, no filter, no download, not saved after closing | unusable for real review |
| U5 | The `formulaTranslations` array (Clean/Adjusted/NeedsManualReview with diagnostics) is returned but **not shown** in the report step | user can't see which formulas need work |
| U6 | No persisted import report / history; nothing in the new app points at items needing manual attention | work is lost when the dialog closes |
| U7 | Long synchronous request (~80–110 s for cares) with a spinner only; Cancel disabled; no progress; risk of gateway timeout | poor experience on big files |
| U8 | Preview shows counts only; it doesn't say which formulas/lookups/summaries will be skipped or which fields lose settings | surprises after import |
| U9 | Create mode allows excluding tables only — no per-element choices; Update mode exists only inside the same dialog (needs `apps:create` even though Update needs app admin) and has no entry in App Settings | discoverability |
| U10 | No "Open app" link after success; no result summary per module | extra clicks |
| U11 | Mapping JSON (API) has no UI; Master/Child mapping not available (out of scope) | – |
| U12 | Forms: multi-page forms collapse, embedded related-record grids missing, choice display (radio/dropdown) lost | visible in every parent form |
| U13 | Reports: filters/grouping missing, column labels not applied | reports look different from Quickbase |

---

## 21. Prioritised recommendations

**P0 — data correctness / security (small, high value)**
1. Import **record filters** from `CustomAccessCriteria` (§19) — otherwise roles see everything.
2. Import **field-level permissions** (§18) and **report `RolePermissions`** → `AppRoleReport` (§12).
3. Apply `IsRequired`, `Description`, `DefaultValue`, `IsUnique` when creating fields (§5) — importer currently drops them.
4. Map Quickbase **icons** to the PowerBase catalog with a safe default (U1).
5. Skip the imported "Add X" URL formula when PowerBase already auto-creates the equivalent (§7), or skip the auto-creation for imports.

**P1 — fidelity**
6. Field settings: choices, decimals/format, link text, date options (§6).
7. Report filters and grouping (§11); summary matching criteria (§7).
8. Form rules: role conditions, DisplayMessage/AbortSave without target, live-value Change (needs PBL fields for message type/severity and value source) (§10).
9. Embedded report form elements; form pages/tabs; form settings (§8–9).
10. Map URL/Email/Phone/RichText formulas to their own `Formula_*` variants (§0).
11. Role functional permissions (`ManageUsers`, `EditApp`) (§16); app security/formatting settings (§1).
12. Read the `Formula` on Reference fields and **report** it; warn on `ConnectedData`.

**P2 — engine / platform decisions**
13. Allow Reference fields (and lookups of references) in formulas.
14. Multi-branch `If`, `Left/Right/NotLeft` with delimiters, `ToTimeOfDay`, `datetime` vars.
15. Allow `CombinedText` summaries over Reference fields.
16. Fix the code-page recovery so the 3 pages survive; split HTML/CSS/JS.
17. Dashboard / homepage converters (not needed for cares).

**UI**
18. Import report page: group skipped items by module/reason, filter, CSV export, formula-translation table, persisted history (U4–U6, U10).
19. Background job + progress for large files (U7); richer preview (U8); entry point in App Settings (U9).

---

## 22. How these numbers were produced

- Node/property inventory: C# harness calling `QblSerializer` on `cares.qbl.txt` (no files written to the repo).
- Value counts: same harness; Excel read directly from the `.xlsx` XML.
- "Converter reads": string-literal scan of `QblToPblConverter.cs` + `QblFieldTypeMap.cs`; spot-checked by reading the relevant methods.
- Import-side numbers: the last import report from a scratch tenant (before the final dependency-round change, so skipped counts are upper bounds).
