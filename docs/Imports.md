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
| Migrations | tenant `064`–`070`, control `012` (`database/migrations`) |
| Tests | `tests/PowerBase.Imports.Tests` (`dotnet test`) |
| Frontend | `pages/apps/table-settings/imports/*`, `core/api/*/imports*`, `core/services/import-run-watcher.service.ts` |

## What a user can do

* **Copy** (add every record as new) or **Merge** (update the record whose unique key matches, add the rest). A merge key, and a "Remove duplicates" column, may be encrypted: the destination's key values are decrypted into an in-memory index once per pass (about 100 MB per million records).
* Read from **another table** (any table the user can read, same tenant) or from a **file** (`.csv`, `.tsv`, `.txt`, `.xlsx`).
* Choose which source records to take with **conditions** (AND/OR, nested; relative dates such as "today" are worked out at run time).
* Map each destination field from a **source field/column**, a **fixed value**, or a **formula** (the platform formula language).
* Per-column rules: **Remove duplicates**, **Require field**, **Ignore blanks**.
* Choose what happens on a unique-rule break: import the valid rows (default), skip whole duplicate groups, or import nothing if any row has a problem.
* Run in the background, stop a run, get an in-app notice and an email (counts and a link, never rows), download a CSV of every row not imported.
* Table imports can run on a **schedule** (hourly/daily/weekly/monthly/cron, with a time zone).
* Fill **several tables from one source in the same run** (see below).
* Start a run from a script with the **run API**.
* Imports whose tables or fields changed are flagged **needs attention** and will not run until fixed.

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
* A file import cannot be scheduled. The uploaded file is deleted when the run ends; uploads nobody imported are removed after 24 hours.
* **Excel with several sheets:** tick "Read every sheet" to import all sheets into the same table, in workbook order. Each sheet must have the columns the import uses (found by heading or position); a sheet without a row of column names is passed over; a sheet missing a used column stops the run before anything is written, naming the sheet. Problem rows are reported with their sheet and row.
* Not supported: a different sheet for each table (every sheet feeds every table), `.xls` (save as `.xlsx`), Reference/User/Address/File/range destination fields.

### Several destination tables in one run

One source (a table, or a file / every sheet of a workbook) can fill up to **5 tables** in a single run, e.g. one row of a student file filling
both a Students table and a Contacts table.

* **Setting it up:** in the import builder, the section *Also copy into other tables* adds a card per extra table. Each card has its own import type
  (Copy or Merge, with its own merge key), field mappings (field / fixed value / formula) and column rules. The import's own table (the one whose
  Imports screen you are in) is configured exactly as before. The source, its conditions, the duplicate policy, the people told and the schedule are
  shared by every table. The extra tables must be in the same app, and each table can be filled only once.
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
* **Where it lives:** under the table it was created from (listed in that table's Imports screen with "fills N tables"). It is not listed under the other tables.
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

POST /tables/{tableId}/import-files                -> multipart "file"; returns { fileId, fileName, format, sizeBytes, sheets }
```

Repeating a start with the same client token returns the first run (`200`, `replayed: true`) instead of starting another.

## Operations

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
details files are therefore readable by anyone who has the exact URL (the names are long random strings and import files are deleted when
the run ends). Making the container private, or using a separate private container for imports, closes this.

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
