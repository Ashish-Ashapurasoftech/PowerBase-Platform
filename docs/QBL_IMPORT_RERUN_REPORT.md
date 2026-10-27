# cares.qbl import — re-run report and gap analysis

Run: real 9.1 MB `cares.qbl` through the API (background job, as for any file over 3 MB), after the formula-engine,
reference-field and importer changes. Verified by the import report returned by the job
(`GET /apps/import/jobs/{id}/report`) and by the opt-in test class `CaresRealFileTests`.

## 1. Progress across runs

| Measure | Run 1 (before formula work) | Run 2 (alias + coercion) | Run 3 (this run) |
|---|---|---|---|
| Fields created | 872 | 1,007 | **1,069** |
| Formulas needing manual review | 279 | 144 | **82** |
| Formulas imported (clean + adjusted) | — | — | **350** (268 clean, 82 adjusted) |
| Form rules created | 20 | 20 | **23** |
| Skipped / limited items | 2,030 | 1,771 | **1,569** |
| Tables / relationships / reports / forms / roles / variables | 95 / 161 / 501 / 96 / 14 / 52 | same | same |

Import completed to 100 %, the completion email was sent, and the test app was deleted afterwards.

## 2. Test class

`tests/PowerBase.UnitTests/Import/Qbl/CaresRealFileTests.cs` (opt-in: set `POWERBASE_CARES_QBL` or leave `cares.qbl(.txt)` on the Desktop; without a file it does nothing, so CI is unaffected):

1. The export converts to a **valid** PBL document (≥ 90 tables, > 1,000 fields, > 150 relationships, roles and forms present, no validator errors).
2. **Security guard:** every role record filter refers only to fields declared in the document (a filter on a missing field would silently widen access).

Both pass. Full-suite status: 3,433 unit tests pass; the one failure (`SummaryCriteriaValidatorTests.FindProblem_TextOperatorAgainstParentField_IsRefused`) predates this work.

## 3. What the 82 remaining formulas are

| Cause | Formulas | Fix |
|---|---|---|
| Depend on a **lookup/summary that could not be created** (listed in §4) | 33 | Resolved when the blocking summary/lookup limits are lifted |
| Depend on **another failed formula** | 13 (+ 9 that also depend on a lookup/summary) | Cascade; resolves with the above |
| **Text compared with a number** (`=`, `>`, `<>` with Text and Number) | about 14 | See below — Reference now reads as Text |
| `&` joining text with a number/date that the rewriter could not fix because another error was present in the same formula | about 17 | Clears once their other errors clear |
| Unknown fields of unsupported types (`Address1/2` sub-parts, **file-attachment** fields such as `Template File Attachment`, `IsCSV`/`IsHTML`) | 4 + ~8 | PowerBase has no formula access to File or Address parts |
| Numeric field ids inside token-URL formulas (`[1226]`) | 2 | Source refers to fields not on the table |
| Genuine dialect differences: `If` with 7 arguments (`Age`), `Split` with 1 argument (`Contact Sharing Info`), `Time` passed where Date/DateTime expected (`StartTime`, `EndTime`), `Number` vs `Date` comparison (`Qualified Session`) | 5 | Rewrite rules or manual |

**New finding.** References are now readable in formulas but present as **Text**. Quickbase types a reference as the parent's key, which is a **number** when the key is Record ID#. The roughly 14 "Text and Number" comparisons in this run are the visible cost (for example `[Related X] = [Record ID#]`). Recommended follow-up: type a Reference by its parent key (Number for Record ID#, otherwise the key field's type) instead of always Text. That needs the key type available where formulas compile, so it is a separate, careful change.

## 4. Why the blocking lookups and summaries were not created (what is blocking formulas)

| Reason | Items | PowerBase gap? |
|---|---|---|
| Lookup source field not created (itself a failed formula/summary) | 57 | Cascade |
| Summary target field not created | 43 | Cascade |
| Summary **matching criteria compares to a calculated field** | 42 + 13 | **Yes** — summary filters run in SQL and cannot evaluate formulas/lookups |
| Summary **of a lookup of a missing/calculated field** | 34 | **Yes** — same limit |
| Summary query uses a **relative date** (`today`, …) | 32 (+32 duplicate listing) | **Yes** — no relative-date operators in summary filters (reports have them) |
| Combined Text over a **Reference** field | 15 | **Yes** — a Reference stores a record id; needs a join to the parent key |
| Lookup chain deeper than 3 | 3 | PowerBase rule |

## 5. Other skipped items (not formula-related)

| Items | Reason | Category |
|---|---|---|
| 440 + 133 | Form elements pointing at fields that were not created | Cascade of §3/§4 |
| 249 | Reports referencing fields not yet created | Cascade |
| 30 | A report/section reused several times in one table or form; only the first is imported | PowerBase limit (one per table) |
| 28 | Report filters on fields that do not exist | Cascade |
| 28 | Field-level access rules for fields that were not created | Cascade |
| 19 | Role record filter on a field that does not exist → role got **no access** to that table (safe default, never widens) | Cascade; resolves with fields |
| 14 + 8 | Embedded report elements/forms whose report or relationship was not created | Cascade |
| 17 | Form rules: no representable action (6), unsupported condition structure (6), target scope (5) | **PowerBase gap** |
| 7 | Formula fields with no expression (buttons) | Not importable |
| 4 | Code pages / home page (Code Pages not enabled for this tenant; home page has no equivalent) | Tenant setting / gap |
| 1 | App "Searchable" (different meaning from hide-from-public-search) | Intentional |

## 6. Missing mappings and capabilities, by module

**Fields and formulas**
- Reference fields typed by their parent key (see §3).
- Formula access to File and Address sub-parts.
- `Right`/`NotRight`-style delimiter semantics were implemented from usage; confirm against Quickbase.

**Relationships and summaries** (largest remaining blocker)
- Summary criteria on calculated fields; summaries of lookups of calculated fields.
- Relative-date operators in summary filters.
- Combined Text over a Reference field.

**Roles and permissions**
- Records filters on computed fields (`CurrentUser*` lookups) rely on those fields existing; until then roles get no access (safe).
- "Restrict access to new tables" and role home pages have no target.

**Reports**
- More than one report with the same name on one table.

**Forms**
- Form rule actions/conditions/target scopes not representable (17 rules).
- Form elements for File-type fields (no PowerBase form element).

**App and tenant**
- Home page / dashboards; Code Pages require the tenant feature.

## 7. Recommended order

1. Summary filters on calculated fields and relative dates — removes the largest cascade (about 130 items directly, and most of the 33 + 13 dependent formulas).
2. Combined Text over a Reference (unblocks `Client ID` and the key fields of three tables).
3. Type References by parent key, to remove the Text-versus-Number comparisons.
4. Form-rule gaps (17 rules).
5. Report and section reuse.
