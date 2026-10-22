# QBL → PBL Mapping Spec — everything required to keep the values

Date: 2026-10-19 · Evidence: `cares.qbl.txt` property inventory + current converter/importer + PowerBase domain/command models.
Companion list of things PowerBase cannot hold: [POWERBASE_MISSING_FEATURES.md](POWERBASE_MISSING_FEATURES.md).
Overview of gaps: [QBL_TO_PBL_GAP_ANALYSIS.md](QBL_TO_PBL_GAP_ANALYSIS.md).

**This list contains only values that have a home in PowerBase.** Each row says where the value comes from in QBL, where it must land, the transform, and today's status:

- ✔ imported today · ◐ partly · ✘ not imported (target exists) · **PBL** = PBL model needs a new property first · *(count)* = occurrences in cares.qbl.

---

## 1. App

| QBL | PBL (today / needed) | PowerBase target | Transform | Status |
|---|---|---|---|---|
| `Name` | `PblApp.Name` | `App.Name` | – | ✔ |
| `Description` | `PblApp.Description` | `App.Description` | – | ✔ |
| `AppColor` (`!Var` → hex) | `PblApp.Color` | `App.Color` | resolve `!Var` via ParameterDefinitions | ✔ |
| `AppIcon` (`!Var` → "Application") | `PblApp.Icon` | `App.Icon` | **QB icon name → PowerBase icon-catalog id**, default `building`/`folder` when unmapped | ◐ stored raw, renders broken |
| `CurrencySymbol` | **PBL** `App.Formatting.CurrencySymbol` | `AppFormattingSettings.Currency.Symbol` | – | ✘ |
| `CurrencySymbolPosition` (Before/Between/After) | **PBL** | `Currency.Position` | Between→Before (with space) | ✘ |
| `NumberFormat.SeparatorStyle` (CommaGroupingPeriodDecimal / PeriodGroupingCommaDecimal) | **PBL** | `Number.ThousandSeparator` | `,` or `.` | ✘ |
| `NumberFormat.SeparatorPlaces` (none/2/3) | **PBL** | `Number.DisplayPattern` | 3→Standard, 2→Indian-style, none→no grouping | ✘ |
| `DateFormat` (MM-DD-YYYY, DD-MM-YYYY, YYYY-MM-DD…) | **PBL** | `Date.FormatString`, `Date.Separator` | split pattern/separator | ✘ |
| `CanCopyApp` (Admin/Everyone) | **PBL** `App.Security` | `AppSecurityOptionsSettings.AllowNonAdminsToCopy` | Everyone→true | ✘ |
| `CanExportData` | **PBL** | `AllowNonAdminsToExport` | Everyone→true | ✘ |
| `DisableNonAdminSyncConnections` | **PBL** | `AllowNonAdminsToConnect` | inverted | ✘ |
| `RequireAppToken` | **PBL** | `RequireAppTokens` | – | ✘ |
| `Searchable` | **PBL** | `HideFromPublicSearch` | inverted (verify meaning) | ✘ |
| `Branding.AppHeader.BackgroundColor` | **PBL** `App.Branding` | `AppBrandingSettings.BrandColorHex` | approximate (header colour → brand colour) | ✘ |
| `TableOrder` (list of table refs) | **PBL** order on `PblTable` | `AppTable.DisplayOrder` | index in list | ✘ |
| `Variables[].Name/Value` | `PblVariable` | `AppVariable` | name rules (no `[ ] \`, not `var_N`), value ≤ 500 | ✔ (52) |
| `Pages[]` `QB::CodePage` `Name`/`RawCode` | `PblPage` | `Page` (type Code, `CodeHtml`) | split RawCode into HTML/CSS/JS if possible | ◐ parser drops them (3) |
| `Manager` | – | app owner/admin `AppUser` | importing user becomes owner; optionally add manager if a matching user exists | n/a |

## 2. Table

| QBL | PBL | PowerBase target | Transform | Status |
|---|---|---|---|---|
| `Name` | `PblTable.Name` | `AppTable.Name` | – | ✔ |
| `Description` | `Description` | `Description` | – | ✔ |
| `RecordNameSingular/Plural` | `SingularLabel/PluralLabel` | same | – | ✔ |
| `TableIconName` *(95)* | `Icon` | `AppTable.Icon` | **QB icon name → catalog id** (43 distinct names in cares: Folder, Personal Record, Spreadsheet, Edit, Table Header, Group, Envelope, Calendar Year, Building…) | ◐ stored raw |
| `IncludeInTableBar` (false ×5) | **PBL** `IsShowInBar` | `AppTable.IsShowInBar` | – | ✘ |
| `AliasHint` (`_DBID_LEA`) | `AliasHint` | `AppTable.Alias` | keep the alias (same `_DBID_NAME` pattern) so `[_DBID_X]` formulas stay valid | ◐ rewritten to `Dbid("…")` instead |
| `KeyField` | **PBL** `KeyFieldRef` | `AppTable.KeyFieldId` | field ref → Id | ✘ |
| `RecordTitleField`, `UseCustomRecordTitleField` *(2)* | **PBL** | `AppTable.DisplayFieldId` | – | ✘ |
| `DefaultRecordPicker` | **PBL** | `DefaultRecordPickerField1/2/3Id` | first three fields | ✘ |
| `CustomDataRules.TurnedOn` / `.Formula` *(1)* | **PBL** | `IsCustomDataRuleEnabled`, `CustomDataRule` | translate formula | ✘ |
| `SaveOnExit` | **PBL** | `IsAutoSaveOnLeave` | – | ✘ |
| `DefaultReportSettings.DefaultSortOrder` | **PBL** | `AppTable.DefaultReportSettings` (JSON) | field ref → Fid | ✘ |
| `DefaultReportSettings.DefaultColumnHeaderText`, `InlineEditing`, `DefaultGroupDisplay`, `DefaultRowNumberVisibility`, `DefaultFilters.AllowQuickSearch/DynamicFields` | **PBL** | same JSON (column header text, grid edit, group collapsed, row numbers, quick search, dynamic filters) | enum maps | ✘ |
| `ReportGroups` | skipped | – | informational only | n/a |

## 3. Field — common properties

| QBL | PowerBase target | Transform | Status |
|---|---|---|---|
| `Label` | `AppField.Label` + `Name` | dedupe label (numbered) | ✔ |
| `Comments` *(55)* | `AppField.Description` | – | ✘ (importer ignores) |
| `HelpText` *(2)* | `AppField.HelpText` | – | ✘ |
| `IsRequired` *(55)* | `AppField.IsRequired` | – | ✘ (read, then dropped by importer) |
| `IsUnique` *(45)* | `AppField.IsUnique` | – | ✘ |
| `DefaultValue` *(90)* | `AppField.DefaultValue` | per-type: Checkbox Unchecked/Checked→false/true; text as-is; `CurrentUser` for user fields | ✘ |
| `IsAuditable` | `AppField.IsAuditable` | – | ✘ |
| `IsSearchable` | `AppField.IsSearchable` | – | ✘ |
| `AllowedInReports` | `AppField.IsReportable` | – | ✘ |
| `IsBold` | `Settings.DisplayBold` | – | ✘ |
| `DisplayWithoutWrapping` | `Settings.NoWrap` | – | ✘ |
| `Width` / `DefaultColumnWidth` | `Settings.ColumnWidth` | – | ✘ |
| `RolePermissions` on field *(22 fields)* | `AppRoleFieldPermission` (+ table `FieldAccessLevel = CustomAccess`) | View/Modify/None per role | ✘ |

## 4. Field — per-type settings (all ✘ today; `PblField.Settings` is never set)

| QBL type | QBL property | PowerBase `Settings` property | Transform |
|---|---|---|---|
| Text / Multiline | `MaxLength` | `Validation.MaxLength` | 0 → null |
| Rich text | `MaxLength`, `Width` | `RichTextSettings.MaxLength/ColumnWidth` | – |
| Choice (38) | `InputOptions.ListChoices` / `Choices` | `SelectSettingsBase.Options` | keep order; `ChoiceOrder: Alphabetical` → sort |
| Choice | `MaxLength` | `MaxLength` | – |
| Number / Percent / Currency | `DecimalPlaces` (FloatingPoint / n) | `NumericSettings.Decimals` | FloatingPoint → null |
| | `NumberFormat.SeparatorStyle` | `Separator` | `,` / `.` |
| | `Units` | `UnitsDescription` | – |
| | `DoesTotal`, `DoesAverage` | `ShowTotalInReports`, `ShowAverageInReports` | – |
| | `TreatBlankAsZero` | `TreatBlankAsZero` | – |
| | Currency symbol/position | `Symbol`, `Position` | – |
| | `DisplayAs` | `DisplayAs` | – |
| Date | `ShowMonthAsAlpha`, `HideCurrentYear`, `ShowDayOfWeek` | `ShowMonthName`, `HideYearIfCurrent`, `ShowDayOfWeek` | – |
| Date/Time | `ShowTime` | `ShowTime` | – |
| Time of day | `Use24HourClock`, `DisplayAs` (HH:MM / HH:MM:SS) | `TimeSettings.Use24Hour`, `Format` | – |
| Duration | `DisplayUnits`, `DecimalPlaces`, `DoesTotal/Average`, `TreatBlankAsZero` | `DurationSettings.Display`, `Decimals`, `ShowTotalInReports`, `ShowAverageInReports`, `TreatBlankAsZero` | unit-name map |
| Email | `LinkText` | `EmailSettings.LinkText` | – |
| | `DisplayAs` (FullEmailAddress / OmitDomain / OmitAfterUnderscore) | `ShowFullAddress` / `ShowBeforeAt` / `ShowBeforeUnderscore` | one flag per value |
| Phone | – | `PhoneSettings` (display only) | – |
| URL | `LinkText`, `DisplayAsButton`, `EmbedAsIframe` | `UrlSettings.LinkText/DisplayAsButton/EmbedAsIframe` | – |
| | `Target` (SamePage/NewWindow/Popup) | `OpenTarget` | map names |
| File | `LinkText`, `ShowAsImages` | `FileSettings.LinkText`, `ShowImagePreview` | – |
| | `RevisionsToKeep` | `KeepAllRevisions`, `RevisionLimit` | 0 → keep all |
| | `AllowPromoteVersion` | `AllowOlderVersionsAsCurrent` | – |
| Checkbox | `ShowAsCheckmark` | `BooleanSettings.ShowYesNo` | inverted |
| | `DoesTotal` | `ShowTotalsRow` | – |
| User | `DisplayAs` (FirstNameLastName / LastNameFirstName / UserName) | `UserFieldSettings.DisplayAs` | map names |
| | `UserSet` / `Choices` | `UserSetMode`, `CustomUserPublicIds` | users must exist |
| Address | sub-fields (Street1…Country) | `AddressSettings` + `AddressSubFields` | ✔ already used for lookups |

### Formula fields

| QBL | PowerBase | Transform | Status |
|---|---|---|---|
| `Text::Formula` | `Formula_Text` (`Expression`) | – | ✔ |
| `URL::Formula` *(165)* | **`Formula_Url`** (`FormulaUrlSettings.Template`, `LinkText`, `DisplayAsButton`, `OpenTarget`) | expression → `Template` | ✘ (imports as Formula_Text) |
| `Email::Formula` / `PhoneNumber::Formula` / `RichText::Formula` | `Formula_Email` / `Formula_Phone` / `Formula_RichText` | pick variant by QBL type, not only by compiled result | ✘ |
| `Numeric/Currency/Percent::Formula` | `Formula_Number` (`Decimals`, `Separator`, `Symbol`, `Position`, `UnitsDescription`, totals) | copy numeric display props | ◐ expression only |
| `Date/DateTime/TimeOfDay/Duration/Checkbox/User/MultiselectText::Formula` | `Formula_Date/DateTime/Time/Duration/Bool/User/MultiSelect` | copy display props (`ShowMonthName`…, `ShowYesNo`) | ◐ expression only |
| Formula text | rewrite: `[_DBID_X]`→ keep alias or `Dbid("X")`; numeric `[1226]`/`_fid_N` → `[fid_N]` via source `Id` map; `Left/Right/NotLeft/NotRight(text, "delim")` and multi-branch `If` → rewrite or flag | | ◐ |

## 5. Relationships, Reference, Lookup, Summary

| QBL | PowerBase target | Transform | Status |
|---|---|---|---|
| `Relationship::Child.Parent` | `Relationship` (`CreateRelationship`) | – | ✔ |
| Reference `Label`, `IsRequired` | reference field label / required | – | ◐ label ✔, required ✘ |
| Reference `Proxy` *(112)* | `Relationship.ProxyFieldId` (first lookup) | ✔ implicit | ◐ |
| Reference `FilterField`/`MatchField`/`ConditionalValues` *(1)* | `ReferenceSettings.FilterConditions` / `FilterTree` | – | ✘ |
| Reference `UseRecordPickerForGridEdit` *(161)* | – (see missing list) | | n/a |
| Lookup `TargetField`, label | `AddLookupFields` (`SourceFid`, `Label`, `SourceSubField`) | retry rounds until source exists | ◐ (47 lost) |
| Summary `Function` | `SummarySettings.Function` | Count→Count, Total→Sum, Average→Avg, Maximum→Max, Minimum→Min, DistinctCount→DistinctCount, AnyRelatedRecords→Exists, CombinedText→CombinedText | ✔ |
| Summary `FieldToSummarize` | `TargetFid` | – | ✔ |
| Summary **`Query`** *(257)* | `SummarySettings.FilterTree` / `AddSummaryFieldCommand.MatchingCriteria` | QB query → `FilterGroup` | ✘ |
| Summary CombinedText options (delimiter, sort, distinct) | `Delimiter`, `SortFid`, `SortDescending`, `DistinctValues` | – | ✘ |
| ReportLink | auto-created by PowerBase | – | n/a |

## 6. Forms

| QBL | PowerBase target | Transform | Status |
|---|---|---|---|
| `FormV2.Name` | `Form.Name` | – | ✔ |
| `ShouldAutoAddFields` | `Form.AutoAddNewFields` | – | ✘ |
| `ShouldShowBuiltInFieldsFooter` | `Form.ShowBuiltInFields` | – | ✘ |
| `AlwaysTabsForViewRecord` | `Form.AlwaysTabsOnView` | – | ✘ |
| `AllowedSaveActions` (SaveAndNew, SaveAndNext, SaveAndKeepWorking, SaveAndView) | `Form.SaveOptions` (SaveNew,SaveNext,SaveKeepWorking,SaveView) | rename | ✘ |
| `DisplayPagesAs` (Tabs/Steps) | `Form.PageNavMode` | – | ✘ |
| `Pages[].Name` *(112)* | `FormPage.Heading` | keep page tier | ✘ (page tier dropped) |
| `Sections[].Title` | `FormSection.Name`/heading | – | ✔ |
| `IsCollapsedByDefault` | `FormSection.IsCollapsed` | – | ✔ |
| `ShowColumnDividers` | `FormSection.ShowDividers` | – | ✘ |
| `Columns[].Title`, `LayoutWidth`, `BackgroundColor` | `FormSectionBlock.Heading`, `Width`, `BackgroundColor` | – | ✔ |
| Element `Field` | `FormElement.AppFieldId` | by field name | ✔ |
| `LabelMode` / `LabelOverride` | `LabelMode` / `CustomLabel` | Default/Custom/Hidden | ✔ |
| `HelpText` | `HelpTextOverride` | – | ✔ |
| `Width` | `WidthMode/WidthValue` | – | ✔ |
| `ModesToShowIn` (View/Edit/Create) | `ShowOnView/Edit/Add` | – | ✔ |
| `ForceReadOnlyOnForm` / `ForceRequireOnForm` | `IsReadOnly` / `IsRequired` | – | ✔ |
| `MultipleChoiceDisplayAs` *(43)* (Dropdown/RadioVertical/RadioHorizontal/Segmented) | `FormElement.DisplayAs` | enum map | ✘ |
| `FilterChoicesBy` *(665, almost all Default)* | reference filter | – | ✘ |
| Element `Report` *(153)* (`Report`, `ReportLinkField`, `HideToolbar`, `AllowCreateRecords`, `ShouldOpenInEmbedded`) | `FormElement` type `Report` (+ element content) | needs report ref → created report | ✘ (skipped) |
| Element `RichText` | static text element | – | ✔ |
| Element `Group` *(40)* | `GroupId` | currently flattened | ◐ |
| `PinnedSection` *(4)* | `FormSection.IsPinned` | – | ✘ |
| Table `Forms.Properties.RoleDefaults` | `AppRoleTableFormOverride` (edit/add form per role) | – | ✘ |

## 7. Form rules

| QBL | PowerBase target | Transform | Status |
|---|---|---|---|
| `Name`, `Description` | `FormRule.Name/Description` | – | ✔ |
| `Disabled` | `IsActive` (inverted) | – | ✔ |
| `RunOn` (4) | `RunTrigger` | RecordOpenForEdit→EditOrAdd, RecordSave→Save, RecordSaveAfterValidating→SaveAfterValidating, RecordOpenSaveOrChange→AnyChange | ✔ |
| `FireOnlyOnChange`, `Tags` | – | – | ✘ |
| `When.TrueWhen` (All/Any) | `ConditionLogic` | all/any | ✔ |
| Condition `Field` (+`Comparison`, `Value`) | `FormRuleCondition` (`ConditionKind=field`) | 12 operators | ✔ |
| Condition `Role` *(14)* (`IsInRole`/`IsNotInRole`) | `ConditionKind=role` | role ref → role | ✘ (skipped) |
| Condition `FormulaIsTrue` *(6)* | `IsExpressionMode` + `ExpressionText` | translate formula | ✔/◐ |
| Action Show/Hide/Require/Unrequire/MakeEditable/MakeReadonly/SetLabel/SetColor | `FormRuleAction.ActionType` | map names | ✔ |
| Action `Change` literal `Value` | `ChangeValue` + `ActionValue` | – | ✔ |
| Action `Change` with `Value: !Ref{Field}` *(6)* | `ValueSource` + `SourceFieldId` (live value) | – | ✘ (skipped; **PBL** needs `ValueSource`, `SourceFieldRef`) |
| Action `DisplayMessage` *(5)* `Message`, `MessageType` (Modal/Notification/Field), `MessageLevel`, `FieldTarget` | `ActionValue` (text), `MessageType`, `MessageSeverity`, target field | **PBL** needs `MessageType`, `Severity`; allow no target | ✘ (dropped — needs a target) |
| Action `AbortSave` *(2)* `Message` | `PreventSave` + `ActionValue` | allow no target | ✘ |
| Targets (Field / Section / Column / Page) | `TargetElement/Section/Block/Page` | – | ✔ (Page ✘) |

## 8. Reports

| QBL | PowerBase target | Transform | Status |
|---|---|---|---|
| `Name`, `Description`, `ShowDescription` | `CreateReportCommand.Name/Description`, `Options.ShowDescriptionOnReportPage` | – | ◐ name only |
| `Columns` (list or Default) | `Columns` (Fids) | Default → empty | ✔ |
| `ColumnsUI[].Label` / `Alignment` / `Pinned` | – | see missing list | n/a |
| `SortAndGroup[]` sort | `SortFields` | – | ✔ |
| `SortAndGroup[]` group flag *(532 reports)* | `GroupByFieldId`, `TableSortGroup` (`SortGroupLevelCommand.IsGroup`, `GroupByMode`) | QB group mode → EqualValues/… | ✘ |
| `Filters.Criteria` *(121 non-default)* | `FilterTree` (`FilterGroup`) | QB query → condition tree | ✘ |
| `Filters.DynamicFields` | `DynamicFilterType`, `CustomDynamicFilterFields` | – | ✘ |
| `Filters.AllowQuickSearch` | `AllowQuickSearch` | – | ✘ |
| `HideTotals` | `HideTotals` | – | ✘ |
| `GroupDisplay` | `GroupDefaultCollapsed` | Collapsed→true | ✘ |
| `ColumnHeaderText` | `Options.ColumnHeaderText` | – | ✘ |
| `AllowEditingRecords` / `AllowViewingRecords` | `Options.ShowEditIcon` / `ShowViewIcon` | – | ✘ |
| `DisableDeleteCheckbox` | `Options.DisableBulkDelete` | – | ✘ |
| `InlineEditing` | `Options.DisableGridEdit` | inverted | ✘ |
| `RowNumberVisibility` | report option (row numbers) | – | ✘ |
| `RolePermissions` *(532 × roles)* | `VisibleToRoleIds` / `AppRoleReport` | role ref → role | ✘ |
| Summary report grouping/aggregates | `RowGroupLevels`, `Aggregations` | – | ✘ (none in cares) |

## 9. Roles, table permissions, field permissions, record filters

| QBL | PowerBase target | Transform | Status |
|---|---|---|---|
| Role `Name`, `Description`, `Default` | `AppRole.Name/IsDefault` | seeded roles (Administrator, Viewer, Participant) are adopted + overwritten | ✔ |
| `ManageUsers` | `AppRolePermission` `users:manage`, `users:invite` | – | ✘ |
| `EditApp` | `apps:update`, `roles:manage`, `tables:*`, `fields:*`, `forms:*`, `reports:*` | permission bundle | ✘ |
| `DisableAccess` | no-access role (empty permission set) | – | ✘ |
| `RestrictAccessToNewTablesForThisRole` | new tables default to `ViewScope=None` for the role | – | ✘ |
| `Default` | `AppRole.IsDefault` | – | ✔ |
| `TableHomePageSettings.RoleViews` | `AppRole.HomePageId` (page/report) | one per role | ✘ |
| Table `RolePermissions.CanViewRecords.When` (Always/Never/CustomRule) | `ViewScope` (AllRecords/None/…) | CustomRule + criteria → AllRecords + record filter | ◐ (CustomRule → None) |
| `CanModifyRecords.When` | `ModifyScope` | same | ◐ |
| `CanAddRecords`, `CanDeleteRecords`, `CanSaveCommonReports`, `CanEditFieldProperties` | `CanAdd`, `CanDelete`, `CanSaveSharedReports`, `CanEditFieldProperties` | – | ✔ |
| **`CustomAccessCriteria`** *(2,560; 1,328 view + 1,232 modify)* | `AppRoleRecordFilter` (`Conjunction`, `FilterJson`) via `UpdateRecordFilters` | see operator table below | ✘ |
| Field `RolePermissions` *(22 fields)* | `AppRoleFieldPermission.Access` (Modify/View/None) | – | ✘ |

### Quickbase query operators needed for `CustomAccessCriteria` and report `Filters.Criteria`

Syntax: `{'FID'.OP.'value'}` combined with `AND`/`OR` and parentheses; `FID` is the source field `Id` (every QBL field carries it).

| QB op | Meaning | PowerBase operator |
|---|---|---|
| `EX` / `XEX` | is / is not | `eq` / `ne` |
| `CT` / `XCT` | contains / does not contain | `contains` / `notContains` |
| `SW` / `XSW` | starts with / does not | `startsWith` / (not) |
| `GT` `GTE` `LT` `LTE` | numeric/date compare | `gt` `gte` `lt` `lte` |
| `BF` `AF` `OBF` `OAF` | date before/after (or on) | `lt` `gt` `lte` `gte` |
| `HAS` / `XHAS` | multi-value contains | `contains` / `notContains` |
| `TV` / `XTV` | true value / not | `eq true` / `eq false` |
| `_curuser_` value | current user | `UseCurrentUser = true` / `isCurrentUser` |
| `IR`/`XIR` (in range, relative dates) | – | see missing list |

Observed in cares: only `CT` (`'8'.CT.'Test'`, `'29'.CT.'Fidelis letters'`) and `EX` (`'200'.EX.'1'` and ten others) — enough to cover **all 2,560** with two operators.

## 10. Source-id and housekeeping mappings (needed for the above to work)

| Need | Mapping |
|---|---|
| **FID → field** | QBL `Id:` on every field node → built once per table, used for numeric `[1226]` / `_fid_N` in formulas and `{'8'…}` in criteria |
| `$Role_X` / `$Table_X` / `$Field_X` logical refs | already qualified by table (`Qualify`); store in `meta.ImportSourceRef` (done) |
| Icon names | one shared dictionary QB name → PowerBase catalog id, used by app **and** table icons |
| `!Var` / `ParameterDefinitions` | resolved at parse time (done) |
| Quickbase table alias | `AliasHint` → `AppTable.Alias` |
