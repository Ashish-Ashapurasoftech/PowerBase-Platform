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
* Choose what happens on a unique-rule break. The three options are named for what they do and explained in the screen (`app-import-policy-picker`, text in `core/utils/import-policy-options.ts`, shared by the template screen and the quick import dialog): **Import valid rows - keep the first of each duplicate** (`importValid`, default), **Import valid rows - leave out every duplicated row** (`excludeDuplicateGroups`), and **All or nothing - import nothing if any row has a problem** (`abortIfAnyIssue`). The screen shows what the chosen option does point by point, when to choose it, and the three side by side with one example file (Ann, Bob, Bob again, Cid with a bad number).
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

### Reference fields (child → parent tables)

A **Reference** field (the child side of a table relationship, e.g. Student → Course) can be a destination; Lookup and Summary fields are computed and never are, and a hidden helper field
(`AppField.IsHidden`, such as a reference's hidden "(original)" text copy) is never offered. A relationship is kept on the parent's **Record ID#**; people know a parent record by its key.

* **What is shown:** the Reference field itself, under its own name (e.g. "Department Name"), is the destination. Its display lookup (computed) and the hidden "(original)" copy are never destinations.
* A value from the file or source table is the parent's **key** (the relationship's display key, else the parent table's key field, else - when no key was ever set - the field its reference picker labels records with: the table's first picker field, its display field, or its first ordinary stored field; e.g. a course's name) or its **Record ID#**, as every Reference write accepts.
  **When the relationship (or the parent table) has a key field, a value is looked up by that key only** (case and surrounding spaces do not matter) and is never taken for a Record ID#: a file that says `1` means the record whose key is `1`, not the record whose Record ID# is 1 (feedback, 2026-10-15). Only a parent that has **no key field** accepts a live Record ID# (as the form and API do) before the picker-label fallback. Whatever is looked up is asked once per chunk.
  A parent with no field to look a value up in is matched on the Record ID# only. A key field that is **encrypted** (every field is, in an app with app-level encryption) cannot be searched in the database, so the parent's records are read decrypted once per run and searched in memory (up to 500,000 records; beyond that the run says to import Record ID#s instead). Two parent records with the same value: the first one.
* A value that finds **no parent record does not reject the row**: the reference is left blank and the text is kept in the field's hidden "(original)" copy, which the grid and reports show in its place (the same as when a field was converted into a reference). A Reference created straight into a relationship has no such copy yet: the first import run that meets a value with no parent record **creates it** (the same hidden Text field a conversion makes, named "<label> (original)", encrypted when the app is, recorded in the Reference's settings), once, in the real run and never in the check before it, and only for a person who may create fields in the table; if it cannot be made, the row is still imported blank and the details file says the text was not kept. A required Reference that stays blank is rejected like any missing required value.
  A value that matches is stored as the Record ID#, and any stale text in the "(original)" copy is cleared.
* The person must be able to read the parent table. A Reference that only accepts some parent records (a dependent-dropdown rule) cannot be imported yet, and the import is refused with that message. A Reference is never what records are matched on. A fixed value can name the parent every row gets.

### User fields

A **User** field can be a destination, with a **fixed value** only: the mapping screen shows the app's users as a dropdown, and every record gets the chosen user (the same for every row; a source column or a formula cannot name a user). The fixed value is saved as the user's public id; when the import is planned (`ImportPlanBuilder.ResolveFixedUserAsync`) it is looked up once and each record gets the plain user id the column stores, as every record write does. A user that no longer exists, a value that is not a user, or a unique User field is refused when the import is saved or run. Multi-user fields are not supported yet.

### The Record ID#

The Record ID# is assigned by the system, so it is **only ever matched on, never written**. A **Merge** import can pick it under *Match existing records on* (always offered); once it is the key it is also offered as a destination for one row, mapped from a source column or field that holds the Record IDs (a file column, a source-table field, or a virtual column; the data must be whole numbers). It is not offered in a Copy, and not as a destination while another field is the key (a row that went to it is cleared when the key changes). The plan builder refuses it anywhere else (`ImportPlanBuilder`: "cannot be imported into"); `ImportRecordIdRule` was removed.
* A row whose Record ID# is not in the destination table is an **error** (`RecordIdNotFound`, "Record ID not found") and is never inserted; a blank or non-numeric one is a "match key missing" error; a Record ID# repeated in the file is reported by the existing repeated-key check. Every other merge key still inserts rows that match nothing.
* The store looks a Record ID# up only for whole numbers from 1 to the largest id a record can have; anything else (zero, negative, fractional, text, a number too big for an id) simply matches nothing and is reported as "Record ID not found" or a type error, and never throws (`ImportDataStore.FindRecordIdsAsync`, covered by a real-SQL test).
* **Lenient mapping (decision 2026-10-29):** a file's first 200 rows cannot prove what a million-row file holds, so the Record ID# is offered for **every** file column while it is the key (the mapping grid makes an exception for it in the destination list; the column-fit endpoint also adds it to `FitsFields` when at least one sampled value is a whole number, or the column is empty in the sample), and saving is never blocked for a file source. Rows that are not valid Record IDs are reported by the run, one by one, with the reason. A **table** source is still type-checked on save, because its field types are known. A heading never matches it by itself (a file column called "Record ID" is not auto-listed or auto-mapped). A table that reads several sheets lists only heading-matched columns, so Record ID# cannot be chosen there yet.
System fields other than the Record ID# and formula fields are never writable.

### Variables

A template can have **variables** (`ImportVariables`, saved in the template's options; no migration): the same idea as a virtual column, but **global**. A variable is either a **fixed text** or a **formula** worked out once per run (for example `Today() - Days(30)`), and it has the **type its formula returns** (text, number, checkbox, date, date and time). It is available everywhere in the template, for every table it fills.
* **Names** always start with `var_` (letters, digits and underscores after it, at most 50 characters), are unique ignoring case, may not be only digits after `var_` (those spell an id reference), and may not be the name of a source column or a virtual column. At most 50 variables; a fixed text up to 1,000 characters, a formula up to 4,000. Checked when the template is saved, when it runs, and by the "needs attention" check.
* **A variable's formula** may use functions and other variables (any order; a cycle is refused) but **no source column**: it belongs to the whole import, not to a row. It is compiled and calculated once, with the run's clock and the person who runs it, so every table and sheet sees the same "today". A formula that fails, or returns a type a variable cannot hold (a duration, a list ...), fails the save or run naming the variable.
* **In a formula** write `[var_name]`: it is a typed column (a number variable is a number, a date variable a date; no conversion needed). Variables are added, before the import's own virtual columns, to the virtual columns of every plan (`ImportVariables.AsColumns`, ids from `FidBase` = 2,000,000 by position), so the formula engine itself is untouched and the existing virtual-column code does the calculating. A mapping can also take a variable as its source (the backend allows it; the mapping screen does not list them yet).
* **In a filter value or a fixed value** write `{{var_name}}`. The value is worked out once and its text (numbers in the invariant form, dates `yyyy-MM-dd`, checkboxes `true`/`false`) replaces the token at the top of `ImportPlanBuilder.BuildAsync` (`ImportVariables.Apply`), so everything after it sees plain values. Used in the whole-import filter, each table's own filter, each sheet's filter, fixed mapping values and fixed virtual columns. A name nobody defined is an error ("Unknown variable {{var_x}}"), never kept as text; other `{{text}}` is left as typed; a value is never searched for another token.
* **The live formula check** (`ImportFormulaCheck.Variables`, `OwnVariable`) offers and types the variables; a variable's own formula is checked with no source columns and without itself.
* **In a filter, as a choice:** the shared filter value control (`pb-filter-value-control`, through `pb-filter-group`) has an optional `variables` input (default none, so reports, roles and every other screen are unchanged). When the import screen passes its variable names - in the whole-import filter, each table's own filter and each sheet's filter - the value-type dropdown ("the value", "the value in the field") gets one more choice, **a variable**, and the value box becomes a list of the variables, for a field of any type (text, number, date, checkbox, user ...). It is stored as the literal `{{var_name}}`; a value that names a variable shows as "a variable". Not offered for the "in the past/next N days" and "during" conditions (their value is a count and a unit).
* **In a mapping row:** the source dropdown lists each variable as `var_name (variable)`; a variable is also in the formula editor's field list and autocomplete. A variable keeps the id the screen gave it (`ImportVariable.Fid`, from `FidBase`; one saved without an id is numbered by position from `FidFallback`), so deleting another variable never makes a mapping point at a different one.
* A template without variables is saved, read and run exactly as before. A changed value or formula changes the saved configuration, so an "only changed" import reads everything again.

### The filter operator "does not have a duplicate"

Every import filter (the whole import's, each table's, each sheet's; table and file sources) has one more operator, **does not have a duplicate** (`noDuplicate`), for a field that holds one value (not multi-select, multi-user, file, address, range or rich text; `ImportPlanBuilder.CheckNoDuplicate`). A row passes when its value in that field does not repeat in the source; the checkbox **Import the first row** (the condition's value `"true"`) also lets the first row of each repeated value pass, so only the later repeats are left out. Reports, roles and every other screen do not offer it: the shared filter group has an optional `extraOperators` input (default none) that only the import screens set.
* **What is counted:** the rows that pass the filter's other conditions, in the order they are read (Record ID# order for a table, row order for a file). So `Status = Active AND Email does not have a duplicate` counts only active rows. A blank value is never a duplicate. Values are compared trimmed and ignoring case, numbers and dates in one form.
* **How it runs:** a condition like this is never sent to the database (`ImportPlanBuilder` adds its field to the in-memory fields). Before the first real pass the run reads the source once (`ImportRunProcessor.CountFilterDuplicatesAsync`, `ImportMultiTargetRunner.CountFilterDuplicatesAsync`), with each such condition passing every row while the rows that pass the rest are counted in an `ImportDuplicateIndex` (64-bit hashes, about 24 bytes per distinct value; a field with more than 5,000,000 distinct values is refused with a message). A table's or sheet's own filter counts the rows that reach it. A row the filter leaves out is not imported, skipped or reported, like any filtered-out row. An import without such a condition reads the source once, as before.
* **Limits:** in an "any of" (OR) group the count includes the rows that pass the group as a whole; the run's progress shows no movement during the counting read; "read every sheet" of a workbook numbers each sheet's rows from 1, so two sheets' first rows can both count as "first".

### A table's own filter with one table (2026-10-30)

Every table, the first included, has a "Only copy these rows into this table" filter, also when the import fills one table. With one table (`ImportSingleTableFilter.Fold`, applied in `BuildAllAsync` and before the single-table run plans) the table's filter is combined with the whole-import filter (both must hold) and the saved configuration keeps the two apart. With several tables, or a table that reads sheets separately, each filter stays where it is.

### Import without a template (one-time import)

The Imports page has **Import without a template** (`/imports/new?once=1`): the template builder with no name, no schedule and no "only changed"; its button is **Run import**. It sends the setup and the uploaded file to `POST tables/{id}/quick-imports` (`StartQuickImportHandler`), which saves a definition through the normal handler, starts it, and removes the definition, so the run shows in the history and on the run page and no template is kept. Everything a template has works (filters, variables, rules, several tables, sheets read separately); only a schedule and "only changed since the last run" are refused. A table's ⋮ menu has the smaller dialog version for a single table.

### Row outcomes

Every source row ends as exactly one of: **inserted**, **updated**, **skipped** (a rule left it out on purpose) or **errored** (a data or
constraint problem). `read = inserted + updated + skipped + errored`. A bad row never stops the others.

> **Require field** only rejects blanks. It does not check duplicates. Tick **Remove duplicates** to skip repeats; otherwise a repeated
> value in a unique field is an error row, and with the "import nothing if any row has a problem" policy one such row stops the run.

### File imports

**At run time** (feedback, 2026-10-14): the file picked for a run may be **Excel or CSV whichever the import was set up with**; it is read as what it is (first sheet of a workbook, separator detected for a CSV). An import that maps sheets separately still needs a workbook. When the file's **headings differ** from the template's, the columns are taken **by their place** (the same order as the template) as long as the file has at least as many columns; the run fails, naming the missing columns, only when it has fewer. Several runs of a **file import** may go together (each has its own file); a **table import** still runs one at a time, and so does a scheduled run. An import reading a table is not started twice at once: "This import is already running".

**Dates in a file** (feedback, 2026-10-14): a Date or Date & Time field reads whatever way the file writes the date (`2026-04-05`, `05/04/2026`, `5-Apr-2026`, `April 5, 2026`, `05-04-26`, with an optional time such as `14:30` or `2:30 PM`; `ImportDateParser`). The value is stored as a real date and shown in the app's date format. Where day and month could swap (`05-04-2026`) the **app's date format** (App settings > Formatting) decides which comes first; a date that cannot be that order (`25-04-2026` under month-first) is read the other way round. Text that is not a date, or an impossible one (`31-02-2026`), is reported on its row. A bare number such as `45000` is not read as an Excel serial date.

**Date and time formats are chosen on the mapping row (feedback, 2026-10-16).** A date in a file is only text: `02/10/2026` is 2 October or 10 February, and `12/12/2026` a date only if you know the order. So a column of text that goes to a **Date**, **Date & Time** or **Time** field carries the format the person chose, shown as a dropdown on its row: the date formats are the app settings' own (`MM-DD-YYYY`, `MM-DD-YY`, `DD-MM-YYYY`, `DD-MM-YY`, `YYYY-MM-DD`; slash, dash or dot between the parts is all the same), the time formats are 12-hour (`hh:mm AM/PM`, with seconds or without) or 24-hour (`HH:mm`, with seconds or without). A new row starts with the app's date format and 12-hour time, visibly, for the person to change. The cell is read **exactly** in that format (`ImportDateFormats`): a date that is not valid in it (25/12/2026 under month-first) is reported on its row, never read the other way round; a 12-hour time needs its AM/PM and a 24-hour one refuses it. A month written as a word, and a real Excel date or time cell (which reaches the import as `2026-07-30` or `16:00:00`), are read whatever the format. A Time field stores `HH:mm` (or `HH:mm:ss`).
* **A real Excel date or time cell is read as the sheet shows it** (2026-10-19). A workbook cell that holds a date is a number plus a number format (`dd/mm/yyyy`, `mm-dd-yyyy`, `hh:mm AM/PM`...); the person sees the text, so the import draws that text (`ImportExcelDisplay`, in `ImportXlsxReader`) and reads it in the format chosen for the column, exactly as for a date typed as text. A sheet showing `12-11-1985` read as DD-MM-YYYY is 12 November; one showing `12/21/2005` read as DD-MM-YYYY is refused (no month 21). A number format that cannot be drawn (elapsed time, unknown codes) falls back to the machine form (`1985-12-11`), which is read whatever the format. This also means a date cell going to a **Text** field now arrives as its display text (`12-11-1985`) rather than `1985-12-11`.
* **Time rules.** AM or PM written on a cell is never ambiguous and is honoured in either kind of column (`9:05 PM` and `21:05 PM` are both 21:05); only a contradiction (`14:00 AM`) is refused. In a 12-hour column a time with no AM/PM is refused when it could be morning or evening (`9:00`) but accepted when it can only be 24-hour (`14:40`, `0:15`) or has seconds (the form an Excel time cell is shown in).
* **A Date & Time shows as written, with no time zone to choose.** A Date & Time is stored in UTC and the screens show it in the viewer's zone (the same as one saved from a form), so a clock reading stored as it is would show 5:30 later in India. The mapping therefore carries `ImportFieldMapping.TimeZone`, filled in silently from the browser of the person who sets the import up (no dropdown); the reading in the file is converted from that zone to UTC (07:15 AM India is stored 01:45 UTC and shows 07:15 again). A run started from the screen (Run now, the quick-import dialog) also sends the browser's zone (`X-Time-Zone`, kept on the run only as `ImportDefinitionConfig.RunTimeZone`), which is used for a mapping that names none, so **an import saved before this need not be opened again**; a run with no browser (a schedule, the run API, PowerFlow) stores the reading as written, as UTC. Time-only fields are plain text and are not affected.
* Saved in the mapping (`ImportFieldMapping.DateFormat` / `TimeFormat`); the plan builder and the "needs attention" check refuse a text column going to a date or time with no format, so **an import saved before this change is flagged until it is opened and saved once with the formats shown**. A source that is already a date or time (a table's Date field, a formula returning a date) needs none.
* **Formulas and virtual columns see the same thing the field gets.** A formula that reads a file column (`[Date] & "/" & [Start Time]`) reads it in the format chosen on the row that column goes to (`ImportPlan.ColumnFormats`, `ImportFileFormulaContext`): the date is shown in the app's date format and the time as `h:mm:ss AM/PM`, so `02/10/2026` chosen as DD-MM-YYYY is the 2nd of October in the formula as well as in the Date field. A column no mapping reads as a date or time keeps the older reading (an unambiguous form is normalised; an ambiguous order follows the app's date format).
* The flexible reading of the paragraph below is no longer used for these mappings; it remains only for the column fit check on a new file, formula-kind legacy mappings, and what a formula sees of a cell (`ImportFormulaText`).

* A sample file is uploaded while setting up the import so the columns can be seen and mapped; the real file is uploaded **each time the import is run**.
* Settings: sheet, row holding the column names (0 = none), first data row, separator, and whether a column is found by its **heading** (it may move) or its **position**.
* Every file value is text. Each is converted to the destination field's type as it is written; a value that does not convert is reported for its row (with its row number in the file).
  Dates should be `2026-03-09` (Excel date cells are converted automatically); other layouts are read month-first. Checkbox text: `true/false/yes/no/y/n/1/0`.
* Conditions on a file's columns compare as text. Formulas refer to columns by heading, e.g. `Upper([Name])`.
* A file import cannot be scheduled. The uploaded file is **kept for the retention** (see *History and files*) after the run; an upload nobody imported is removed after 24 hours.
* **Excel with several sheets:** tick "Read every sheet" to import all sheets into the same table, in workbook order. Each sheet must have the columns the import uses (found by heading or position); a sheet without a row of column names is passed over; a sheet missing a used column stops the run before anything is written, naming the sheet. Problem rows are reported with their sheet and row.
* Not supported: `.xls` (save as `.xlsx`), Reference/User/Address/File/range destination fields.

#### Which columns a table is offered

Every column of the file is listed as a source, so any of them can be mapped by hand. What is mapped **by itself** is a column whose heading matches one of the table's importable fields (ignoring case, spaces, underscores and punctuation, so
`Employee ID` = `employee_id`; Record ID#, system, formula and unsupported fields never match) **and** whose data fits that field's type. What a column can be mapped to **by hand** is every field its data fits, whatever its heading says
(a column of numbers goes to a number or a text field, not to a date or a checkbox). The server decides (`ImportColumnFit`, `POST import-files/{fileId}/column-fit`, one call per table for all its sheets; each column answers with `fieldFid` /
`fits` for its heading's field and `fitsFields` for the rest): it reads the first 200 data rows of each sheet and tries every filled cell with the run's own conversion and format checks (max length, regex, email / phone / URL); one failing
cell takes the field out, blank cells are ignored, and a column with no filled cell fits every field. Fields of one type with no format share one answer, so a table with hundreds of fields costs a few checks per column.
A saved mapping is never dropped by this. Formulas and virtual columns can use every column. If the check cannot be made, every column and every field is offered, and the run reports rows that do not convert.
When a workbook is mapped **sheet by sheet** (below), a table is built from the columns whose heading matches one of its fields, so only those are used there.

**Opening a new template (feedback, 2026-10-15):** a workbook is first read as one sheet, like a CSV, so **every column of the file is listed** (before, a workbook with several sheets opened straight into "The sheets I pick", which lists only matched columns, while an edited template listed them all). Only the columns whose heading names a field and whose values fit it are auto-mapped; by hand, a column can go only to a field its values fit. "The sheets I pick" is a choice the user makes, and a template saved that way opens that way.

**Find each column by.** *Its heading*: a column is found again by its heading in each new file, so it can move, and it is mapped by name. *Its position*: a column is the one in the same place in every file, whatever its heading says (it can change).
The columns are then named **Column 1, Column 2...** in the mapping, in formulas and in messages (`ImportFileColumns.NamedBy`), nothing is mapped by name, and the user maps each column to a field by hand. Sheets cannot be picked by field in this mode.

#### Sheets mapped separately, per table

On the screen a table has **one list of columns**, whichever sheets it takes them from: a workbook with several sheets shows a tick-box per sheet, and each ticked sheet adds the columns that fit the table (a field in several sheets is listed once; the destination of a column is the field it matched). Formulas and virtual columns can use only those columns; a formula runs on the sheets that have all the columns it uses, so it cannot mix columns of different sheets. The list is spread over the sheets when saved (one `ImportSheetMapping` per sheet with `Combined`), and rows of different sheets that share the common field become one record (see below). An import saved before this is edited as it was saved.

Each table of a workbook import chooses how it reads the workbook: **as chosen above** (the selected sheet, or "Read every sheet" with the same columns) or **sheet by sheet**.
Sheet by sheet, the table picks one or more sheets (every sheet with a fitting column is suggested), and each is mapped on its own: its own header row and first data row, mapping,
column rules, virtual columns and filter (`ImportSheetMapping`, stored per table as `sheetMappings`; the layout of each sheet used is stored once in `file.sheets`). The same sheet can feed several tables
with different mappings, and one table can use sheet mode while another reads the file whole.

* A sheet is read **once** per run and shared by every table that uses it; a table with several sheets gets one plan per sheet. Counts, the details file and Remove duplicates / unique checks are per **table**, across its sheets (the details file has a **Sheet** column and the union of the fields its sheets write).
* **Combining sheets into one record.** When a record is spread over sheets (e.g. a student's details on one sheet, contact on another), the table can *combine on* a destination field (`JoinFieldFid` on each sheet mapping, the same field for every sheet that has it; the screen suggests the merge key or a field several sheets map).
  Rows with the same value in that field (trimmed, case-insensitive) become **one record** before any check or write (`ImportJoinedChunkReader`): each field takes the first non-blank value, in sheet order; a sheet's own filter and virtual columns apply first.
  When no field is chosen, the run picks one itself: the merge key if two or more sheets map it, else a unique field two or more sheets map (never a non-unique one); choosing "nothing" on purpose is saved as field 0 and keeps every row a record. A sheet that does not map the field, a row with no value in it, and a second row of the same sheet with the same value are records of their own (nothing is dropped). Required checks, Remove duplicates, merge and unique checks see the whole record, and a field only has to be mapped in one sheet; the merge key too.
  Every row of a combined table's mapping must take its value from a column (a fixed value or formula is a virtual column of its sheet). **Rows read** is the number of rows the sheets had (500 rows in five sheets); the table's counts, "rows it got" and the details file are per **record** (the details file says which sheet and row each record started in); at most 1,000,000 records are combined in memory per run.
* A table in sheet mode ignores the import-wide filter and the table filter; each sheet has its own filter. Merge needs the key mapped in every sheet of the table.
* A missing sheet, or a sheet without its row of column names, stops the run before anything is written, naming the sheet. The sheet layouts are checked by *needs attention* like the rest.
* `POST import-files/{fileId}/sheets` reads several sheets (each with its own layout) in one call. Limits: 50 sheets per table, 100 sheets in a workbook mapping.

**The filter of the whole import opens with its first condition ready** (feedback, 2026-10-15): the first field of the source, its first operator, and no value yet. A condition nothing has been typed in is left out when the import is saved (`pruneEmptyFilterNodes`), so it filters nothing until it is filled in; the person can remove the row. It is the same for a table and a file source.

### The list of imports: source filter and schedule switch

The Templates list has a **source filter** beside the search box (All sources / File / Table; it combines with the search), and a table import that has a schedule has an **on/off switch** next to Run, so a schedule can be paused or resumed without opening the import. The switch calls `PUT import-definitions/{id}/schedule/enabled` (`ImportScheduleController`, `SetImportScheduleEnabledHandler`), which flips only `ImportSchedule.Enabled` (the settings, anchor and mapping stay as saved) and works out the next run when switched on; switching on also re-binds the run identity to the person who did it (as saving does) and is refused for an import that needs attention. The list item carries `ScheduleEnabled` (null when there is no schedule). The import's own screen is unchanged and still edits the whole schedule.

### Import from a table's own menu (Phase I, no template)

A table's records view has **Import** in its top-right ⋮ menu (shown only to a person who can **add** records to that table; **Merge** is offered only if they can also **edit**). It opens a dialog: choose **a file** (CSV or Excel, one sheet) or **another table**, map what goes where with the template screens' own mapping (matching by heading, type check, fixed values, formulas, virtual columns), choose Copy or Merge, the duplicate policy and who else to email, and run. Nothing is kept as a template; to repeat or schedule an import, create a template in Imports. Not offered here: schedules, several tables in one run, sheets read separately, filters, reading only what changed.

* **Backend:** `POST tables/{tableId}/quick-imports` (`QuickImportController`, `StartQuickImportHandler`). It is the template import under another door, not a second engine: the definition is saved through `SaveImportDefinitionHandler` (every check a saved import goes through, permissions included), the run is started through the unchanged `StartImportRunHandler`, and the definition is then **soft-deleted** (in a `finally`, so a failed start leaves nothing). A run needs a definition row (`ImportRun.ImportDefinitionId` is required), and runs of soft-deleted definitions are already first-class: every list of templates (Imports, PowerFlow, pipelines, the checker, the scheduler) filters `IsDeleted = 0`, while History, the run page, the "my imports" notices and the details files do not, so a quick import shows in History as "Quick import - <file name or source table>". No migration, and no existing handler or query changed.
* **Cost:** one soft-deleted `ImportDefinition` row per quick import (a small JSON). For about one request the temporary definition exists and could appear in a list refreshed at that moment.
* **Frontend:** `QuickImportDialogComponent` (`shared/components/quick-import-dialog`), the config is assembled by `core/utils/quick-import-config.ts` (with a spec); the template builder and its components are untouched.

The dialog's **Merge** also offers the **Record ID#** as the key (and, while it is the key, as the destination of one row), exactly as the template builder does; with the Record ID# as key and no row for it, the dialog says to add one (`quickImportBlocker`).

### Only records changed since the last run

A merge **from a table** can be set to **only read records changed since the last run** (`OnlyChangedSinceLastRun`, off by default). A scheduled import then does not read, compare and rewrite records nothing has touched (a nightly merge of 70,000 records that changes 40 reads and updates 40).

* It applies to **scheduled runs only**: a run started by a person or through the API always reads every record. The last finished run counts whichever way it was started.
* When a run is queued, `StartImportRunHandler` asks the run repository for the import's last run that **finished** (success or partial; failed and cancelled runs did not read everything) and puts its **start** time, minus a one-minute margin, in the run's own copy of the definition (`ChangedSinceUtc`). The plan builder then adds `Date Created > since OR Date Modified > since` (system fields 1 and 2, exact UTC times) to the source filter, next to the import's own conditions and the person's role filter. A record that was never changed has no change time, so its creation counts.
* The **first run**, and the **first run after the import is edited** (its mapping, key, rules, filters, type or tables - compared with the last run's own copy by `ImportRunSince.Fingerprint`; who is told, the schedule and the name do not count), read every record, because records nothing touched have never been through the new mapping.
* Only a merge from a table can use it, and every table the import fills must merge: a copy would add the changed records again. It is refused otherwise.
* The source must not be the table being filled: each run would change its own source records (Date Modified), so every record would be read and rewritten every time. The plan builder refuses it, and the import screen hides the option in that case.
* Limits: a record is read when it, not something it points to, changed (a change in a parent record that only shows through a Lookup is not seen); a record that failed in a partial run is tried again only when its source record changes; a change made only in a destination table is not put back until its source record changes. To read everything again, switch the option off for a run.

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

### Listing imports for a PowerFlow

`GET /apps/{appId}/powerflow/import-templates?tableId={guid}&templateId={guid}&sourceKind={file|table}` (`PowerFlowImportTemplatesController`, its own handler; read-only, nothing of the import screen or the run API is used or changed).
For a step that picks an import: choose the app, then a table, and list the imports that fill it. All query parameters are optional and are combined with **AND**:

| Parameter | Keeps |
|---|---|
| `tableId` | imports that fill that table, as their first table or any other (a table of another app is a **404**) |
| `templateId` | that import (an id that is no import of the app is an empty list) |
| `sourceKind` | imports that read a `file` or a `table` (any case, surrounding spaces ignored; blank = no filter; anything else is a **400**, `sourceKind` in the errors) |

Responses: **200** `{ "data": [ { templateId, name, importType, sourceKind, tableId, tableName, tableIds[], needsAttention, attentionReason } ] }`; **400** a malformed or empty (all-zero) `appId` / `tableId` / `templateId`, or a bad `sourceKind`; **401** not signed in;
**403** not a member of the app; **404** a `tableId` of another app. The order of checks is: who may ask, then what was asked, then the data, so a stranger learns nothing from a validation message. `templateId` is what `POST /apps/{appId}/imports/{templateId}/run` takes.
It does not recheck imports (`needsAttention` is as last saved or checked), changes nothing, and sends `Cache-Control: no-store`. An import whose saved options are damaged is still listed, with its first table only. The list is not paged (an app has few imports).

#### Starting and following an import from a PowerFlow

All in `PowerFlowImportTemplatesController`, which calls none of the import screen's endpoints. The run is started by the same code as the import run API (`StartImportRunHandler`), so it is checked, queued and reported the same way; only the file upload is folded into the same call.

* `POST /apps/{appId}/powerflow/import-templates/{templateId}/run` - **multipart/form-data** (shows a file picker in Swagger). Fields: `file` (a CSV or Excel .xlsx; **required for a file import, not allowed for a table import**) and `notifyEmails` (optional, addresses separated by commas or semicolons; replaces the people saved on the import for this run).
  Optional header `X-PowerBase-Client-Token` (up to 200 characters) makes a retry safe: the same token returns the same run (**200**, `replayed: true`) and the file sent again is discarded. Otherwise **202** `{ "data": { "runId", "replayed" } }`.
  A table import needs no body at all (an empty form). A file import uploads the file to the import's first table (needs the right to add records there, like the screen), runs it, and deletes it when the run ends; if the run is refused the upload is removed at once.
* `GET /apps/{appId}/powerflow/import-runs/{runId}` - status (`status`, `progress`, `rowsRead`, `inserted`, `updated`, `skipped`, `errored`, `unchanged`, times, `errorDetail`, `hasFeedback`), poll until `success`, `partial`, `failed` or `cancelled`. Counts only.
* `GET /apps/{appId}/powerflow/import-runs/{runId}/feedback` - CSV of the rows that were not imported (the person who started the run, or an admin).

Errors: **400** no file for a file import, a file for a table import, a wrong / empty / too large file, a bad address list, an empty id, an overlong token or a failed pre-run check (e.g. a merge field with duplicates); **401** not signed in; **403** not a member of the app or no permission on the import's tables;
**404** no such import in the app; **409** already running. Checks run in this order: who may ask, what was sent, then the data.

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
