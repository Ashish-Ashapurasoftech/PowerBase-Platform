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

| M# | Duration | Key Scope | Payment |
|----|----------|-----------|---------|
| M0 | — | Advance | 5% |
| M1 | 3 months | Figma, Tenant, App/User/Table/Field Mgmt, Basic Formula Fields, Table+Summary Reports, Basic Form Rules, Audit Logs | 30% |
| M2 | 1 month | Connected Tables, Summary+Lookup Fields, Copy App, QBL Import, Formula/Date as Reference, Advanced Form Rules | 10% |
| M3 | 1 month | Advanced Roles+Groups, Split Admin Capabilities, Column Type Conversion, Chart Reports, Conditional Formatting, Report Link in Forms, Azure Global Search | 10% |
| M4 | 1 month | Archive/Restore, Automations/Pipelines, Master App Deployment, External Tenant DB | 20% |
| M5 | 1 month | Data Migration, User Tokens, Public APIs (DoQuery, ImportFromCSV, CRUD, Reports, Fields) | 20% |
| M6 | 1 month | Go Live + 3-month Hypercare | 5% |

**[NEW] Deferred (not in current milestones — need separate planning + budget):**
- Super Admin / Realm Admin Panel (users, storage, billing, usage analytics) — suggest M3 planning session
- Full Backup System (daily snapshots, schema revert, data revert, point-in-time restore) — separate milestone required
- Cross-tenant Master App — future milestone post-M6

---

## APPS

### App Management (M1)
- Apps page: list, count, search, export CSV, delete (single or bulk)
- App Settings: home, branding, navigation, users/groups, roles/permissions, app variables, audit & logs

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
| Text | Free-form | Unicode, configurable max length |
| Number | Integer/decimal | No formatting stored |
| Decimal | High-precision | For financial/calculated values |
| Boolean | True/False | Stored as 0/1 |
| Date | Date only | ISO format, no time component |
| DateTime | Date + time | UTC storage |
| Phone Number | Smart text | See Phone Handling |

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

---

## SHARED FILTER ENGINE (Core Infrastructure) **[UPDATED]**
Single filter engine used by: reports, conditional summaries, reference dropdown filtering
Stored as JSON: groups (AND/OR) + conditions (field/operator/valueSource) + valueSource (constant or "from another field")
Runtime: Filter JSON → query compiler → SQL
Supports: nesting, dynamic values from current record/form, cross-table comparisons

**[NEW] Type-awareness requirement:** All filter builder surfaces must dynamically filter operator lists and comparable field lists based on the selected field's data type. Incompatible combinations are never shown to the user.

**[NEW] Runtime Variables (planned, post-M2):** Filter conditions will support system-context values (current user, current date, current user's role/group) as value sources. Not in M2 scope. Milestone TBD.
