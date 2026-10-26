# cares.qbl import — skipped-items gap analysis

Source: `Downloads\Skipped (not created).txt` (2,030 lines, 337 distinct messages) from the latest import.
The import itself succeeds (95 tables, 501 reports, 161 relationships, 96 forms). The skipped list is dominated by
**cascades from a small number of root causes**; fixing the roots removes most of it.

## Root causes (ordered by impact)

| # | Root cause | Direct hits | Cascades into | Type |
|---|---|---|---|---|
| 1 | **279 formula fields need manual review** (not created) | 279 | ~629 form elements, ~249 reports, ~133 form elements (phase msg), 69 lookups, 61 summaries, 32 report filters, 29 field-access, 26 role record-filters, 3 key fields | Fixable (mostly importer) |
| 2 | Summary criteria compare to a calculated field | 41 + 8 | – | PowerBase limit |
| 3 | Summaries with relative-date queries (`today`) | 32 | e.g. `Has Active MCO` | Importer + small PB feature |
| 4 | Reports/sections used twice on one table | 30 + 3 | – | PowerBase limit (one per table) |
| 5 | Summary/lookup of a lookup or Reference (Combined Text of a Reference, chained summaries) | 28 + 7 + 5 + 3 | – | PowerBase limit |
| 6 | Form rules with no PB equivalent (actions, condition structure, target scope) | 6 + 6 + 5 + 6 | – | PowerBase gap |
| 7 | Formula fields with no expression (button-like fields) | 7 | – | Not importable |
| 8 | Lookup chain deeper than 3 | 3 | – | PowerBase limit |
| 9 | Code pages / Home page | 4 | – | Tenant feature off / no equivalent |

## Root cause 1 in detail: why 279 formulas fail (reason counts, a formula can have several)

| Reason | Count | What it is | Fix |
|---|---|---|---|
| `Unknown field [X]` | 560 | (a) `[_DBID_CLIENTS]`-style **table-ID references** used to build "Add record" URLs (~80 uses); (b) references to fields that are themselves uncreated (dependency cascade: `Client ID`, `Related Client`, `*Client Programs`, `HCBS Status`…) | (a) map `[_DBID_X]` to the PowerBase table identifier / alias in the URL, or skip with an explicit "button URL" diagnostic; (b) resolved automatically once the formulas they depend on compile |
| `URLEncode argument expects Text but got Number` | 75 | Quickbase coerces silently | Importer: wrap Number/Date arguments in `ToText(...)` when the type checker reports this |
| `'&' requires text on both sides` / wrap in `ToText()` | 32 + 22 + 10 | Same implicit coercion in concatenations | Same auto-`ToText()` wrapping in `FormulaTranslator` |
| `Unknown function 'ToTimeOfDay'` | 11 | QB `ToTimeOfDay` | Map to the PowerBase time-of-day function if one exists, else keep review |
| `Left/Right argument expects Number but got Text` | 14 | Quickbase `Left(number, n)` | Wrap in `ToText()` |
| `Unknown variable type 'dateTime'` | 4 | Variable type names in `Let`-style formulas | Normalise to `timestamp` |
| `ToTimestamp expects N arguments` | 2 | Different arity | Rewrite arguments |
| `Operator '>' cannot compare Number and Date` | 2 | Type mismatch | Keep review |

Implicit-coercion fixes (~150 reasons) and the `[_DBID_*]` handling are the two highest-value next steps.

## Other findings

- **3 tables lose their key field** (`Clients`, two copies): the key `Client ID` is a Summary field that was not created. Fixed indirectly by root cause 1/5.
- **26 roles got "no access" instead of a record filter**: the filter refers to `CurrentUser*` formula fields that were not created. This is the safe behaviour (never widens access) and will resolve once those formulas import.
- **Field-level access (29)** was not applied because the field was not created; same cascade.
- **App `Searchable`** intentionally not mapped (different meaning).

## Missing features in PowerBase (cannot be fixed by import code)

1. Summary matching criteria on calculated (formula/lookup) fields.
2. Relative-date criteria (`today`, `yesterday`) in summary filters.
3. More than one embedded report of the same name per table / one section reused per form.
4. Combined-Text summary over Reference fields; lookups chained deeper than 3.
5. Form-rule actions/conditions/target scopes used by the source (≈23 rules).
6. Home page / app dashboard resources; Code Pages disabled for the tenant.
7. Per-role default pages, decimal-comma formatting.

## Recommended next steps

1. Auto-coerce text in `FormulaTranslator` (URLEncode, `&`, Left/Right) — expected to recover most of the 279 formulas.
2. Resolve `[_DBID_X]` table references.
3. Re-run the import and re-measure: most of the ~1,300 cascade lines should disappear.
4. Decide on the PowerBase features in the list above.
