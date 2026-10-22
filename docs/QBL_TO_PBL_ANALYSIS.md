# QBL → PBL Import — Codebase Analysis

> Read-only analysis of the two project folders below, done to plan the "QBL to PBL" feature (importing a Quickbase QBL YAML export into PowerBase).
> Date: 2026-10-15.

| Part | Folder | Git branch at time of analysis |
|---|---|---|
| Backend | `E:\ConsoleApp\PowerBase\PowerBase-Platform` | `FiltersAndPermission` |
| Frontend | `E:\ConsoleApp\PowerBase\UI\PB-Frontend` | `Feature/PB-2-Mahesh` |

All backend paths below are relative to `PowerBase-Platform\`. All frontend paths are relative to `PB-Frontend\src\app\` unless stated otherwise.

---

## 1. Headline

**A QBL → PBL import already exists and works end to end, but only for the "Create New App" mode.** It covers:

- Backend: a YAML parser, the QBL→PBL converter, a PBL validator, a formula translator, a multi-pass import handler, and preview/import API endpoints.
- Frontend: a four-step import wizard on the Apps list page.
- Tests: unit and integration tests.

The planned feature is therefore an **extension** of this pipeline (more mappings, more import modes, a richer wizard), not a new build.

---

## 2. Backend — tech stack and structure

- **Platform:** .NET 8, ASP.NET Core Web API (Controllers), Dapper, SQL Server / Azure SQL.
- **Solution:** `PowerBase.sln`.
- **Key packages** (`src/PowerBase.Application/PowerBase.Application.csproj`): YamlDotNet 18.1.0, FluentValidation, ClosedXML, Scriban, NCrontab.

| Project | Purpose |
|---|---|
| `src/PowerBase.API` | Controllers (40), Attributes, Middleware, Models (DTOs), Pipelines workers, `Program.cs` |
| `src/PowerBase.Application` | Use-case handlers per feature, repository interfaces (`Common/Interfaces`, 55 files), Import / Imports logic |
| `src/PowerBase.Domain` | Entities (59), Enums, Constants, Exceptions, `FieldSettings`, ValueObjects |
| `src/PowerBase.Infrastructure` | Dapper repositories (35), Services (SchemaEngine, JWT, Blob, Search…), Persistence, UOW, Migrations runner, Imports |
| `src/PowerBase.Formula` | Standalone formula engine (Lexer, Parser, Binder, TypeChecker, Evaluator, Builtins) |
| `src/PowerBase.Migrator` | Console migrator |
| `tests/PowerBase.UnitTests`, `tests/PowerBase.IntegrationTests`, `tests/PowerBase.Formula.Tests`, `tests/PowerBase.Imports.Tests` | xUnit test projects |

### Conventions

- **Controllers:**
  - Use `[ApiController]`, `[Route(...)]` and `[RequireAuth]`.
  - Use `[RequirePermission(PermissionCodes.X)]` or `[RequireAppPermission]` for authorization. These are custom attributes in `src/PowerBase.API/Attributes`.
- **Responses:** `ApiResponse<T>` / `ApiListResponse<T>` (`src/PowerBase.API/Models/ApiResponse.cs`), shaped as `{ data, meta }`.
- **Errors:**
  - Typed domain exceptions (`BadRequestException`, `ValidationException`, `DuplicateException`, …) are mapped by `src/PowerBase.API/Middleware/ExceptionHandlingMiddleware.cs`.
  - The same folder holds `JwtMiddleware` and `QueryContextMiddleware`.
- **Handlers:**
  - There is no MediatR. Handlers are plain classes with `HandleAsync`, laid out as `Application/{Feature}/Commands|Queries/{UseCase}/`.
  - Every handler is registered by hand in `src/PowerBase.API/Program.cs` with `builder.Services.AddScoped<...Handler>()`.
  - The import handlers are registered at `Program.cs:486-489`. `FormulaEngine` is a singleton at `Program.cs:318`.
- **Repositories:** interface in `Application/Common/Interfaces`, Dapper implementation in `Infrastructure/Repositories`. `IQueryContext` carries the user and tenant.
- **DDL:** only through `src/PowerBase.Infrastructure/Services/SchemaEngineService.cs`. Data tables are named `data.t_{id}` and columns `f_{fid}`.

### Database and migrations

- **Database layout:** per-tenant databases plus a control database. `Persistence/TenantConnectionFactory.cs` and `TenantConnectionResolver.cs` pick the tenant DB; `ControlConnectionFactory.cs` connects to the control DB.
- **Migration folders:**
  - `database/migrations/control/`: 16 scripts for the shared control DB.
  - `database/migrations/tenant/`: about 94 scripts, numbered `001`–`074`, run against each tenant DB. **New schema for this feature goes here, starting at 075.**
  - `database/migrations/*.sql` at the root: the older shared-DB set, which is legacy.
- **Runner:** `src/PowerBase.Infrastructure/Migrations/MigrationRunner.cs` records applied scripts in `dbo._migrations`.

> **Note:** `CLAUDE.md` still describes the "skeleton" phase: 4 field types, a single shared DB, and formulas/relationships/imports listed as out of scope. The real codebase is well past that. Update `CLAUDE.md` before starting this feature so it does not block or mislead.

---

## 3. Backend — how PowerBase models an app

Entities are in `src/PowerBase.Domain/Entities`. The tables are created by `database/migrations/tenant`.

| Concept | Entity / table | Notes |
|---|---|---|
| App | `App.cs` / `meta.App` (tenant `003`) | Name, Description, Icon, Color, Status, JSON Formatting / SecurityOptions / Branding / LayoutSettings, DefaultAppRoleId, IsEncrypted, RowVersion. **No master/child/ownership columns.** |
| Table | `AppTable.cs` / `meta.AppTable` | Alias, Singular/PluralLabel, Icon, KeyFieldId, DisplayFieldId, DefaultRecordPickerField1-3Id, CustomDataRule, PhysicalTableName, DefaultReportSettings |
| Field | `AppField.cs` / `meta.AppField` | TypeCode, Name (stable identifier), Label, **Fid** (int, the Quickbase-style FID), Settings (JSON), DefaultValue, IsRequired/IsUnique/IsHidden/IsEncrypted/IsAutoFill… Versions are in `AppFieldVersion` / `AppFieldVersionChange` (tenant `054`). |
| Field types | `Domain/Enums/FieldTypeCode.cs` | 29 types: Text, TextMultiLine, RichText, SingleSelect, MultiSelect, Number, Currency, Percent, Rating, Date, DateTime, Time, Duration, Boolean, Email, Phone, Url, File, Address, User, MultiUser, DateRange, NumericRange, Formula, Reference, Lookup, Summary, ReportLink, ActionButton |
| Field settings | `Domain/FieldSettings/FieldSettingsModels.cs` | One settings class per type: TextSettings, NumericSettings, DateSettings, AddressSettings, UserFieldSettings, FormulaSettings plus the Formula* family, and so on |
| Relationship | `Relationship.cs` / `meta.Relationship` (tenant `019`, `024`, `056`) | One-to-many. ParentTableId, ChildTableId, ReferenceFieldId/ReferenceFid (a physical BIGINT FK column on the child), ProxyFieldId, DisplayKeyFieldId. Lookups (child) and Summaries (parent) are AppFields whose Settings reference the relationship and are computed at read time. |
| Relationship logic | `Application/Relationships/` (28 files) | `Commands/CreateRelationship/CreateRelationshipCommandHandler.cs` creates the Reference field, the Lookup and Summary fields, and an automatic ReportLink in one call. Also AddLookupFields, AddSummaryField, UpdateDisplayKey, UpdateReferenceFilter, … |
| Roles / permissions | `AppRole`, `AppRolePermission`, `AppRoleTablePermission`, `AppRoleFieldPermission`, `AppRoleRecordFilter`, `AppRoleReport`, `AppRoleTableFormOverride`, `Capability`, `CapabilityPermission`; tables `meta.AppRole*`, `meta.AppRolePage` | Table permission fields: ViewScope / ModifyScope (`None` / `OwnRecords` / `AllRecords`), CanAdd, CanDelete, CanSaveSharedReports, CanEditFieldProperties, FieldAccessLevel. Also Groups (`Group`, `GroupMember`, `GroupApp`) and `AppUser`. |
| Forms | `Form` → `FormPage` → `FormSection` → `FormSectionBlock` → `FormElement`; `FormRule` + `FormRuleCondition` + `FormRuleAction` (tenant `004`, `032`, `053`–`063`) | Handlers are in `Application/Forms/` (CreateForm, SaveFormLayout, CreateFormRule, SaveFormRule, …) |
| Reports | `Report.cs` / `meta.Report` | ReportType (`Table` / `Summary` / `Chart`) plus Definition (JSON). Config is validated in `Application/Reports/Validation` (`ReportConfigValidatorRegistry`). Grid-edit rules and forms are in tenant `061`/`062`. |
| Pages | `Page.cs`, `PageVersion.cs` / `meta.Page` (tenant `027`) | PageType `Dashboard` or `Code` (CodeHtml/CodeCss/CodeJs), versioned, nav settings, default home |
| App variables | `AppVariable.cs` / `meta.AppVariable` | Referenced from formulas |
| Pipelines | `Pipeline*` entities, tenant `034`+ | Automations, not part of the QBL import |
| Formula engine | `src/PowerBase.Formula` | `Lexing/Lexer.cs`, `Syntax/Parser.cs`, `Binding/Binder.cs`, `Checking/TypeChecker.cs`, `Evaluation/Evaluator.cs`, `Builtins/*` (Text, Number, Date, Logical, List, Record, User, System). Adapters live in `Application/Formulas` (`AppFieldSchema`, `FormulaCanonicalizer`, `ValidateFormulaQueryHandler`). It already supports Quickbase-style `var` declarations, `If` without an else branch, `UserToName` and `Dbid()`. |

---

## 4. Backend — the existing QBL / PBL import (`src/PowerBase.Application/Import`)

### 4.1 What "PBL" is

**PBL = PowerBase Blueprint Language**, a JSON import format (`Import/Pbl/PblModels.cs:3-20`). It is the format a QBL file is converted into, and it can also be authored by hand.

```
PblDocument { Version "1.0", App, Tables[], Relationships[], Forms[], Roles[] }
  PblTable        { LogicalRef, Name, Labels, Icon, Fields[], Reports[] }
  PblField        { LogicalRef, Name, Label, TypeCode, IsRequired, DefaultValue, Settings(JSON),
                    ResultType, FormulaExpression, AliasHint }
  PblReport       { LogicalRef, Name, ReportType, Columns[fieldName], SortFields[] }
  PblRelationship { ParentTableRef, ChildTableRef, ReferenceFieldName, Lookups[], Summaries[] }
  PblForm         { TableRef, Sections[ Blocks[ Elements[] ] ], Rules[ Conditions[], Actions[] ] }
  PblRole         { Name, IsDefault, TablePermissions[ FieldPermissions[] ] }
```

- Every element has a `LogicalRef`, for example `$Table_Clients`. Cross-references use logical refs or field `Name`s, never physical IDs. A source DBID may be carried in `AliasHint`, but only for cosmetic purposes.
- Files:
  - `PblModels.cs`, `PblRelationshipModels.cs`, `PblFormModels.cs`, `PblRoleModels.cs`: the model classes.
  - `PblSerializer.cs`: System.Text.Json.
  - `PblValidator.cs`: 580 lines. It produces `PblIssue` items with Error/Warning severity and codes. `SupportedFieldTypeCodes` is at `:46`, `FormulaTypeCode` at `:58` and `IsCreatableFieldType` at `:62`.
- **PBL is import-only today.** There is no PBL export.

### 4.2 Parsing QBL (`Import/Qbl`)

| File | Role |
|---|---|
| `DocumentFormatDetector.cs` | Sniffs the content: `{` means PBL JSON, anything else is QBL YAML |
| `ImportDocumentParser.cs:34` | The single entry point. Returns `(PblDocument, conversionIssues)`. |
| `QblSerializer.cs` | Parses YAML with YamlDotNet into a loose AST and resolves `!Var` against `ParameterDefinitions`. If parsing fails, it retries once with `QB::CodePage` blocks stripped (`QblRawTextSanitizer.cs`). |
| `QblYamlTags.cs` | A custom `INodeDeserializer` for the `!Ref` (mapping), `!BadRef` (scalar) and `!Var` tags |
| `QblAst.cs` | `QblDocument`, `QblResourceNode` (Type / Properties / Children), `QblRef`, `QblBadRef`, `QblVarRef` |
| `QblFieldTypeMap.cs` | Maps QB field types to PowerBase types: scalars at `:22-42`, formula result types at `:51-63`, relationship types at `:82-86`, skipped system fields at `:94-98`, address shadow fields at `:125-130`, unsupported types at `:153` |
| `QblToPblConverter.cs` (1516 lines) | `Convert()` `:37`; table-ID rewrite `RewriteQuickbaseTableIdRefs` `:293`; `ConvertTablePermission` `:380`; relationship building `:572` / `:618`; `ConvertField` `:735`; `ConvertReport` `:810`; `ConvertForm` (FormV2) `:934`; `ConvertFormRule` `:1185` |
| `QBL_COMPATIBILITY_MATRIX.md` | The authoritative QBL→PBL mapping table, built from a real 55,844-line QBL v0.12 export |

### 4.3 What is already mapped

- **Fields:** all scalar types (Text … MultiUser, Address), plus formula fields with result types Text, Bool, Number, Date, DateTime, Duration and User.
- **Relationships:** Reference, Lookup and Summary are folded into `PblRelationship`, including Address sub-field lookups.
- **System fields:** RecordID, DateCreated and similar are skipped, because PowerBase seeds its own.
- **Reports:** `QB::Report::Table` and `GridEdit` map to Table; `Summary` maps to Summary.
- **Forms:** `QB::FormV2` maps Page → Section → Column (Block) → Element. Field and RichText elements are mapped; Group elements are flattened.
- **Form rules:** RunOn, TrueWhen, 12 condition operators, "formula is true" expression mode, and 10 actions plus `ChangeValue`.
- **Roles:** roles plus table-level `RolePermissions` (CRUD scopes), and report role visibility.

### 4.4 Not yet supported (flagged as warnings, never silently dropped)

| Gap | Note |
|---|---|
| `QB::Report::Chart` | PowerBase supports Chart, but `ConvertReport` doesn't map it yet. **Easy add.** |
| `QB::Application::Variable` | Marked "no equivalent" in the matrix, but PowerBase **has** `AppVariable`. **Easy add.** |
| `QB::CodePage` | PowerBase now has Code pages (`Page.PageType = Code`), so this is a candidate |
| `QB::FormV2::Element::Report` (embedded report) | The settings contract hasn't been confirmed |
| Form conditions `Role`, `DaysAgo`, `Range`, `Relative` | No PowerBase operator exists for these |
| `Action::Change` with `Value: !Ref{Field}` | Only literal values are supported |
| `CustomAccessCriteria` (row-level filter) | Uses a different query language; not translated |
| Calendar / Timeline / Kanban / Map reports | No equivalent |
| Cross-app relationships | Needs Master/Child |
| Predecessor, WorkDate, VCard, ICalendar fields | No equivalent |
| Formula: numeric `[1226]` field refs, `NotLeft` / `NotRight` with a text delimiter, Quickbase API URLs | Flagged for manual review |
| Pipelines / automations | Out of scope |

### 4.5 Formula translation (`Import/FormulaTranslation`)

- `FormulaTranslator.cs` compiles each formula with `FormulaEngine` against the table's real, already-created fields. The result status is `Clean`, `Adjusted` or `NeedsManualReview`.
- `QuickbaseDialectRewriter.cs` (called at `FormulaTranslator.cs:66`) wraps non-text operands of `&`, and non-text arguments to text parameters, in `ToText()`.
- `[_DBID_TABLE]` is rewritten to `Dbid("Table")` inside the converter.

### 4.6 Import orchestration

`Import/Commands/ImportAppFromPbl/ImportAppFromPblCommandHandler.cs` (845 lines) reuses the existing feature handlers in passes (documented at `:27-55`):

1. `CreateAppCommandHandler`: the app, tables and scalar fields.
2. `CreateAppRoleCommandHandler` + `UpdateTablePermissionsCommandHandler`: roles and table permissions.
3. `CreateRelationshipCommandHandler`: Reference, Lookup, Summary and ReportLink fields.
4. `FormulaTranslator` + `BulkCreateFieldsCommandHandler`: formula fields.
5. `CreateFormCommandHandler` + `SaveFormLayoutCommandHandler`: forms.
6. `GetFormLayoutQueryHandler` + `CreateFormRuleCommandHandler` / `SaveFormRuleCommandHandler`: form rules.
7. `CreateReportCommandHandler`: reports.

- It runs **synchronously in one HTTP request**.
- There is no DB transaction. On failure it compensates by calling `DeleteAppCommandHandler` (`:368-376`).
- The output is `ImportReport.cs`: created counts, `Skipped[]` and `FormulaTranslations[]`.
- **Modes:** `ImportMode` has only `CreateNewApp` (`ImportAppFromPblCommand.cs`). "Update Existing App" and "Create Child App linked to Master" are deferred to M4.

### 4.7 Preview

`Import/Queries/ImportPreview/ImportPreviewQueryHandler.cs` parses and validates without creating anything. It returns tables and fields (with `IsSupported` and `IsPendingValidation` for formulas), relationships, forms, roles, errors and warnings.

`ImportPreviewTableItem.MappingChoice` is always `"Local"` and `MasterMappingAvailable` is always `false` (`ImportPreviewResult.cs:32-37`). These are placeholders for Master App mapping.

### 4.8 API

`src/PowerBase.API/Controllers/ImportController.cs`: route `apps/import`, `[RequireAuth]`, `[RequirePermission(PermissionCodes.AppsCreate)]`.

| Method | Route | Body | Returns |
|---|---|---|---|
| POST | `/apps/import/preview` | multipart `file` | `ApiResponse<ImportPreviewResult>` |
| POST | `/apps/import?mode=CreateNewApp` | multipart `file` | `201`, `ApiResponse<ImportReport>` |

The uploaded file is read fully into a string. There is no explicit size limit in the controller.

### 4.9 Master App / Child App / ownership / sync

**No implementation exists.** A search found no master/child, ownership, binding or sync entities, tables or services. The only traces are:

- the deferred-mode comments in `ImportAppFromPblCommand.cs`;
- the cross-app relationship warning (`QblToPblConverter.cs:169`);
- the preview placeholders above;
- the descriptions in `POWERBASE_SCOPE.md` (Copy App, Master App deployment, QBL import modes, M2/M4).

### 4.10 Related but separate: record-data import (`src/PowerBase.Application/Imports`)

This is the CSV/XLSX → table **record** import, not QBL. It consists of:

- 35 Application files: ImportPlanBuilder, ImportRunProcessor, ImportChunkWriter, schedules, PowerFlow templates;
- `src/PowerBase.Infrastructure/Imports`;
- the controllers `ImportDefinitionsController`, `ImportFilesController`, `ImportRunApiController`, `ImportFormulaController`, `PowerFlowImportTemplatesController`;
- migrations tenant `064`–`071` and control `012_import_queue.sql`.

It is the reusable building block if QBL import later needs background jobs or record-data import.

### 4.11 Tests

| File | Tests |
|---|---|
| `tests/PowerBase.UnitTests/Import/PblValidatorTests.cs` | 42 |
| `tests/PowerBase.UnitTests/Import/Qbl/QblToPblConverterTests.cs` | 38 |
| `tests/PowerBase.UnitTests/Import/FormulaTranslatorTests.cs` | 10 |
| `tests/PowerBase.UnitTests/Import/ImportAppFromPblCommandHandlerTests.cs` | 8 |
| `tests/PowerBase.UnitTests/Import/Qbl/QblYamlTagTests.cs` | 5 |
| `tests/PowerBase.IntegrationTests/Import/ImportTests.cs` | 9 |

No sample `.qbl` or `.yaml` fixture is checked in; the tests use inline YAML.

---

## 5. Frontend — `E:\ConsoleApp\PowerBase\UI\PB-Frontend`

### 5.1 Stack

- Angular 21: standalone components, lazy `loadComponent` routes, `@if` control flow.
- UI: PrimeNG 21.1.8 (Aura preset), primeflex, Tailwind 4, Lucide icons.
- State: NgRx Store + Effects 21.
- Editors: CodeMirror and Monaco (formula and code editors). Also chart.js and angular-gridster2 (dashboards).
- API base URL: `src/environments/environment.development.ts` (`http://localhost:5159`) and `environment.ts` (Azure).

### 5.2 Structure (`src/app`)

| Folder | Contents |
|---|---|
| `app.routes.ts` | Routes. `/auth/*`, `/select-tenant`, `/app/apps` (Apps list), `/app/:appId/...` (workspace: tables, records, reports, settings, pages, pipelines, imports), admin |
| `app.config.ts` | `provideRouter`, `provideHttpClient(withInterceptors([jwt, error, loading]))`, `provideStore` / `provideEffects`, PrimeNG theme |
| `core/api/services/` | One HttpClient service per resource (30), e.g. `apps.service.ts`, `tables.service.ts`, `fields.service.ts`, `relationships.service.ts`, `forms.service.ts`, `reports.service.ts`, `roles.service.ts`, `pages.service.ts`, **`import.service.ts`** (QBL/PBL), `imports.service.ts` (CSV) |
| `core/api/models/` | TypeScript DTOs. `api-response.ts` = `{ data, meta }`; **`import.ts`** mirrors the backend import DTOs |
| `core/api/interceptors/` | `jwt.interceptor.ts`, `error.interceptor.ts`, `loading.interceptor.ts` |
| `core/guards/` | `auth`, `identity`, `admin`, `tenant-admin`, `builder.guard.ts` (schema/form/report builder, powerFlows, imports) |
| `core/services/`, `core/utils/` | App context, permissions, form grid engine, CSV import helpers (`import-mapping-rows.ts`, `import-column-fit.ts`, `import-field-rules.ts`, …) |
| `store/` | NgRx slices: `auth`, `tables`, `records`, `reports`, `forms`. There is **no apps or import slice**; app-level pages call services directly. |
| `pages/` | Feature screens (apps list, workspace, table settings, reports, pipelines, …) |
| `layout/` | shell, app-workspace-shell, sidebar, top-nav, header, breadcrumb-bar, admin layouts |
| `shared/components/` | The `pb-*` design system: `pb-dialog`, `pb-button`, `pb-file-input`, `pb-select`, `pb-confirm-dialog`, `pb-loading-overlay`, `pb-empty-state`, `pb-toast`, `pb-formula-editor`, … |

### 5.3 Existing QBL import UI

- **Component:** `pages/apps/apps-list/import-wizard/import-wizard.component.ts` (132 lines) and `.html` (184 lines). It is a standalone component rendered inside `pb-dialog`.
- **Steps:** `'upload' | 'review' | 'importing' | 'report'` (`.ts:8`). This is custom step state, not a PrimeNG Stepper.
- **Upload:** a plain `<input type="file" accept=".json,.pbl,.qbl,.yaml,.yml">` (`.html:17`).
- **Review step:** shows tables, fields, relationships, forms, roles, errors and warnings, with supported vs. pending-formula field counts (`.ts:82-100`).
- **Report step:** shows the `ImportReport`.
- **Entry point:** `pages/apps/apps-list/apps-list.component.ts:278` opens it after an `apps:create` permission check. It is mounted at `apps-list.component.html:247`.
- **Service:** `core/api/services/import.service.ts`, with `preview(file)` and `import(file, mode)`.
- **Models:** `core/api/models/import.ts`. `ImportMode = 'CreateNewApp'` only, and `mappingChoice: 'Local'` only.

### 5.4 Other wizard / upload patterns to reuse

- `pages/apps/apps-list/app-creation-wizard/`: a multi-step create-app wizard.
- `pages/apps/table-settings/relationships/relationship-wizard-dialog/`.
- `pages/apps/table-settings/imports/`: `import-builder`, `import-run`, `imports-history`, `imports-list`. This is the CSV import flow with column mapping.
- `pages/apps/pipelines/pipeline-builder/`: `import-with-csv`, `upload-file`, `import-to-quickbase`.
- No screen uses `p-stepper` or `p-fileupload`. Wizards use custom step state plus `pb-dialog`, and uploads use `pb-file-input` or a native input.

---

## 6. Recommendations for the QBL → PBL feature

1. **Extend, don't rebuild.** Keep the existing chain: `ImportDocumentParser` → `QblToPblConverter` → `PblValidator` → `ImportAppFromPblCommandHandler`, behind `ImportController` and `import-wizard.component`.
2. **Quick wins in the converter:**
   - Chart reports.
   - `QB::Application::Variable` → `AppVariable`.
   - Optionally `QB::CodePage` → Code page.
   - Then embedded Report form elements, numeric `[FID]` formula refs, and role-based form conditions.
3. **Robustness:**
   - Move large imports to a background job. The `Application/Imports` queue and run pattern is a model for this.
   - Add an upload size limit.
   - Consider a real transaction or a staged commit instead of compensating with delete-on-failure.
4. **New modes** (Update Existing App, Child App linked to Master) need new master/child, ownership and binding schema in `database/migrations/tenant/075+`, plus domain and services. Hook them into `ImportMode` and the preview fields `MappingChoice` / `MasterMappingAvailable`.
5. **Wizard:**
   - Add a mode selection step and per-table mapping (Local vs. Master).
   - Add a richer issues/skipped view, grouped by severity and element.
   - Optionally add a PBL download of the converted document. This needs a new export or convert endpoint.
6. **Docs:** update `CLAUDE.md`. Its skeleton-phase scope rules conflict with the current codebase.
