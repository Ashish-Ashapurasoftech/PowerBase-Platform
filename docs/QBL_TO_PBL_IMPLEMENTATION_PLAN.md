# QBL → PBL Import — Implementation Plan

Status: **Plan only. No code changed.** Date: 2026-10-15
Inputs reviewed: `QBL_to_PBL_Implementation.md` (primary), `QBL_Reference.md`, `cares.qbl`, the 4 spec screenshots, and the current code in `PowerBase-Platform` and `PB-Frontend`.

---

## 1. Where we are

A QBL → PBL import already exists for **Create New App** (parser → AST → converter → validator → 7-pass importer → report, plus `/apps/import/preview` and `/apps/import`, plus a 4-step Angular wizard). This feature **extends** that pipeline. Nothing is rebuilt.

Missing against the in-scope spec:

| # | Gap | Spec source |
|---|---|---|
| A | **Update Existing App** mode (diff, per-element approve/skip, no silent overwrite, never delete) | Import Modes, 2.2 step 4 |
| B | **JSON mapping file** (API/developer only) | 2.3 |
| C | Converter gaps hit by `cares.qbl` | 2.2 steps 2–3, 2.4 |
| D | Formula report completeness ("no formula silently dropped") | 2.4 |

Out of scope (Master/Child not built): "Create Child App Linked to Master" mode, Master/Child ownership tags, per-table "map to Master table" dropdown, Binding Type. Everything imports as ordinary local elements.

---

## 2. Corrections to the existing plan (found while verifying against code)

These change the effort estimate; the existing document should be amended.

1. **App variables already exist in PowerBase** (`AppVariablesController`, `CreateAppVariableCommandHandler`, `AppVariableNameRules`, formulas read them as `[Name]`). The existing plan treats them as needing a new `PblApp.Variables` + importer pass — correct — but the compatibility matrix still says "no equivalent". It is only converter + PBL model + one importer pass. **Name rules matter**: names containing `[ ] \` or looking like `var_5`/`fid_14` are rejected, so the importer must sanitize or skip-with-warning.
2. **Code pages are also supportable**: `CreatePageCommand` takes `PageType`, `CodeHtml`, `CodeCss`, `CodeJs`. QBL `RawCode` is one blob, so we need a splitter (or put the whole blob in `CodeHtml`). Confirm with the Pages owner which `PageType` value means "code page".
3. **Chart reports are not a cares.qbl gap** — the file contains 0 charts (532 `Report::Table`, 7+ report formula columns). Keep it as a low-priority item, not on the critical path.
4. **Legacy forms**: cares has 95 `QB::Form` and 96 `QB::FormV2`. The matrix says legacy forms are *intentionally skipped* because FormV2 is the live one. Recommendation: keep skipping, but emit **one informational Skipped entry per table** instead of 95 noisy rows. Not a coverage hole.
5. **Numeric FID refs**: the file has ~268 lines using `_fid_N` or `[1234]`. PowerBase already has `FieldIdReference` (`fid_14` spelling), so the fix is a rewrite from the *source* field `Id` to the *target* field name/Fid, not a new engine feature.
6. `ImportController`'s XML doc says "file must already be PBL JSON" — stale; the handler calls `ImportDocumentParser.Parse`. Fix the comment.
7. **No size limit set on `ImportController`** (other upload controllers use `RequestSizeLimit`/`RequestFormLimits`). `cares.qbl` is 8.7 MB / 227,867 lines → default Kestrel multipart limits are 128 MB, so it works, but set an explicit limit for consistency.
8. `CLAUDE.md` is outdated (still says "skeleton, 4 field types, no relationships, no forms"). The import feature depends on far more than that. Not part of this feature, but flag it.

---

## 3. Architecture fit

Follows existing conventions (hand-registered handlers in `Program.cs`, `HandleAsync`, no MediatR, `Application/{Feature}/Commands|Queries/{UseCase}`, interfaces in `Application/Common/Interfaces`, Dapper repos in `Infrastructure/Repositories`, `ApiResponse<T>`, tenant migrations in `database/migrations/tenant` starting at **075**, since 074 is the last).

New files, by layer:

| Layer | New | Purpose |
|---|---|---|
| Domain | `Entities/ImportSourceRef.cs`, enum `ImportElementType` | Which PowerBase element came from which QBL logical ref |
| Application | `Import/Mapping/ImportMapping.cs`, `ImportMappingValidator.cs` | Per-element decisions |
| Application | `Import/Queries/ImportDiff/*` | Diff query |
| Application | `Import/Commands/UpdateAppFromPbl/*` | Apply approved decisions |
| Application | `Common/Interfaces/IImportSourceRefRepository.cs` | |
| Infrastructure | `Repositories/ImportSourceRefRepository.cs` | Dapper, tenant-scoped, `IsDeleted` aware |
| API | `ImportController` additions, `Models/Import/*` DTOs | `/diff`, `mapping`, `targetAppId` |
| DB | `075_import_source_refs.sql` | Table + index on (AppId, ElementType, SourceLogicalRef) |
| Frontend | wizard steps `mode`, `diff` | |

---

## 4. Work packages (in order)

### WP1 — Converter / PBL / importer gaps (no new modes, lowest risk, highest visible value)
| Item | Change |
|---|---|
| App variables (52) | `PblApp.Variables`; converter maps `QB::Application::Variable`; importer pass using `CreateAppVariableCommandHandler`; invalid names → Skipped with reason |
| Code pages (3) | `PblDocument.Pages`; converter maps `QB::CodePage`; importer pass via `CreatePageCommandHandler` |
| `!Var` (67) / `!BadRef` (409) | Verify every one yields a Warning and appears in the report, never an Error that blocks |
| Connected tables (5 `ConnectedRecordSet`, 244 `ConnectedColumn`) | Table is imported as a local table; Skipped entry "connection not recreated". Confirm the converter doesn't treat `ConnectedColumn::*` as fields |
| Legacy forms (95) | Collapse to one informational Skipped entry per table |
| `QB::HomePage` (1), Calendar/Timeline/Kanban/Map, role/DaysAgo conditions, cross-app relationships | Stay Skipped, each with a clear reason |
| `QB::Report::Chart` | Add to `ConvertReport` switch (not exercised by cares; do last, with a synthetic test) |
| Numeric FID refs | Build a `FidMap` (source field `Id` → field Name) in the converter, pass it to `QuickbaseDialectRewriter`; rewrite `[1226]` / `_fid_N` |
| `NotLeft` / `NotRight` with text delimiter | Rewrite rule in `QuickbaseDialectRewriter` or builtin overload; else NeedsManualReview |
| QB-only functions (`URLRoot`/`Rurl` already exist; `GetRecords`, `GetFieldValues`, `Eval`, …) | NeedsManualReview, as the spec requires |
| Formula report (spec 2.4) | Verify `FormulaTranslations` has one row per formula (467 in cares): counts of Clean / Adjusted / NeedsManualReview must sum to total. Add this as an asserted invariant in a test |

Exit criteria: full `cares.qbl` preview completes; every QBL node either imports or appears in `Skipped`/warnings; formula counts reconcile.

### WP2 — Source-ref tracking
- Migration `075_import_source_refs.sql`: `meta.ImportSourceRef (Id, TenantId?, AppId, ElementType, ElementId, SourceLogicalRef NVARCHAR(128), CreatedAt, UpdatedAt)`. (Check whether tenant DBs carry `TenantId`; the repo convention should be followed, not the doc's draft column list.)
- Create-mode importer writes one row per created Table/Field/Relationship/Report/Form/Role/Page/Variable.
- Logical refs are ≤30 chars after prefix in QBL and **stable across renames**, so they are the correct re-import key (matches spec 2.3).
- Apps not created by import: fallback match by table Name/Alias and field Name, shown in the diff as "matched by name — confirm".

### WP3 — Mapping model and JSON mapping file
- `ImportMapping { mode, targetAppId, elements[{ sourceLogicalRef, targetElementId?, action: Add|Overwrite|Skip }] }`.
- Matching by logical ref only, never DBID (spec 2.3). The `targetElementId` override lets a user resolve renames.
- Validator: refs exist in the PBL, targets belong to the target app (tenant-scoped), no duplicate targets, Overwrite requires a target.
- The wizard builds this internally; JSON upload (multipart `mapping` part) is API/developer only.

### WP4 — Update Existing App
**Diff** (`ImportDiffQueryHandler`): parse → convert → validate → load target app schema + source refs → classify each element: **New**, **Changed** (before/after), **Identical**, **OnlyInApp** (preserved; never deleted). Return `ImportDiffResult`.

**Apply** (`UpdateAppFromPblCommandHandler`): applies only Add/Overwrite decisions; rejects any *Changed* element lacking an explicit decision (**no silent overwrite**).

Per element type, reuse existing handlers:

| Element | New | Changed |
|---|---|---|
| Table | `CreateTable` | metadata only (labels, description) |
| Field | `BulkCreateFields` | `UpdateField` — label, description, required, default, settings, formula. **Type change → conflict, Skip-only** |
| Relationship | `CreateRelationship` | Skip-only for v1 (re-pointing relationships is risky) |
| Report | `CreateReport` | `UpdateReport` |
| Form / rules | `CreateForm` + layout/rules | `SaveFormLayout`, `SaveFormRule` |
| Role / permissions | `CreateAppRole` | `UpdateTablePermissions` |
| Variable / Page | create handlers | update handlers |

Same 7-pass order as Create mode.

**Transaction decision (needs a spike before building):** Create mode compensates with `DeleteApp` and has no DB transaction. Update must not delete anything on failure. `TenantUnitOfWork` exists, but each sub-handler may open its own connection. Spike: does an ambient transaction flow through the sub-handlers? If not, fall back to **per-element atomicity + a resumable report** (each element reported Applied/Failed/Skipped, nothing rolled back, re-run is idempotent thanks to WP2 source refs).

### WP5 — API
- `ImportMode.UpdateExistingApp`.
- `POST /apps/import/diff` (`file`, `targetAppId`, optional `mapping`) → `ImportDiffResult`.
- `POST /apps/import?mode=…` accepts `targetAppId`, `mapping`.
- Permissions: `AppsCreate` for Create; app-level edit permission for Update (confirm the existing `RequireAppPermission` shape).
- Explicit `RequestSizeLimit`/`RequestFormLimits`.
- Swagger docs for all new endpoints; error codes through `ExceptionHandlingMiddleware`.

### WP6 — Frontend wizard
Steps: `mode → upload → review → diff (Update only) → importing → report`.
- `mode`: Create New / Update Existing (target app via `pb-select`).
- `review`: existing preview, plus an include/exclude checkbox per detected table (cheap stand-in for the spec's per-table decision; no Master binding).
- `diff`: grouped New / Changed / Only-in-app, per-element Overwrite/Skip, `pb-confirm-dialog` before commit.
- `report`: add applied/skipped decisions and the formula translation table (Clean / Adjusted / Manual review) with filter + CSV download.
- Update `models/import.ts`, `import.service.ts` (`diff()`, `import(file, mode, mapping?, targetAppId?)`).
- Entry point for Update in the app settings, permission-guarded.

### WP7 — Large file / performance
- Time the full `cares.qbl` (95 tables, 12,400 refs) through preview and import.
- If a request would exceed a safe HTTP time, run it through the existing `Application/Imports` queue as a background job with progress; otherwise keep it synchronous. Decide from measurements, not up front.

### WP8 — Tests and docs
- Unit: mapping validator, diff classifier, source-ref repo, each new converter mapping, FID rewrite, variable-name sanitizing, formula-count reconciliation.
- Integration: Create then Update round-trip; Update preserves only-in-app elements; Update rejects undecided Changed elements; failure leaves prior data intact.
- Fixtures: trimmed `cares.qbl` (a handful of tables covering relationships, formulas, forms, roles, variables, a code page) checked in; the full file used only in a local/perf test (8.7 MB, not committed).
- Docs: update `QBL_COMPATIBILITY_MATRIX.md`, fix stale `ImportController` comment, amend the implementation doc per §2.

---

## 5. Suggested sequencing and size

| Order | Package | Relative size | Depends on |
|---|---|---|---|
| 1 | WP1 converter gaps | M | — |
| 2 | WP2 source refs | S | — |
| 3 | WP3 mapping | S | WP2 |
| 4 | WP4 + WP5 Update mode and API | L (largest risk) | WP2, WP3, transaction spike |
| 5 | WP6 frontend | M | WP5 |
| 6 | WP7 perf | S | WP1 |
| 7 | WP8 tests/docs | continuous | |

WP1 is independently shippable (improves Create mode now). Update mode (WP2–WP6) is the bulk of the work.

---

## 6. Risks

1. **Update atomicity** (see WP4 spike) — the biggest technical unknown.
2. **Spec conflict**: Import Modes says match by *ID (QuickbaseTableId → PowerBaseTableId)*; 2.3 says logical reference, never DBID. Plan follows 2.3 (as the code already does), with `targetElementId` override.
3. **Formula fidelity**: 467 formulas, many nested. Expect a real share of NeedsManualReview; the report must make that visible, not hide it. Establish a baseline count from `cares.qbl` early (WP1) so regressions are detectable.
4. **QBL deletes data on update; we must not**: Update mode never deletes; "OnlyInApp" is always preserved. Spec says "unless explicitly requested" — recommend not offering explicit delete in v1.
5. **Field type changes** on matched fields can lose data → conflict, Skip-only.

---

## 7. Open questions for you

1. Update mode v1: is **additive + overwrite of metadata** enough, with type changes, relationship changes and deletes excluded?
2. Code pages: which `PageType` should an imported QBL code page become, and should `RawCode` be split into HTML/CSS/JS or stored whole?
3. Should the full `cares.qbl` be committed as a fixture (8.7 MB) or kept local with a trimmed copy in the repo?
4. Is a per-table include/exclude checkbox in the review step acceptable as the interim replacement for the Master-mapping dropdown?
5. If the transaction spike fails, is "per-element atomic + resumable report" acceptable for Update?
