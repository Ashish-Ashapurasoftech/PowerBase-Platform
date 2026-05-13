# PowerBase — Claude Code Knowledge Base

> Read this file completely before writing any code.
> This is the single source of truth for the project. If something is not in here, ask before assuming.

---

## What is PowerBase

A multi-tenant, low-code application platform — think Quickbase, but built from scratch on Azure with a modern .NET 8 stack. Users can define their own tables, fields, records, forms, and reports without writing code.

This document covers **Skeleton (Phase 1) only**. Do not build anything outside this scope.

---

## Repository layout

```
powerbase/
├── CLAUDE.md                          ← you are here
├── .github/
│   ├── pull_request_template.md
│   └── workflows/
│       ├── ci.yml
│       └── deploy-staging.yml
├── src/
│   ├── PowerBase.API/                 ← Controllers, Models (DTOs), Middleware, Program.cs
│   ├── PowerBase.Application/         ← Use cases (commands/queries), interfaces, validators
│   ├── PowerBase.Domain/              ← Entities, enums, constants, domain exceptions
│   └── PowerBase.Infrastructure/      ← Dapper repos, schema engine, Azure services, UOW
├── tests/
│   ├── PowerBase.UnitTests/
│   └── PowerBase.IntegrationTests/
├── database/
│   ├── migrations/                    ← Numbered SQL migration scripts
│   ├── seeds/                         ← Seed data scripts
│   └── schema/                        ← Reference DDL (not run directly)
└── docs/
    ├── architecture.md
    └── api.md
```

For the full breakdown of what goes inside each project, see the *Clean Architecture rules* section below.

---

## Tech stack

| Layer | Choice | Notes |
|---|---|---|
| Language | C# / .NET 8 | |
| API | ASP.NET Core 8 Web API | Minimal API or Controllers — use Controllers for consistency |
| ORM | Dapper | No EF Core. Raw SQL + Dapper only |
| Database | SQL Server (Azure SQL) | Four schemas: core, meta, data, audit |
| Auth | JWT (HS256) | Custom implementation — no ASP.NET Identity |
| Logging | Serilog | Console + Application Insights sinks |
| Validation | FluentValidation | One validator class per request DTO |
| API Docs | Swashbuckle (Swagger) | Every endpoint documented |
| Frontend | Angular + TypeScript | |
| State | NgRx (Store + Effects) | |
| HTTP | Angular HttpClient | With JWT interceptor |
| Testing (unit) | xUnit + FluentAssertions + NSubstitute | |
| Testing (integration) | xUnit + Testcontainers (SQL Server) | |

---

## Database — critical knowledge

### Four schemas

- **`core`** — platform-wide: `User`, `FieldType`, `SystemRole`, `SystemConfig`
- **`meta`** — tenant-scoped metadata: `Tenant`, `TenantUser`, `TenantRole`, `App`, `AppTable`, `AppField`, `Report`
- **`data`** — dynamically created tables (`data.t_{AppTableId}`) — no static tables here
- **`audit`** — operational: `UserSession`, `LoginAttempt`, `PasswordReset`, `ActivityLog`

### Rules that cannot be broken

1. **Every query against meta/data/audit MUST filter by `TenantId`.** The `QueryContext` (see below) injects this automatically. Never write a raw `SELECT` without it.
2. **All DDL for the `data` schema goes through `SchemaEngineService` only.** Never write `CREATE TABLE` or `ALTER TABLE` inline in a controller or repository.
3. **Never expose `Id` (BIGINT) via API.** Expose `PublicId` (UNIQUEIDENTIFIER) only. Exception: `AppField.Id` is intentionally exposed as the FID equivalent for Quickbase compatibility.
4. **Passwords: BCrypt only.** Never MD5, SHA1, or plain text.
5. **Tokens: hash before storing.** The `audit.UserSession.JwtId` stores the `jti` claim. The `audit.PasswordReset.TokenHash` stores SHA-256 of the token. Plaintext never enters the DB.
6. **Every UPDATE must include `RowVersion` for optimistic concurrency.**

### Physical naming in `data` schema

- Table: `data.t_{AppTableId}` — e.g. `data.t_17`
- Column: `f_{AppFieldId}` — e.g. `f_103`
- The user-visible `Name` is in `meta.AppField.Name`. It never touches SQL.

### Skeleton field types (4 only)

| Code | SQL type |
|---|---|
| Text | `NVARCHAR(500)` |
| Number | `DECIMAL(18,4)` |
| Date | `DATE` |
| Boolean | `BIT` |

---

## Clean Architecture rules

```
API  →  Application  →  Domain
 ↓
Infrastructure  →  Application interfaces
```

### Layer responsibilities

- **Domain** — pure C# entities, enums, constants, domain exceptions. No dependencies on anything. No Dapper attributes. No JSON attributes. No Azure SDK. If a domain entity needs to know about a database column or HTTP status, the architecture is wrong.
- **Application** — use cases as command/query handlers, DTOs, validators, interfaces. Depends on Domain. No Dapper, no direct database calls — only through repository interfaces.
- **Infrastructure** — implements Application interfaces. Dapper repositories, `SchemaEngineService`, JWT service, email service, Azure SDK usage.
- **API** — controllers, middleware, DI registration. Calls Application handlers. Maps domain exceptions to HTTP status codes.

**Never reference Infrastructure from API directly except in DI registration (`Program.cs`).**

### Where each kind of "model" lives

This is the most common confusion. Three different kinds of model, three different layers:

| Kind | Lives in | Examples | Purpose |
|---|---|---|---|
| **Domain entity** | `Domain/Entities/` | `App`, `AppTable`, `User`, `Tenant` | Represents a business concept. Has no idea databases or APIs exist. |
| **DTO (request/response)** | `API/Models/` | `CreateAppRequest`, `AppResponse` | Shape of the API contract. Optimised for the client, not the database. |
| **Persistence model** | (none — Dapper maps directly to Domain entities) | — | Intentionally empty. One of the reasons we use Dapper over EF Core. |

Domain entities go through Dapper directly — no separate persistence class. API never returns domain entities; always map to a DTO first. The moment you return `App` directly, every internal property leaks into the API contract.

### Project folder layout (mirrors IVR project conventions)

```
src/
├── PowerBase.API/
│   ├── Attributes/                ← custom attributes (e.g. AuthorizeBuilderAttribute)
│   ├── Controllers/               ← one controller per resource
│   │   ├── AuthController.cs
│   │   ├── AppsController.cs
│   │   ├── TablesController.cs
│   │   ├── FieldsController.cs
│   │   ├── RecordsController.cs
│   │   └── ReportsController.cs
│   ├── Middleware/
│   │   ├── ExceptionHandlingMiddleware.cs
│   │   ├── JwtMiddleware.cs
│   │   └── QueryContextMiddleware.cs
│   ├── Models/                    ← API DTOs (requests & responses)
│   │   ├── Auth/
│   │   │   ├── SignupRequest.cs
│   │   │   ├── LoginRequest.cs
│   │   │   └── LoginResponse.cs
│   │   ├── Apps/
│   │   │   ├── CreateAppRequest.cs
│   │   │   ├── AppResponse.cs
│   │   │   └── AppListItemResponse.cs
│   │   ├── Tables/
│   │   ├── Fields/
│   │   ├── Records/
│   │   └── Reports/
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   └── Program.cs
│
├── PowerBase.Application/
│   ├── Apps/                      ← one folder per feature/aggregate
│   │   ├── Commands/
│   │   │   ├── CreateApp/
│   │   │   │   ├── CreateAppCommand.cs
│   │   │   │   ├── CreateAppCommandHandler.cs
│   │   │   │   └── CreateAppCommandValidator.cs
│   │   │   └── DeleteApp/
│   │   └── Queries/
│   │       ├── GetApp/
│   │       └── ListApps/
│   ├── Auth/
│   ├── Tables/
│   ├── Fields/
│   ├── Records/
│   ├── Reports/
│   └── Common/
│       ├── Interfaces/            ← repository & service contracts
│       │   ├── IAppRepository.cs
│       │   ├── IUserRepository.cs
│       │   ├── ISchemaEngineService.cs
│       │   ├── IJwtService.cs
│       │   └── IQueryContext.cs
│       ├── Mappings/               ← entity ↔ DTO conversion
│       │   └── AppMappings.cs
│       └── Behaviors/              ← MediatR pipeline behaviors (if used)
│           └── ValidationBehavior.cs
│
├── PowerBase.Domain/               ← THE missing piece, now explicit
│   ├── Entities/                   ← one file per table in the schema
│   │   ├── User.cs
│   │   ├── Tenant.cs
│   │   ├── TenantUser.cs
│   │   ├── TenantRole.cs
│   │   ├── App.cs
│   │   ├── AppTable.cs
│   │   ├── AppField.cs
│   │   ├── Report.cs
│   │   ├── FieldType.cs            ← seeded reference data
│   │   └── SystemRole.cs
│   ├── Enums/
│   │   ├── AppStatus.cs            ← Active, Archived, Draft
│   │   ├── TenantStatus.cs         ← Active, Suspended, Trial, Cancelled
│   │   ├── ReportType.cs           ← Table (skeleton); Summary/Chart in Phase 2
│   │   ├── FieldTypeCode.cs        ← Text, Number, Date, Boolean
│   │   └── Visibility.cs           ← Personal, Shared, RoleScoped
│   ├── Constants/
│   │   ├── SchemaNames.cs          ← "core", "meta", "data", "audit"
│   │   ├── SystemRoleCodes.cs      ← "SuperAdmin", "User"
│   │   ├── DefaultTenantRoles.cs   ← "Administrator", "User"
│   │   └── PhysicalNaming.cs       ← table/column name builders (t_{id}, f_{id})
│   └── Exceptions/
│       ├── DomainException.cs      ← abstract base
│       ├── NotFoundException.cs
│       ├── DuplicateException.cs
│       ├── UnauthorizedActionException.cs
│       └── ValidationException.cs
│
└── PowerBase.Infrastructure/
    ├── Helper/                     ← utility classes (matches IVR convention)
    │   ├── PasswordHasher.cs
    │   ├── JwtTokenBuilder.cs
    │   └── DapperTypeHandlers.cs
    ├── Repositories/               ← Dapper implementations of repo interfaces
    │   ├── BaseRepository.cs       ← shared connection management, tenant filter helpers
    │   ├── UserRepository.cs
    │   ├── TenantRepository.cs
    │   ├── AppRepository.cs
    │   ├── AppTableRepository.cs
    │   ├── AppFieldRepository.cs
    │   ├── ReportRepository.cs
    │   └── RecordRepository.cs     ← dynamic SQL against data.t_X
    ├── Services/
    │   ├── SchemaEngineService.cs  ← ONLY class allowed to write DDL
    │   ├── JwtService.cs
    │   ├── PasswordService.cs
    │   └── QueryContext.cs         ← scoped service holding UserId + TenantId
    ├── UOW/                        ← Unit of Work pattern (matches IVR convention)
    │   ├── IUnitOfWork.cs
    │   └── UnitOfWork.cs
    └── Persistence/
        └── DbConnectionFactory.cs  ← SqlConnection factory used by all repositories
```

### Where to put a new file — quick lookup

When implementing a new feature, files generally land like this:

| If you're creating... | It goes in... |
|---|---|
| A table-backed business entity | `Domain/Entities/` |
| An enum used by entities | `Domain/Enums/` |
| A constant or static helper for naming | `Domain/Constants/` |
| A typed exception thrown by handlers | `Domain/Exceptions/` |
| A request body the API accepts | `API/Models/{Feature}/` |
| A response body the API returns | `API/Models/{Feature}/` |
| A use case (write operation) | `Application/{Feature}/Commands/{UseCase}/` |
| A read use case | `Application/{Feature}/Queries/{UseCase}/` |
| A FluentValidation validator | next to the Command/Query it validates |
| An interface for a repository or service | `Application/Common/Interfaces/` |
| A Dapper repository implementation | `Infrastructure/Repositories/` |
| An Azure or external service implementation | `Infrastructure/Services/` |
| A controller | `API/Controllers/` |
| A custom middleware | `API/Middleware/` |
| Entity-to-DTO mapping | `Application/Common/Mappings/` |

---

## Coding conventions

### Naming
- Classes, methods, properties: `PascalCase`
- Local variables, parameters: `camelCase`
- Private fields: `_camelCase`
- Constants: `UPPER_SNAKE_CASE`
- Interfaces: `IServiceName`
- DTOs: `CreateAppRequest`, `AppResponse`, `AppListResponse`
- Commands: `CreateAppCommand`, `CreateAppCommandHandler`
- Queries: `GetAppQuery`, `GetAppQueryHandler`

### File structure per feature

See the complete project layout in the *Clean Architecture rules* section above. Quick reminder of what's where for a typical feature (e.g. "Apps"):

```
Domain/Entities/App.cs                          ← business entity
Domain/Enums/AppStatus.cs                       ← related enums
API/Models/Apps/CreateAppRequest.cs             ← request DTO
API/Models/Apps/AppResponse.cs                  ← response DTO
Application/Apps/Commands/CreateApp/*.cs        ← use case + validator
Application/Common/Interfaces/IAppRepository.cs ← repo contract
Infrastructure/Repositories/AppRepository.cs    ← Dapper implementation
API/Controllers/AppsController.cs               ← HTTP surface
```

Note on DTO location: API request/response DTOs live in `API/Models/`, matching the IVR project convention. Some Clean Architecture variants put DTOs in `Application/` — we deliberately don't, to keep `Application` independent of HTTP concerns and to mirror what the team already knows.

### SQL in repositories
- All queries are written as `const string` at the top of the repository class
- Named parameters always: `@tenantId`, `@appId` — never positional
- `OFFSET/FETCH NEXT` for all list endpoints — no `TOP` without paging
- Column lists always explicit — never `SELECT *`

```csharp
// Correct
private const string GetByPublicId = @"
    SELECT Id, PublicId, Name, Description, OwnerId
    FROM meta.App
    WHERE TenantId = @tenantId
      AND PublicId = @publicId
      AND IsDeleted = 0";

// Wrong
var sql = "SELECT * FROM meta.App WHERE ...";
```

### Error handling
- Domain errors: throw typed exceptions (`AppNotFoundException`, `DuplicateAppNameException`)
- Global exception middleware in API catches these and maps to HTTP status codes
- Never return `null` from a repository method that fetches by ID — throw `NotFoundException`
- Validation errors return `400` with `{ errors: { field: ["message"] } }` shape

### Response shape (all endpoints)
```json
// Success
{ "data": { ... }, "meta": { "timestamp": "..." } }

// Error
{ "error": { "code": "APP_NOT_FOUND", "message": "..." } }

// List
{ "data": [...], "meta": { "total": 100, "page": 1, "pageSize": 20 } }
```

---

## Authentication and request context

### JWT claims
Every JWT contains:
- `sub` — `UserId` (BIGINT, as string)
- `tid` — `TenantId` (BIGINT, as string)
- `jti` — unique token ID (GUID, stored in `audit.UserSession`)
- `exp` — expiry

### QueryContext
A scoped service injected into every repository:

```csharp
public interface IQueryContext
{
    long UserId { get; }
    long TenantId { get; }
    string IpAddress { get; }
}
```

Every repository constructor receives `IQueryContext`. Never pass `tenantId` manually to repository methods — read it from `IQueryContext`.

---

## Schema engine

The `SchemaEngineService` is the only class allowed to write DDL against the `data` schema.

```csharp
public interface ISchemaEngineService
{
    Task CreateTableAsync(AppTable table, CancellationToken ct);
    Task AddColumnAsync(AppTable table, AppField field, CancellationToken ct);
    // Never: RenameColumn, DropColumn, ChangeType — not in skeleton
}
```

Rules:
- Wrap both the `meta` INSERT and the DDL in a single transaction
- Always create the standard filtered index after `CREATE TABLE`
- Never string-concatenate user input — `AppTableId` and `AppFieldId` are integers you control
- Columns always created as `NULL` regardless of `IsRequired` — requiredness is enforced in the validator, not the DB column

---

## API endpoints — skeleton scope

### Auth
| Method | Route | Description |
|---|---|---|
| POST | `/auth/signup` | Create user + tenant + seed roles |
| POST | `/auth/login` | Returns JWT |
| GET | `/auth/me` | Current user |

### Apps
| Method | Route | Description |
|---|---|---|
| POST | `/apps` | Create app |
| GET | `/apps` | List tenant apps |
| GET | `/apps/{publicId}` | Get app |
| DELETE | `/apps/{publicId}` | Soft delete |

### Tables
| Method | Route | Description |
|---|---|---|
| POST | `/apps/{appId}/tables` | Create table (runs DDL) |
| GET | `/apps/{appId}/tables` | List tables |
| GET | `/tables/{publicId}` | Get table + fields |
| DELETE | `/tables/{publicId}` | Soft delete (no DDL drop) |

### Fields
| Method | Route | Description |
|---|---|---|
| POST | `/tables/{tableId}/fields` | Add field (runs ALTER TABLE) |
| GET | `/tables/{tableId}/fields` | List fields |

### Records
| Method | Route | Description |
|---|---|---|
| POST | `/tables/{tableId}/records` | Insert record |
| GET | `/tables/{tableId}/records` | List records (paged) |
| GET | `/tables/{tableId}/records/{id}` | Get single record |
| PATCH | `/tables/{tableId}/records/{id}` | Update record |
| DELETE | `/tables/{tableId}/records/{id}` | Soft delete |

### Reports
| Method | Route | Description |
|---|---|---|
| POST | `/tables/{tableId}/reports` | Save report definition |
| GET | `/apps/{appId}/reports` | List reports |
| GET | `/reports/{publicId}` | Get report definition |
| GET | `/reports/{publicId}/run` | Execute report |

---

## What is NOT in scope (skeleton)

Do not build these. If a task seems to require one of these, stop and confirm:

- Formulas, formula parser, formula evaluator
- Form designer or form rules
- Relationships between tables (lookups, references)
- Summary or computed fields
- Charts or dashboard
- Pipelines / automations
- Master / child app propagation
- File attachments
- Record-level permissions (app-owner check only in skeleton)
- Group management
- SSO / MFA / social login
- API tokens (user tokens for external integrations)
- Webhooks
- Import / export jobs
- Audit log query UI
- Azure AI Search (use SQL LIKE for skeleton search)
- Redis / SignalR / Service Bus (add when actually needed)
- Always Encrypted with Secure Enclaves (deferred to Phase 3)
- BYO Azure data plane (deferred to Phase 4)

---

## Git setup and workflow

### Branch model

```
main               ← production-ready, protected
  └── staging      ← auto-deployed to staging on merge, protected
        └── develop ← integration branch, all features merge here
              └── feature/PB-123-create-app-endpoint
              └── feature/PB-124-schema-engine
              └── fix/PB-125-login-jwt-expiry
              └── chore/PB-126-add-serilog
```

### Branch naming

```
feature/PB-{issue-number}-{short-description}
fix/PB-{issue-number}-{short-description}
chore/PB-{issue-number}-{short-description}
hotfix/PB-{issue-number}-{short-description}   ← goes directly to main + backmerge
```

### Commit format (Conventional Commits)

```
<type>(scope): short description

[optional body]
[optional footer: Closes #123]
```

Types: `feat`, `fix`, `chore`, `refactor`, `test`, `docs`, `ci`
Scopes: `auth`, `apps`, `tables`, `fields`, `records`, `reports`, `schema-engine`, `db`, `api`, `infra`

Examples:
```
feat(auth): add POST /auth/signup endpoint
feat(schema-engine): implement CREATE TABLE for data schema
fix(records): inject tenant filter in list query
test(apps): add integration test for create app flow
chore(db): add migration 004 for meta.Report table
refactor(tables): extract DDL generation to SchemaEngineService
docs(api): update swagger description for records endpoints
```

### PR rules

1. **One feature per PR.** If it's touching more than one domain, split it.
2. **PR title = commit format.** `feat(auth): add POST /auth/login endpoint`
3. **Every PR needs:** passing CI, at least one reviewer approval, no unresolved comments.
4. **Link the issue** in the PR description: `Closes #123`
5. **No force pushes** to `develop`, `staging`, or `main` — ever.
6. **Merge strategy:** Squash merge into `develop`. Merge commit into `staging` and `main`.
7. **Branch is deleted** after merge.

### PR description template (`.github/pull_request_template.md`)

```markdown
## What
Brief description of what this PR does.

## Why
Why is this change needed? Link to issue: Closes #

## How
Brief description of the approach taken. Anything non-obvious.

## Checklist
- [ ] Unit tests added/updated
- [ ] Integration test added if this is a new endpoint
- [ ] Swagger documentation updated
- [ ] No hardcoded strings (use constants)
- [ ] TenantId filter present in all queries
- [ ] No Id (BIGINT) exposed in API responses (use PublicId)
- [ ] Migration script added if schema changed
```

### CI pipeline (`.github/workflows/ci.yml`)

Runs on every PR to `develop`:
1. `dotnet restore`
2. `dotnet build --no-restore`
3. `dotnet test --no-build` (unit tests)
4. `dotnet test --no-build` (integration tests, Testcontainers spins up SQL Server)
5. Fail fast — any red test blocks the PR

---

## Environment configuration

Never hardcode. Use `appsettings.{Environment}.json` + Azure Key Vault in production.

```json
// appsettings.Development.json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=PowerBaseDB;..."
  },
  "Jwt": {
    "SecretKey": "dev-secret-only-not-production",
    "ExpiresInMinutes": 1440,
    "Issuer": "powerbase-dev",
    "Audience": "powerbase-dev"
  },
  "Logging": {
    "MinimumLevel": "Debug"
  }
}
```

Never read `ExpiresInMinutes` as a hardcoded value inside code — always from config. (This was a known bug in the IVR project.)

---

## Database migrations

Migrations are numbered SQL scripts in `database/migrations/`:

```
001_create_schemas.sql
002_core_user_fieldtype_systemrole_systemconfig.sql
003_meta_tenant_tenantuser_tenantrole.sql
004_meta_app_apptable_appfield.sql
005_meta_report.sql
006_audit_tables.sql
007_seed_fieldtypes.sql
008_seed_systemroles.sql
```

Rules:
- One script per logical unit. Never mix unrelated tables in one script.
- Scripts are run in order by the migration runner at startup.
- Scripts are idempotent where possible: `IF NOT EXISTS (SELECT ...) CREATE TABLE ...`
- Never edit a migration that has already been run in any environment. Write a new one.
- Seed scripts are separate from schema scripts.

---

## Testing expectations

### Unit tests
- Every `CommandHandler` and `QueryHandler` has unit tests
- Every `Validator` has unit tests covering valid and invalid cases
- Every `SchemaEngineService` method has unit tests (mock the DB connection)
- No tests against real database in unit test project

### Integration tests
- Every API endpoint has at least one happy-path integration test
- Integration tests use Testcontainers to spin up a real SQL Server
- Run migrations before each test class
- Tests are isolated: each test class gets a fresh database state

### Naming
```csharp
// Method_Scenario_ExpectedResult
public async Task Handle_ValidCommand_CreatesAppAndReturnsPublicId()
public async Task Handle_DuplicateName_ThrowsDuplicateAppNameException()
public async Task Validate_EmptyName_ReturnsValidationError()
```

---

## Key decisions already made — do not re-open these

These were reasoned through extensively. Do not suggest alternatives without a strong technical reason:

| Decision | Choice | Reason |
|---|---|---|
| ORM | Dapper | Team familiarity, full SQL control for dynamic queries |
| DB | SQL Server / Azure SQL | Team expertise, Azure-native integration |
| Schema storage model | Physical tables (`data.t_X`) | Performance over EAV; right for SQL Server |
| Tenancy (skeleton) | Shared DB + TenantId column | 3-person team, skeleton phase; per-tenant DB is the long-term target |
| PKs | BIGINT IDENTITY internal + UNIQUEIDENTIFIER external | Clustered index performance + API stability |
| External IDs | PublicId (UNIQUEIDENTIFIER) for all entities except AppField.Id | Quickbase FID compatibility |
| Field type count (skeleton) | 4 only — Text, Number, Date, Boolean | Avoid scope creep |
| Formula engine | Deferred to Phase 2 | Under-scoped in original scope; needs its own phase |
| Permissions (skeleton) | App-owner check only | Full RBAC is Phase 2 |
| Pipelines engine | Durable Functions (when built) | Do not build from scratch |
| Always Encrypted | TDE + CMK only for now | Always Encrypted deferred to Phase 3 |

---

## Quickbase compatibility note

PowerBase is designed to be compatible with Quickbase integrations at the API level. Key identifiers:

| Quickbase | PowerBase |
|---|---|
| DBID (app ID) | `meta.App.PublicId` |
| TID (table ID) | `meta.AppTable.PublicId` |
| FID (field ID) | `meta.AppField.Id` — intentionally exposed |
| RID (record ID) | `data.t_X.Id` (internal) / `PublicId` (external) |

When naming API routes and response fields, keep this compatibility in mind.

---

## Common pitfalls — read before writing any query

1. **Missing TenantId filter.** Always `WHERE TenantId = @tenantId`. Always.
2. **Exposing BIGINT Id.** Return `PublicId` in responses. Use `Id` only for internal joins.
3. **String concatenation in DDL.** The schema engine builds table/column names from integer IDs only. User-provided `Name` never enters a SQL string.
4. **Hardcoded JWT expiry.** Read from `IConfiguration["Jwt:ExpiresInMinutes"]`.
5. **SELECT * in repositories.** Always explicit column list.
6. **Missing `IsDeleted = 0` filter.** Every non-audit query must exclude soft-deleted rows.
7. **Forgetting `RowVersion` on UPDATE.** All updates must include optimistic concurrency check.
8. **Calling infrastructure from API directly.** Only DI registration (`Program.cs`) can reference Infrastructure. Controllers call Application handlers only.
