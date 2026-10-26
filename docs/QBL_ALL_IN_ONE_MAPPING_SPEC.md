# all_in_one.qbl → PowerBase — mapping required to keep every value

Date: 2026-10-21 · Source file: `all_in_one.qbl` (44.9 MB, QBL, **13 applications, 400 tables**)
Companion: [QBL_ALL_IN_ONE_MISSING_FEATURES.md](QBL_ALL_IN_ONE_MISSING_FEATURES.md) — what PowerBase cannot hold, by module.
Supersedes the cares-only [QBL_TO_PBL_MAPPING_SPEC.md](QBL_TO_PBL_MAPPING_SPEC.md) (which predates the importer work now in the code).

**How to read this.** Every row is a QBL value that *has a home in PowerBase* and must be mapped to keep it. Values with no home are in the companion file. Counts are occurrences across **all 13 apps** unless an app is named. Evidence: a line-level scan of the whole file (types, property keys, nested keys, query operators, formula functions) plus the converter/importer source as it is today.

Status — ✔ implemented in the importer today · ◐ partly · ✘ not implemented (target exists) · **PBL** = the PBL model needs a new property first · *(verify)* = confirm with the owning team.

---

## 0. What is in the file

### 0.1 Per-app inventory

| App | Tables | Data fields¹ | Formula | Lookup | Summary | Reference | Rel. | Forms (V2) | Rules | Table rpt | Summary rpt | Cal/Map | Charts | Dash. | Widgets | Roles | Vars | Code pg | Unsupp. |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| CARES Imports | 22 | 124 | 63 | 69 | 9 | 27 | 27 | 22 | 8 | 90 | 0 | 0 | 0 | 0 | 0 | 6 | 0 | 0 | 8 |
| CARES Time Sheets | 63 | 516 | 395 | 531 | 180 | 143 | 143 | 71 | 804 | 398 | 11 | 2 | 6 | 5 | 28 | 17 | 52 | 1 | 12 |
| CARES Training Tracking | 16 | 85 | 73 | 77 | 26 | 24 | 24 | 17 | 23 | 73 | 0 | 0 | 1 | 1 | 1 | 9 | 52 | 0 | 0 |
| CCR Timesheet Storage | 17 | 79 | 71 | 48 | 19 | 22 | 22 | 17 | 13 | 129 | 5 | 0 | 0 | 1 | 6 | 7 | 52 | 2 | 0 |
| CFTSS Billing Process | 14 | 55 | 20 | 28 | 8 | 12 | 12 | 14 | 2 | 50 | 1 | 0 | 0 | 1 | 0 | 6 | 52 | 0 | 0 |
| CFTSS Compliance Process | 74 | 467 | 370 | 309 | 122 | 119 | 119 | 87 | 648 | 414 | 11 | 1 | 11 | 4 | 35 | 20 | 52 | 2 | 3 |
| CFTSS Goals & Objectives | 8 | 34 | 22 | 28 | 21 | 8 | 8 | 8 | 0 | 31 | 0 | 0 | 0 | 1 | 0 | 3 | 0 | 0 | 0 |
| DSP Supervision | 33 | 306 | 218 | 247 | 106 | 79 | 79 | 10 | 107 | 207 | 4 | 0 | 2 | 0 | 0 | 14 | 54 | 2 | 0 |
| HR Onboarding Process | 30 | 425 | 297 | 289 | 127 | 57 | 57 | 32 | 161 | 198 | 3 | 2 | 4 | 10 | 19 | 24 | 0 | 1 | 0 |
| Ramapo Management | 19 | 78 | 93 | 75 | 11 | 28 | 28 | 0 | 0 | 81 | 0 | 2 | 0 | 0 | 0 | 5 | 3 | 16 | 0 |
| Serenity Database | 47 | 403 | 197 | 250 | 65 | 60 | 60 | 47 | 46 | 200 | 0 | 0 | 0 | 2 | 1 | 18 | 52 | 0 | 13 |
| VDB Database | 35 | 402 | 237 | 309 | 145 | 63 | 63 | 37 | 92 | 198 | 0 | 0 | 0 | 3 | 8 | 19 | 0 | 0 | 1 |
| VDB Financial Docs | 22 | 134 | 65 | 81 | 12 | 27 | 27 | 22 | 8 | 91 | 0 | 0 | 0 | 0 | 0 | 6 | 0 | 0 | 8 |
| **Total** | **400** | **3,108** | **2,121** | **2,341** | **851** | **669** | **669** | **384** | **1,912** | **2,160** | **35** | **7** | **24** | **28** | **98** | **154** | **369** | **24** | **45** |

¹ Data fields = scalar fields excluding formulas, lookups, summaries, references, report links, address sub-component shadow fields, unsupported fields and the 5 system fields (Record ID#, Date Created, Date Modified, Record Owner, Last Modified By — PowerBase seeds its own). Legacy `QB::Form` forms (402) are not counted; FormV2 is the live form (see §6).

### 0.2 Things that exist only in some apps (drive the per-app work in §13)

| Item | Where |
|---|---|
| Connected tables (`ConnectedData`) — 64 tables | Time Sheets 19, Compliance 8, DSP Supervision 10, Onboarding 5, CCR 7, Billing 4, VDB Database 4, Training 3, Goals 3, Serenity 1 |
| Dashboards — 28 (98 widgets) | Onboarding 10, Time Sheets 5, Compliance 4, VDB Database 3, Serenity 2, 1 each in Training, CCR, Billing, Goals |
| Home pages — 14 (+ 12 home-page widgets) | one per app (`Default Dashboard`), Onboarding `HOME`, Ramapo `Service Calls` |
| Code pages — 24 | Ramapo 16, CCR 2, Compliance 2, DSP Supervision 2, Time Sheets 1, Onboarding 1 (+1 rich-text page) |
| Calendar / Map reports — 5 / 2 | Calendar: Time Sheets 2, Compliance 1, Onboarding 1, Ramapo 1 · Map: Onboarding 1, Ramapo 1 |
| Charts — 24 (Pie 15, Gauge 6, Stacked bar 3) | Compliance 11, Time Sheets 6, Onboarding 4, DSP 2, Training 1 |
| Summary reports — 35 (+ 24 summary variables, 8 summary formulas) | Time Sheets 11, Compliance 11, CCR 5, DSP 4, Onboarding 3, Billing 1 |
| Report formula columns — 42 | Time Sheets 15, Compliance 9, CCR 15, Onboarding 1, Billing 1, Goals 1 |
| Unsupported fields (reference a deleted field) — 45 | Serenity 13, Time Sheets 12, Imports 8, Financial Docs 8, Compliance 3, VDB Database 1 |
| App variables — 369 | 52 each in 6 apps; DSP 54; Ramapo 3 |

---

## 1. App

| QBL (13 apps) | PowerBase target | Transform | Status |
|---|---|---|---|
| `Name`, `Description` | `App.Name`, `App.Description` | – | ✔ |
| `AppColor`, `AppIcon` (`!Var`) | `App.Color`, `App.Icon` | resolve `!Var`; QB icon name → PowerBase catalog id | ✔ (`QblIconMapper`) |
| `CurrencySymbol` (`$` ×13), `CurrencySymbolPosition` (`Between` ×13) | `AppFormattingSettings.Currency` | Between → before | ✔ |
| `NumberFormat.SeparatorStyle` (comma/period ×13), `SeparatorPlaces` | `Number.ThousandSeparator`, `DisplayPattern` | – | ✔ separator · ◐ places (`none` ×13 only) |
| `DateFormat` (`MM-DD-YYYY` ×13) | `Date.FormatString` | – | ✔ |
| `CanCopyApp` (Admin ×13), `CanExportData` (Everyone ×13), `DisableNonAdminSyncConnections`, `RequireAppToken` (true ×13), `Searchable` (false ×13) | `AppSecurityOptionsSettings` | invert where noted | ✔ |
| `Branding.AppHeader.BackgroundColor` | `AppBrandingSettings.BrandColorHex` | header colour → brand colour | ✔ |
| `TableOrder` | `AppTable.DisplayOrder` | index in list | ✔ |
| `Variables[]` (369) | `AppVariable` | names with `[ ] \` or `var_N` rejected → flag; value ≤ 500 | ✔ |
| `Pages[]` `QB::CodePage` (24) `Name`, `RawCode` | `Page` (Code) | whole blob → one HTML code page; needs tenant flag + `pages:code` | ✔ (one page, not split) |
| `RoleOrder` | `AppRole` order | list index | ✘ (PowerBase ranks all imported roles equal — *verify*) |
| `Manager` (`YSL18` ×13) | app owner | importing user becomes owner | n/a |
| `Branding.UseCustomHeaderAndFooter` (6 apps), `AppHeader`/`AppFooter` text & links | – | see missing list | – |

## 2. Table (400)

| QBL | PowerBase target | Transform | Status |
|---|---|---|---|
| `Name`, `Description` | `AppTable.Name/Description` | – | ✔ |
| `RecordNameSingular/Plural` | `SingularLabel/PluralLabel` | – | ✔ |
| `TableIconName` | `AppTable.Icon` | QB icon → catalog id | ✔ |
| `AliasHint` (`_DBID_…`) | `AppTable.Alias` | kept so `[_DBID_X]`/`Dbid("X")` formulas resolve | ✔ |
| `IncludeInTableBar` (false ×28) | `IsShowInBar` | – | ✔ |
| `SaveOnExit` (false ×19) | `IsAutoSaveOnLeave` | – | ✔ |
| `KeyField` (Record ID# ×30 explicit; others custom) | `KeyFieldId` | field ref → Id; applied before relationships | ✔ |
| `UseCustomRecordTitleField` + `RecordTitleField` (6) | `DisplayFieldId` | – | ✔ |
| `DefaultRecordPicker` (up to 3 fields) | `DefaultRecordPickerField1/2/3Id` | – | ✔ |
| `CustomDataRules.TurnedOn/Formula` (14 on) | `IsCustomDataRuleEnabled`, `CustomDataRule` | translate; untranslatable stored **switched off** with source text | ✔ |
| `DefaultReportSettings` — `DefaultSortOrder`, `DefaultColumnHeaderText` (Wrap ×400), `InlineEditing` (true ×400), `DefaultGroupDisplay` (Collapsed ×400), `DefaultRowNumberVisibility`, `DefaultFilters` | `AppTable.DefaultReportSettings` JSON | enum maps | ✔ |
| `DefaultReportSettings.DefaultRowHeight` (Short ×400), `ActionOnNewFieldAdd` (MakeDefault ×400), `DefaultColorCoding` | – | – | ✘ → missing list |
| `TableNameOverride` (1: "Activities") | display name | use as label only when `RecordNamePlural` empty | ✘ |
| `Searchable` (true ×400), `IndexFileAttachments` (true ×4) | – | – | ✘ → missing list |
| `TableHomePageSettings` (`SameForEveryone` 369 / `BasedOnRole` 31) | `AppRole.HomePageId` | `RoleViews` → role home page; depends on dashboards import (§10) | ✘ |
| `ConnectedData` (64) | – | imported as a plain local table; **must warn** | ◐ (verify warning is emitted) |
| `ReportGroups` (415) | – | informational | n/a |

## 3. Field — common properties

| QBL | PowerBase target | Transform | Status |
|---|---|---|---|
| `Label` | `AppField.Label` + `Name` | dedupe within table (numbered); reference/lookup/summary names also de-duplicated against system fields (fix 2026-10-21) | ✔ |
| `Comments` | `Description` | – | ✔ |
| `HelpText` | `HelpText` | – | ✔ |
| `IsRequired` (true ×~253) | `IsRequired` | – | ✔ |
| `IsUnique` (true ×~253) | `IsUnique` | – | ✔ |
| `DefaultValue` | `DefaultValue` | Checkbox Checked/Unchecked → true/false; `DefaultIsToday/DefaultIsNow` → dynamic default; User `CurrentUser` → `{"mode":"CurrentUser"}`; a named user cannot be carried | ✔ |
| `IsAuditable` (true ×few) | `IsAuditable` | – | ✔ |
| `IsSearchable` (false only) | `IsSearchable` | only "false" is honoured | ✔ |
| `AllowedInReports` (false only) | `IsReportable` | – | ✔ |
| `IsBold`, `DisplayWithoutWrapping`, `DefaultColumnWidth` | `Settings.displayBold/noWrap/columnWidth` | QBL `Width` (input chars) deliberately not carried | ✔ |
| field `RolePermissions` (Modify/View/None per role) | `AppRoleFieldPermission` + table `FieldAccessLevel=CustomAccess` | – | ✔ |
| `AllowDataCopy` (false on ≥ 200 fields) | – | no PowerBase equivalent *(verify)* | ✘ → missing list |
| `AddToAllNewReports` | – | – | ✘ → missing list |

### 3.1 Field-type map (all types found in the file)

| QBL type | Count | PowerBase type | Status |
|---|---:|---|---|
| Text | 1,463 | Text | ✔ |
| TextMultiline | 93 | TextMultiLine | ✔ |
| RichText | 19 | RichText | ✔ |
| TextMultipleChoice | 302 | SingleSelect | ✔ |
| MultiselectText | 20 | MultiSelect | ✔ |
| Number | 302 | Number | ✔ |
| Currency | 15 | Currency | ✔ |
| Percent | 1 | Percent | ✔ |
| Date / DateTime / TimeOfDay | 238 / 50 / 16 | Date / DateTime / Time | ✔ |
| Duration | 1 | Duration | ✔ |
| Checkbox | 435 | Boolean | ✔ |
| Email / PhoneNumber / URL | 64 / 78 / 49 | Email / Phone / Url | ✔ |
| FileAttachment | 55 | File | ✔ |
| Address (+ 6 sub-component shadow fields each) | 24 | Address | ✔ shadow fields folded into the composite |
| User | 27 | User | ✔ |
| Formula variants (Text, URL, RichText, Email, Phone, Numeric, Currency, Percent, Date, DateTime, TimeOfDay, Duration, Checkbox, User, MultiselectText) | 2,121 | `Formula_*` | ✔ type picked from the QBL type, not only the compiled result |
| Reference / Lookup / Summary / ReportLink | 669 / 2,341 / 851 / 687 | via relationships (§5) | ✔ (ReportLink auto-created) |
| RecordID, DateCreated, DateModified, RecordOwner, LastModifiedBy | 400 each | PowerBase's seeded system fields | ✔ (Record Owner labelled "Record Entity"/"Created By" in 18 tables → keep as description) |
| `Unsupported` (explanation: "reference to a deleted or missing field") | 45 | – | ✔ skipped and reported with Quickbase's explanation |

## 4. Field — per-type settings

| QBL type · property | PowerBase `Settings` | Transform | Status |
|---|---|---|---|
| Text/Multiline `MaxLength` (always 0 in this file) | `validation.maxLength` | 0 → none | ✔ |
| Choice `InputOptions.ListChoices` (301) | `options` | keep order; `ChoiceOrder: Alphabetical` (66) → sorted | ✔ |
| Choice `InputOptions.InputType: FromAnotherField` (1) | – | – | ✘ → missing list |
| Choice `AllowCreateChoices` (true ×35) | – | – | ✘ → missing list |
| Number/Percent/Currency `DecimalPlaces` (FloatingPoint / 0 / 2), `NumberFormat.SeparatorStyle`, `Units`, `DoesTotal`, `DoesAverage`, `TreatBlankAsZero` | `decimals`, `separator`, `unitsDescription`, `showTotalInReports`, `showAverageInReports`, `treatBlankAsZero` | FloatingPoint → null | ✔ |
| Currency symbol/position (incl. formula currency) | `symbol`, `position` | Between → inherits app position (stored) / `betweenSignAndNumber` (formula) | ✔ |
| Date `ShowMonthAsAlpha`, `HideCurrentYear`, `ShowDayOfWeek` | `showMonthName`, `hideYearIfCurrent`, `showDayOfWeek` | – | ✔ |
| DateTime `ShowTime` | `showTime` | – | ✔ |
| `ShowTimeZone` (false ×all) | – | nothing to carry (always false) | n/a |
| TimeOfDay `Use24HourClock`, `DisplayAs` (HH:MM) | `use24Hour`, `format` | – | ✔ |
| Duration `DisplayUnits`, `DecimalPlaces`, totals | `display`, `decimals`, … | unit-name map | ✔ |
| Email `LinkText`, `DisplayAs` (FullEmailAddress ×all) | `linkText`, `showFullAddress` | – | ✔ |
| Email `DefaultDomain`, `SortByDomain`, `EnableEmailAll` (all default) | – | nothing set in this file | n/a |
| URL `LinkText`, `DisplayAsButton`, `EmbedAsIframe`, `Target` (SamePage/NewWindow/Popup) | `linkText`, `displayAsButton`, `embedAsIframe`, `openTarget` | – | ✔ |
| URL `ButtonColor` (626 URL formulas + 1), `Abbreviate` (11), iframe width/height (4) | – | – | ✘ → missing list |
| File `LinkText`, `ShowAsImages`, `RevisionsToKeep` (3 / All), `AllowPromoteVersion` | `linkText`, `showImagePreview`, `revisionLimit/keepAllRevisions`, `allowOlderVersionsAsCurrent` | – | ✔ |
| File `UseNewBrowserWindow` (false ×2), `AllowOpenAccess` (true ×1) | – | – | ✘ → missing list |
| Checkbox `ShowAsCheckmark` (true ×all), `DoesTotal` | `showYesNo` (inverted), `showTotalsRow` | – | ✔ |
| User `DisplayAs` (LastNameFirstName ×all) | `displayAs` = LastFirst | – | ✔ |
| User `Choices`/`UserSet` | – | users are per tenant; reported | n/a (reported) |
| Phone `HasExtension` (true ×65), `IsInternational`, `DefaultCountry` (US ×all) | – | – | ✘ → missing list |
| Address `Format` (US ×19 / International ×5), `DefaultMapType`, `InputWidth` | – | – | ✘ → missing list |
| RichText `MaxLength`, `Height`, `Width`; `AllowMentions` (false) | `maxLength` | – | ✔ maxLength |
| Text/Multiline `Height` (rows) | – | – | ✘ |
| Text `EditHistory` (`Enable` false ×1,463; `PrependHistory` true ×61 but inactive) | – | not enabled anywhere → nothing to carry | n/a |

### 4.1 Formula fields (2,121)

| QBL | PowerBase | Transform | Status |
|---|---|---|---|
| All `*::Formula` types | `Formula_Text/Number/Date/DateTime/Time/Duration/Bool/User/MultiSelect/Url/Email/Phone/RichText` | variant from QBL type; display settings copied | ✔ |
| Formula text (2,855 formula blocks incl. report/rule/data-rule formulas) | `FormulaExpression` | dialect rewrite: `[_DBID_X]` → `Dbid("X")`, numeric `[1226]` / `_fid_N` → field names via source-`Id` map, Quickbase-only syntax flagged | ✔ translator · outcome per formula reported (`FormulaTranslations`) |
| Function coverage | — | **all 186 distinct called names resolve** except `UserRoles` (5 calls) — the rest of the unmatched names in the scan are words in formula comments | ✔ except `UserRoles` ✘ |
| Formula `Reference` fields (97 formula-driven references; see §5) | – | – | ✘ |

## 5. Relationships, Reference, Lookup, Summary

| QBL | PowerBase target | Transform | Status |
|---|---|---|---|
| `Relationship::Child.Parent` (669) | `Relationship` | – | ✔ |
| Reference `Label`, `IsRequired` (22), `DefaultValue` | reference field | – | ✔ |
| Reference `Proxy` (485) | `Relationship.ProxyFieldId` (first lookup) | – | ◐ |
| Reference `DisplayType` (Number 358, Text 157, User 57, formulas 95) | reference key type follows the parent key field | – | ✔ implicit |
| Reference `ConditionalValues` (81) | `ReferenceSettings.FilterConditions` / `FilterTree` | QB query → filter group | ✘ |
| Reference **`Formula`** (97) | – | relationship key computed by formula (e.g. current-user record id) | ✘ → missing list |
| Reference `DisplayAsLinkToParent` (true ×582), `UseRecordPickerForGridEdit` (false ×all) | – | – | ✘ / n/a |
| Lookup `Lookup.TargetField` (2,341; 3 are `None`) | `AddLookupFields` (`SourceFid`, label, `SourceSubField`) | retried in rounds until the source exists; address sub-component → composite + sub-field | ✔ |
| Lookup display props (Width, DisplayAs, LinkText, ShowTime…) | lookup `Settings` | – | ◐ (verify) |
| Summary `Function` (CombinedText 280, Maximum 209, AnyRelatedRecords 140, Count 131, Total 60, DistinctCount 7) | `SummarySettings.Function` | Count, Sum, Max, Exists, CombinedText, DistinctCount | ✔ |
| Summary `FieldToSummarize` (582) | `TargetFid` | – | ✔ |
| Summary **`Query`** (615 non-trivial) | `SummarySettings.FilterTree` | QB query → `FilterGroup` (see §12 operators); untranslatable → summary skipped & reported (would otherwise count every child) | ✔ |
| Summary `DisplayAsLinkToChildren` (true ×820), `DrillDownReport`, `EvaluateOnlyOnChange` (false) | – | – | ✘ → missing list |
| CombinedText delimiter/sort/distinct | `Delimiter`, `SortFid`, `DistinctValues` | – | ◐ (verify) |
| Cross-application relationships | – | none in this file (CrossAppAccess = None ×13) | n/a |

## 6. Forms (384 FormV2; 402 legacy `QB::Form` are skipped — see missing list)

| QBL | PowerBase target | Transform | Status |
|---|---|---|---|
| `FormV2.Name` | `Form.Name` | – | ✔ |
| `ShouldAutoAddFields` (true ×376), `ShouldShowBuiltInFieldsFooter`, `AlwaysTabsForViewRecord` (true ×61), `AllowedSaveActions`, `DisplayPagesAs` (Tabs ×383 / Steps ×1) | `AutoAddNewFields`, `ShowBuiltInFields`, `AlwaysTabsOnView`, `SaveOptions`, `PageNavMode` | rename | ✔ |
| `Pages[].Name` (566) / `Color` | `FormPage.Heading` / – | page tier kept; colour has no home | ✔ name · ✘ colour |
| `Sections[].Title`, `IsCollapsedByDefault`, `ShowColumnDividers`, `IsPinned` | `FormSection` | – | ✔ |
| `Columns[].Title`, `LayoutWidth`, `BackgroundColor`, `ShowBackgroundColor` (1,404) | `FormSectionBlock.Heading/Width/BackgroundColor` | – | ✔ |
| Element `Field` (15,154) | `FormElement.AppFieldId` | by field name | ✔ |
| `LabelMode` (Default 13,981 / Custom 898 / Hidden 275), `LabelOverride` | `LabelMode`, `CustomLabel` | – | ✔ |
| `HelpText`, `Width` (px / %), `ModesToShowIn`, `ForceReadOnlyOnForm` (315), `ForceRequireOnForm` (337) | element overrides | – | ✔ |
| `MultipleChoiceDisplayAs` (Dropdown ×504) | `FormElement.DisplayAs` | enum map | ✔ |
| `FilterChoicesBy` (5,120; 5,001 Default), `ShouldValidate` (10 true), `HideMap` (39) | – | – | ✘ / n/a |
| Element `Report` (625: embedded report) — `ReportLinkField`, `HideToolbar` (115), `AllowCreateRecords` (18 false), `ShouldOpenInEmbedded`, `SubForm` (202), `Height` (20) | `FormElement` Report (`EmbeddedReportRef`, `ShowToolbar`, `AllowCreateRecords`, `OpenInSubForm`) | needs the report created first (reports run before forms) | ✔ |
| Element `RichText` (159) | static text element | – | ✔ |
| Element `Group` (127 groups) | `GroupRef` | – | ✔ |
| Table `Forms.Properties.RoleDefaults` | `AppRoleTableFormOverride` | per-role form | ✘ |
| Legacy `QB::Form` (402) — ~9,500 elements | – | skipped; emit **one** informational line per table | ✔ skipped |

## 7. Form rules (1,912 — Time Sheets 804, Compliance 648, Onboarding 161)

| QBL | PowerBase target | Transform | Status |
|---|---|---|---|
| `Name`, `Description` | `FormRule.Name/Description` | – | ✔ |
| `Disabled` (true ×403) | `IsActive` inverted | keep disabled rules | ✔ |
| `RunOn` (RecordOpenSaveOrChange 1,842 / RecordSave 58 / RecordSaveAfterValidating 5 / RecordOpenForEdit 7) | `RunTrigger` | AnyChange / Save / SaveAfterValidating / EditOrAdd | ✔ |
| `FireOnlyOnChange` (true ×1,710), `Tags` (39 with tags) | – | – | ✘ |
| `When` condition group (All/Any, nested groups 1,977) | `ConditionLogic` + conditions | flatten one level; deeper nesting flagged | ✔ / ◐ |
| Condition `Field` (3,005): IsEqualTo 1,676, IsNotEqualTo 953, Contains 115, Before 76, After 58, IsGreaterThan 50, ≥ 33, < 32, ≤ 22, OnOrAfter 14, OnOrBefore 12, DoesNotContain 12, HasTrueValue 11, Includes 6, DoesNotHaveTrueValue 3, `Changed` 2 | `FormRuleCondition` (operator map) | – | ✔ (verify `Changed`) |
| Condition `Role` (567: IsInRole 422, IsNotInRole 164) | `ConditionKind=role` | role ref → role | ✔ (verify) |
| Condition `FormulaIsTrue` (176) | `IsExpressionMode` + `ExpressionText` | formula translated | ✔ |
| Condition `DaysAgo` (12), `Range` (6), `Relative` (13), `IsDuring` (5), `IsNotDuring` (1) | – | no operator | ✘ → missing list |
| Actions: Show 988, Require 426, Change 402, Hide 345, Unrequire 255, MakeReadonly 228, SetLabel 30, MakeEditable 26 | `FormRuleAction.ActionType` | map names | ✔ |
| Action `Change` literal / live value (`Value: !Ref`) | `ChangeValue` / `ValueSource` + `SourceFieldId` | – | ✔ |
| Action `DisplayMessage` (418): `MessageType`, `MessageLevel`, `FieldTarget` | `MessageType`, `MessageSeverity`, target | allow message without target | ✔ |
| Action `AbortSave` (336) `Message` | `PreventSave` + text | allow no target | ✔ |
| Targets (Field / Section / Column / Page) | `TargetElement/Section/Block` | Page target | ✔ / ✘ page |
| Legacy `FormRule` (50) | – | skipped with legacy forms | ✔ skipped |

## 8. Reports — table reports (2,160)

| QBL | PowerBase target | Transform | Status |
|---|---|---|---|
| `Name`, `Description`, `ShowDescription` (9) | report name / description / `ShowDescriptionOnReportPage` | duplicate names within a table → first kept (warned) | ✔ |
| `Columns` (list; `Default` ×987) | `Columns` (Fids) | Default → all fields | ✔ |
| `SortAndGroup[]` (758 report-field entries): sort + group flag + group mode | `SortFields`, `IsGroup`, `GroupMode` | – | ✔ |
| `Filters.Criteria` (478 non-default) | `FilterTree` | QB query → conditions; `_curuser_` (3,665 uses in file) → current user; `_ask1_` (107) → "ask at run time"; `_fid_N` → compare to field | ✔ |
| `Filters.DynamicFields` (Default ×2,113), `AllowQuickSearch` | `DynamicFilterType`, `AllowQuickSearch` | – | ✔ |
| `HideTotals` (20), `GroupDisplay`, `ColumnHeaderText`, `AllowEditingRecords` (32 off), `AllowViewingRecords` (9 off), `DisableDeleteCheckbox`, `InlineEditing` (7 No), `RowNumberVisibility` | report options | inverted where noted | ✔ |
| `DisplayOnlyNewOrChangedRecords` (402 true), `ShowSummaryTable` (11 true), `HideMainTable` (7 true) | – | – | ✘ → missing list |
| `ColorCoding.ColorBy` (Default 463, Formula 23, RowValue 5) + `ColorFormula` (28) | – | – | ✘ → missing list |
| `ColumnsUI` (label/alignment/pin) | – | – | ✘ |
| `DefaultRowHeight`, `Format` | – | – | ✘ |
| Report `RolePermissions` (2,160 × roles) | `VisibleToRoleIds` | role ref → role | ✔ |
| `QB::Report::GridEdit` | table report with grid edit | – | ✔ |

### 8.1 Summary reports (35), report formulas, charts, calendar, map

| QBL | PowerBase target | Status |
|---|---|---|
| `QB::Report::Summary` — `GroupingAndCrosstabs.Rows`, `Crosstabs` (None ×26), `Summary::Column` (62: CountOfRecords 23, others Total 37), `HideTotals`, `Sort`, `DrilldownReport` | Summary report (`RowGroupLevels`, `Aggregations`) | ✔ type exists (verify column/aggregation mapping on these 35) |
| `Report::SummaryFormula::Numeric/NumericPercent/Text` (8), `Report::SummaryVariable` (24: Total 23, DistinctCount 1) | – | ✘ (formula aggregates) → missing list |
| `Report::Formula::Checkbox/Date/Numeric/NumericPercent/Text/TimeOfDay` (42 report-level formula columns) | – | ✘ → missing list (could become table formula fields) |
| `QB::Chart::Pie` (15), `StackedBar` (3), `Gauge` (6) — `DataDetails`, `Series`, `DataLabels`, `Sorting`, `GoalLine`, `GaugeLimits`, `Colors`, `Filters` | **Chart report** (`Bar/StackedBar/Pie/Donut/Gauge…`) | ✘ not converted today — target exists (Pie→Pie, StackedBar→StackedBar, Gauge→Gauge) |
| `QB::Report::Calendar` (5), `QB::Report::Map` (2) | – | ✘ → missing list |

## 9. Dashboards, home pages, rich-text page

| QBL | PowerBase target | Status |
|---|---|---|
| `QB::Dashboard` (28) + `Tab` (49) | `Page` (dashboard) with `DashboardDefinition` | ✘ not converted — target exists |
| Widget `Table` (22) / `Summary` (10) / `Chart` (15) → `Report` | `Report` widget pointing at the imported report / chart report | ✘ |
| Widget `Button::OpenReport` (13) / `Button::OpenLink` (15) | `ButtonBar` | ✘ |
| Widget `RichText` (11) → `Text` · `WebPage` (3) → `WebPage` · `Search` (1) → `Search` · `Spacer` (2) → `Spacer` | matching widget | ✘ |
| Widget `Kpi` (6) | – | ✘ → missing list |
| Widget layout `X/Y/Width/Height`, `Color`, `Icon`, `Alignment`, `OpenInNewWindow` | widget position/size; colour/icon/alignment | ✘ (grid sizes differ — needs scaling rule) |
| `QB::HomePage` (14) + widgets (`Report`, `RichText`, `WebPage`, `Button`, `ButtonBar`) | `Page` + `AppRole.HomePageId` | ✘ |
| `QB::RichTextPage` (1) | `Page` (text) | ✘ |
| Code pages (24) | `Page` (Code) | ✔ |

## 10. Roles, table permissions, field permissions, record filters

| QBL | PowerBase target | Transform | Status |
|---|---|---|---|
| Role `Name`, `Description`, `Default` (13 default) | `AppRole.Name/Description/IsDefault` | seeded Administrator/Viewer/Participant adopted | ✔ |
| `ManageUsers` (43), `EditApp` (28) | permission bundles (`users:*`; `apps:update`, `roles:manage`, `tables:*`, `fields:*`, `forms:*`, `reports:*`) | – | ✔ |
| `DisableAccess` (false ×154), `RestrictAccessToNewTablesForThisRole` (1) | empty permission set / `ViewScope=None` for new tables | – | ✔ |
| Table `RolePermissions`: `CanViewRecords/CanModifyRecords.When` — Always 7,206 / Never 4,162 / **CustomRule 418** / **UserInField 8** | `ViewScope` / `ModifyScope` | CustomRule + criteria → AllRecords + **record filter**; Always/Never direct | ✔ CustomRule · `UserInField` ✘ |
| `CustomAccessCriteria` (10,657 expressions; only the 418 CustomRule ones are live) | `AppRoleRecordFilter` (`FilterJson`) | see §12 | ✔ |
| `CanAddRecords`, `CanDeleteRecords`, `CanSaveCommonReports`, `CanEditFieldProperties` (5,897 each) | `CanAdd`, `CanDelete`, `CanSaveSharedReports`, `CanEditFieldProperties` | – | ✔ |
| `CanRefreshConnectedData` (940) | – | no connected tables | ✘ |
| Field `RolePermissions` | `AppRoleFieldPermission` | see §3 | ✔ |
| `AppUI.Hide*` (154 roles: HideSettings 71, HideHelp 65, HideUsers 59, HideTestAs 58, HideFavorites 41, HideSearch 17) | – | – | ✘ → missing list |
| `TableUI.Global.Hide*` (3,490 role×table: HideInBar 2,920, HideGridEdit 576, HideNewRecord 487, HideCreateCustomizeReport 42, HideImportExport 40, HidePersonalSettings 32, HideEmail 25, HidePrint 17, HideSaveSpreadsheet 14) | `records:create`, `reports:create`, … where a permission code exists | partly: `HideNewRecord`→ no `CanAdd`; `HideGridEdit` → report grid edit; the rest | ✘ |

## 11. Source-id and housekeeping mappings needed for all of the above

| Need | Mapping | Status |
|---|---|---|
| FID → field | every QBL field carries `Id:`; used for `[1226]`/`_fid_N` in formulas and `{'8'.EX.'x'}` in criteria | ✔ |
| Logical refs (`$Table_X`, `$Field_X`, `$Role_X`, `$App_X`) | table-qualified; **refs repeat across apps** (e.g. `$Table_Time_Sheets`, `$Field_Name`) → all lookups are per-application; `meta.ImportSourceRef` is per new app id | ✔ |
| `!Var` / `ParameterDefinitions` | resolved at parse time; one definition set shared by the 13 apps (`App_Name_N`, `RecordSet_ExternalSetId_N`, `DataSource_Quickbase_ConnectionId_N`) | ✔ |
| Icon names | one shared dictionary | ✔ |
| Connected data sources / record sets (15 + 45 top-level resources, 8,000+ `ConnectedColumn` nodes) | not imported; connected tables arrive as ordinary tables | ◐ |
| Reference-field name clashing with a system/another field (e.g. `Last Modified By` → `User Settings`, Time Sheets) | renamed `Name (2)` + warning | ✔ (added 2026-10-21) |

## 12. Quickbase query operators to translate (criteria, summary queries, record filters)

Observed across the whole file (`{'FID'.OP.'value'}`; 13,589 operator uses):

| QB op | Uses | Meaning | PowerBase operator | Status |
|---|---:|---|---|---|
| `EX` / `XEX` | 8,113 / 646 | is / is not | `eq` / `ne` | ✔ |
| `TV` / `XTV` | 3,669 / 4 | true value | `eq true` / `eq false` | ✔ |
| `CT` / `XCT` | 388 / 143 | contains / not | `contains` / `notContains` | ✔ |
| `HAS` / `XHAS` | 120 / 6 | multi-value contains | `includes` / `notIncludes` | ✔ |
| `SW` / `XSW` | 4 / 3 | starts with | `startsWith` / `notStartsWith` | ✔ |
| `GT` `GTE` `LT` `LTE` | 29 / 14 / 19 / 2 | compare | `gt` `gte` `lt` `lte` | ✔ |
| `BF` `AF` `OBF` `OAF` | 116 / 71 / 70 / 88 | date before/after (or on) | `lt` `gt` `lte` `gte` | ✔ |
| `IR` / `XIR` | 36 / 38 | in range / not in range (relative dates) | none | ✘ → missing list |
| `DEX` | 2 | unrecognised in this file — **verify meaning** | – | ✘ |
| Values: `_curuser_` 3,665 · `_ask1_` 107 · `_fid_N` (compare to field) ≥ 80 · empty 227 | | | `UseCurrentUser` · `ValueMode=ask` · `ValueMode=field` · null | ✔ |

---

## 13. Per-application mapping notes

What each app needs beyond the common mappings. "Blockers" are items that make the app import incomplete today.

| App | Specific mapping work | Blockers / notable gaps |
|---|---|---|
| **CARES Imports** (22 tables) | 27 relationships; 6 roles; 154 role criteria; formulas 63 | 8 unsupported fields (deleted refs); 1 reference formula; no dashboards |
| **CARES Time Sheets** (63) | 804 form rules (largest set → verify nesting/`DaysAgo`); 17 roles with 2,010 criteria expressions; 52 variables; 5 dashboards (28 widgets); 6 pie charts; 11 summary reports; 15 report formula columns; 2 calendars; 1 code page; Address ×4 | **Reference field "Last Modified By" clash (fixed)**; 19 connected tables; 25 formula-driven references; 12 unsupported fields |
| **CARES Training Tracking** (16) | 9 roles; 52 variables; 1 dashboard + 1 chart widget; 1 pie chart | 3 connected tables; 1 reference formula |
| **CCR Timesheet Storage** (17) | 7 roles; 5 summary reports; 2 code pages; 1 dashboard (6 widgets: buttons + summary); 15 report formula columns incl. checkbox | 7 connected tables; 6 formula-driven references |
| **CFTSS Billing Process** (14) | 6 roles; 1 summary report; 1 TimeOfDay report formula | 4 connected tables |
| **CFTSS Compliance Process** (74) | 648 form rules; 20 roles with 2,808 criteria; 11 summary reports + 24 summary variables + 8 summary formulas; 11 charts (6 gauges); 4 dashboards (35 widgets incl. **6 KPI**); calendar; 2 code pages | **6 KPI widgets**; 8 connected tables; 29 formula-driven references; 3 unsupported |
| **CFTSS Goals & Objectives** (8) | 3 roles; 1 dashboard (empty) | 3 connected tables |
| **DSP Supervision** (33) | 107 rules; 14 roles; 4 summary reports; 2 pie charts; 2 code pages; 54 variables | 10 connected tables; 12 formula-driven references |
| **HR Onboarding Process** (30) | 24 roles (many vendor roles with criteria 1,328); **10 dashboards** (19 widgets: RichText 8, Table 6, Chart 4, WebPage 1); home page with 3 rich-text + 1 web-page widget; 1 rich-text page; 1 code page; calendar; **map**; 4 charts | map report; 5 connected tables; 8 formula-driven references |
| **Ramapo Management** (19) | **16 code pages** (JS/HTML helpers); home pages (4 buttons, 2 button bars, 2 report widgets); calendar; **map**; no forms (0 FormV2) | map report; 2 formula-driven references; code pages need tenant flag + `pages:code` |
| **Serenity Database** (47) | 18 roles, 1,611 criteria; 2 dashboards; 46 rules | 13 unsupported fields; 1 connected table |
| **VDB Database** (35) | 19 roles; 92 rules; 3 dashboards (8 widgets: 5 table, 1 search, 1 rich text, 1 web page) | 1 unsupported field; 4 connected tables |
| **VDB Financial Docs** (22) | 6 roles; 8 rules | 8 unsupported fields (deleted refs) |

---

## 14. Suggested order of mapping work (largest value kept first)

| # | Work | Why / size |
|---|---|---|
| 1 | **Multi-app import hardening** — name-clash/dup handling, per-app report, `ReferenceFieldName` clash rename (done) | all 13 apps; one Time Sheets app was blocked today |
| 2 | **Dashboards + home pages → `Page`/widgets**, role home page | 28 dashboards, 14 home pages, 98 widgets (most visible gap after reports) |
| 3 | **Charts → chart reports** (Pie/StackedBar/Gauge) and dashboard chart widgets | 24 charts — direct PowerBase equivalents |
| 4 | **Summary-report completeness** (aggregations, summary variables/formulas) | 35 summary reports, 24 variables |
| 5 | Remaining field settings with a home: phone/address/choice extras only if product adds them; otherwise report | see missing list |
| 6 | Form-rule `FireOnlyOnChange`, tags; page colour | cosmetic |
| 7 | Table `RoleViews` → role home page; `TableNameOverride` | depends on #2 |
| 8 | Role `UserInField` view/modify scope; `RoleOrder` | 8 / 154 |
| 9 | Report-formula columns → hidden formula fields on the table (optional) | 42 columns |
| 10 | Warn on every connected table (64) and every skipped legacy form (one line per table) | transparency |
