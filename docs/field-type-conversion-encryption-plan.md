# Change Type for encrypted fields — phase plan

Extends Field Type Conversion ([field-type-conversion.md](field-type-conversion.md)) to fields whose values are stored
encrypted: every non-system field of an **encrypted app**, and any field with its own **Encrypted** setting.

## How encrypted values are stored (why this needs its own path)
- App-level AES-GCM with the app's own key (`FieldEncryptionContext`, `CachedDekEncryptionService`); random nonce,
  so equal values never give equal ciphertext.
- Only **text-like** types are encrypted. `PhysicalNaming.IsEncryptionExemptTypeCode` keeps Number, Currency, Percent,
  Rating, Date, DateTime, Time, Duration, Boolean, User, Reference, ranges and all formulas as plain typed columns even in
  an encrypted app. So whether a field is encrypted depends on its **type**, and a type change can cross the boundary:
  - text-like → text-like: stays encrypted, converted **in place** (`VARCHAR(MAX)` kept);
  - text-like → exempt (e.g. Text → Number): decrypted, stored **plain** in the new typed column;
  - exempt → text-like (e.g. Number → Text): new `VARCHAR(MAX)` column holding **ciphertext**;
  - formula → text-like: new encrypted column; anything → formula: column dropped.
- Tenants on SQL **Always Encrypted** (secure enclaves) use a different scheme and are refused up front (E4).

## Rules
- Only module files change (`Application/Fields/TypeConversion/`, `Infrastructure/Services/FieldColumnConversion*`);
  existing encryption code is called, never edited. Non-encrypted fields keep the existing, tested path unchanged.
- Values are decrypted and re-encrypted with the same `FieldEncryptionContext` record reads/saves use, and written as
  the plain text a record save stores (`FieldColumnConversionSql.ToEncryptedPlainText`).
- Data + the field's new type are written in one transaction (all-or-nothing).
- No commit/push without approval.

## Phases

| Phase | Scope | Est. | Status |
|---|---|---|---|
| **E1** | Preview reads encrypted values: decrypt → count / sample. Read-only; changing stays blocked. | 1 d | ✅ Done — verified locally (samples `test12test12 → 12`, `85% → 85`) |
| **E2** | Stored → stored conversion on encrypted fields: decrypt → convert → re-encrypt in place, one transaction. Formula ↔ stored still blocked. | 2 d | ✅ Built. ⚠ Bug found in E3: it re-encrypted **every** target, so Text → Number stored ciphertext in a Number field (exempt-type rule above). Fixed by E3 |
| **E3** | Source and target encryption decided **separately** from each type (the four crossings above); formula ↔ stored lifted. | 1 d | ✅ Done — verified locally in the DB (see E5) |
| **E4** | Always Encrypted (enclave) tenants: preview shows a blocker, change call refused (409) before any read/write; non-encrypted fields and formula → formula still allowed. | 0.5 d | ✅ Built + unit suite green. Not testable locally (no enclave tenant) — confirm on dev |
| **E5** | QA: crossing cases on an encrypted app, regression on a normal app, full unit suite. | 1.5 d | ✅ Local QA done (results below); UI + dev-server smoke test pending |

Total ≈ 6–7 dev days.

## Testing per phase
- **E1:** in an encrypted app, `POST …/change-type/preview` shows readable before → after samples; `POST …/change-type`
  is refused; field and values unchanged. Normal apps unchanged.
- **E2:** in an encrypted app, Text → Number, Date → Text → Date, Checkbox → Text, Text → User, Multi-line → Text all
  convert; values read back correctly; DB column still holds ciphertext; formula targets still blocked; normal apps unchanged.
- **E3:** formula → Text keeps evaluated values (encrypted); Text → formula clears data; normal apps unchanged.
- **E5:** full scenario run on both app kinds; all unit tests green.

## E5 results (local, 2026-11-13)
- **Unit suite (Release, incl. E4):** 12,370 passed, 2 skipped, 1 failed — `AppImportJobTests.Policy_OnlyFilesOverThreeMegabytesRunInBackground`,
  an existing failure on develop, unrelated to this module.
- **Encrypted app — crossing cases (value via API / column in DB):**

  | Change | Values read back | Stored as |
  |---|---|---|
  | Contact Text → Email | `mahesh@x.com`; `not an email` → blank | `varchar`, ciphertext (in place) |
  | Score Text → Number | `test12test12` → 12, `85%` → 85 | `decimal`, plain |
  | Qty Number → Text | 5, 7 | `varchar`, ciphertext |
  | Tag Formula → Text | `Mahesh x`, `Riya x` | `varchar`, ciphertext |
  | Drop Text → Formula | blank | column dropped |
- **Normal (non-encrypted) app — 15 changes, all as before:** Text → Number (first number), Number → Currency,
  Currency → Percent (×100), Date → Text (app format), Text → Date (first date), Checkbox → Text (Yes/No),
  Text → Checkbox, Number → Checkbox (0/1 only), Text → Email, Text → User (app users by email), Multi-line → Text
  (newlines → space), Date → Number (first number shown), Formula → Text, Formula → Formula, Text → Formula.
  No previews blocked, no new errors.
- **Not covered locally:** E4 enclave blocker (no enclave tenant), UI pass, dev-server smoke test.

## Test data to clean up (local)
- App **test**, field *Score* (table `t_10031`): a Number field holding ciphertext from the E2 bug. Change it back to Text
  or delete the field.
- Scratch apps **ZZ Encrypted Change Type** (incl. *Score* in `t_10032` and *Tag* from the first E3 attempt) and
  **ZZ Normal Change Type …**: safe to delete.

## Build note
Teammates added a `RunMigrations` post-build target to `PowerBase.API.csproj` for **Debug** builds (`migrate control` +
`migrate tenants`), which can hang. Running tests with `-c Release` skips it.

## Search refresh no longer blocks Change Type (fixed in E5)
Before: after the data change, Change Type awaited the shared `FieldSlotSync.SyncAsync`, which writes the first page to Azure
Search and publishes to Service Bus with retries. Where those are unreachable (this laptop), the request kept loading for
minutes although the conversion was already committed.

Now (`ChangeFieldTypeCommandHandler.RefreshSearchIndexAsync`, module file only; `FieldSlotSync` untouched):
1. the search job is recorded in `meta.SearchFieldJob` (database, durable);
2. the job is queued for the indexer worker, which writes every page; the request waits at most 3 s for the queue;
3. if the queue is unreachable, the send continues in the background (singletons only) and `SearchOutboxRelay` re-sends the
   recorded job, so the index still catches up;
4. the shared-index-capacity check (`SearchWideMigration`) still runs, its Azure calls in the background;
5. where jobs can't be recorded (no job table), the previous `FieldSlotSync.SyncAsync` path runs unchanged.

Trade-off: the first page is no longer written inside the request, so search reflects the change once the indexer worker runs
(seconds where Service Bus works). Verified locally: 6 changes (normal + encrypted) each answered in ~3 s, jobs recorded.
