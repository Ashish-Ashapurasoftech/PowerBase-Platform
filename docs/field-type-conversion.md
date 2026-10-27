# Field Type Conversion ("Change Type") — Spec

Quickbase-compatible field type conversion for PowerBase (scope §10.1 Formula → Scalar, §10.2 Scalar → Scalar).
This file is the source of truth for the conversion rules. The code that implements them lives in
`src/PowerBase.Application/Fields/TypeConversion/` (new, isolated module — existing field/record code is not modified
by Phase 1), and the tests that pin them are in `tests/PowerBase.UnitTests/Fields/TypeConversion/`.

## General rules
- The system attempts a typecast. If it fails, the value becomes **NULL** (shown blank).
- **No record is ever deleted.** Only the one field's values change.
- **Any → Text is always retained.**
- The operation is logged (audit `SchemaChanged`, Phase 3).
- "First occurrence" rule: when text holds more than one candidate (number, date, time, email, phone), the first one
  wins and the rest is ignored (`test12_27` → `12`).

## Supported types
| | Codes |
|---|---|
| Scalar | Text, TextMultiLine, RichText, SingleSelect, Number, Currency, Percent, Rating, Date, DateTime, Time, Duration, Boolean, Email, Phone, Url, File, User |
| Formula | Formula_Text, Formula_Number, Formula_Date, Formula_DateTime, Formula_Duration, Formula_Bool, Formula_User, Formula_Url, Formula_Time, Formula_Phone, Formula_Email, Formula_RichText |
| **Not supported** (Change Type hidden) | ActionButton_* (File, Signature, Prompt, Data), MultiSelect, Address, MultiUser, Formula_ListUser, Formula_MultiSelect, Reference, Lookup, Summary, ReportLink, DateRange, NumericRange, system fields (Record ID#, Date Created/Modified, Record Owner, Last Modified By) |

Restrictions:
- Same type → not offered.
- **File** as a source can only become Text / Multi-line text (file names) or a formula. As a target every value
  becomes blank (destructive warning).
- **Relationship parent key** fields may only become a key-eligible type: Text, Number, Currency, Percent, Rating, Date.
- A change that would make a Summary field invalid is blocked (Phase 3, `SummaryDependencyGuard`).

## Conversion kinds
| Kind | Behaviour |
|---|---|
| Scalar → Scalar | Every stored value goes through the value rules below. |
| Numeric family (Number ↔ Currency ↔ Percent ↔ Rating) | Same as the existing "Display As" switch: entering Percent ×100, leaving Percent ÷100 (Percent is stored as a whole percentage, 115 = 115 %). |
| Formula → Scalar | Each record's evaluated formula value is converted with the value rules (the formula type is treated as its scalar twin, e.g. Formula_Text = Text) and stored. The expression is discarded and the field becomes editable. |
| Scalar → Formula | Stored data is discarded (destructive warning). Settings are reset to defaults: an old expression is **never** restored. |
| Formula → Formula | Expression kept, recompiled against the new result type. Compile errors are shown as a warning, not a blocker. |

## Value rules (Scalar → Scalar, and Formula → Scalar via the scalar twin)

### To Text / TextMultiLine
| From | Result |
|---|---|
| Text, Email, Url, SingleSelect | Value as-is. |
| TextMultiLine → Text | Line breaks collapse into one space. |
| RichText | HTML kept as-is (QB parity). |
| Number, Currency, Percent, Rating | Plain invariant number, no grouping or symbol (`1200.5`), so nothing is rounded away. |
| Date | App date format (default `MM-DD-YYYY`). |
| DateTime | App date format + ` h:mm AM/PM`. |
| Time | `h:mm AM/PM`. |
| Duration | Smart text (`3 days`, `90 mins`). |
| Boolean | `Yes` / `No`. |
| Phone | The number, plus ` x{ext}` when there is an extension. |
| File | File name(s), comma-separated. |
| User | The user's **email** (lossless round-trip; user id text if the user no longer exists). |

### To RichText
The text form of the value, **HTML-encoded**, with line breaks as `<br>`. RichText is rendered as trusted HTML, so
markup typed into a Text field shows literally instead of running (security: no stored XSS). This deliberately differs
from QB, which renders HTML typed into text.

### To SingleSelect
The text form of the value, on one line. The distinct values become the field's choices (first-seen order, capped at
500; values beyond the cap are still stored and flagged in the preview).

### To Number / Currency / Percent / Rating
| From | Result |
|---|---|
| Text-like (HTML stripped first) | First numeric occurrence. Sign, decimals and `,` thousand groups understood. |
| Boolean | 1 / 0. |
| Duration | Value in the source's display unit (Weeks/Days/Hours/Seconds), minutes otherwise. |
| Date, DateTime, Time, User, File | First numeric occurrence in the value **as shown** (date in the app's format, user as email, file name): `10-02-2026` → 10 (MM-DD-YYYY; the day for DD-MM-YYYY), `1:30 PM` → 1, `invoice 2024.pdf` → 2024. No number → NULL. |

Examples: `test12test12` → 12, `test12test` → 12, `test1` → 1, `test` → NULL, `Mahesh - 4000` → 4000,
`$1,200.50` → 1200.50, `-7.5 kg` → -7.5, `<b>42</b>` → 42.

### To Boolean
| From | Result |
|---|---|
| Text-like | `true/false`, `yes/no`, `1/0` (case-insensitive, trimmed). Anything else → NULL. |
| Number family | 0 → unchecked, 1 → checked, else NULL. |
| Others | NULL. |

### To Date / DateTime
| From | Result |
|---|---|
| Text-like | First date found: the app date format first (MM-DD vs DD-MM order), then ISO `YYYY-MM-DD`, then month-name forms (`Oct 2, 2026`, `2 October 2026`). A time after the date is kept for DateTime. Unparseable → NULL — **never 01-01-1970**. |
| Date → DateTime | Midnight. |
| DateTime → Date | Date part. |
| Others | NULL. |

### To Time
Text-like: first `h:mm[:ss] [am/pm]` or `h am/pm` found. DateTime → its time part. Others → NULL.

### To Duration (stored as whole minutes)
| From | Result |
|---|---|
| Text-like | `3 days`, `2h 30m`, `1 wk 2 days`, `1:30` (h:mm), `1:30:15` (h:mm:ss). A bare number uses the target's display unit. |
| Number family | Value in the target's display unit (Weeks/Days/Hours/Seconds), minutes otherwise. |
| Others | NULL. |

### To Email / Phone / Url / User / File
| To | Rule |
|---|---|
| Email | First email address found in the text; User → that user's email. Else NULL. |
| Phone | First phone-like run with ≥ 7 digits (`+`, spaces, `()`, `.`, `-` allowed), optional `x`/`ext` extension. Stored as `{"number","ext"}`. Else NULL. |
| Url | Text as-is (trimmed, one line); the display adds `http://`. |
| User | First email in the text matched (case-insensitive) against **this app's users**. Unmatched → NULL. |
| File | Always NULL (destructive). |

## Dependents (Phases 3–4)
- Formulas reference fields by fid, so nothing needs to be re-saved; the preview lists dependent formulas that will no
  longer compile.
- Reports keep their saved configuration. Invalid parts (Sum on text, numeric grouping on text, operators that don't fit
  the new type) are ignored at run time and come back if the type is reverted.
- Lookups get their stored source type rewritten; Summaries are guarded.

## Test scenarios (from QB analysis)
| # | Scenario | Expected in PowerBase |
|---|---|---|
| 1 | Text `test12test12` → Number | 12 |
| 2 | Text `test` → Number | blank |
| 3 | Formula Text `Mahesh - 4000` → Number | 4000 (QB gives 0 — fixed) |
| 4 | Text `abc` → Date | blank (QB gives 01-01-1970 — fixed) |
| 5 | Text `10-02-2026` → Date | Oct 2 2026 (app MM-DD-YYYY) |
| 6 | Text `Yes` / `0` / `maybe` → Boolean | checked / unchecked / blank |
| 7 | Number 5 → Boolean | blank |
| 8 | Boolean → Text | Yes / No |
| 9 | Text `mahesh@x.com` → User (app user) | that user; Text again → `mahesh@x.com` |
| 10 | RichText `<h2><u>Mahesh</u></h2>` → Text | HTML kept |
| 11 | RichText `<p>Total 25</p>` → Number | 25 |
| 12 | Multi-line `testing⏎testing1` → Text | `testing testing1` |
| 13 | Text → Duration `3 days` | 4320 minutes |
| 14 | Formula Text → Text → Formula Text | data kept, then cleared with an empty expression |
| 15 | Currency → Text → Currency | values round-trip; summary/chart aggregations come back |
| 16 | Name field (used in a formula) Text → Number → Text | formula works again without re-save |

## Open points to confirm with business
- SingleSelect choice cap (500).
- Url: QB stores `http://` in the value; we only add it on display.
- RichText → Text keeps HTML (QB parity) rather than stripping it.
- Text → RichText encodes HTML (security) instead of rendering it like QB. Rendering it would need a server-side HTML
  sanitizer first.
- Bare number → Duration with display "Smart" or a clock format uses minutes.
