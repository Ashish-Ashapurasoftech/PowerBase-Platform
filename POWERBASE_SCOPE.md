# POWERBASE – SCOPE REFERENCE
> Compact reference for Claude Code. Read this before implementing any feature. All items marked **[PB]** are PowerBase-exclusive features (not in Quickbase).
> Change markers: **[NEW]** = added in last revision | **[UPDATED]** = modified from original

---

## ARCHITECTURE OVERVIEW
- Multi-tenant SaaS platform (Quickbase alternative)
- Stack: ASP.NET Core 8, Angular, Azure SQL, Azure Blob, Azure Key Vault, Azure Cognitive Search
- Each tenant gets isolated data; Master App is tenant-specific (cannot cross tenants)
- Customer data can be hosted in PowerBase Azure OR client's own Azure subscription (Managed Identity access, least-privilege, fully audited)
- **[NEW]** **Cross-tenant capability:** Architecturally must not be hard-blocked at DB/code level. Enforced as a permission gate only. Delivery deferred to future milestone (post-M6). Do NOT design schemas that make cross-tenant impossible to add later.

---

## MILESTONE PLAN

| M# | Duration | Key Scope | Payment | Status | % Done | Remaining Blockers / Open Items |
|----|----------|-----------|---------|:---:|:---:|---|
| M0 | — | Advance | 5% | **COMPLETE** | **100%** | Baseline established. |
| M1 | 3 months | Figma, Tenant, App/User/Table/Field Mgmt, Basic Formula Fields, Table+Summary Reports, Basic Form Rules, Audit Logs | 30% | **IN PROGRESS** | **92%** | Centrally managed error catalog (#3); Sensitive-app approval UI (#4). |
| M2 | 1 month | Connected Tables, Summary+Lookup Fields, Copy App, QBL Import, Formula/Date as Reference, Advanced Form Rules | 10% | **IN PROGRESS** | **76%** | Copy App engine (#19, #20); Connected Tables (#26); QBL import upload UI wizard (#22, #23). *(Lookups, Summaries & Reference Filters closed)* |
| M3 | 1 month | Advanced Roles+Groups, Split Admin Capabilities, Column Type Conversion, Chart Reports, Conditional Formatting, Report Link in Forms, Azure Global Search | 10% | **IN PROGRESS** | **88%** | Column type conversion edge cases; Sidebar navigation module (#40); Code Pages deferred to M5. |
| M4 | 1 month | Archive/Restore, Automations/Pipelines, Master App Deployment, External Tenant DB | 20% | **IN PROGRESS** | **82%** | Record Restore UI (#42); Master/Child propagation engine (#21); External DB deferred post-M6. *(PowerFlows/Pipelines engine & UI closed)* |
| M5 | 1 month | Data Migration, User Tokens, Public APIs (DoQuery, ImportFromCSV, CRUD, Reports, Fields) | 20% | **IN PROGRESS** | **60%** | Reusable Template Import (#47); Row-level feedback files (#48); External Public Developer API portal (#49). |
| M6 | 1 month | Go Live + 3-month Hypercare | 5% | **PENDING** | **0%** | Dependent on completion of M1–M5. |
| **TOTAL** | — | **Full Contractual Scope** | **100%** | **IN PROGRESS** | **~80%** | **Weighted overall codebase completion.** |

**[NEW] Approved Scope Additions (resolved in scope discussions; milestone/budget allocation needed):**
- **Action Buttons** (Signature, File, Prompt, Data variants; July 2 URL & Multi-Capture updates) — Core M1+, advanced July 2 additions
- **Template-Driven File Import & Table-to-Table Import Engine** (Excel/CSV multi-target, virtual formula columns, feedback reports) — Approved additions
- **Sidebar Navigation Redesign** (Configurable navigation items, custom groups, report hovers, add-record links) — Approved addition
- **Snapshot Fields** (One-time creation copy, server-side read-only, bulk async re-initialization) — Approved addition
- **Test As User / Test As Role** (Live permission simulation with banner and real-admin audit logging) — Approved addition / Phase 2
- **Centrally Managed Handled Error Messages** (English error catalog with tenant-wide propagation) — Approved addition

**[NEW] Deferred & Future Scope (outside current contract — separate planning + budget):**
- Super Admin / Realm Admin Panel (users, storage, billing, usage analytics) — suggest M3 planning session
- Full Backup System (daily snapshots, schema revert, data revert, point-in-time restore) — separate milestone required
- Cross-tenant Master App — future milestone post-M6
- Code Pages (client-side custom web app execution sandbox) — depends on M5 Public API
- Users as a System Table (Phase 2 app-level user profile table over master user identity — estimated 4 days)
- Partial Connected Tables (filtered/relationship-aware bidirectional sync) — future roadmap

---

## APPS

### App Management (M1)
- Apps page: list, count, search, export CSV, delete (single or bulk)
- App Settings: home, branding, navigation, users/groups, roles/permissions, app variables, audit & logs

### Centrally Managed Handled Error Messages [PB] (Approved Addition)
- All handled (non-system) English error messages are centrally administered in the platform admin panel.
- Text modifications propagate automatically across all apps in the tenant without requiring code changes or redeployments.
- Raw system exceptions (e.g. unhandled database connection failures, 500 runtime exceptions) remain system-generated and non-editable.
- Covers handled validation messages, operational constraints, and permission denial notices.

### Limit App Access **[UPDATED]**
- Realm-level approval status per user; only "Approved" users can access sensitive apps
- Super Admin controls access at app level
- **[NEW]** Note: a full Super Admin/Realm Admin panel (users, space, billing, global settings) is a separate deferred feature — not delivered in M1

### Copy App [PB] (M2) **[UPDATED]**
> **[NEW] IMPORTANT:** Copy App is a standalone one-time operation producing an independent app. It is NOT the same as Master App / Child App creation. These are two distinct features that share underlying copy infrastructure but have different UX entry points, different purposes, and different post-creation behavior.

**Three copy modes:** **[UPDATED]**

**Schema Only** – copies: tables, fields, relationships, forms, reports, roles & permissions, rules/formulas/workflows, app-level settings. Does NOT copy: records, file attachments, audit logs.

**Schema + Data** – copies everything in Schema Only, plus: all table records; file attachments (where supported). **[UPDATED]** Field values copied as-is. System Record IDs regenerated; reference field values updated to point to the new Record IDs (FK re-mapping). New app is NOT automatically linked to any Master App unless explicitly chosen.

**[NEW] Partial Clone** – user selects which tables to include; per selected table, chooses Schema Only or Schema + Data. Unselected tables are excluded entirely.

**[UPDATED] Large Data Handling:** No hard record limit. Large copies run as a background async job with email/notification on completion. UI shows copy status; user is not blocked.

**[UPDATED] Use cases:** fresh department app, reusable template, testing/onboarding, sandbox within same tenant, schema backup.
> **[UPDATED]** Removed use case: "migrating apps between environments" — no environment concept exists in PowerBase. The isolation unit is the tenant.

---

## MASTER APP [PB] (M4) **[UPDATED]**
> **[NEW]** Distinct from Copy App. Creating a Child App from a Master App uses the copy engine under the hood, but is a separate UX flow initiated from the Master App — not from the generic Copy App feature.

### Concept
- Master App = source template (not a strict parent-overrides-all)
- Child Apps = apps created from Master App that may diverge with dept-specific customizations
- Changes selectively propagated to child apps without overwriting child-specific work
- **[UPDATED]** Master App is tenant-specific; cannot connect to another tenant's apps (permission-gated in future, not architecturally blocked)

### Key Design: Propagation = Additive + Non-Destructive
- Master-owned elements: updated on propagation
- Child-owned elements: preserved always
- No auto rollback of child customizations
- Applies to: tables, fields, relationships, forms, rules, settings

### Ownership & Change Tracking
Every schema element tracks origin:

| Element | Ownership |
|---------|-----------|
| Table | Master or Child |
| Field | Master or Child |
| Relationship | Master or Child |
| Rule/Workflow | Master or Child |

### How Child Creation Works **[UPDATED]**
1. Admin initiates "Create Child App" from Master App (separate flow from Copy App)
2. System uses copy engine to duplicate the Master App
3. New app marked: Linked to Master App + Eligible for updates
4. All copied elements = Master-owned automatically
5. New elements added in child after creation = Child-owned automatically

### Change Propagation Flow
1. System detects changes in Master App (new fields, modified defs, new relationships, updated rules)
2. Changes tagged as Master updates
3. Admin chooses: deploy to all child apps OR selected child apps
4. Deployment: only Master-owned elements updated/added; Child-owned ignored; no deletions unless explicitly approved

### [NEW] Conflict Resolution Rules (Settings & Elements)
When a Master update conflicts with a child's state:
- **Master-owned element modified in Master** → update propagated to child; child version overwritten
- **Master-owned element edited in child** → editing a Master-owned element in a child auto-detaches it (converts to Child-owned); future Master updates no longer affect it
- **App-level settings conflict** → Master-owned settings win on propagation; settings explicitly detached by child admin are preserved
- **No implicit merge** — there is no three-way merge; it is always: Master wins on Master-owned, Child wins on Child-owned

### [NEW] Child Override / Detach Mechanism
Child app admins can, per Master-owned element:
- **Detach:** converts the element from Master-owned to Child-owned permanently. Future Master propagations skip this element for this child
- **Ignore this update:** skips a specific incoming Master update for this child only, without permanently detaching. Element remains Master-owned but this update is skipped
Both options available via context menu on any Master-owned element in the child app. Super Admin only can configure whether child admins have detach permission.

### What NEVER Gets Overwritten Automatically **[UPDATED]**
- Fields/tables added only in child apps
- Child-specific relationships
- Dept-specific workflows/rules
- **[NEW]** Elements that have been detached (converted to Child-owned)
- Local configuration changes on child-owned settings

---

## IMPORT APP VIA PBL [PB] (M2) **[UPDATED]**

### PowerBase Blueprint Language (PBL) **[UPDATED]**
- JSON-based declarative format describing an app as structured metadata
- PBL can define: app metadata, tables, fields, data types, formulas, relationships, forms/layouts, reports, roles/permissions, rules/workflows, Master App bindings
- **[UPDATED]** PBL does NOT define Pipelines/Automations in M2 (Pipelines are M4; PBL support for Pipelines deferred to M4+)

### QBL → PBL Import Flow
1. QBL file uploaded
2. Parser converts QBL into internal AST
3. Converter maps AST to PBL
4. Importer executes one of three import modes (see below)

**[NEW] QBL compatibility:** PBL supports a defined subset of QBL V12 constructs — tables, fields (all scalar types), formulas, relationships, forms, reports, roles/permissions. NOT all QBL V12 features are supported. A specific compatibility matrix is required before M2 implementation (research spike needed).

### Import Modes **[UPDATED]**
**Create New App** — imports QBL as a brand new app in the tenant. All elements created fresh.

**[UPDATED] Update Existing App** — merges QBL into an existing app:
- Elements matched by ID (QuickbaseTableId → PowerBaseTableId via mapping file)
- New elements in QBL not present in existing app → added
- Matching elements → user shown diff; user confirms overwrite per element or skips
- Elements in existing app but not in QBL → preserved (never deleted unless explicitly requested)
- Conflicts surfaced in UI before committing; no silent overwrites

**Create Child App Linked to Master** — creates a new child app using the QBL as source, immediately linked to a specified Master App. All imported elements marked Master-owned per the Import Mapping File.

> **[NEW]** Note: "child-specific elements preserved / only mapped master elements updated / no destructive overwrites" — these rules apply ONLY to the Update Existing App mode, not to Create New App.

### Import Mapping **[UPDATED]**
**[UPDATED] Format:** `QuickbaseTableId (DBID) → PowerBaseTableId`, `BindingType → Master | Local`
- **[UPDATED]** Use Quickbase Table ID (DBID), not table name — names can change and cause mapping failures
- **[NEW]** Example: `{ "qbTableId": "bck7gp3q", "qbTableName": "Clients" (display only), "pbTableId": "tbl_001", "bindingType": "Master" }`
- Unmapped tables → created as local tables in tenant (Local-owned)
- Tables mapped with `bindingType: Master` → linked to specified Master App table

**[NEW] UI-driven mapping wizard (preferred UX):**
1. Upload QBL
2. System parses and shows all detected tables
3. Per table: user selects "Create as new local table" OR "Map to existing Master App table" (dropdown)
4. User sets BindingType per table
5. System generates mapping internally — no manual JSON file required for UI users
6. Manual mapping file format available for API/developer use only

**[NEW] Ownership defaults:** Without explicit mapping, all imported elements default to Local-owned. The system never infers or guesses ownership from the QBL file content.

---

## USERS & GROUPS

### Managing Users
- Users page: add, remove, change role, send invitation, export CSV
- Search by email or full name; filter by access type, status, role, group membership, user picker visibility
- Group members from domain group don't display on Users page

### Working with Roles & Permissions

**Levels of Access Control:**
1. App Level – can user open the app
2. Table Level – which tables + what actions
3. Forms & Reports Level – which forms/reports/dashboards visible
4. Field Level – view / edit / hide completely
5. Record Level [PB] – see below

**Action Permissions:** View / Add / Edit / Delete records (per role, per table)

**Built-in Roles:** Viewer, Participant, Administrator (customizable or create from scratch)
**Role: None** – no access; user can still appear in user-type fields
**Default Role** – auto-selected on share/import/form-field add
Role changes apply immediately to all assigned users/groups

### Role-Based Builder Permissions [PB] (M3) **[UPDATED]**
Problem: Quickbase Admin = all-powerful (sees all data + can undo own restrictions).
PowerBase splits Admin into **Builder Capabilities**:

| Capability | Controls |
|-----------|---------|
| Schema Builder | Structure only, no data |
| Form Builder | UI only |
| Report & Chart Builder | Analytics only |
| Automation Builder | Logic & workflows |
| Security & Role Manager | Permissions only |

- Builder permissions and data access are **never tied together**
- **[NEW]** A user with Report & Chart Builder capability sees: report structure, field names, aggregation results. They do NOT see individual record values. Report preview shows row counts and aggregate outputs only — never raw record data
- Super Admin = only all-access role; can grant/revoke capabilities, override permissions, lock others out
- No admin can grant themselves more access than Super Admin allows

### [NEW] Role Editing Permission Hierarchy [PB] (M3)
Each role has a setting controlling which other roles users in that role can manage/edit. Options:
- **None** – cannot edit any other roles
- **Roles Below This Role** – can only edit roles ranked lower in the explicit role hierarchy. Roles have an assigned rank (Super Admin = 1, Admin = 2, etc.); lower number = higher privilege
- **Manually Select Which Roles** – explicit list of which roles this role can manage

Rules:
- This setting is configurable by Super Admin only
- A user can never add themselves or others to a role equal to or above their own (hard system rule, not configurable)
- "Manually Select Which Roles" list always excludes the user's own role and any roles above it

### Record-Level Permissions [PB]
- If user lacks permission to a record → that record does NOT EXIST for them
- Not returned in queries, not in reports/dashboards, not accessible by record ID, not counted in totals, cannot be inferred through relationships
- Applies to: UI, public APIs, automations/integrations
- Controlled via custom rules with logical conditions (any complexity)
- Rules may reference: current user, user roles/groups, record fields, related/lookup fields, computed/formula fields

### Groups
- Named collection of users; only app-creators can create groups
- Assign role to entire group; permissions apply to all members
- Remove from group → immediate permission loss
- Group managers: add/remove members, assign managers, delete group (deletes all associated permissions)

### User Tokens
- Enable secure API access without passwords; user-specific (inherit user's permissions)
- API behaves exactly like UI (same rules, same restrictions)
- Token creation restricted to Admins/delegated roles [PB]
- Admin controls: which users can create tokens, which apps token is valid in, global API enable/disable per user
- App-level token restrictions: block API for sensitive apps, allow automation only in approved apps
- Tokens: revoke instantly, rotate without changing password, disable without affecting UI access
- Admin Console shows: Token ID, name, description, owner, created date, last used, active status, apps used in

### Test As User / Test As Role [PB] (Phase 2 / Approved Addition)
Enables administrators to preview and verify exactly what a specific user or role can see and do within an app:
- **Two Simulation Modes:**
  - **Test As Specific User:** Simulates the exact user-specific security context, evaluating group memberships, user-scoped record rules (e.g., `AssignedTo = CurrentUser()`), and personal filters.
  - **Test As Role:** Simulates generic role-based permissions (e.g., Participant, Viewer) without tying to an individual person's identity.
- **Architectural Safeguards:**
  - **No Session Hijacking:** The system reuses the real server-side authorization engine directly in-memory; it does NOT log in as the target user or require/expose their credentials.
  - **Persistent Visual Banner:** A permanent, prominent banner displays across the top of the entire UI ("You are testing as [User/Role]") with a clear "Exit Test Mode" button.
  - **Audit Attribution:** Access to simulation mode is gated to an explicit administrative capability (`CanTestAsUser`). All queries and actions performed while in Test Mode are logged in the audit trail explicitly naming the real administrator who initiated the test.
  - **Security Parity:** Action Button permission exceptions, record-level security, and field visibility rules remain strictly active during simulation.

---

## TABLES & RELATIONSHIPS

### Table Operations (M1)
Add / Move between apps / Remove from app / Hide from table bar / Delete / Table aliases

### Connected Tables [PB] (M2) **[UPDATED]**
> **[NEW]** Distinct from standard relationships. Connected Tables are a tighter same-tenant coupling with field-to-field sync and cascade behavior. Standard relationships (Reference/Lookup/Summary) are separate.

- **[UPDATED]** Same tenant only — this is a hard constraint, not a permission setting
- Two creation modes: (1) define connected table at creation time, (2) convert existing local table to connected
- Field-to-field mapping between parent and child
- **[UPDATED]** Parent record deleted → child record deleted OR reference field set to null, based on child table cascade setting

### Table-to-Table Relationship Fields **[UPDATED]**
- **Reference** – field on child linking to a parent record; the reference key can be any supported field type (see Relationship Keys below)
- **Lookup** – brings parent field value into child record; always read-only (computed); not editable; can traverse relationships; usable in formulas; can serve as reference keys
- **Summary** – aggregates child records up to parent

### Relationships in PowerBase [PB] (M2)
Supports: One-to-Many (1→N) primary pattern
Plus: conditional reference dropdowns (dynamic, context-aware)

**Reference Field Conditional Filtering (Dependent Dropdowns):** **[UPDATED]**
- Dropdown options filtered based on another field's value on same form
- Full logical filtering:
  - Multiple conditions, AND/OR, nested groups
  - Operators: =, !=, >, <, >=, <=, contains, startsWith, in, etc.
  - **[UPDATED]** Compare against: static constants (hardcoded values set at configuration time, e.g. "Active"), another field on the current form, runtime variables (planned post-M2: current user, current date, current role)
- Filter stored as structured JSON; evaluated at runtime using shared filter engine

**[NEW] Type-Aware Filter Builder (Required):**
- Operator list is dynamically filtered based on the selected field's data type
- "Value from another field" dropdown shows only fields of compatible type
- Incompatible combinations (e.g. `>` on a text field, date compared to a numeric field) are never presented to the user
- Applies to all filter builder surfaces: reports, reference dropdown filters, conditional summary filters

**Reference Filter Builder UI:** **[UPDATED]**
- Add conditions, select operator, select value source (static constant or field reference), group conditions (AND/OR), nest conditions
- **[UPDATED]** Note: the same filter engine powers reports, reference dropdown filters, and conditional summary filters. The builder UI appears contextually in each area — it is not one shared UI component

**Live Relationship Evaluation (Before Save):**
- Reference filtering + lookup resolution happen before record saved
- Enables: accessing parent values immediately after selecting reference, validation with lookup data, blocking invalid saves

### Lookup Fields **[UPDATED]**
- **[UPDATED]** Always read-only (computed from parent via reference) — there is no editable mode
- Can traverse relationships (multi-hop lookups)
- Usable in formulas
- **[NEW]** Can serve as reference keys in relationships (see Relationship Keys)
- **[NEW]** Formula fields that include lookup values and carry a unique constraint: if a parent record change causes the computed formula value to change and violates uniqueness, the parent save must surface this as a validation error

### Summary Fields [PB] (M2) **[UPDATED]**
Aggregates child records up to parent.

**[UPDATED] Supported Aggregations:** Count, **[NEW] Distinct Count,** Sum, Min, Max, Average, Combined Text (Concatenation)

**[UPDATED] Type-Based Rules (strictly enforced — UI restricts, backend revalidates):**
- Sum / Average → numeric fields only
- **[UPDATED]** Min / Max → numeric and date fields only. Text fields: NOT supported (lexicographic ordering produces incorrect results for numeric-looking strings)
- **[NEW]** Distinct Count → any field type; counts unique values only
- **[UPDATED]** Combined Text → any field type; non-text values (numbers, dates) are automatically cast to their string representation using the field's configured display format — the UI does not block selection of non-text fields for Combined Text

**Combined Text Configuration:** **[UPDATED]**
- Configurable delimiter (comma, newline, pipe, etc.)
- Optional sorting (by date/sequence)
- **[UPDATED]** Optional distinct values: if enabled, duplicate values across child records appear only once in the concatenated output. Example: values ["Active","Active","Closed"] with distinct → "Active | Closed"

**Conditional Summary Fields:**
- Aggregate only a subset of child records
- Unlimited logical conditions (same filter engine as reports)
- **[UPDATED]** Compare against: static constants, child fields, parent fields (e.g. count tasks where Task.Priority = Client.DefaultPriority — the filter references a field on the parent record itself), lookups/formulas

**[NEW] Scalar → Summary Conversion (M3):**
- An existing scalar field can be converted to a summary field type
- Requires: a valid relationship to a child table must exist first; conversion blocked without it
- Existing stored values are replaced by the aggregation result after conversion
- Operation is logged; reversible (summary → scalar copies current aggregated values to storage)

### Relationship Keys [PB] (M2) **[UPDATED]**
Reference keys can be: Scalar field, Formula field, Lookup field, Date field / Date-derived formula
(Date and formula fields as reference keys = PowerBase improvement over Quickbase, which blocks these)

**[UPDATED] Supported reference key capabilities:**
- Scalar keys: standard behavior
- Formula keys: may combine multiple fields, include conditional logic, produce text/numeric/date output, not required to be unique
- Lookup keys: resolved first then used for matching; enables chaining without duplicating data
- **[NEW]** Date keys: date-only fields are timezone-safe (stored as ISO date string YYYY-MM-DD, no time component, compared by value equality). DateTime fields as reference keys match on UTC values only — no timezone conversion applied at match time

**[NEW] Timezone Policy for Date Reference Keys:**
- Date-only field as key: no timezone issue. Compared as ISO date strings. Safe.
- DateTime field as key: matched on UTC value only. Application and display timezone not applied during JOIN. User responsibility to ensure data consistency.
- Formula extracting date from DateTime: formula must explicitly specify timezone for the extraction (UTC default). This is a required parameter, not implicit.

**[UPDATED] "Relationship-safe" Validation (enforced at relationship creation):**
A formula key is validated as relationship-safe before the relationship can be saved. Criteria:
1. Deterministic output — no volatile functions (RAND(), NOW()) unless user explicitly acknowledges and accepts
2. Defined stable output type — text, number, or date (not ambiguous or runtime-typed)
3. No circular dependencies — formula cannot reference a field that itself depends on this relationship
4. No self-reference through the relationship being defined
5. Output type matches the child reference field type — type mismatch = relationship save rejected with specific error
6. If lookup-based: lookup chain must resolve independently before this relationship

**[NEW] Formula Key Lifecycle Rules:**
- Formula definition change after relationship exists: system warns "This formula is used as a reference key in [X] relationships. Changing it may orphan existing child records." Requires explicit confirmation. After save: re-validation pass on all child reference values; orphaned records flagged.
- Formula evaluating to null: that parent record has no matching children. Valid state. Null-key parents excluded from reference dropdown options.

**[UPDATED] Performance — Formula Key Materialization:**
- Formula key values are computed by the application at record save time and stored in a dedicated indexed column (app-level materialization)
- JOINs use the stored indexed value, not live formula evaluation
- If formula definition changes: a re-materialization job runs across all affected records
- High-volume tables require this indexed materialized column from day one — do not defer

### [NEW] Relationship Metadata & Cascade Options
Each relationship stores:
- Parent table ID, child table ID, relationship name
- Reference field definition (child → parent link)
- **[UPDATED]** Reference key type: scalar | formula | lookup | date (what type the chosen reference field is — not a list of permitted types)
- Cascade behavior setting (see below)

**[NEW] Cascade Options (when parent record is deleted):**
| Option | Behavior |
|---|---|
| Restrict | Parent delete blocked if child records exist |
| Cascade Delete | Child records automatically deleted |
| Set Null | Child reference field set to null; child record preserved |
| Set Default | Child reference field set to configured default value |

**[NEW] Cascade Options (when parent record key value changes):**
| Option | Behavior |
|---|---|
| Cascade Update | Child reference field updated to match new parent key value |
| Restrict | Parent key update blocked if children exist |

Default: Restrict for delete, Cascade Update for key changes. Both configurable per relationship.

### Backend Design **[UPDATED]**
- **[UPDATED]** Relationship metadata: parent table id, child table id, name, reference field def, reference key type, cascade behavior settings
- Shared Filter Engine: one engine for reports + conditional summaries + reference dropdowns
- Filter stored as JSON (groups with AND/OR, conditions with field/operator/valueSource)
- Runtime: Filter JSON → query compiler → SQL

---

## FIELD TYPES & CONSTRAINTS

### [UPDATED] True Scalar Field Types (M1)
> **[UPDATED]** Self-contained stored values only. Reference, Lookup, and User are NOT scalar types — see Reference & Derived Field Types below.

| Type | Description | Notes |
|------|-------------|-------|
| Text | Free-form single-line | Unicode, configurable max length |
| Multi-line Text | Free-form multiline | Unicode, supports line breaks, configurable rows |
| Rich Text | Formatted text | Markdown/HTML subset for styled notes and descriptions |
| Number | Integer/decimal | Numeric storage without formatting |
| Decimal | High-precision | Configurable decimal places, financial/calculated values |
| Currency | High-precision currency | Configurable currency symbol, symbol position, decimals, thousands separator |
| Rating | Visual rating | Numeric value presented as stars/scale |
| Percent | High-precision percent | Numeric value stored as decimal, formatted as percentage |
| Boolean | Checkbox (True/False) | Stored as 0/1 |
| Date | Date only | ISO format (YYYY-MM-DD), default Today, `t`/`[`/`]` keyboard shortcuts |
| DateTime | Date + time | UTC storage, tenant/app timezone display conversion |
| Time of Day | Time only | HH:MM / HH:MM:SS format |
| Duration | Time span | Stored as seconds/milliseconds; displayed in named units (days, hours, minutes, seconds) |
| Phone Number | Smart text | Preserves punctuation; normalized digits for SMS/telephony integration |
| Email Address | Validated email | RFC format validation, clickable mailto |
| URL | Web link | Protocol enforcement (http/https), clickable link, iframe display toggle |
| Formula URL | Dynamic web link | Formula-generated URL with link text and window target |
| Multiple Choice | Single select from list | Configurable choices list, strict validation |
| Multi-select | Multi-select from list | List-type field supporting multiple selections and list operators |

All types: UTF-8/Unicode compliant (Hebrew, Gujarati, Hindi, Arabic, etc.)

### [NEW] Reference & Derived Field Types (M1)
> Fields that depend on relationships or external sources — these are NOT scalar types.

| Type | Description | Notes |
|------|-------------|-------|
| Reference | Foreign key to parent record | Key type: scalar, formula, lookup, or date |
| Lookup | Derived value from parent via reference | Always read-only; cannot be made editable |
| User | Foreign key to platform user registry | Managed at platform level, not tenant-table data |

### User Field Type [PB] **[UPDATED]**
- **[UPDATED]** Users managed in platform user database (not tenant-table data) — this is a reference to the system user table, not a scalar value
- User object: UserId, Email, Name, Roles, Groups, Status
- Enables: current user logic, row-level security, formula-based permissions

### Phone Number Handling
- Stored as text (not numeric) — preserves +, -, (, ), spaces, country codes
- Normalized digits extracted internally for API calls (SMS, WhatsApp, telephony)
- Display format configured at app level: (XXX) XXX-XXXX, XXX-XXX-XXXX, +CountryCode, custom

### Constraints

**DB-Level (Hard – enforced at API + persistence):**
- Required: API fails if missing
- Unique: must be unique (NULLs allowed; multiple blanks OK)
- Default Value: auto-applied on insert
- Data Type: enforced on save

**UI/Form-Level (Soft – UI only):**
- Required in Form: UI only, does NOT block API inserts (matches Quickbase behavior)
- Regex Validation: format validation only (email, ZIP, custom identifiers)
- Range Validation: UI only

**Unique Constraint:** Multiple blanks allowed; once value entered must be unique; updates validated

### API Behavior Summary
| Scenario | Result |
|----------|--------|
| Missing DB-required field | API fails |
| Missing Form-required field | API succeeds |
| Invalid type conversion | Value → NULL |
| Unique constraint violation | API fails |
| Regex violation (UI) | Blocked in UI |
| Regex violation (API) | Optional enforcement |

### Field Type Conversion (M3) **[UPDATED]**
**Formula → Scalar:** current evaluated values copied to storage; formula discarded; field becomes editable
**[NEW] Scalar → Summary:** requires a valid child relationship to exist first; existing values replaced by aggregation; operation logged
**Scalar → Scalar:**
| From | To | Behavior |
|------|----|----------|
| Text → Number | Convertible retained | Non-numeric → NULL |
| Number → Text | Always retained | Safe |
| Number → Boolean | 0/1 only | Others → NULL |
| Text → Boolean | 0/1/true/false only | Else NULL |
| Any → Text | Always retained | Safe |
No record loss; operation logged.

### Controlled Field ID Assignment [PB]
- Field IDs need not be auto-incremented
- Manual mode: admin sets explicit ID (must be unique, in range, not conflicting with system fields)
- IDs immutable once data exists (configurable)
- Duplicate IDs rejected; reserved system IDs blocked; conflicts across related tables prevented

### Special Field Types
**Date Fields:** Default to today (checkbox); keyboard shortcut 't' = today, '[' = day before, ']' = day after; configurable format
**Duration Fields:** Stored as duration; display options: HH:MM, HH:MM:SS, :MM, :MM:SS, Smart Units, Weeks, Days, Hours, Minutes, Seconds; configurable decimal places
**Numeric Field Subtypes:** Simple Number, Star Rating (1–5 stars), Percent, Currency
**URL Fields:** Plain URL (full user-entered) or Formula-URL (partial + concatenated)
**Reportable Flag:** Controls field availability in Report Builder for filtering/sorting/grouping
**Color-Coding:** Formula rich text fields with HTML; formula-driven color based on field values

### Formula Fields (M1 basic, M2 advanced)
Formula fields evaluated at runtime.
**Types:** Text, Numeric, Date, DateTime, Time of Day, Duration, Checkbox, Phone Number, Email Address, User, List-User, URL, Work Date, Rich Text, Multi-select Text
**Formula → Scalar Conversion:** evaluated values copied; formula discarded
**[UPDATED] Formula as Reference Fields [PB]:** may combine values, conditional logic, text/numeric/date output, not required to be unique. Subject to relationship-safe validation (see Relationship Keys).

### Field Change Auditing, History & Notes [PB]

**Mandatory Change Audit (Who/What/When):**
Any change to field definition automatically recorded: who, when, what changed, previous → new value. Applies to: field type, formula logic, lookup config, reference config, summary logic, validation rules, display properties, permissions, any metadata.

**Mandatory Change Notes [PB]:** User MUST enter a change note before saving any field config change. Note explains why + what changed. Cannot save without note.

**Field Notes & Purpose Documentation:**
- At creation: describe purpose, business meaning, expected usage
- Ongoing: append-only notes (not tied to change events)

**Combined Field History Timeline:** creation event + all changes + change notes + manual notes + author + timestamp

### System Columns (auto on every table)
| Column | Purpose |
|--------|---------|
| CreatedOn | Timestamp |
| CreatedBy | User |
| ModifiedOn | Timestamp |
| ModifiedBy | User |
| RecordId | Unique identifier |

### Range Fields
Logical fields: Date Range (Start+End), Numeric Range (Min/Max), Age Range, Period Range
Single logical field; stored as structured values; queryable; usable in formulas/filters

### Snapshot Fields [PB] (Approved Addition)
A scalar field that captures a one-time copy of a selected lookup or formula value upon record creation:
- **One-Time Capture:** When a record is first created, the engine reads the source field's current value and copies it into the snapshot scalar column.
- **Server-Side Read-Only:** Once populated, the field is permanently read-only across UI, API, import, and automations. It does NOT update when the source parent record or formula changes.
- **Null Safety:** If the source value is blank at creation time, the snapshot field remains blank.
- **Bulk Re-initialization:** If initial data was incorrect or a structural change requires updating existing records, authorized administrators can trigger an audited, asynchronous background job to re-evaluate and re-populate snapshot values across existing records.

---

## REPORTS (M1 table/summary, M3 charts)

### Report Types
| Category | Capabilities |
|----------|-------------|
| Table Reports | Grid edit, bulk operations, inline editing, formulas, filtering |
| Summary Reports | Grouped data, aggregate calculations |
| Chart Reports | Bar, line, pie, donut; interactive filtering |

### Table Reports
Core: pickable columns, column reordering, report-level formulas, dynamic filtering, single/multi-column sorting, grouping, ALL/ANY (AND/OR) filter logic
Output formats: Excel (xlsx), CSV
Options: hide totals/averages, show only new/changed records, enable record actions (view/edit)
Saving: shared (everyone / users in my role / specific roles / hidden/URL-only) or personal (creator only)
Shared reports require App Manager permission.

### Grid Edit [PB]
Spreadsheet-like interface for high-volume editing (table reports only).
Actions: edit multiple records inline, delete multiple, bulk update cells, bulk record selection.
**Form rules ARE executed during Grid Edit** (per record, even bulk edits, immediate on value change).
Every Grid-Edit-enabled report must specify which form's rules apply.
Checkbox behavior: consistent across form/report/grid edit; triggers form rules immediately; evaluated per record in bulk.

### Summary Reports **[UPDATED]**
Group by one+ fields; aggregate functions (count, **[NEW] distinct count,** sum, avg, min, max); conditional summaries; sort by summary values.

### Chart Reports [PB] (M3)
Types: Bar, Line, Pie, Donut
Capabilities: based on table or summary reports, interactive filtering, grouping, aggregations, drill-down, real-time updates, respects record-level permissions
Chart interactions that modify data trigger: form rules, validation logic, checkbox behavior

---

## FORMS & FORM DESIGNER (M1 basic, M2 advanced)

### Layout
- Drag & drop sections (reorder, resize, organize into logical groups)
- Field placement: (1) Drag & Drop from left panel, (2) "+ Add Fields" button → multi-select list (Quickbase-style preferred UX)
- Both methods supported

### Form Rules & Conditional Logic (M1 basic, M2 advanced)
**Triggers:** field value conditions, user role conditions, date/status conditions, field change detection [PB]

**Detecting Field Changes [PB] (M2):**
- Tracks previous value + new value
- Rules can evaluate: has value changed? Changed from X to Y?
- Enables: show warning on modify, require reason on change, trigger validation only on change

**Rule Conflicts & Priority Resolution [PB]:**
- Multiple rules may conflict on same field
- Higher-priority rule wins (deterministic, explicit priority ordering)

**Blank/Not Set Conditions [PB]:**
- "Is set" / "Is not set" as first-class operators in rule builder
- No value input shown when condition is "Is blank"

**Trigger on Formula Change [PB] (M2):**
- Triggers: when value changes, when condition becomes true, when condition becomes false
- Applies to normal fields AND formula-based fields

**Formula Fields as Data Sources for Rule Actions [PB]:**
- Use formula field to: set another field's value, change label, populate warning/alert text, control visibility/requiredness, drive validation messages

**Dynamic Rule Actions [PB]:**
- Dynamic Labels: label = value from formula field (updates live, no reload)
- Dynamic Messages: "You cannot save after {{Calculated Deadline}}" (formula field)
- Dynamic Prevent-Save: If Today > Calculated Cutoff (formula) → prevent save + dynamic message

### Runtime Formula Evaluation (No Refresh) [PB]
- Formulas recalculate immediately as user enters data
- UI updates instantly (no save, no page refresh)
- Values computed in memory; persistence only on Save
- Formula + Form Rules work together in real time (reactive UI)

### Report Link Field Type & Embedded Report Filtering [PB] (M3)
- Enables dynamic filtering of reports based on current record values
- Configuration: source field (current table), target table, target field, matching rule (exact match default)
- At runtime: reads report link field → extracts source value → filters target table → displays matching records (auto, no reload, per record)
- Embedded report types: Child list (relationship-based) | Report Link list (field-to-field mapping)
- Reports tied to tables (not apps); filtering always table-to-table

### Quickbase Form Migration & Rule-Construct Mapping [PB] (M2)
Quickbase applications contain two distinct, incompatible form architectures: Legacy Forms and New Dynamic Forms.
- **Mapping Strategy:** Rather than maintaining two separate form engines in PowerBase, the QBL importer performs **rule-construct mapping**.
- **Unified Translation:** Every recognized legacy rule construct (including legacy field property overrides and legacy field-change triggers) is translated directly into unified PowerBase reactive form rules and value-change triggers.
- **Reconciliation & Reporting:** Any legacy construct that cannot be cleanly mapped is documented in an import translation report with the form name, field ID, and reason, ensuring zero silent data or rule loss.

---

## FIELD TYPES FOR FILTERING (Show Dropdown)
All, Text, Numeric, Date, Duration, Checkbox, Phone Number, Address, Email Address, User, List-User, Multi-select Text, File Attachment, URL, Report Link, Record ID#, Relationship, Formula, Default in reports, Reportable, Searchable

---

## DELETED RECORDS & RESTORE [PB] (M4)

### Soft Delete Model
- Records never immediately destroyed (archived, not hard-deleted)
- Archived record: hidden from forms, reports, APIs, automations
- Stored in secure archive state; full metadata preserved (original values, deleted by, deleted datetime, optional reason)
- `IsDeleted = true` flag

### Record Restoration
- Authorized users (Super Admin / Data Recovery role): view archived records, restore to original state, reattach to relationships/reports
- Restoration: does NOT create new record; preserves original Record ID; maintains referential integrity

### Archive Retention
- Configurable per tenant (30 days / 90 days / 1 year / custom)
- Final duration finalized with client per compliance/storage/business needs
- After retention period: permanent deletion

---

## AUDIT LOGS [PB] (M1) **[UPDATED]**

### Overview **[UPDATED]**
- **[NEW]** "At rest" definition: data stored on disk in any persistent storage — databases, blob storage, search indexes, backups — as opposed to data in transit over a network
- **[UPDATED]** Stored separately from primary transactional database; Azure-managed logging datastore (Azure Blob Storage with indexed metadata OR Azure Log Analytics — implementation choice to be finalized before M1 build; choice affects cost, query capability, and retention management)
- High volume, long retention, performance-isolated from main application
- Always available, queryable, filterable, downloadable
- **[NEW]** Audit logs are read-only and append-only by design. They record state changes but do not revert them. Reverting records is handled by M4 Archive/Restore — never through the audit log

### Retention **[UPDATED]**
- **[UPDATED]** Configurable per tenant: 90 days / 1 year / 3 years / custom
- **[NEW]** After active retention window: logs move to cold/archive storage (cheaper) but remain queryable
- **[NEW]** Logs are never hard-deleted unless tenant explicitly requests deletion (compliance consideration)
- **[UPDATED]** Downloadable in 7-day increments (download chunk size limit only — not a query range limit)

### Access
- Log access = separate permission domain (not just another app feature)
- Some roles/accounts may have no visibility

### Log Entry Contents
User identity, timestamp, app/table/record context, action (Create/Update/Delete), **previous value**, **new value**, field-level change details

### Logged Activity Types
| Activity | Types |
|----------|-------|
| User events | Creation, token CRUD, logins/logouts, app invite/grant/role-update/remove |
| Group events | User added/removed from group |
| Role events | Create/delete/rename role; allow/disallow record add/delete/edit permissions per table |
| Login failures | Invalid credentials, deactivated user, invalid token |
| Data access | Record access/create/modify/delete; report/dashboard access; API_DoQuery; API_DoQueryCount; table data access (UI + API) |
| Schema changes | App/table/field/relationship create/delete; offline mode; dashboard CRUD |

### Querying Audit Logs (Realm Admin) **[UPDATED]**
Filters:
- Start and end date **(query range: up to retention period; no 7-day cap on queries, only on downloads)**
- App name
- **[NEW]** Table name / Table ID
- **[NEW]** Record ID (critical for compliance: "who touched record #123?")
- User email
- **[NEW]** Action type (Create / Update / Delete)
- **[NEW]** Field name
- Log ID

---

## AUTOMATIONS & PIPELINES [PB] (M4)

### Overview
Logic-driven workflows reacting to events, scheduled jobs, or bulk operations.
Pipeline is NOT limited to trigger record — can query any table, loop, branch, act dynamically.

### Pipeline Builder (Visual)
- No-code/low-code canvas; drag & drop steps
- Left panel: data context (trigger record fields, query results, loop variables, system context)
- Right panel: searchable steps tray (triggers, queries, conditions, loops, actions)

### Step Types

**1. Triggers**
- Record-based: On Add, On Modify, On Delete (provides initial context only; subsequent steps not restricted to this record)
- Bulk Trigger [PB]: process many records (all overdue invoices, 10k+ records, retroactive rule changes); executes in batches with pagination, throttling, retry handling
- Schedule Trigger: cron-like (daily, weekly, monthly)
- External Trigger (Webhook): external system calls generated endpoint; payload → pipeline input; supports auth, headers, JSON mapping

**2. Query Steps**
- Query any table; reference fields by FID only (never names)
- Filter using: field values, trigger data, user context, date logic, formula expressions
- Select fields to expose downstream

**3. Loop / Iteration**
- For Each Record: loops through Query or Bulk Trigger results
- Each iteration: current record, index, aggregates (count, etc.)
- Supports: nested logic, conditional branching per record

**4. Conditions (Logic Engine)**
- Levels: step level, inside loops, before actions
- Supports: If/Else If/Else, AND/OR/NOT, formula expressions, cross-table comparisons

**5. Actions**
- Record Actions: Create / Update / Delete (target any table; use trigger data, query results, loop vars, computed expressions)
- Email Action (Dynamic): configurable From (system/user/mailbox), To/CC/BCC (fields/query results/expressions), template with dynamic placeholders + conditional sections, attachments (record files or generated)
- File Upload: to Blob storage or external systems; tracked via metadata/logs; usable in loops
- External API / Webhook: HTTP REST calls; supports headers, auth tokens, dynamic payloads

**Trigger Checkbox [PB]:**
- System-created field per trigger; auto-unchecked after successful trigger; user cannot modify/delete this field's schema type

---

## HIPAA-ALIGNED TECHNICAL ARCHITECTURE [PB] **[UPDATED]**
> **[NEW] Important:** The technical safeguards described below are designed to support HIPAA compliance requirements. They are necessary but not sufficient for HIPAA compliance. Full HIPAA compliance additionally requires: signed Business Associate Agreements (BAAs) with Azure and any subprocessors, organizational policies and procedures, staff training programs, incident response and breach notification plans, and formal compliance assessment. PowerBase delivers the technical architecture layer only. Compliance certification requires a separate legal and compliance engagement.

### Data at Rest **[UPDATED]**
> **[NEW]** "At rest" = data stored on disk in any persistent storage — databases, blob storage, search indexes, backups.

- Azure SQL: TDE with Customer Managed Keys (CMK) in Azure Key Vault
- Column-level PHI protection: Always Encrypted with Secure Enclaves (SSN, Name, DOB, AddressLine) — server-side range/pattern compares possible while keys never leave enclave
- Azure Blob Storage: SSE with CMK + optional client-side encryption for ultra-sensitive files
- Azure Cognitive Search: index encryption at rest (CMK supported for sensitive indexes)

### Key Management
- Azure Key Vault: RBAC, purge protection, soft delete, rotation policies for CMKs
- Managed Identity for API/Functions → no keys in code or config
- Audit: access to keys/secrets/decryption events logged to Azure Monitor/Log Analytics; PHI access traceable
- BAA & HIPAA/HITECH controls: administrative, physical, and technical safeguards (access control, minimum necessary, audit, breach notification)

---

## PUBLIC API (M5)

Endpoints:
- Get a report
- Run a report (table report, summary report, user input only)
- API_DoQuery
- API_ImportFromCSV
- Get tables
- Get fields for tables
- Insert/update record
- Delete record

User Tokens: secure API access; user-specific permissions; not shared/global; token scope configurable per app; revoke/rotate without affecting UI login.

---

## GROUPS & PERMISSION MANAGEMENT **[NEW]**

**Groups** are named collections of PowerBase users for bulk permission assignment.

**Who can create groups:** Only users who have permission to create apps.

**What groups enable:**
- Assign app access to many users at once
- Assign a role to an entire group
- Manage access centrally (e.g. one "Sales" group → assign role → all 20 members inherit it)
- Quickly revoke access (remove user from group → immediately loses all group-granted permissions)
- Provision users before they access any data
- Manage permissions consistently across multiple apps

**Group Management capabilities (for Group Managers):**
- Add or remove members
- Assign additional managers
- Delete the group (removes all group-based permissions for all members)

**Permission inheritance rule:** All permissions assigned to the group's role apply automatically to every group member. Removal is immediate.

---

## REPORT LINK FIELD TYPE & EMBEDDED REPORT FILTERING **[NEW]**

A **Report Link** is a special field type that enables dynamic filtering of reports based on values from the current record. Used to embed related data without strict relationships, filter reports contextually, and display record-specific lists inside forms.

**Configuration — when creating a Report Link field, define:**
- **Source Field (current table):** Any field from the current table (e.g. Project.Region, Project.ClientId)
- **Target Table:** A table (not an app) — e.g. Clients, Tickets, Invoices
- **Target Field (target table):** Any field in the target table to match against
- **Matching Rule:** Exact match (default); additional match types TBD

**Runtime behavior:**
1. Form opens → system reads the Report Link field
2. Extracts source field value from the current record
3. Applies the mapping as a filter on the target table
4. Displays only matching records in the embedded report
5. Happens automatically, without page reload, per record

**Report Ownership Model (intentional design):**
- Reports are explicitly tied to **tables**, not apps
- When configuring a Report Link, user selects: Target Table → Report defined on that table
- Filtering logic always operates table-to-table, never app-to-app
- This avoids ambiguity and mirrors the actual data model (contrast: Quickbase shows an App selector because tables are grouped by app, but the app itself is not the filtering unit)

---

## FORMULA FIELDS & RUNTIME EVALUATION **[NEW]**

**Core contract:** PowerBase forms support runtime evaluation of formulas and conditional logic. Calculated values update immediately as users enter data — without page refresh, record save, or navigation away from the form.

**Key distinction — "No Refresh" ≠ "No Save":**
| Aspect | Behavior |
|--------|----------|
| UI update | Instant — calculated value displayed immediately |
| Page reload | Never required for formula recalc |
| Database write | Only on explicit Save |
| In-flight values | Computed in memory during editing session |

**Example:** `DayOfWeek = DayName([Selected Date])` — user enters "January 2" → `DayOfWeek` shows "Friday" immediately. No Save. No Refresh.

**Formula + Form Rules interaction (all in-memory, no save/refresh):**
1. User changes a Date field
2. Formula recalculates → `DayOfWeek` updates
3. Dependent formula evaluates → `IsWeekend = (DayOfWeek = Saturday OR Sunday)`
4. Form rule fires → if `IsWeekend = true` → show warning, require justification
All steps happen in a single runtime session without save/refresh.

**Reactive UI system:** Formula evaluation is part of the Reactive UI engine. UI responds to field value changes, formula outputs, and rule outcomes — no page reloads, no state loss.

**Implementation note:** Reactive formula evaluation must be frontend-driven (Angular reactive forms / signal-based state). Backend is not called on every keystroke — formula logic for runtime evaluation runs client-side; only the final record is persisted on Save.

---

## DATABASE OWNERSHIP, HOSTING & DATA CONTROL MODEL [PB] **[NEW]**

**Core model — separation of logic and data:**
- **Application logic** (rules, permissions, formulas, execution engines): owned and managed by PowerBase
- **Raw customer data**: can be owned, stored, and paid for by the client — either inside PowerBase-managed Azure, or inside the client's own Azure subscription

**Why this matters:**
- Full customer data ownership
- Enterprise compliance
- Vendor lock-in protection
- Flexible deployment options

**Data access model (when data is in client's Azure):**
- PowerBase accesses via Managed Identity / Service Principal
- Least-privilege permissions only
- No direct human access to customer databases
- All access is audited and logged
- PowerBase plugs into client's Azure environment without taking ownership of it

**Deployment options:**
| Option | Data Location | Managed By |
|--------|--------------|------------|
| Standard | PowerBase Azure | PowerBase |
| Client-hosted | Client's Azure subscription | Client pays, PowerBase accesses via MI |

**Milestone:** Pricing and deployment configuration finalized per-tenant at contract stage. Client-hosted deployment is a post-M6 offering (requires per-tenant infrastructure provisioning pipeline).

---

## KEY POWERBASE VS QUICKBASE DIFFERENCES **[UPDATED]**

| Feature | Quickbase | PowerBase |
|---------|-----------|-----------|
| Admin role | All-powerful, can undo own restrictions | Split into Builder Capabilities; Super Admin only all-access |
| Record-level permissions | Filtering | True security (record doesn't exist if no permission) |
| Reference field types | Scalar only; date fields blocked | Scalar, Formula, Lookup, Date all supported |
| Reference dropdown filtering | Basic field=field | Full logical filtering (AND/OR, nested, operators) |
| Summary conditions | Limited patterns | Unlimited logical conditions |
| **[NEW]** Summary aggregations | Count, Sum, Min, Max, Average | + Distinct Count, + Combined Text with auto-cast |
| Deleted records | Limited restore | Soft delete with configurable archive retention + full restore |
| Form rules: blank detection | Workaround needed | First-class "Is set / Is not set" operators |
| Form rules: formula change trigger | Not available | Full support (value change, condition true/false) |
| Form rules: field change detection | Not available | Tracks previous + new value, detect change events |
| Dynamic labels/messages | Static only | Formula-driven (live update) |
| Date field as reference key | Not supported | Supported |
| **[UPDATED]** Builder permissions | No separation from data | Build without seeing data; Report Builder sees aggregates only, never raw records |
| **[UPDATED]** Copy app modes | Schema Only / Schema+Data | + Partial Clone; async background copy (no hard record limit) |
| Token creation | Any admin | Explicit permission required [PB] |
| Token scope control | Limited | Per-user, per-app, API enable/disable [PB] |
| **[UPDATED]** Audit logs | In app DB | Separate Azure logging datastore; configurable retention; richer query filters [PB] |
| Mandatory change notes | No | Required on every field config change [PB] |
| Automations | Record-scoped | Cross-table, bulk, loop, query any table [PB] |
| Data hosting | Vendor only | Vendor or client's own Azure [PB] |
| **[NEW]** Master-owned element override | N/A | Child can detach or ignore specific Master updates [PB] |
| **[NEW]** Action Buttons | Basic rich text links / URL buttons | Native Action Buttons (Signature, File, Prompt, Data) with narrow permission exceptions and in-place reactive updates [PB] |
| **[NEW]** Snapshot Fields | Workaround via pipelines/webhooks | Native field type; snapshots lookup value at creation, server read-only, bulk re-init [PB] |
| **[NEW]** Test As Mode | Test role only (basic) | Test As Specific User and Test As Role; reuses real engine; banner + admin audit [PB] |
| **[NEW]** Sidebar Navigation | Auto-generated tables only | Independently managed navigation module; custom names, groups, report hovers, add-record links [PB] |
| **[NEW]** Template Imports | Manual CSV import only | Multi-target Excel/CSV templates with virtual formula columns and partial commit error feedback [PB] |
| **[NEW]** App User Profiles | Separate manual table required | Buildable Users table per app over central immutable Master User identity (Phase 2) [PB] |
| **[NEW]** Custom Code Pages | Unrestricted or iframe | Session-API authenticated, CSP-secured, Monaco-integrated Code Pages [PB] |

---

## SHARED FILTER ENGINE (Core Infrastructure) **[UPDATED]**
Single filter engine used by: reports, conditional summaries, reference dropdown filtering, table imports, and notifications
Stored as JSON: groups (AND/OR) + conditions (field/operator/valueSource) + valueSource (constant or "from another field")
Runtime: Filter JSON → query compiler → SQL
Supports: nesting, dynamic values from current record/form, cross-table comparisons

**[NEW] Type-awareness requirement:** All filter builder surfaces must dynamically filter operator lists and comparable field lists based on the selected field's data type. Incompatible combinations are never shown to the user.

**[NEW] Runtime Variables (planned, post-M2):** Filter conditions will support system-context values (current user, current date, current user's role/group) as value sources. Not in M2 scope. Milestone TBD.

---

## ACTION BUTTONS [PB] (Approved Addition)

Action Buttons allow users to perform targeted, single-click operations that write directly to specific fields, prompt for input, capture signatures, or upload files without navigating through standard form editing flows.

### Four Button Variants
| Variant | Identifier | Behavior & Data Flow | Target Field Type |
|---|---|---|---|
| **Signature Button** | `SIGNATURE` | Opens an interactive signature pad modal; captures signature vector/image, saves to Azure Blob Storage, and writes the secure file URL to the target field. | File / Attachment |
| **File Button** | `FILE` | Opens a file picker / dropzone dialog; uploads file to Blob Storage with configurable timestamp/naming prefix and writes reference to the target field. | File / Attachment |
| **Prompt Button** | `PROMPT` | Pops a centered modal dialog prompting the user for one or more inputs (free text, choice dropdown, number, date); writes the entered value(s) into the configured target field(s). | Text, Choice, Number, Date |
| **Data Button** | `DATA` | Performs an immediate, non-interactive write of a preconfigured static value or formula-evaluated result directly into the target field. | Any compatible scalar field |

### Critical Behavioral Rules
1. **Rule 1 — Works Without General Edit Permission (Privileged Narrow Write):**
   - A user who has **View-Only** table permission can still execute a configured Action Button if granted button execution access in their role.
   - The server authorizes this as a narrow, preconfigured write exclusively to the designated target field(s).
   - Record-level security still applies: if the record is invisible to the user, they cannot execute the button against it.
   - All database constraints (e.g. required, unique, type) are enforced server-side.
2. **Rule 2 — No Page Refresh (In-Place Update):**
   - Clicking an Action Button updates the record in place reactively via Angular state/signals.
   - The form and surrounding lists remain uninterrupted; no page reload occurs.
3. **Rule 3 — Referenceable in Formula Fields (Dynamic & Conditional Buttons):**
   - Formula fields can generate dynamic Action Buttons using formula functions (e.g., `ActionButton("Approve", "btn_approve_12", "green")`).
   - Enables conditional button labels, colors, disabled states, and dynamic destination parameters based on in-memory record state.

### Modal & Dialog Save-State Behavior (July 2 Update)
- All prompt, file, and signature interactions display within a centered, responsive modal dialog.
- **Unsaved Form State Preservation:** Triggering an Action Button dialog while on an unsaved record form preserves all dirty/unsaved form inputs in client memory.
- **Navigation Safety:** If an Action Button triggers an external URL or navigation redirect:
  - If configured to open in a new tab/window (`_blank`), the current form remains unchanged.
  - If navigating in the same window, a browser confirmation dialog ("You have unsaved changes. Leave without saving?") warns the user before abandoning state.

### External URL Mode (July 2 Update)
Enables sending an Action Button link to external, unauthenticated third parties (e.g., collecting an external vendor signature or client confirmation via email):
- **Per-Button Configuration:** Toggle between `PowerBase-Only` (default, session-authenticated) and `External URL-Accessible`.
- **Public Controller Endpoint:** Generates a secure, tokenized URL (e.g. `https://app.powerbase.com/action?token={secure_hash}`).
- **Mandatory Password Gate:** When External URL Mode is enabled, a **Password Gate is strictly required** (static passphrase, formula-generated password, or reference to a record field). The external user cannot trigger the write without entering the correct password.
- **Server-Side Security & Audit:** Write scope remains strictly restricted to the preconfigured target field(s). The audit log attributes the write to an external actor identified by IP address, timestamp, and token identifier.

### Multi-Capture Composition (July 2 Update)
Allows combining multiple sequential capture steps into a single button click flow rather than forcing builders to create multiple buttons:
- **Sequence Composition:** Builders configure an ordered list of capture elements (e.g., Step 1: Text Prompt for "Approval Reason" → Step 2: Checkbox Confirmation for "Terms Accepted" → Step 3: Signature Pad for "Signer").
- **Design-Time Validation:** The system strictly rejects any configuration where two capture elements in the sequence write to the same target field.
- **Transactional Commit:** All values in the multi-capture sequence commit together in a single atomic server-side write upon completion.

---

## TABLE-TO-TABLE IMPORT [PB] (Approved Addition)

Saved, repeatable import configurations that move and synchronize data between any two accessible tables within the same tenant.

### Core Execution Modes
| Mode | Behavior | Conflict & Duplicate Handling |
|---|---|---|
| **Copy (Append)** | Reads records matching the source filter and inserts them as new records in the destination table. | Generates new system Record IDs; does not check for existing matches unless unique constraints apply. |
| **Merge (Upsert)** | Compares source records against destination records matching on a designated **Unique Destination Key Field**. | If matching destination key exists → updates configured fields. If no match → inserts new record. If duplicate keys exist in destination → fails before writing. |

### Configuration Capabilities
- **Source Filtering:** Uses the Shared Filter Engine to restrict which source records are included.
- **Field Mapping:**
  - **Dynamic Field Mapping:** Maps source fields to destination fields by Field ID (FID).
  - **Static Defaults:** Assigns constant fallback or default values to destination fields.
  - **Virtual Columns (On-the-Fly Formula Mapping):** Builders write PBL formula expressions that evaluate at import time (e.g., combining `FirstName` + `LastName`, or formatting phone numbers) before inserting into the destination table.
- **Per-Column Import Options:**
  - `Remove Duplicates`: Deduplicates input records based on the selected column.
  - `Require Field`: Rejects source records where the specified column is blank.
  - `Ignore Blanks`: Does not overwrite existing destination values if the source value is blank.

### Operational Resilience & Integrity
- **Broken Field Detection:** If a mapped source field is later renamed or modified, mapping persists safely by FID. If a mapped field is deleted, the import configuration is automatically flagged as `Needs Attention` before the next run rather than silently corrupting data.
- **Asynchronous Execution:** Imports run as background jobs managed by Azure Service Bus / background workers, preventing HTTP request timeouts.
- **Row-Level Commits & Error Feedback:**
  - Valid rows commit successfully in batches; bad rows do not block good rows.
  - An interactive downloadable CSV feedback file is generated containing all failed source rows along with exact column-level validation rejection reasons.
- **Triggers:** Saved imports can be executed manually via UI, scheduled on a recurring cron timer, invoked via Public API, or triggered from an Automation Pipeline step.
- **Audit Logging:** Every import run logs source file/table references, user identity, row counts, and error summaries.

---

## TEMPLATE-DRIVEN FILE IMPORT (EXCEL / CSV) [PB] (Approved Addition)

A high-performance, reusable file import engine that allows builders to configure, save, and execute standardized spreadsheet uploads.

### Core Features
- **Multi-Target Table Mapping:** A single Excel workbook (or multi-worksheet file) can map to multiple destination tables in a single import run (e.g., sheet 1 to `Customers`, sheet 2 to `Invoices`).
- **Worksheet & Header Positioning:** Configurable worksheet name, start row, header row, and column mapping by either header name or positional index (A, B, C...).
- **Filter Formulas:** Pre-import PBL filter expressions determine whether a row qualifies for processing before database insertion.
- **Virtual Columns:** Allows on-the-fly formula transformations during CSV/Excel ingestion.
- **Duplicate & Blank Policy:** Configurable per-column options for handling duplicates (first wins vs. abort) and blank values.
- **Completion Notifications:** Configurable email notification sent upon background job completion with summary metrics (rows inserted, updated, rejected) and a direct link to download the error feedback report.
- **Shared Engine:** Built on the exact same core ingestion pipeline as Table-to-Table imports, ensuring identical validation, batching, and error reporting behaviors.

---

## PAGES: DASHBOARD PAGES & CODE PAGES [PB] (M3 / Deferred)

A unified presentation model decoupling custom dashboarding and developer code from raw database tables.

### Unified Pages Architecture
- All pages are managed in the App Pages registry with metadata: `PageId`, `AppId`, `Title`, `Slug`, `PageType` (`Dashboard` or `CodePage`), `RolePermissions`, `CreatedBy`, `ModifiedBy`.
- Supports versioning (`PageVersion`), change notes, draft vs. published states, and full audit tracking.

### 1. Dashboard Pages (M3+)
- Drag-and-drop canvas designed for operational reporting and data visualization.
- **Widget Ecosystem:**
  - Embedded Table Reports (with search, quick-peek, and master-detail)
  - Embedded Chart Reports (Bar, Line, Pie, Gauge, Waterfall)
  - KPI Metric Cards (single-value aggregates, trends)
  - Global Page Filters (interactive dropdowns connected to the Shared Filter Engine that filter multiple widgets simultaneously)
  - Text & Markdown guidance tiles

### 2. Code Pages (Deferred / Requires M5 Public API)
- Enables developers to write custom web applications (HTML, JavaScript, CSS) hosted directly within the PowerBase application chrome.
- **Security & Authorization Sandbox:**
  - **Same-Origin Session API:** Code Pages execute in the browser and interact with PowerBase data exclusively through the same-origin authenticated Public API using the logged-in user's session.
  - **Server-Side Enforcement:** The backend re-validates all permissions per API call; a Code Page cannot bypass record-level security, table permissions, or field restrictions.
  - **Content Security Policy (CSP):** Strict script-src and connect-src policies with administrative CDN domain allowlists.
  - **Sanitization & Quotas:** Enforced code file size limits, HTML sanitization, and global tenant-level enablement toggles.
- **Developer Experience:** Integrated Monaco code editor with syntax highlighting, live preview, version diffs, and instant rollback.

---

## SIDEBAR NAVIGATION REDESIGN [PB] (Approved Addition)

Replaces automatic table listing with an independently managed, highly customizable navigation structure.

### Navigation Architecture
- **Dedicated Navigation Editor:** Builders configure the navigation hierarchy in App Settings without altering table schemas.
- **Supported Navigation Items:**
  1. **Table Item:** Links to a table default view, with an optional hover dropdown revealing child shared reports.
  2. **Report Item:** Direct navigation link to a specific shared table, summary, or chart report.
  3. **Direct Add Record Item:** One-click action item that opens the record creation modal for a designated table.
  4. **External URL Item:** Links to external portals, documentation, or intranet systems.
  5. **Expandable Group Item:** Collapsible section header organizing related tables and reports into logical categories.
- **Advanced Capabilities:**
  - **Custom Display Names:** Navigation labels can differ completely from underlying database table names.
  - **Duplicate Table Entries:** The same table can appear multiple times in the sidebar under different groups with different default report/filter configurations.
  - **Role-Based Visibility:** Each navigation item has role permissions governing which user roles can see it.
  - **Table Default Visibility:** Backend and lookup tables can remain completely hidden from the sidebar unless explicitly exposed.

---

## NATIVE NOTIFICATIONS & DATE REMINDERS [PB] (Milestone TBD)

An automated messaging and alerting engine built natively into the platform, operating independently of the heavier Automation Pipeline workflow engine.

### Notification Variants
1. **Custom Emails:** Rich HTML emails sent on record events (create, update, status change) featuring dynamic field tokens (`{{ClientName}}`, `{{InvoiceAmount}}`).
2. **In-App Notifications:** Real-time bell notifications delivered to users inside the PowerBase web UI.
3. **Date Reminders:** Time-based scheduled alerts calculated relative to date fields (e.g., "7 days before Due Date", "1 day after Expiration Date").

### Key Engine Rules
- **Recipient Resolution:** Dynamic recipient targeting based on User fields (e.g. `AssignedTo`), static user lists, or role memberships with CC/BCC support.
- **Recipient Permission Verification:** The engine verifies that recipients have read access to the underlying record before generating the notification, preventing data leakage.
- **Trigger Conditions:** Evaluated using the Shared Filter Engine (nested AND/OR logic).
- **Bulk Notification Policy:** Batches or throttles alerts during mass updates/imports to prevent recipient inbox flooding.

---

## USERS AS A SYSTEM TABLE [PB] (Future Scope Phase 2)

**Status:** Future Scope Phase 2 (Estimated 4 days — excluded from original scope and agreed base contract).

### The Requirement
In Quickbase, a user has only an authentication identity and assigned roles. To attach richer business data (e.g., Department, Direct Manager, User Type, Office Location, Certifications), builders are forced to manually create and synchronize a duplicate "Employees" table.
PowerBase solves this by treating the app's Users as a first-class, buildable system table.

### Two-Layer Architecture: Master Identity vs. App User Profile
To prevent app-level modifications from compromising tenant-wide Single Sign-On, global auditability, and master security, PowerBase decouples identity from profile attributes:

| Layer | Stored In | Managed By | Fields & Contents |
|---|---|---|---|
| **Master User Identity** | Tenant Master DB | Platform System | Central, read-only identity: `UserId`, `Email`, `Name`, `Roles`, `Groups`, `Status`. Guaranteed consistent across all apps in the tenant. |
| **App User Profile** | App Database | App Builder | Buildable table auto-created per app with one row per user who has access to that app, foreign-keyed to Master User via `UserId`. |

### Buildable Capabilities on the Users Table
- **Add Custom Fields:** Builders can add any standard field type (Text, Choice, Date, File, Formula, etc.) to the app's User Profile layer.
- **Establish Relationships:** Relate Users to other tables (e.g., `Users` → `Departments`, `Offices` → `Users`).
- **Enforce Constraints:** Define required fields, unique constraints, and validation rules (e.g., "Department is required when adding a user to this app").
- **Custom Forms & Reports:** Build customized user profile view forms, employee directories, and filtered staff reports.
- **Field-Level Role Permissions:** Control which fields are editable (e.g., Managers can assign Departments, but only Admins can set User Types).
- **Visual Separation:** Appears in the app's navigation under a dedicated "System Tables" section, visually distinguished from standard builder-created tables.

