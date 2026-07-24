# QBL v0.12 → PBL Compatibility Matrix

Tracks what a QBL (Quickbase) export can bring into PowerBase via PBL import, by phase.
Anything not listed as "Supported" is flagged in the import preview/report — never silently
dropped or silently ignored, per the import feature's scope note.

## Field types

| QBL type | Phase | PBL type (`FieldTypeCode`) |
|---|---|---|
| `QB::Field::Text` | 1 | `Text` |
| `QB::Field::Text::MultiLine` | 1 | `TextMultiLine` |
| `QB::Field::Text::RichText` | 1 | `RichText` |
| `QB::Field::Text::Multichoice` | 1 | `SingleSelect` |
| `QB::Field::Text::Multiselect` | 1 | `MultiSelect` |
| `QB::Field::Numeric` | 1 | `Number` |
| `QB::Field::Numeric::Currency` | 1 | `Currency` |
| `QB::Field::Numeric::Percent` | 1 | `Percent` |
| `QB::Field::Numeric::Rating` | 1 | `Rating` |
| `QB::Field::Date` | 1 | `Date` |
| `QB::Field::DateTime` | 1 | `DateTime` |
| `QB::Field::TimeOfDay` | 1 | `Time` |
| `QB::Field::Duration` | 1 | `Duration` |
| `QB::Field::Checkbox` | 1 | `Boolean` |
| `QB::Field::EmailAddress` | 1 | `Email` |
| `QB::Field::PhoneNumber` | 1 | `Phone` |
| `QB::Field::URL` | 1 | `Url` |
| `QB::Field::Address` | 1 | `Address` |
| `QB::Field::FileAttachment` | 2 | `File` (type mapping exists; Phase 1 import defers actual creation until the import flow integrates with file storage) |
| `QB::Field::User`, `::User::List` | 2/4 | `User`, `MultiUser` |
| `QB::Field::Formula`, `::URL::Formula` | 2 | `Formula` (via translation layer) |
| `QB::Field::Reference` | 4 | `Reference` |
| `QB::Field::Lookup` | 4 | `Lookup` |
| `QB::Field::Summary` | 4 | `Summary` |
| `QB::Field::ReportLink` | 4 | `ReportLink` |
| `QB::Field::VCard`, `::ICalendar` | — | **Unsupported.** No PowerBase equivalent; flagged for manual review. Quickbase itself exports these as "Unsupported" in v0.12. |

## Structural elements

| QBL element | Phase | Notes |
|---|---|---|
| `QB::Application` (metadata) | 1 | Name, description, icon |
| `QB::Table` | 1 | |
| Scalar fields (above) | 1 | |
| Formula fields | 2 | Translated via `PowerBase.Formula` `Binder`/`TypeChecker`; translation report lists clean / adjusted / needs-manual-review |
| Reports (table/summary) | 2 | Maps to `Report.Definition` (`ReportDefinition`, `SortSpec`) |
| Relationships | 4 | |
| Forms, form sections, form rules | 4 | |
| Roles / permissions | 4 | |
| Connected tables | — | Not committed (see scope doc: "We cannot promise this in QBL") |
| Pipelines / automations | — | Not committed for PBL in M2; deferred to M4+ |
| Dashboards, charts | — | Not in current PBL scope |

## Formula functions

PowerBase's formula engine (`src/PowerBase.Formula/Builtins/*`) already registers the large
majority of Quickbase's function surface under matching names (`If`, `Case`, `ToText`,
`Contains`, `Left`, `Right`, `DateAdd`, `Nz`, `Part`, `List`, `RegexMatch`, …). The Phase 2
formula translation layer's job is therefore mostly: re-point `[Field]` references to mapped
PowerBase fields via logical-ref matching, apply a small function-name/alias delta where one
exists, then validate the result through the existing `Binder`/`TypeChecker`. Quickbase
functions with no PowerBase equivalent are flagged for manual review rather than translated.

## Import modes

| Mode | Phase |
|---|---|
| Create New App | 1 |
| Update Existing App | 5 (needs Master/ownership infra) |
| Create Child App Linked to Master | 5 (needs Master App feature, M4) |
