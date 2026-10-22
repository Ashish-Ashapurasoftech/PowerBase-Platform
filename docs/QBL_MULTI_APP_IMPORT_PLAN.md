# QBL → PBL Import — Multiple Applications per File

Status: **Implemented** (backend, migration `082`, unit tests, frontend) — integration tests written but not run (Docker unavailable). Failure policy: partial success. Date: 2026-10-21
Scope: Import wizard → `New App` and `Update Existing App` modes, QBL files only (PBL JSON stays single-app).

---

## 1. Requirement

| Mode | Rule |
|---|---|
| **New App** | Import **every** `QB::Application` found in the QBL file, each as its own PowerBase app. |
| **Update Existing App** | The file must contain **exactly one** application. A file with 2+ applications is rejected with a clear message. Nothing is diffed or written. |

---

## 2. Where the "one app only" limit lives today

The limit is not a single check — the whole pipeline assumes one app per document:

| # | Location | Today |
|---|---|---|
| 1 | `QblToPblConverter.Convert` (`Qbl/QblToPblConverter.cs:42`) | `qbl.Resources.FirstOrDefault(Type == "QB::Application")` — **silently ignores every other app.** Returns one `PblDocument`. |
| 2 | `ImportDocumentParser.Parse` | Returns one `(PblDocument, issues)`. Used by preview, create, diff and update. |
| 3 | `PblDocument.App` | A single `PblApp`; tables/relationships/forms/roles hang off the document, not the app. |
| 4 | `ImportAppFromPblCommandHandler.HandleAsync` | Parse → exclude tables → validate → `CreateApp` → passes 2–7 → return one `ImportReport`. On failure it deletes that one app. |
| 5 | `ImportPreviewResult` | `AppName` + flat `Tables/Relationships/Forms/Roles`. |
| 6 | `ImportMapping.ExcludedTableRefs` | Flat list of table refs. **Table refs are only unique inside one app** (`$Table_Contacts` can exist in two apps), so this cannot address multi-app. |
| 7 | `AppImportJobRunner` / `AppImportJob` / `AppImportJobDto` | One `AppPublicId` + `AppName` per job; one report; email template has one `AppName`/`AppLink`. |
| 8 | `UpdateAppFromPblCommandHandler` / `ImportDiffQueryHandler` | Both go through `PblDocumentTools.Load` → `Parse`. A multi-app file today would **silently update from app #1** — the new rule must close this. |
| 9 | Wizard (`import-wizard.component.ts`) | One preview, one `excludedTableRefs` set, one `ImportReport`. |

Good news: `ApplyDocumentAsync`, `ImportSourceRefRecorder`, `PblValidator`, the diff builder and the update planner are all already **per-document / per-app-id**. They do not need to change — we call them once per app.

---

## 3. Design decisions

1. **Keep `PblDocument` single-app.** PBL is a single-app format and the 7-pass importer depends on it. Multi-app is a *QBL container* concern: the file → N `PblDocument`s.
2. **Add `ImportDocumentParser.ParseAll`** returning `IReadOnlyList<ParsedApp>`; keep `Parse` for the single-app callers but make it fail loudly instead of silently picking the first app (see §4.2).
3. **App-scoped mapping.** Exclusions and renames are keyed by **app ref**, because table refs collide across apps.
4. **Per-app transactions, not a batch transaction.** Same reason the code already documents (each handler owns its connection; DDL would hold schema locks for the whole run). Each app keeps today's "delete the half-built app on failure" rollback.
5. **Failure policy (decision needed — see §9 Q1):** recommended = *continue with the remaining apps* and report which succeeded/failed, because apps are independent and one bad app (e.g. a duplicate name) shouldn't discard minutes of work on the others. Alternative = roll back all apps created in this run.
6. **Uniform response shape.** New-app import always returns a batch report (`apps[]`), even for one app, so the UI has one code path. Frontend and API are in the same repo, so this is a coordinated change, not a public break. (Alternative: keep `ImportReport` when exactly one app — rejected, two shapes forever.)
7. **Cross-app relationships stay out of scope.** `QB::Relationship::CrossAppChild/Parent` keep their current warning (`QBL_UNSUPPORTED_CROSS_APP_RELATIONSHIP`). Having both apps in one file makes linking possible later, but it is Master/Child territory.

---

## 4. Backend

### 4.1 Parsing / conversion

| File | Change |
|---|---|
| `Import/Qbl/QblToPblConverter.cs` | Split `Convert(QblDocument)` into `ConvertAll(QblDocument) → IReadOnlyList<QblConversionResult>` (one per `QB::Application`, in file order) and a private `ConvertApplication(appRef, appNode, qbl)` that holds today's body (passes A–D). `Convert` stays as a thin wrapper = "exactly one app or error", used by existing callers/tests. Add `QBL_MISSING_APPLICATION` only when zero apps. |
| | Everything in the body is already local to `appNode` (tables, roles, variables, pages come from `appNode.ChildMap(...)`), so it is a mechanical extraction. Verify nothing mutates shared static state; `ParameterDefinitions` (`!Var`) are document-level and read-only — fine to share. |
| | Add `QblConversionResult.AppRef` and `AppName` so results are identifiable. |
| `Import/Qbl/QblDocumentInfo.cs` (new, tiny) | `CountApplications(QblDocument)` / `ListApplications(...) → (ref, name)`; used by the single-app guard and the preview without running the full conversion. |
| `Import/Qbl/ImportDocumentParser.cs` | Add `ParseAll(string) → IReadOnlyList<ParsedApp>` where `ParsedApp(string AppRef, PblDocument Document, List<PblIssue> Issues)`. PBL JSON → list of one. `Parse` = `ParseAll` + `EnsureSingleApp` (§4.2). |

### 4.2 The single-app guard (Update flow)

One shared rule, enforced **server-side in three places** (preview for UX, diff and update for authority — the client is never trusted):

```csharp
// Domain/Exceptions or Application/Import — new constant + exception message
public const string MULTIPLE_APPS_NOT_ALLOWED_FOR_UPDATE = "IMPORT_MULTIPLE_APPS_NOT_ALLOWED_FOR_UPDATE";
```

- Put the check in `PblDocumentTools.Load(content, validator)` (already the single entry used by `ImportDiffQueryHandler` and `UpdateAppFromPblCommandHandler`) → calls `ParseAll`, and if `count > 1` throws `BadRequestException(code, message)`.
- Count rule: top-level `Resources` entries with `Type == "QB::Application"` exactly (not `QB::Application::Variable`, etc.). Zero → existing `QBL_MISSING_APPLICATION`.
- Message (names help the user pick the right export):

  > This file contains 3 applications (Sales CRM, HR Portal, Inventory). Update Existing App works with a file that contains exactly one application. Export a single application from Quickbase and try again, or use **New App** import to create all of them.

- HTTP `400`, `{ error: { code: "IMPORT_MULTIPLE_APPS_NOT_ALLOWED_FOR_UPDATE", message } }` — matches the existing error envelope. The app list in the message is capped (e.g. first 5 + "and N more").
- The Administrator-role check (`RequireAppRoleAsync`) still runs first in `UpdateAppFromPblCommandHandler`, so the rejection never leaks file contents to someone without access.
- Does **not** compare the file's app name with the target app's name (today's behaviour, unchanged). Optional follow-up: warn on mismatch.

### 4.3 Preview

| File | Change |
|---|---|
| `Queries/ImportPreview/ImportPreviewQuery.cs` | Add `ImportMode Mode = CreateNewApp`. |
| `ImportPreviewResult.cs` | Add `AppCount` and `Apps: List<ImportPreviewAppItem>`. `ImportPreviewAppItem` = `{ AppRef, AppName, IsValid, Tables, Relationships, Forms, Roles, Variables, Pages, Errors, Warnings }` (today's flat lists move inside it). Top-level `IsValid` = no file-level errors **and** at least one valid app. Top-level `Errors` hold file-level problems only (parse errors, the multi-app rejection). |
| `ImportPreviewQueryHandler.cs` | `ParseAll`; validate each document independently with `PblValidator`; build one `ImportPreviewAppItem` per app. If `Mode == UpdateExistingApp && AppCount > 1` → return `IsValid=false` with the `IMPORT_MULTIPLE_APPS_NOT_ALLOWED_FOR_UPDATE` error and **no** `Apps` detail (nothing to review). |
| `ImportController.Preview` | Accept `[FromQuery] ImportMode mode = CreateNewApp`. |

Preview stays a pure read: nothing is created.

### 4.4 Create (New App)

| File | Change |
|---|---|
| `ImportAppFromPblCommandHandler.cs` | Extract the body of `HandleAsync` after parsing into `ImportOneAsync(ParsedApp, AppMappingEntry?, ct) → ImportReport` (unchanged logic: exclude tables → validate → CreateApp → passes → source refs). New `HandleBatchAsync(command)`: `ParseAll` → for each included app run `ImportOneAsync` in file order inside try/catch → collect `ImportBatchReport`. Keep `HandleAsync` for PBL/single-app callers (`UpdateAppFromPblCommandHandler` uses `ApplyDocumentAsync` only, so it is untouched). |
| `ImportReport.cs` | Add `ImportBatchReport { List<ImportReport> Apps; List<ImportAppFailure> Failed; totals }`. `ImportAppFailure { AppRef, AppName, Code, Message }` (use the same `Describe(ex)` text logic the job runner has so internals aren't leaked outside Development). |
| Failure handling | A failed app is already auto-deleted by the existing catch block. Record it in `Failed`, continue (per Q1). A cleanup failure (`AggregateException` today) is reported in `Failed[].Message` with the "must be deleted manually" text. `OperationCanceledException` aborts the batch; apps already completed stay. |
| Duplicate names | `CreateAppCommandHandler` throws `DuplicateException("App","name",…)`. Cases: (a) name already exists in the tenant, (b) two apps in the file share a name. Plan: pre-check names in the **preview** (tenant lookup via `IAppRepository`) and surface a per-app warning/blocking error `APP_NAME_ALREADY_EXISTS`; allow the user to rename via `NameOverride` (§4.5). At import time the exception is still caught as a per-app failure — preview is advisory, not a lock. |
| Progress | `ImportProgress.Report(stage, percent)` is absolute 0–99. Add `ImportProgress.BeginApp(index, count, name)` that sets an `AsyncLocal` slice so the existing calls are remapped to `[index/count … (index+1)/count]` and the stage is prefixed `"App 2 of 3 · Inventory — Creating tables"`. No change to the dozens of existing `Report` calls. |
| Permissions | Each app needs `apps.create` (already checked once by `[RequirePermission(AppsCreate)]`). Check for tenant app-count/plan limits in `CreateAppCommandHandler`; if a limit exists, a batch can hit it mid-way → it surfaces as a per-app failure, but **preview should warn** ("only N more apps can be created"). |

### 4.5 Mapping contract (`Import/Mapping/ImportMapping.cs`)

```jsonc
{
  "excludedTableRefs": ["$Table_Old"],          // legacy: applies to the single app only (kept for Update + old clients)
  "apps": [                                      // New App only
    { "appRef": "$App_Sales", "include": true,  "nameOverride": null, "excludedTableRefs": ["$Table_Audit"] },
    { "appRef": "$App_HR",    "include": false }
  ],
  "elements": [ ... ]                            // Update only, unchanged
}
```

- New `ImportMappingApp` class + `Apps` list. An app absent from `apps` = included, no overrides (so a bare upload imports everything).
- Validation (`ImportMapping.Parse` / handler): unknown `appRef` → `400` `MAPPING_UNKNOWN_APP`; all apps excluded → `400` `IMPORT_NO_APPS_SELECTED`; `nameOverride` trimmed, non-empty, same length/character rules as `CreateAppCommandValidator`, unique among the batch.
- In Update mode `apps` is ignored (and `excludedTableRefs` keeps working as today), so the Update contract is unchanged.

### 4.6 Controller (`ImportController.cs`)

| Endpoint | Change |
|---|---|
| `POST /apps/import/preview?mode=` | `mode` param; returns the new `ImportPreviewResult`. |
| `POST /apps/import` | Returns `ImportBatchReport` (201) or `AppImportJobDto` (202). Swagger `ProducesResponseType` updated. |
| `POST /apps/import/diff`, `/update` | Unchanged signatures. Multi-app files now return `400 IMPORT_MULTIPLE_APPS_NOT_ALLOWED_FOR_UPDATE` via `PblDocumentTools.Load`. |
| `RunsInBackground` | Today: size > 3 MB. A multi-app file can be slow even when small, and a long synchronous request will time out. Change to `RunsInBackground(size, appCount)` = `size > 3 MB || appCount > 1`. Get `appCount` cheaply with a line-based count of `Type: QB::Application` (exact line match, no full parse) in `ImportController`/`AppImportJobService`; the job re-validates with the real parse anyway. |
| Docs | Fix the stale XML doc ("one app"); add the new error code to `docs/api.md`. |

### 4.7 Background jobs

| Item | Change |
|---|---|
| `AppImportJobRunner.RunAsync` | Call `HandleBatchAsync`; store `JsonSerializer.Serialize(batchReport)` in `ResultJson`. Job status: `Completed` if ≥1 app created; `Failed` only if **all** apps failed (or file-level error). Partial success = `Completed` with `Failed[]` non-empty (shown in the report and the email). |
| `AppImportJob` / `AppImportJobDto` | Add `AppCount` (int, total apps in file) and `AppsCreated` (int). `AppPublicId`/`AppName` stay populated for single-app jobs (back-compat with the app list and email links); for multi-app, `AppName` = `"Sales CRM +2 more"`, `AppPublicId` = first created app. Migration `database/migrations/tenant/0NN_app_import_job_multi_app.sql` (next free number after the latest): `ALTER TABLE … ADD AppCount INT NOT NULL DEFAULT 1, AppsCreated INT NOT NULL DEFAULT 0` — idempotent (`IF COL_LENGTH(...) IS NULL`). Do **not** edit `081_…`. |
| `AppImportEmailTemplates` | New placeholders: `AppCount`, `AppsCreated`, `AppsFailed`, `AppNames` (comma list), `AppList` (HTML-escaped list with a link per app). Existing placeholders keep working (`AppName` = joined names, `AppLink` = first app, or the apps list page when more than one) so tenant overrides don't break. Update default Completed/Failed bodies; add a "partial" paragraph when some apps failed. `Render(..., encode: true)` already HTML-encodes values — `AppList` must be built from encoded parts, then inserted via a placeholder flagged raw-safe, or rendered as plain text with line breaks (simplest: plain text list; links only to first app). |
| Progress | Uses §4.4 slicing; one job, one progress bar, stage text carries "App i of n". |
| Restart behaviour | Unchanged: an interrupted job is `FailInterruptedAsync`. A restart mid-batch leaves already-finished apps in place and reports a failure — document it; do not try to resume. |

### 4.8 What does *not* change

`PblValidator`, `ApplyDocumentAsync` passes, `ImportSourceRefRecorder` (keyed by the new app's PublicId), diff builder, update planner, `UpdateAppFromPblCommandHandler` logic, tenant-filter and RowVersion rules (all writes still go through the existing command handlers, so TenantId / soft-delete / DDL-through-SchemaEngine rules in CLAUDE.md are inherited).

---

## 5. Frontend (`PB-Frontend`)

Files: `core/api/models/import.ts`, `core/api/services/import.service.ts`, `pages/apps/apps-list/import-wizard/import-wizard.component.{ts,html}`, `apps-list.component.{ts,html}` (job rows).

### 5.1 Models / service

- `ImportPreviewResult` → `{ isValid, appCount, apps: ImportPreviewApp[], errors, warnings }`; `ImportPreviewApp` holds today's `tables/relationships/forms/roles/...` plus `appRef`, `appName`, `isValid`.
- `ImportMapping` → add `apps?: { appRef; include; nameOverride?; excludedTableRefs }[]`.
- New `ImportBatchReport`, `ImportAppFailure`; `AppImportJob` gains `appCount`, `appsCreated`; update `isAppImportJob` guard and `jobReport` typing.
- `ImportService.preview(file, mode)` sends `?mode=`.

### 5.2 Wizard — New App mode

```
mode → upload → review (apps) → importing → report | background
```

- **Review step** becomes an app list. For `appCount === 1` it looks like today (no regression). For N > 1:
  - Header: "This file contains **N applications**." + summary counts for the *included* apps (tables / fields / reports …).
  - One collapsible card per app: include checkbox (default on), editable name (pre-filled; inline error if it clashes with an existing app or another app in the file), per-app status badge (Valid / Has errors / Name already exists), and the existing table tree with the per-table include toggles **scoped to that app**.
  - An app with blocking errors is shown with its errors, forced to *excluded*, and cannot be ticked. Other apps can still be imported; the confirm button reads **"Import 2 of 3 apps"**.
  - Select all / none; confirm disabled when zero apps are included or an included app has an unresolved name clash.
- State: replace `excludedTableRefs: Set<string>` with `Map<appRef, Set<tableRef>>`, add `includedApps: Set<appRef>` and `nameOverrides: Map<appRef,string>`; `mappingBase()` serialises to `apps[]`. Update `includedTables`, `totalFieldCount`, `supportedFieldCount`, `pendingValidationFieldCount` to sum across included apps.
- **Importing / background step:** text says "Importing N applications…" and shows the job stage ("App 2 of 3 · Inventory — …"); the apps list shows one job row labelled "Sales CRM +2 more" with a progress bar.
- **Report step:** a result per app (open-app link, counts, skipped/formula issues, collapsible), a **Failed** section (app name + reason), and a banner for partial success ("2 of 3 apps imported, 1 failed"). Totals row at the top. Single-app report renders as today.

### 5.3 Wizard — Update Existing App mode

- Unchanged flow (`mode → upload → review → diff → importing → report`), plus the guard:
  - `loadPreview()` passes `mode`. If the response has the `IMPORT_MULTIPLE_APPS_NOT_ALLOWED_FOR_UPDATE` error (or `appCount > 1` while `isUpdateMode`), **stay on the upload step**, show the server message in the existing error banner (`loadError`), clear the selected file and the diff button; do not advance to review.
  - Banner includes a secondary action: **"Import as new apps instead"** → `setMode('CreateNewApp')` keeping the chosen file, so the user isn't forced to re-pick it.
  - Upload step helper text in Update mode: "The file must contain a single application."
- Defence in depth: `loadDiff()` / `confirmUpdate()` error handlers already surface `err.error.error.message`, so a direct API call with a multi-app file shows the same text.
- The review step in Update mode only ever sees `apps.length === 1`; render it exactly as today (flatten `apps[0]`).

### 5.4 UX copy

| Where | Text |
|---|---|
| Upload, Update mode (hint) | The file must contain exactly one application. |
| Rejection | *(server message, §4.2)* |
| Review, New mode | This file contains {N} applications. Choose which ones to import — each becomes a separate app. |
| Name clash | An app named “{name}” already exists. Rename it to import. |
| Partial result | {X} of {N} apps were imported. {Y} could not be imported — see details below. |

---

## 6. Validation rules (summary)

| # | Rule | Mode | Where enforced | Result |
|---|---|---|---|---|
| V1 | File parses (YAML/JSON) | both | `ParseAll` | `INVALID_YAML` / `INVALID_JSON` |
| V2 | ≥ 1 `QB::Application` | both | converter | `QBL_MISSING_APPLICATION` |
| V3 | Exactly 1 application | **Update** | preview + `PblDocumentTools.Load` (diff, update) | `400 IMPORT_MULTIPLE_APPS_NOT_ALLOWED_FOR_UPDATE` |
| V4 | At least one app selected | New | handler + mapping validator | `400 IMPORT_NO_APPS_SELECTED` |
| V5 | Each included app passes `PblValidator` | New | per app; invalid app is excluded/reported, others continue | per-app errors |
| V6 | App names unique within the file and vs. the tenant (after `nameOverride`) | New | preview (advisory) + `CreateApp` (authoritative) | `APP_NAME_ALREADY_EXISTS` / per-app failure |
| V7 | `appRef` in mapping exists | New | mapping validator | `400 MAPPING_UNKNOWN_APP` |
| V8 | Caller is Administrator of the target app | Update | unchanged | `403` |
| V9 | Upload ≤ 64 MB | both | unchanged | 413 |

---

## 7. Work packages (in order)

| # | Package | Layer | Depends on | Size |
|---|---|---|---|---|
| 1 | Extract `ConvertApplication`, add `ConvertAll`, `ParsedApp`, `ParseAll`, app counting | Application | — | S |
| 2 | Single-app guard in `PblDocumentTools.Load` + error code + message; preview `Mode` | Application/API | 1 | S |
| 3 | `ImportMapping.Apps` + validation; app-scoped exclusion | Application | 1 | S |
| 4 | `ImportOneAsync` extraction, `HandleBatchAsync`, `ImportBatchReport`, per-app failure handling, progress slicing | Application | 1, 3 | M |
| 5 | Preview result restructure + name-clash pre-check | Application | 1, 3 | M |
| 6 | Controller: `mode` on preview, new response types, `RunsInBackground(size, appCount)`, Swagger, `docs/api.md` | API | 2, 4, 5 | S |
| 7 | Jobs: runner → batch, DTO/entity/migration, email placeholders + defaults | Application/Domain/DB | 4 | M |
| 8 | Frontend models + service + wizard (review, report, upload guard, job rows) | Frontend | 5, 6, 7 | L |
| 9 | Tests (§8), docs (`QBL_TO_PBL_*`, `api.md`, note in `CLAUDE.md`) | all | all | M |

Suggested PR split (one feature per PR, per CLAUDE.md): **PR 1** = packages 1–3 (parser/guard/mapping, Update rejection ships first and is independently useful — it also fixes the silent "update from app #1" hazard); **PR 2** = 4–7 (backend create + jobs); **PR 3** = 8 (frontend). Commit scopes: `feat(api)`, `feat(db)`, `feat(apps)`.

---

## 8. Tests

**Unit (`PowerBase.UnitTests/Import`)**
- Converter: file with 3 apps → 3 `QblConversionResult`s, in order, each with only its own tables/roles/variables; same `$Table_X` ref in two apps stays isolated; zero apps → `QBL_MISSING_APPLICATION`; single-app file produces identical output to today (regression — reuse `QblToPblConverterTests`, `CaresRealFileTests`).
- `ParseAll` / `Parse`: PBL JSON → 1 app; `Parse` on multi-app throws the guard exception.
- `PblDocumentTools.Load`: multi-app → `BadRequestException` with code and a message that lists names (capped); single-app unchanged.
- Preview handler: New + 3 apps → 3 `Apps`, mixed valid/invalid; Update + 2 apps → `IsValid=false` + code, no apps; name-clash warning.
- `ImportMapping.Parse`: unknown `appRef`, all excluded, duplicate `nameOverride`, legacy `excludedTableRefs` still honoured.
- `HandleBatchAsync` (NSubstitute): all succeed; app 2 throws → app 1 kept, app 2 deleted + reported, app 3 still imported; cleanup failure reported; cancellation stops; progress slices monotonic across apps.
- `AppImportJobRunner`: partial success → `Completed` with failures; all fail → `Failed`; email values (`AppCount`, `AppNames`) and old placeholders; `RunsInBackground(small, 2)` is true.
- `UpdateAppFromPblCommandHandler` / `ImportDiffQueryHandler`: multi-app file rejected before any repository write.

**Integration (`PowerBase.IntegrationTests/Import/ImportTests.cs`, Testcontainers)**
- `POST /apps/import` with a 2-app QBL → 201/202, two apps listed under `GET /apps`, each with its own tables, source refs recorded per app.
- Same with one app excluded via `mapping.apps`.
- `POST /apps/import/diff` and `/update` with a 2-app QBL → `400 IMPORT_MULTIPLE_APPS_NOT_ALLOWED_FOR_UPDATE`, target app unchanged.
- Duplicate app name mid-batch → partial report.
- Tenant isolation: a second tenant sees none of the apps (existing pattern).

**Fixtures:** build a small synthetic 2–3 app QBL by composing `cares.qbl`-style nodes (do not commit the 8.7 MB real file). Get at least one **real** multi-app export to confirm the assumption in §9 Q5.

**Frontend:** component tests for review step (include/exclude, rename clash, "Import 2 of 3"), Update-mode rejection stays on upload and offers "Import as new apps instead", report with partial failure.

---

## 9. Open questions / risks

| # | Question / risk | Recommendation |
|---|---|---|
| Q1 | **All-or-nothing vs. partial success** for a batch | Partial success + clear report (apps are independent; rollback-all would waste long runs and still needs per-app cleanup). Confirm with product. |
| Q2 | Duplicate **app name**: auto-suffix ("Sales CRM (2)") or force a rename? | Force a rename in the wizard (explicit), no silent suffixing — consistent with how field labels are reported. |
| Q3 | Should a multi-app **Update** file be rejected even if one app's name matches the target? | Yes — rule is "exactly one", simple and predictable. Mention the "Import as new apps instead" shortcut. |
| Q4 | Cross-app relationships between apps in the same file | Out of scope now; keep warning. Candidate follow-up once Master/Child exists. |
| Q5 | **Do real multi-app QBL exports exist, and how do they look?** `cares.qbl` has one app. The plan assumes several `QB::Application` entries under top-level `Resources`. | Get a real sample before PR 1; if apps are nested or exported as separate documents, only §4.1 changes. |
| Q6 | Memory/time of very large multi-app files (≈ 9 MB per app; the whole file is read into a string and the job stores the bytes) | Acceptable at current 64 MB limit; parse once and reuse the `QblDocument` across apps. Revisit the limit/streaming if exports exceed it. |
| Q7 | Tenant limits on number of apps | Check `CreateAppCommandHandler` / plan limits; surface in preview if present. |
| Q8 | Breaking response shape for `POST /apps/import` | Frontend and API ship together; call out in PR description; no external consumers known — confirm. |
| R1 | Silent data loss today: multi-app files already import only app #1 without telling anyone | PR 1 should land first so no path silently drops apps. |
| R2 | Long synchronous requests for small multi-app files | Mitigated by `RunsInBackground(size, appCount)`. |
| R3 | Email template overrides in tenants referencing `{{AppName}}` / `{{AppLink}}` | Placeholders stay valid with defined multi-app semantics (§4.7). |

---

## 10. Definition of done

- A QBL with N apps imports N separate apps (or a reported subset) from the wizard and via the API, in the foreground or as one background job with a single progress bar and one email.
- Update Existing App with a 2+ app file is rejected at preview, diff and update with the same clear message; no write occurs.
- Single-app QBL and PBL behave exactly as before (regression tests green, incl. `CaresRealFileTests`).
- `dotnet build` / unit / integration tests pass; Swagger and `docs/api.md` updated; migration added (never editing an existing one).
