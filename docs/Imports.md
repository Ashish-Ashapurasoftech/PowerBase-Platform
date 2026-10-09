# Imports (table-to-table and file)

A saved, re-runnable **import definition** that fills a destination table from another table or from an uploaded CSV / Excel file.
It is a self-contained module: nothing in the existing record, pipeline or field code was changed to build it.

## Where the code is

| Part | Location |
|---|---|
| Engine, handlers, file readers | `src/PowerBase.Application/Imports/` and `.../Imports/Files/` (several tables in one run: `ImportMultiTargetRunner`) |
| Database access | `src/PowerBase.Infrastructure/Imports/` |
| Background workers | `src/PowerBase.API/Workers/ImportExecutionWorker.cs`, `ImportSchedulerWorker.cs` |
| Endpoints | `ImportDefinitionsController`, `ImportFilesController`, `ImportRunApiController` |
| Migrations | tenant `064`–`071`, control `012` (`database/migrations`) |
| Tests | `tests/PowerBase.Imports.Tests` (`dotnet test`) |
| Frontend | `pages/apps/table-settings/imports/*` (routes under `/app/:appId/imports`), `core/api/*/imports*`, `core/services/import-run-watcher.service.ts` |

## What a user can do

* **Copy** (add every record as new) or **Merge** (update the record whose unique key matches, add the rest). A merge key, and a "Remove duplicates" column, may be encrypted: the destination's key values are decrypted into an in-memory index once per pass (about 100 MB per million records).
* Read from **another table** (any table the user can read, same tenant) or from a **file** (`.csv`, `.tsv`, `.txt`, `.xlsx`).
* Choose which source records to take with **conditions** (AND/OR, nested; relative dates such as "today" are worked out at run time).
* Map **source → destination** on the mapping screen: each row has a *Source*, a *Type* (Field / Fixed value / Formula) and a *Destination* field (or *Do not import*). A table source is chosen from a searchable list of its fields (only the rows the user adds); a file lists all its columns. See *Virtual columns* below.
* Per-column rules: **Remove duplicates**, **Require field**, **Ignore blanks**.
* **Order of the checks for each row** (the first that rejects a row decides its one reason): the import's conditions and a table's own filter (at read time); converting each value; **Require field** and **Ignore blanks**; matching the record (merge); required fields and the table's data rule; a unique value that already belongs to another record in the table; then the rows **compete with each other, in source order**: a unique field or the merge key may be carried by one row of the import (the later one is an error), and **Remove duplicates** leaves out later rows that repeat a value (skipped; for a Copy, so is a value already in the table); finally the write. So **Remove duplicates is the last check**: only rows that survived every other check can use a value up, and the first one that survives them all is kept. A row that is left out, for any reason, takes none of its values, so it never costs a later row its place (a row with a blank required field never causes the next row with the same email to be dropped as a "duplicate"). One limit: a row the database itself rejects while writing is found after this, so its value was already taken.
* Choose what happens on a unique-rule break: import the valid rows (default), skip whole duplicate groups, or import nothing if any row has a problem.
* Run in the background, stop a run, get an in-app notice and an email (counts and a link, never rows), download a CSV of every row not imported.
* Table imports can run on a **schedule** (hourly/daily/weekly/monthly/cron, with a time zone).
* Fill **several tables from one source in the same run** (see below).
* Start a run from a script with the **run API**.
* Imports whose tables or fields changed are flagged **needs attention** and will not run until fixed.

### History and files

**Details file, every run, one per table.** Whatever happens, a run saves a CSV for **each table it filled** (`ImportDetailsWriter`). Before, a file was written only when rows were skipped or failed. Now it lists **every source row the table was given**, in source order: `Source row, Result, Record ID#, Reason, Column, Details, Existing record`, then one column per field the table was mapped (what the row carried).

| Result | Record ID# |
|---|---|
| Inserted | the record the row became |
| Updated | the record it updated (also for imports that merge on the Record ID#) |
| Unchanged | the record it matched; it already held every value, so nothing was written (merge only) |
| Skipped | the record it matched, if it got that far, otherwise blank |
| Error | the record it matched, if it got that far, otherwise blank |

A run that gave a table no rows still has a file with its headings. Files are streamed to a temporary file while the run goes (never all in memory), uploaded once at the end, and the path is kept per table (`meta.ImportRunTarget.DetailsFilePath`; for a run into one table, `ImportRun.FeedbackFileUrl`). Needed to do this: the bulk insert reads back the Record ID# each row was given (one query per 1,000 rows, by the row's own public id); a merge reads the matched records' values for the mapped fields (once per chunk) to find *unchanged* ones, comparing only when it is certain (an encrypted field or a value of an unlike kind counts as changed), and those rows are not written. *Unchanged* has its own counter (`Unchanged` on the run and on each table); it is not a failure.

**Retention.** The details files **and** the file uploaded for the run are kept for `Imports:RetentionDays` (appsettings, default **30**, valid 1–365, otherwise 30; `ImportOptions`) after the run ends. The scheduler's per-tenant pass (`ImportRetentionCleanup`) then deletes them and marks the run (`FilesExpiredOn`); the counts and the history row stay. A file that cannot be deleted now is tried again next pass. Downloads past the retention answer "not found", and the screens say *Expired*.

**History** (`GET apps/{appId}/import-runs`): every run of the app in a period (default the last 30 days, at most 400 days back), newest first, **a row per table** of each run, server-paged (25, at most 100). Filters: from, to, import, table, result (status), how it started. Each row: import, table (and how many tables the run filled), when it started, how long it took, status, **Created / Updated / Unchanged / Skipped / Errors** for that table, a memo (the run's note), who started it, the uploaded file's name, and whether the table's details file and the uploaded file can be downloaded. Any member of the app sees status and counts; the files carry source values, so, as before, only the person who started the run and admins can download them. The screen is the **History** tab next to **Templates** on the app's Imports screen.

Downloads go through the API, never a storage URL: `GET import-runs/{runId}/feedback[?tableId=]` (a table's details file; without `tableId`, the first table's) and `GET import-runs/{runId}/source-file` (the uploaded file).

### Virtual columns

A fixed-value or formula row is a **virtual column**: it has a name and exists only inside the import (saved with it, `OptionsJson.virtualColumns`). Nothing is created in any table, so there is no "field already exists" problem. It can go to a destination field or to *Do not import*; either way **other formulas can use it by name** (`[First Name] & " " & [Last Name]` → `Name`, then `[Name] & [Index]` → `Code`).

* Calculated once per row as the source is read (`ImportVirtualColumnReader`), before any table's mapping sees the row, so every table of a multi-table import can use it. Order follows the dependencies; a column that uses itself, directly or through others, is refused with the names. An unknown column is refused with the column's name.
* Names must be unique (case-insensitive) against every source column and every other virtual column, at most 100 characters, no square brackets. At most 50 columns per import. Ids are `>= 1,000,000` (no table field or file column reaches that).
* Result types: text, number, checkbox, date, date and time. A mapping from a virtual column is converted to the destination's type per row, like a file's text; a row that does not convert is reported for that row, not for the run. A row a formula cannot calculate gets a blank.
* A virtual column is an ordinary source in the mapping (`source: "dynamic"`, `sourceFid` = its id). Imports saved before this existed keep their fixed-value / formula mappings and run exactly as before; the screen shows them as virtual columns named after the destination.
* **Live formula check** (`POST apps/{appId}/import-formula/validate`, `ValidateImportFormulaHandler`): a formula typed on the mapping screen is compiled by the server against the source (a table's fields as the caller's role sees them, or a file's columns, all text) and every other virtual column, exactly as the import compiles it (no cross-table names). It reports every diagnostic, a formula that is too long or too deep, and, for a virtual column, a result it cannot hold. A virtual column that is broken is not offered to other formulas; a column cannot use itself or one that uses it. Members of the app only; a table source needs read access and must be in the app. The server still checks every formula when the import is saved and run.
* Needs attention: a virtual column that no longer calculates (for example a column it uses was deleted) flags the import.

### The Record ID#

The Record ID# is assigned by the system. It is **not offered** as a destination or a merge key, and the server refuses to save a *new* use of it (`ImportRecordIdRule`). An import that already wrote to it or merged on it keeps running and can be saved again as it is; the screen offers it only for such an import. System fields other than the Record ID# and formula fields were never writable.

### Row outcomes

Every source row ends as exactly one of: **inserted**, **updated**, **skipped** (a rule left it out on purpose) or **errored** (a data or
constraint problem). `read = inserted + updated + skipped + errored`. A bad row never stops the others.

> **Require field** only rejects blanks. It does not check duplicates. Tick **Remove duplicates** to skip repeats; otherwise a repeated
> value in a unique field is an error row, and with the "import nothing if any row has a problem" policy one such row stops the run.

### File imports

* A sample file is uploaded while setting up the import so the columns can be seen and mapped; the real file is uploaded **each time the import is run**.
* Settings: sheet, row holding the column names (0 = none), first data row, separator, and whether a column is found by its **heading** (it may move) or its **position**.
* Every file value is text. Each is converted to the destination field's type as it is written; a value that does not convert is reported for its row (with its row number in the file).
  Dates should be `2026-03-09` (Excel date cells are converted automatically); other layouts are read month-first. Checkbox text: `true/false/yes/no/y/n/1/0`.
* Conditions on a file's columns compare as text. Formulas refer to columns by heading, e.g. `Upper([Name])`.
* A file import cannot be scheduled. The uploaded file is **kept for the retention** (see *History and files*) after the run; an upload nobody imported is removed after 24 hours.
* **Excel with several sheets:** tick "Read every sheet" to import all sheets into the same table, in workbook order. Each sheet must have the columns the import uses (found by heading or position); a sheet without a row of column names is passed over; a sheet missing a used column stops the run before anything is written, naming the sheet. Problem rows are reported with their sheet and row.
* Not supported: a different sheet for each table (every sheet feeds every table), `.xls` (save as `.xlsx`), Reference/User/Address/File/range destination fields.

### Several destination tables in one run

One source (a table, or a file / every sheet of a workbook) can fill up to **5 tables** in a single run, e.g. one row of a student file filling
both a Students table and a Contacts table.

* **Setting it up:** in the import builder, the section *Also copy into other tables* adds a card per extra table. Each card has its own import type
  (Copy or Merge, with its own merge key), field mappings (field / fixed value / formula) and column rules. The import's first table (chosen in *Add records to*) is configured exactly as before. The source, its conditions, the duplicate policy, the people told and the schedule are
  shared by every table. The extra tables must be in the same app, and each table can be filled only once.
* **Filters:** the import's own *conditions* are read once, with the source, and apply to **every** table (proved by `ImportGlobalFilterTests` for table and file sources and for three tables). On top of that each table can have a **filter of its own** (`ImportTargetConfig.Conditions` for an added table, `ImportDefinitionConfig.TableConditions` for the first): of the rows read, only those that match go into that table. They are checked in memory on each row after the shared read (same operators as the conditions, same relative-date and current-user resolution, no "ask the user"), so one read serves tables with different filters; the fields a filter uses are added to what is read, even when the table does not map them. A row a table's filter leaves out is not imported, skipped or reported **for that table** (no line in its details), and its problems in other tables are unaffected. Counts reconcile per table: *rows it got* = imported + skipped + errored, shown on the run page as "N of M". The duplicate policies (skip duplicate groups, abort if any issue) look only at the rows each table is given. A table filter on an import into one table is refused (use the conditions). Deleting a field a filter uses flags the import as needing attention. Changing the source clears every table filter in the editor.
* **How it runs:** the source is read **once**. Every chunk is handed to each table's own writer, so each table has its own checks, rules and
  transaction. Tables are processed in the order saved (own table first).
* **Failures are per table:** a row that is fine for one table and bad for another is imported into the first and reported for the second. Counts are
  kept per table (`meta.ImportRunTarget`); the run's own counters are the totals of its tables and "rows read" counts each source row once.
  `read = inserted + updated + skipped + errored` holds for every table, not for the totals.
* **Duplicate policy across tables:** *import valid rows* applies to each table separately; *skip whole duplicate groups* is decided per table;
  *import nothing if any row has a problem* is global: a problem in any table stops the run before anything is written in any table.
* **Reporting:** the run page shows a *By table* breakdown. The details CSV starts with a `Table` column, one `Values` column (`Field=value; ...`)
  and issue messages are prefixed with the table name. The completion email lists each table's counts. An import into one table is reported exactly as before.
* **Needs attention:** the check covers every table's tables, merge key and mapped fields; the reason names the table.
* **The list:** search by import name or by a table's name; tick imports (or *Select all* for those showing) and *Delete selected* removes them in one call (past runs stay in the audit log).
* **Where it lives:** in the app's **Imports** menu item (`/app/{appId}/imports`), one list for every table. Each row shows where it reads from and the table it fills first ("fills N tables" for several). The first table is chosen when the import is created and cannot be changed afterwards (the import is saved under it). Old links to a table's `settings/imports…` pages redirect to the app-level screens. The menu item and screens need the right to create records.
* **Safe by construction:** an import into one table never uses the multi-table code; it runs through the original processor unchanged. Existing
  imports have no extra tables and behave as before. A mid-run database error ends the run as failed; chunks already written to earlier tables stay
  (there is no resume, as for one table), and each table's counts reflect what it actually received.
* Not included (follow-ups): linking tables to each other in the same run (Reference fields), an "all or nothing per source row" option, and listing the import under every table it fills.

## Limits

| Limit | Value |
|---|---|
| Upload size | 300 MB |
| Rows per file | 5,000,000 |
| Columns per file | 1,000 |
| Cell length | 100,000 characters |
| Waiting uploads per user | 10 |
| Mappings per import | 250 (per table) |
| Tables filled by one import | 5 (its own plus 4 more) |
| Formula length / nesting | 4,000 characters / 50 levels |
| Concurrent runs | 4 overall, 2 per tenant |
| Schedules | no more often than every 15 minutes |
| Rows kept on the run page | 5,000 (the details file has all of them) |

## Run API

Same bearer tokens as the rest of the API (including user tokens limited to chosen apps); the same checks as the Run button apply, as the token's user.

```
POST /apps/{appId}/imports/{importId}/run          -> 202 { data: { runId, replayed } }
     headers: X-PowerBase-Client-Token: <any unique string, optional>
     body (optional): { "notifyEmails": [...], "fileId": "<id of an uploaded file>" }   (fileId only for file imports)
GET  /apps/{appId}/imports/runs/{runId}            -> status, progress, counts, error text (no rows)
GET  /apps/{appId}/imports/runs/{runId}/feedback   -> CSV of rows not imported (starter or admin only)

POST /apps/{appId}/import-definitions/delete      -> { ids: [...] }, bulk delete (all or nothing; the right to add records to each import's first table; max 200)
GET  /apps/{appId}/import-definitions             -> every import of the app (any member; opening, editing and running still check each table)
POST /tables/{tableId}/import-files                -> multipart "file"; returns { fileId, fileName, format, sizeBytes, sheets }
```

Repeating a start with the same client token returns the first run (`200`, `replayed: true`) instead of starting another.

## Operations

* `Imports:RetentionDays` (appsettings, optional, default 30) sets how long details files and uploaded source files are kept. Migration `071` adds the unchanged counters, the per-table details path, the files-expired marker and the indexes the history and clean-up read.
* Apply migrations: `DOTNET_ENVIRONMENT=Development dotnet run -- migrate tenants` from `PowerBase.Migrator` (and the control migration `012`).
* The two hosted services start with the API. The scheduler polls every 60 s and takes a per-tenant database lock, so several API instances are safe.
* Uploaded files and details files go through `IFileStorageService` (local disk or Azure Blob).
* A run interrupted by a restart is **failed**, not resumed (resuming could write a chunk twice); the user starts a new run.
* Completion emails link to `Frontend:BaseUrl` (or a trusted `Origin`); the address is never taken from an untrusted request.
* Request size: uploads use a per-endpoint limit of the file limit plus 1 MB. If the API is ever hosted behind IIS or a gateway, raise its own body limit to match.

## Security review (Phase 11)

| Risk | Control |
|---|---|
| Zip bomb inside an `.xlsx` | The zip's declared sizes are checked before unpacking: refused above 4 GB total or when a large part claims more than 100× compression. |
| Memory exhaustion by a large file | Files are streamed (CSV and Excel); row, column and cell-length caps; nesting cap for formulas (the formula parser recurses and would overflow the stack). |
| Disguised or wrong file types | The type is decided from the file's bytes as well as its name; old `.xls` and renamed zips are refused. |
| Path tricks in file names | Stored under a generated name; the shown name has folders and control characters removed. |
| Spreadsheet formula injection in the details file | Cells starting `= + - @` are prefixed with an apostrophe. |
| Reaching other tables from a formula | Cross-table formula functions find nothing in an import (the row context answers only for its own columns). |
| Seeing or stopping someone else's run | Rows, details file and stopping are limited to the person who started the run and admins; other members see status and counts. |
| Someone else's upload | An upload belongs to its uploader; to everyone else it does not exist. |
| Cross-app access | Run API and run status return "not found" for an import or run of a different app. |
| Repeated/duplicated starts | Overlap guard per import; client-token idempotency; scheduler claims each occurrence with a compare-and-swap. |
| Stale or removed access | A scheduled run re-checks the saved-by user's access when it runs; saving an import re-binds it to the saver. |
| Stored secrets | Client tokens are stored as SHA-256 digests. Storage paths are never sent to clients. |

**Known exposure to decide on:** the shared Azure Blob container is created with public *blob* read access. Uploaded import files and
details files are therefore readable by anyone who has the exact URL (the names are long random strings; both kinds of file now stay for the
retention, 30 days by default, so the exposure lasts longer than it did). Making the container private, or using a separate private container for imports, closes this.

## Performance (measured by `ImportScaleTests`, `Category=Load`)

| Scenario | Result |
|---|---|
| Read a 1,000,000-row CSV (38 MB) | ~1 s, heap growth ≈ 4 MB |
| Read a 100,000-row workbook | ~2 s, heap growth ≈ 11 MB |
| Engine, 200,000-row file, 30 % duplicate names | all rows accounted for, ~2 s (in-memory store) |
| Engine, 100,000 source rows into 3 tables | every table reconciles with the rows read, ~2 s (in-memory store) |
| SQL: insert 100,000 rows in 2,000-row chunks / masked-update 50,000 | ~2.4 s / ~0.9 s |

These guard against accidental quadratic behaviour or whole-file buffering; they are not a benchmark of a production database.
Run only these with `dotnet test --filter "Category=Load"`.

## Known gaps

* Audit is **run-level** (started / completed / cancelled with counts), not one audit row per imported record.
* Merge into a table where the user's role limits which records they can edit is refused (fails closed).
* No resume after a crash (by design, see above).
* A multi-table import cannot link its tables (no Reference-field import yet) and has no "all or nothing per source row" option.
* The optional hooks into field deletion (instant flagging, usage warning in the delete dialog) were not added because they would change existing code; changes are detected the next time an import is listed, opened, run or scheduled.
