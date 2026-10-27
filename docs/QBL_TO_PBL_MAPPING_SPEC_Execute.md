# CLAUDE.md — QBL to PowerBase Mapping Instructions

## 1. Purpose

Implement and validate the Quickbase QBL → PowerBase (PBL) import mappings defined in the mapping specification. Preserve source configuration wherever PowerBase already has a supported destination. Keep the implementation straightforward and consistent with the existing architecture.

**Primary source of truth:** `QBL → PBL Mapping Spec — everything required to keep the values` and the agreed module-by-module mapping requirements. Inspect the current converter/importer and PowerBase domain/command models before changing code.

## 2. Mandatory working rules

1. **Inspect before editing.** Find the QBL parser, converter/importer, mapping models, PowerBase domain entities, command/DTO models, persistence mappings, and relevant tests. Follow existing project patterns.
2. **Reuse existing targets.** Prefer existing PowerBase properties/settings. Add a new property only when a required value has no supported destination and cannot be represented with the existing model.
3. **Do not silently drop supported values.** If a value cannot be mapped, record a clear diagnostic or warning and continue safely where possible.
4. **Preserve import compatibility.** Avoid unrelated refactors, API changes, database migrations, or UI redesigns.
5. **Resolve references by source IDs.** Never assume QBL IDs equal PowerBase IDs. Build and reuse source-to-target maps.
6. **Use one shared icon mapper** for app and table icons. Map Quickbase names to valid PowerBase icon catalog IDs/classes; verify actual rendering. Fallbacks: app `pi pi-database`, table `pi pi-table`.
7. **Keep defaults and precedence explicit.** Imported app-level formatting is the fallback; explicit field-level formatting takes precedence.
8. **Security must not become more permissive.** Confirm inverse semantics before mapping booleans. Never silently broaden access.
9. **Keep changes focused.** Do not implement unsupported QBL features by inventing new settings. Ignore unsupported settings when explicitly out of scope, and report them in diagnostics if practical.
10. **Test every mapping area changed.** Add/update unit and integration tests using the repository's test conventions. Run the relevant test suite and report exact results; do not claim tests passed if they were not run.
11. **Do not overwrite existing user changes.** Inspect `git status` and preserve unrelated work.
12. **Do not stop after analysis.** Implement the complete in-scope mapping work in a coherent pass, then test and summarize remaining blockers.

## 3. Required implementation workflow

1. Inspect repository structure and `git status`.
2. Locate the mapping specification and read it fully.
3. Trace each source property from QBL parsing through conversion/import commands to persisted PowerBase models and UI consumption where relevant.
4. Create a checklist from the modules below and mark each mapping as:
   - `Already supported`
   - `Implemented`
   - `Unsupported / intentionally skipped`
   - `Blocked` (include the reason)
5. Implement mappings using existing conventions. Keep conversion logic centralized where shared behavior is needed (especially icons, source IDs, formatting and enum conversions).
6. Ensure import order supports dependencies: apps/tables/fields first as appropriate, then relationships/lookups, formulas, reports, forms/rules and permissions; use retry/deferred resolution where existing architecture requires it.
7. Add tests for direct mappings, conversions, defaults, missing/unknown values, invalid references and security-sensitive behavior.
8. Run formatting/build/tests using the repository's documented commands. If commands are not documented, inspect solution/project files and CI configuration to determine the correct commands.
9. Review the diff for unrelated changes, data-loss risks, incorrect inversions and accidental permission broadening.
10. Provide a concise completion report: changed files, mappings completed, tests run with outcomes, and any remaining unsupported/blocked items.

## 4. Module mapping checklist

### Module 1 — App properties

- [ ] `Name` → `App.Name`; preserve value.
- [ ] `Description` → `App.Description`; preserve value.
- [ ] `AppColor` → `App.Color`; resolve `!Var` via `ParameterDefinitions`, store resolved hex.
- [ ] `AppIcon` → `App.Icon`; map through shared icon catalog dictionary; fallback `pi pi-database`; verify rendering.
- [ ] `CurrencySymbol` → `AppFormattingSettings.Currency.Symbol`; support `$`, `₹`, `€`, `£`, `¥`; unknown/missing symbol defaults to `$`.
- [ ] `CurrencySymbolPosition` → `Currency.Position`; support Before, Between and After. Between places the symbol after a leading minus sign and before the number (for example `-$123.45`). Imported app position is the default if no field-level position is explicitly set.
- [ ] `NumberFormat.SeparatorStyle` → `Number.ThousandSeparator`: `CommaGroupingPeriodDecimal` uses `,` grouping and `.` decimal; `PeriodGroupingCommaDecimal` uses `.` grouping and `,` decimal.
- [ ] App-level number formatting uses two decimal places. Map `NumberFormat.SeparatorPlaces` to `Number.DisplayPattern` only where supported by the target model and agreed requirements. Standard fallback: `1,234,567.89`.
- [ ] `DateFormat` → `Date.FormatString` and `Date.Separator`; preserve date token order and use `-` as the separator. App format is fallback if no field-level format is explicit.
- [ ] `CanCopyApp` → `AllowNonAdminsToCopy`; `Everyone` means `true`.
- [ ] `CanExportData` → `AllowNonAdminsToExport`; map source permissions correctly.
- [ ] `DisableNonAdminSyncConnections` → `AllowNonAdminsToConnect`; invert boolean.
- [ ] `RequireAppToken` → `RequireAppTokens`.
- [ ] `Searchable` → `HideFromPublicSearch`; invert only after confirming semantic opposition.
- [ ] `Branding.AppHeader.BackgroundColor` → `AppBrandingSettings.BrandColorHex`; preserve hex.
- [ ] `TableOrder` → `AppTable.DisplayOrder`; preserve source ordering.
- [ ] `Variables[].Name/Value` → `AppVariable`; honor PowerBase naming rules and 500-character value limit.
- [ ] `Pages[]` of type `QB::CodePage`: `Name` → page name; `RawCode` → `CodeHtml` where supported. Preserve code content; do not silently discard pages.
- [ ] `Manager` → matching app owner/admin only when a matching user exists; otherwise use importing user as owner.

### Module 2 — Table advanced settings

- [ ] `Name`, `Description`, `RecordNameSingular`, `RecordNamePlural` → corresponding `AppTable` properties.
- [ ] `TableIconName` → `AppTable.Icon`; shared icon mapper; fallback `pi pi-table`; verify rendering.
- [ ] `IncludeInTableBar` → `AppTable.IsShowInBar`; false hides from sidebar without deleting/disabling table.
- [ ] `KeyField` → `AppTable.KeyFieldId`; resolve source field reference and verify it belongs to the table.
- [ ] `DefaultRecordPicker` → `DefaultRecordPickerField1Id`, `DefaultRecordPickerField2Id`, `DefaultRecordPickerField3Id`; preserve order, maximum three, resolve IDs.
- [ ] `RecordTitleField` and `UseCustomRecordTitleField` → `AppTable.DisplayFieldId` and existing custom-title behavior; honor the enable flag and do not add duplicate properties unnecessarily.
- [ ] `AliasHint` → `AppTable.Alias`; preserve valid `_DBID_*` aliases for formulas/references.
- [ ] `CustomDataRules.TurnedOn/Formula` → `IsCustomDataRuleEnabled/CustomDataRule`; translate formula using existing logic.
- [ ] `SaveOnExit` → `AppTable.IsAutoSaveOnLeave`.
- [ ] `DefaultReportSettings` → `AppTable.DefaultReportSettings` JSON; preserve sort order, column header text, inline editing, group display, row-number visibility, quick search and dynamic filters; resolve field refs and map enums.
- [ ] `ReportGroups` is informational only; do not create unsupported behavior.

### Module 3 — Common field properties

- [ ] `Label` → `AppField.Label` and `Name`; preserve label and enforce unique internal names.
- [ ] `Comments` → `AppField.Description`.
- [ ] `HelpText` → `AppField.HelpText`.
- [ ] `IsRequired` → `AppField.IsRequired`.
- [ ] `IsUnique` → `AppField.IsUnique`.
- [ ] `DefaultValue` → `AppField.DefaultValue`; convert checkbox values to booleans, text as-is, and resolve `CurrentUser` for user fields using supported representation.
- [ ] `IsAuditable` → `AppField.IsAuditable`.
- [ ] `IsSearchable` → `AppField.IsSearchable`.
- [ ] `AllowedInReports` → `AppField.IsReportable`.
- [ ] `IsBold` → `Settings.DisplayBold`.
- [ ] `DisplayWithoutWrapping` → `Settings.NoWrap`.
- [ ] `Width` / `DefaultColumnWidth` → `Settings.ColumnWidth`.
- [ ] Field `RolePermissions` → `AppRoleFieldPermission`; map Modify/View/None and configure custom field access as required.

### Module 4 — Type-specific field settings

Populate `PblField.Settings` (or the current equivalent) for each supported field type. Do not leave settings unassigned.

- [ ] Text/Multiline: `MaxLength` → `Validation.MaxLength`; source `0` means `null` where applicable.
- [ ] Rich text: `MaxLength`, `Width` → `RichTextSettings.MaxLength/ColumnWidth`.
- [ ] Choice: `InputOptions.ListChoices` / `Choices` → `SelectSettingsBase.Options`; preserve order unless `ChoiceOrder=Alphabetical`; map `MaxLength`.
- [ ] Number/Percent/Currency: `DecimalPlaces` → `NumericSettings.Decimals` (`FloatingPoint` → `null` where appropriate); separator, units, totals, averages, blank-as-zero, currency symbol/position and `DisplayAs` to matching settings.
- [ ] Date: `ShowMonthAsAlpha` → `ShowMonthName`; `HideCurrentYear` → `HideYearIfCurrent`; `ShowDayOfWeek` → `ShowDayOfWeek`.
- [ ] DateTime: `ShowTime` → `ShowTime`.
- [ ] TimeOfDay: `Use24HourClock` → `TimeSettings.Use24Hour`; `DisplayAs` (`HH:MM`/`HH:MM:SS`) → `TimeSettings.Format`.
- [ ] Duration: map `DisplayUnits`, `DecimalPlaces`, totals, averages and blank-as-zero to `DurationSettings`; translate unit names.
- [ ] Email: `LinkText` → `EmailSettings.LinkText`; map `DisplayAs` to `ShowFullAddress`, `ShowBeforeAt` or `ShowBeforeUnderscore`.
- [ ] Phone: use existing `PhoneSettings` for supported display configuration; do not invent unsupported source settings.
- [ ] URL: map `LinkText`, `DisplayAsButton`, `EmbedAsIframe`, `Target` → `UrlSettings`; map SamePage/NewWindow/Popup to supported `OpenTarget` values.
- [ ] File: map `LinkText`, `ShowAsImages`, `RevisionsToKeep`, `AllowPromoteVersion` to `FileSettings`; `RevisionsToKeep=0` means keep all.
- [ ] Checkbox: map `ShowAsCheckmark` → `BooleanSettings.ShowYesNo` with the required inversion; `DoesTotal` → `ShowTotalsRow`.
- [ ] User: map `DisplayAs`; map `UserSet` / `Choices` to `UserSetMode` / `CustomUserPublicIds`, resolving existing users.
- [ ] Address: preserve Street1…Country subfields through `AddressSettings` and `AddressSubFields`; retain existing lookup behavior.

### Module 5 — Formula fields

- [ ] Text formula → `Formula_Text.Expression`.
- [ ] URL formula → `Formula_Url`; expression → `FormulaUrlSettings.Template`; preserve link text, button and target settings. Do not import as text formula.
- [ ] Email/PhoneNumber/RichText formulas → `Formula_Email` / `Formula_Phone` / `Formula_RichText`, based on source type.
- [ ] Numeric/Currency/Percent formulas → `Formula_Number`; preserve expression and numeric display settings.
- [ ] Date/DateTime/TimeOfDay/Duration/Checkbox/User/MultiselectText formulas → corresponding typed formula models and display settings.
- [ ] Rewrite field references using source `Id` mapping. Preserve valid aliases or use supported `Dbid(...)` form.
- [ ] Handle known formula rewrites only when semantics are preserved; flag unsupported formulas rather than silently corrupting them.

### Module 6 — Relationships, references, lookups and summaries

- [ ] `Relationship::Child.Parent` → existing relationship creation flow.
- [ ] Reference `Label` and `IsRequired` → reference field settings.
- [ ] Reference `Proxy` → `Relationship.ProxyFieldId` (first lookup where appropriate).
- [ ] Reference `FilterField`, `MatchField`, `ConditionalValues` → `ReferenceSettings.FilterConditions/FilterTree` when supported.
- [ ] Skip `UseRecordPickerForGridEdit` if there is no existing target.
- [ ] Lookup `TargetField` and label → `AddLookupFields` (`SourceFid`, `Label`, `SourceSubField`); retry unresolved references after dependencies are created.
- [ ] Summary `Function` → `SummarySettings.Function`: Count, Sum, Avg, Max, Min, DistinctCount, Exists and CombinedText as appropriate.
- [ ] Summary `FieldToSummarize` → `TargetFid`.
- [ ] Summary `Query` → `FilterTree` / matching criteria.
- [ ] CombinedText: preserve delimiter, sort field, descending flag and distinct-value behavior.
- [ ] ReportLink: retain automatic PowerBase creation and avoid duplicates.

### Module 7 — Forms

- [ ] `FormV2.Name` → `Form.Name`.
- [ ] Map `ShouldAutoAddFields`, `ShouldShowBuiltInFieldsFooter`, `AlwaysTabsForViewRecord` to their existing form properties.
- [ ] `AllowedSaveActions` → `Form.SaveOptions`; map SaveAndNew, SaveAndNext, SaveAndKeepWorking, SaveAndView to the matching target enums.
- [ ] `DisplayPagesAs` → `Form.PageNavMode`.
- [ ] `Pages[].Name` → `FormPage.Heading`; preserve page hierarchy.
- [ ] Section title/collapse/dividers → `FormSection` properties.
- [ ] Column title/width/background → `FormSectionBlock` properties.
- [ ] Element field → `FormElement.AppFieldId`; preserve label mode/override, help text, width, view/edit/create visibility, read-only and required flags.
- [ ] `MultipleChoiceDisplayAs` → `FormElement.DisplayAs`.
- [ ] Preserve supported `FilterChoicesBy` reference filters.
- [ ] Report element: preserve report reference, report-link field, toolbar visibility, create permission and embedded behavior after resolving the report.
- [ ] RichText → static text element.
- [ ] Group → `GroupId`, without flattening when grouping can be represented.
- [ ] `PinnedSection` → `FormSection.IsPinned`.
- [ ] Role form defaults → `AppRoleTableFormOverride`.

### Module 8 — Form rules

- [ ] Map Name/Description.
- [ ] `Disabled` → `IsActive` with inversion.
- [ ] Map `RunOn` to supported `RunTrigger` values: edit/add, save, save-after-validation, any change.
- [ ] `When.TrueWhen` → All/Any `ConditionLogic`.
- [ ] Map field conditions, comparison operators and values to `FormRuleCondition`.
- [ ] Map role conditions after resolving role references.
- [ ] Map formula conditions to `IsExpressionMode` and `ExpressionText`.
- [ ] Map Show/Hide/Require/Unrequire/MakeEditable/MakeReadonly/SetLabel/SetColor/Change actions.
- [ ] Preserve literal values and live source-field references for `!Ref{Field}` where supported.
- [ ] Preserve DisplayMessage type/severity/message and allow no target when appropriate.
- [ ] Map AbortSave to `PreventSave` with its message and no mandatory target.
- [ ] Preserve supported Field/Section/Column/Page targets.
- [ ] Ignore `FireOnlyOnChange` and `Tags` if no existing target exists.

### Module 9 — Reports

- [ ] Preserve Name, Description and ShowDescription.
- [ ] Map Columns to field IDs; source `Default` uses PowerBase default behavior.
- [ ] Map sort definitions to `SortFields`.
- [ ] Map grouping to `GroupByFieldId` / `TableSortGroup`, preserving group level/mode.
- [ ] Translate `Filters.Criteria` to `FilterTree` / `FilterGroup`.
- [ ] Map dynamic filters and quick search.
- [ ] Map HideTotals, GroupDisplay, ColumnHeaderText and RowNumberVisibility.
- [ ] Map AllowEditingRecords, AllowViewingRecords and DisableDeleteCheckbox to corresponding options.
- [ ] Map InlineEditing to DisableGridEdit with inversion.
- [ ] Map report RolePermissions to `VisibleToRoleIds` / `AppRoleReport`.
- [ ] Do not add targets for ColumnsUI label/alignment/pinned or summary aggregates if explicitly unsupported by the existing model.

### Module 10 — Roles, permissions and record filters

- [ ] Map role Name/Description/Default to `AppRole`; reuse seeded roles where appropriate.
- [ ] Map ManageUsers to `users:manage` and `users:invite`.
- [ ] Map EditApp to existing app/role/table/field/form/report permission bundles.
- [ ] Map DisableAccess to a role with no effective permissions.
- [ ] Map RestrictAccessToNewTablesForThisRole to default `ViewScope=None` for new tables when enabled.
- [ ] Map role homepage views to `AppRole.HomePageId` after resolving page/report.
- [ ] Map CanViewRecords.When to `ViewScope` and CanModifyRecords.When to `ModifyScope`; preserve custom rules.
- [ ] Map CanAddRecords, CanDeleteRecords, CanSaveCommonReports and CanEditFieldProperties.
- [ ] Map CustomAccessCriteria to `AppRoleRecordFilter` (`Conjunction`, `FilterJson`).
- [ ] Support query operators: EX/XEX→eq/ne; CT/XCT→contains/notContains; SW→startsWith; GT/GTE/LT/LTE→gt/gte/lt/lte; BF/AF/OBF/OAF→lt/gt/lte/gte; HAS/XHAS→contains/notContains; TV/XTV→true/false.
- [ ] Preserve AND/OR and parentheses. Resolve `_curuser_` through the supported current-user condition.
- [ ] Explicitly flag unsupported operators such as IR/XIR; do not silently widen access.
- [ ] Map field RolePermissions to `AppRoleFieldPermission.Access` (Modify/View/None).

### Module 11 — Source IDs and housekeeping

- [ ] Build per-table source QBL field `Id` → PowerBase field ID map before dependent mapping.
- [ ] Use source IDs for formulas, filters, primary key, picker fields, lookups, relationships and summaries.
- [ ] Preserve qualified `$Role_X`, `$Table_X`, `$Field_X` references using existing `meta.ImportSourceRef` conventions.
- [ ] Reuse shared icon mapping for app/table icons.
- [ ] Resolve `!Var` through `ParameterDefinitions`.
- [ ] Preserve `AliasHint` → `AppTable.Alias`.
- [ ] Defer/retry dependencies in a deterministic order; log unresolved references with source context.

## 5. Formatting precedence and fallback rules

- Explicit field-level formatting wins over app-level formatting.
- If field-level currency symbol or position is absent, use the imported app-level setting; if app currency symbol is unknown or absent, use `$`.
- Currency positions: Before, Between (minus sign then symbol then number for negatives), After.
- Number separator styles:
  - `CommaGroupingPeriodDecimal`: `1,234,567.89`
  - `PeriodGroupingCommaDecimal`: `1.234.567,89`
- App-level number decimal places: always 2 per the agreed requirement. Use standard `1,234,567.89` if no valid pattern can be derived.
- Date formats preserve token order and use `-` as separator for the agreed mapping.
- Do not let defaults overwrite explicit source field settings.

## 6. Icon mapping requirements

- Inspect the actual PowerBase icon catalog and UI component before selecting target values.
- Build a single reusable mapping function/dictionary for app and table icons.
- Match by visual meaning where class names differ.
- Verify icon CSS classes/catalog IDs are supported by the project's installed icon library.
- Use app fallback `pi pi-database` and table fallback `pi pi-table` only when no suitable match exists.
- Add tests for known mappings and unknown-name fallbacks.

## 7. Security and data integrity

- Treat permission mapping as security-sensitive.
- Confirm source/target semantics before inverting any boolean.
- Preserve least privilege if a source role, field or criterion cannot be resolved.
- Never turn an unknown/custom access rule into unrestricted access.
- Resolve role/user/field/table/report references explicitly.
- Do not log secrets or sensitive record values. Log identifiers and actionable diagnostics only.
- Avoid destructive data changes and unrelated migrations.

## 8. Test expectations

At minimum, cover:
- Direct property-to-property mappings.
- Enum and boolean inversions.
- Null, missing, unknown and malformed source values.
- App formatting defaults and field-level override precedence.
- Currency symbol positions, including negative values for Between.
- Icon dictionary matches and fallbacks.
- Source ID resolution across tables.
- Field settings for each supported field type.
- Formula variant selection by original QBL type.
- Lookup/relationship dependency resolution and retry behavior.
- Form page hierarchy, report elements and rule actions.
- Report filters, grouping and role visibility.
- Record/field permission conversion, especially custom criteria and unsupported operators.
- Import diagnostics for unsupported or unresolved mappings.

Use representative fixtures from the repository where possible. Do not use real sensitive customer data in tests.

## 9. Completion criteria

Work is complete only when:
- Every checklist item is marked with a status and any skipped/blocked reason.
- All in-scope properties with existing targets are mapped.
- Shared icon and source-ID mapping logic is reused rather than duplicated.
- Formatting defaults and field-level precedence behave as specified.
- Permission mappings do not broaden access.
- Tests/builds have been run and their actual outcomes are reported.
- The final response lists the key files changed, tests executed, and remaining unsupported items.

## 10. Final response format

When implementation is complete, report:
1. Summary of mapping work completed.
2. Modules completed and any intentionally skipped mappings.
3. Files changed (grouped by parser/converter, models, persistence, UI if applicable, tests).
4. Commands run and exact build/test outcomes.
5. Remaining blockers or unsupported QBL properties.

Do not claim success for any build, test, import, or UI validation that was not actually executed.
