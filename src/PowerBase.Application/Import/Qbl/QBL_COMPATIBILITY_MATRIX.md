# QBL → PBL Compatibility Matrix

Tracks what a real QBL (Quickbase) export can bring into PowerBase via PBL import, by slice.
Anything not listed as "Supported" is flagged in the import preview/report — never silently
dropped or approximated into the wrong construct.

Identifiers below were verified against a real 55,844-line QBL v0.12 export, not guessed from
documentation — the public docs (summarized from help.quickbase.com) disagreed with the real
file on several names. Treat a real QBL sample as the source of truth over documentation if the
two disagree again.

## Field types

| QBL type | Slice | PBL type (`FieldTypeCode`) |
|---|---|---|
| `QB::Field::Text` | 1 | `Text` |
| `QB::Field::TextMultiline` | 1 | `TextMultiLine` |
| `QB::Field::RichText` | 1 | `RichText` |
| `QB::Field::TextMultipleChoice` | 1 | `SingleSelect` |
| `QB::Field::MultiselectText` | 1 | `MultiSelect` |
| `QB::Field::Number` | 1 | `Number` |
| `QB::Field::Currency` | 1 | `Currency` |
| `QB::Field::Percent` | 1 | `Percent` |
| `QB::Field::Rating` | 1 | `Rating` |
| `QB::Field::Date` | 1 | `Date` |
| `QB::Field::DateTime` | 1 | `DateTime` |
| `QB::Field::TimeOfDay` | 1 | `Time` |
| `QB::Field::Duration` | 1 | `Duration` |
| `QB::Field::Checkbox` | 1 | `Boolean` |
| `QB::Field::Email` | 1 | `Email` |
| `QB::Field::PhoneNumber` | 1 | `Phone` |
| `QB::Field::URL` | 1 | `Url` |
| `QB::Field::FileAttachment` | 1 | `File` |
| `QB::Field::Address` (composite only — see below) | 1 | `Address` |
| `QB::Field::User` | 1 | `User` |
| `QB::Field::ListUser` | 1 | `MultiUser` |
| `QB::Field::Text::Formula`, `URL::Formula`, `RichText::Formula` | 1 | `Formula` (ResultType `Text`) |
| `QB::Field::Checkbox::Formula` | 1 | `Formula` (ResultType `Bool`) |
| `QB::Field::Numeric::Formula` | 1 | `Formula` (ResultType `Number` — note the real inconsistency: every other formula type is `<BaseType>::Formula`, but numeric is `Numeric::Formula`, not `Number::Formula`) |
| `QB::Field::Date::Formula`, `DateTime::Formula`, `Duration::Formula`, `User::Formula` | 1 | `Formula` (matching ResultType) |
| `QB::Field::Reference` | 2 | Folded into the owning `PblRelationship` — never a standalone field |
| `QB::Field::Lookup` | 2 | Folded into the owning `PblRelationship.Lookups` |
| `QB::Field::Summary` | 2 | Folded into the owning `PblRelationship.Summaries` |
| `QB::Field::ReportLink` | 2 | Not imported directly — `CreateRelationshipCommandHandler` auto-creates an equivalent as a side effect of creating the relationship; flagged only if the QBL field's `LinkText`/`MatchValuesExactly` diverge from PowerBase's auto-generated default |
| `QB::Field::RecordID`, `RecordOwner`, `DateCreated`, `DateModified`, `LastModifiedBy` | — | **Skipped (informational, not a gap).** PowerBase's `IAppSeeder` already auto-seeds equivalent system fields per table. |
| `QB::Field::AddressStreet1/2`, `AddressCity`, `AddressState`, `AddressPostalCode`, `AddressCountry` | — | **Skipped (informational, not a gap).** Quickbase exports each address sub-component as its own shadow field alongside the composite `QB::Field::Address` field; importing them separately would duplicate data the composite field already carries. |
| `QB::Field::Predecessor`, `WorkDate` | — | **Unsupported.** Project-management field types, no PowerBase equivalent. |
| `QB::Field::Unsupported` | — | **Unsupported.** Quickbase's own escape hatch for a construct even its export couldn't map — surface the field's `Explanation` text verbatim as the warning message. |
| `QB::Field::VCard`, `ICalendar` | — | **Unsupported.** No PowerBase equivalent. |

## Structural elements

| QBL element | Slice | Notes |
|---|---|---|
| `QB::Application` | 1 | Name, Description, AppIcon→Icon, AppColor→Color. `TableOrder`/`RoleOrder` drive iteration order only. Currency/date/number-format/timezone app-level defaults have no PowerBase equivalent — informational only. |
| `QB::Table` | 1 | Nests its own `Fields`/`Relationships`/`Reports`/`Forms` maps as children — **not** flat top-level `Resources` entries. |
| Scalar + Formula fields (above) | 1 | |
| `QB::Report::Table`, `GridEdit` | 1 | Direct type-name match to PowerBase's `Table`/`GridEdit` report types. |
| `QB::Report::Summary` | 1 | Direct match to PowerBase's `Summary` report type (not seen in the sample file, but the identifier matches PowerBase's own `ReportType` string). |
| `QB::Report::Calendar`, `Timeline`/`DefaultTimeline`, `Kanban`, `Map`, `Chart` | — | **Unsupported.** No PowerBase report type equivalent (`CreateReportCommandHandler.AllowedReportTypes = Table/Summary/GridEdit` only). Chart may gain a path once the "Add Chart report type" commit (currently only on `develop`) merges into this branch — revisit then. |
| `QB::ReportGroup` | — | Organizational grouping construct only, not a data view — informational, nothing to import. |
| `QB::Relationship::Child` / `Parent` | 2 | Linkage resolved via the Child node's own `Parent: !Ref{Table, Relationship}` property, not a shared flat node. |
| Cross-app relationships (`CrossAppParent`/`CrossAppChild`) | — | **Unsupported.** Out of scope (Master/Child App mode deferred to M4). |
| `QB::FormV2` (Page→Section→Column→Element) | 3 | Legacy `QB::Form` (parallel per-table representation) is **intentionally skipped** — `RoleDefaults` confirms `FormV2` is the live one. Page tier is dropped (contributes ordering only); Section→Section, Column→Block, Element→Element. |
| `QB::FormV2::Element::Field`, `RichText` (static) | 3 | Direct match to PowerBase `ElementType` `Field`/`StaticText`. |
| `QB::FormV2::Element::Group` | 3 | No PowerBase "Group" element type — flatten the group's children into the section/block (preserves every field, loses only the visual grouping) and flag informationally. |
| `QB::FormV2::Element::Report` (embedded subform) | — | **Deferred/unsupported for v1** — PowerBase has a `Report` `ElementType` but its settings contract (which report/relationship it embeds) isn't confirmed from `FormElement.cs` alone. |
| `QB::FormV2::Rule` | 4 | `TrueWhen→ConditionLogic`, `RunOn→RunTrigger` (4-way mapping, exact). |
| `QB::FormV2::Rule::Condition::Field` | 4 | Maps to PowerBase's real 12-operator set (`eq/ne/contains/notContains/startsWith/endsWith/isEmpty/isNotEmpty/gt/gte/lt/lte`). |
| `QB::FormV2::Rule::Condition::Group` (single, uniform logic) | 4 | Maps to the rule's own `ConditionLogic`. Genuinely mixed nested AND/OR groups have no PowerBase representation — flagged, not flattened. |
| "Formula is true" condition | 4 | Maps to `SaveFormRuleCommand.IsExpressionMode=true` + `ExpressionText`, translated through the existing `FormulaTranslator` — a real, confirmed escape hatch, not an approximation. |
| `QB::FormV2::Rule::Condition::Role`, `DaysAgo`, `Range`, `Relative` | — | **Unsupported.** No PowerBase equivalent — `FormRuleConditionSpec.Operator`'s real vocabulary has no role-membership or date-relative operators at all. Confirmed real occurrence: a "Show Dev" rule using `Condition::Role`/`IsInRole`. |
| `QB::FormV2::Rule::Action::Show/Hide/MakeEditable/MakeReadOnly/Require/Unrequire/SetLabel/SetColor/DisplayMessage/AbortSave` | 4 | Map 1:1 to PowerBase's real `FormRuleActionType` set (`Show/Hide/Enable/Disable/Require/NotRequired/ChangeLabel/SetColor/DisplayMessage/PreventSave`). |
| `QB::FormV2::Rule::Action::Change` (literal `Value:`) | 4 (added post-v1) | Sets a field's value — maps to PowerBase's `ChangeValue` action (a static `ActionValue` string applied at evaluation time, same pattern as `ChangeLabel`/`SetColor`). Shaped differently from every other action: its target field ref lives directly under `Field:`, not nested inside `Target:`. Confirmed real occurrence: a "Change File Name" rule. |
| `QB::FormV2::Rule::Action::Change` (`Value: !Ref{Field: ...}`) | — | **Unsupported.** A confirmed second real shape: `Value:` itself pointing at another field ("copy that field's live value in") has no PowerBase equivalent — `ActionValue` is a static string fixed at import time, not re-resolved per evaluation. Flagged rather than silently frozen to whatever the source field held at import. |
| `QB::Application::Role` | 5 | Node itself only supplies `Name`/`Description`/`Default` — `ManageUsers`/`EditApp`/`DisableAccess`/`AppUI`/`TableUI` are cosmetic toggles with no confirmed PowerBase equivalent (informational only). |
| Table-level `RolePermissions` map | 5 | **This is where real CRUD grants live** — on each `QB::Table` node's `Properties.RolePermissions`, keyed by role ref, not on the Role node. `CanViewRecords`/`CanModifyRecords.When: Always/Never` → PowerBase `RecordScopes.AllRecords`/`None`. |
| `CustomAccessCriteria` (row-level filter) | — | **Unsupported, not translatable.** Quickbase's internal query-criteria mini-language (field-by-numeric-id, `CT`/`EX`-style operators) — a different language entirely from `PowerBase.Formula`. Flag "row-level filter present but not translatable, defaults to unfiltered" rather than attempting a naive translation. |
| Field-level permissions | — | **Not found anywhere in the real sample** (`RolePermissions:` appears 214 times, all on Tables/Reports, none on Fields) — `PblFieldPermission` import stays unpopulated for real QBL data. |
| Report-level `RolePermissions` (boolean map) | 5 (stretch) | Maps directly to `CreateReportCommand.VisibleToRoleIds`. |
| `Table.Forms.Properties.RoleDefaults`/`ReportOverrides` | — (stretch) | Conceptually maps to `AppRoleTableFormOverride`/`AppRoleReport` — real data exists for this in exports, treated as optional refinement, not core path. |
| `QB::CodePage` | — | **Unsupported.** App-level custom HTML/JS pages — no PowerBase equivalent, out of scope. |
| `QB::Application::Variable` | — | **Unsupported.** App-level parameters/variables — no PowerBase equivalent, informational only. |
| Connected tables | — | Not committed (see scope doc: "We cannot promise this in QBL"). |
| Pipelines / automations | — | Not committed for PBL in M2; deferred to M4+. |
| Dashboards, charts (app-level) | — | Not in current PBL scope. |

## YAML syntax notes (confirmed against the real export)

- **Three custom tags**: `!Ref` (cross-reference mapping), `!BadRef` (a **scalar string** — not
  a mapping), `!Var` (resolves against a top-level `ParameterDefinitions:` map by parameter
  name — this tag is not documented publicly but is real and present in exports).
- `!Ref` mappings use varying key combinations depending on what's targeted (`{Table, Field}`,
  `{Relationship}`, `{Role}`, `{FormV2Page, FormV2Section}`, etc.) — treat as a flexible bag.
- `!BadRef` occurrences (broken/stale references in the source app) must produce a `Warning`,
  never an `Error` that blocks the rest of the document.

## Formula functions

PowerBase's formula engine (`src/PowerBase.Formula/Builtins/*`) already registers the large
majority of Quickbase's function surface under matching names (`If`, `Case`, `ToText`,
`Contains`, `Left`, `Right`, `DateAdd`, `Nz`, `Part`, `List`, `RegexMatch`, …). Formula
translation is mostly: re-point `[Field]` references to mapped PowerBase fields via logical-ref
matching, then validate through the existing `Binder`/`TypeChecker`. Quickbase functions with no
PowerBase equivalent are flagged for manual review rather than translated.

## Import modes

| Mode | Availability |
|---|---|
| Create New App | Available |
| Update Existing App | Needs Master/ownership infra (M4) |
| Create Child App Linked to Master | Needs Master App feature (M4) |
